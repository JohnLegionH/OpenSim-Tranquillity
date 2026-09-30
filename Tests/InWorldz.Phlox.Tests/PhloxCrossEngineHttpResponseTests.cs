using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-55: one region running YEngine and Phlox, both HTTP pumps running. The core keeps one completed-request queue per
/// region (HttpRequestModule.GetNextCompletedRequest dequeues), and whichever engine's pump takes a response must get it
/// to the script that asked, whichever engine runs it. Before, a YEngine script's response taken by Phlox's pump was
/// posted through Phlox only and lost.
/// Scripts in several prims (the core throttles per prim: 3 at once, then 1 a second) each make requests to a loopback
/// listener that answers each path with its own body and status. Every YEngine request must get exactly one
/// http_response, in the script that made it, with its body and status; no script gets a duplicate, another script's
/// response or a wrong body or status.
/// Not fixable in Phlox (NEEDS JOHN, see the skipped test): YEngine's pump posts what it takes through YEngine instances
/// only (the Shared AsyncCommandManager's own engine list, which Phlox is not in), so a Phlox script's response that
/// YEngine's pump takes is lost.
/// In "phlox-state" (runs alone), as PhloxMimeTypeYEngineTests: it needs YEngine's statics and the core HttpRequestModule's
/// process-wide filter at once. The endpoint is a loopback listener opened by an Except entry.
/// </summary>
[Collection("phlox-state")]
public class PhloxCrossEngineHttpResponseTests
{
    private const int PrimsPerEngine = 10;
    private const int RequestsPerScript = 5;   // 50 per engine

    private readonly ITestOutputHelper _out;
    public PhloxCrossEngineHttpResponseTests(ITestOutputHelper o) => _out = o;

    /// <summary>A loopback endpoint: GET /x/&lt;tag&gt;-&lt;n&gt; answers status 200 (even n) or 202 (odd n), body "&lt;tag&gt;-&lt;n&gt;".</summary>
    private sealed class Echo : IDisposable
    {
        private readonly TcpListener m_tcp = new(IPAddress.Loopback, 0);
        private int m_hits;
        public int Port => ((IPEndPoint)m_tcp.LocalEndpoint).Port;
        public int Hits => Volatile.Read(ref m_hits);

        public Echo()
        {
            m_tcp.Start();
            new Thread(Run) { IsBackground = true, Name = "phlox55-echo" }.Start();
        }

        private void Run()
        {
            while (true)
            {
                TcpClient c;
                try { c = m_tcp.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        private void Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 5000;
                    var s = c.GetStream();
                    var buf = new byte[8192];
                    int n = s.Read(buf, 0, buf.Length);
                    string line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];   // GET /x/tag-n HTTP/1.1
                    string body = line.Split(' ')[1].Substring("/x/".Length);
                    int status = int.Parse(body[(body.LastIndexOf('-') + 1)..]) % 2 == 0 ? 200 : 202;
                    Interlocked.Increment(ref m_hits);
                    byte[] resp = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Accepted")}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    s.Write(resp, 0, resp.Length);
                }
                catch { }
            }
        }

        public void Dispose() => m_tcp.Stop();
    }

    private static string Script(string url, string tag) =>
        "integer n = 0; " +
        "default { state_entry() { llSetTimerEvent(0.3); } " +
        "timer() { if (n >= " + RequestsPerScript + ") { llSetTimerEvent(0); return; } " +
        "  key k = llHTTPRequest(\"" + url + "/" + tag + "-\" + (string)n, [], \"\"); " +
        "  if (k != NULL_KEY && (string)k != \"\") { llSay(0, \"req " + tag + " \" + (string)n + \" \" + (string)k); n++; } } " +
        "http_response(key id, integer st, list m, string b) { llSay(0, \"resp " + tag + " \" + (string)id + \" \" + (string)st + \" \" + b); } }";

    private static readonly Regex Req = new(@"^req (\S+) (\d+) (\S+)$");
    private static readonly Regex Resp = new(@"^resp (\S+) (\S+) (\d+) (\S*)$");

    private sealed record Outcome(int Requests, int Expected, int Hits, int Responses, int LostY, int LostP, int Duplicates,
                                  int WrongScript, int WrongBody, int WrongStatus, int Strays);

    /// <param name="engines">whose responses the wait is for: "Y", or "YP" for both.</param>
    private Outcome Run(string engines)
    {
        using var echo = new Echo();
        using var r = new PhloxOutboundFilterTests.Rig(except: _ => "127.0.0.1:" + echo.Port, withYEngine: true);
        string url = "http://127.0.0.1:" + echo.Port + "/x";

        var tags = new List<string>();
        for (int p = 0; p < PrimsPerEngine; p++)
        {
            foreach (string engine in new[] { "Y", "P" })
            {
                string tag = engine + p;
                var part = SceneHelpers.AddSceneObject(r.H.Scene, "phlox55 " + tag, UUID.Random()).RootPart;
                if (engine == "Y") SchedulerHarnessYEngine.Rez(r.H, part, Script(url, tag));
                else r.H.RezScriptInto(part, Script(url, tag));
                tags.Add(tag);
            }
        }
        int expected = tags.Count * RequestsPerScript;

        List<(string Tag, int N, string Key)> reqs = new();
        List<(string Tag, string Key, int Status, string Body)> resps = new();
        void Read()
        {
            var said = r.H.Said;
            reqs = said.Select(s => Req.Match(s)).Where(m => m.Success)
                .Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value)).ToList();
            resps = said.Select(s => Resp.Match(s)).Where(m => m.Success)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value), m.Groups[4].Value)).ToList();
        }

        // Wait for the result: every request made and answered by the listener, and a response delivered for every request
        // of the engines named (or the limit).
        r.PumpUntil(() =>
        {
            Read();
            if (reqs.Count < expected || echo.Hits < expected) return false;
            var answered = resps.Select(s => s.Key).ToHashSet();
            return reqs.Where(q => engines.Contains(q.Tag[0])).All(q => answered.Contains(q.Key));
        }, 90);
        r.PumpFor(1.0);   // a duplicate or a stray still on its way lands now
        Read();

        var byKey = reqs.ToDictionary(q => q.Key);
        int lostY = 0, lostP = 0, duplicates = 0, wrongScript = 0, wrongBody = 0, wrongStatus = 0;
        foreach (var q in reqs)
        {
            var got = resps.Where(s => s.Key == q.Key).ToList();
            if (got.Count == 0) { if (q.Tag.StartsWith("Y")) lostY++; else lostP++; continue; }
            if (got.Count > 1) duplicates++;
            foreach (var g in got)
            {
                if (g.Tag != q.Tag) wrongScript++;
                if (g.Body != q.Tag + "-" + q.N) wrongBody++;
                if (g.Status != (q.N % 2 == 0 ? 200 : 202)) wrongStatus++;
            }
        }
        int strays = resps.Count(s => !byKey.ContainsKey(s.Key));

        _out.WriteLine($"requests={reqs.Count}/{expected} (Y {reqs.Count(q => q.Tag.StartsWith("Y"))}, P {reqs.Count(q => q.Tag.StartsWith("P"))}) " +
                       $"listener hits={echo.Hits} responses={resps.Count}");
        _out.WriteLine($"lost: YEngine {lostY}, Phlox {lostP}; duplicates={duplicates} wrongScript={wrongScript} " +
                       $"wrongBody={wrongBody} wrongStatus={wrongStatus} strays={strays}; " +
                       $"Phlox pump: dropped={r.H.Engine.AsyncCommands.HttpRequestPlugin.DroppedResponses} " +
                       $"still outstanding={r.H.Engine.AsyncCommands.HttpRequestPlugin.OutstandingCount}");
        return new Outcome(reqs.Count, expected, echo.Hits, resps.Count, lostY, lostP, duplicates, wrongScript, wrongBody,
                           wrongStatus, strays);
    }

    [Fact]
    public void EveryYEngineResponseReachesItsScriptExactlyOnceWhicheverPumpTakesIt()
    {
        var o = Run("Y");
        Assert.Equal(o.Expected, o.Requests);
        Assert.Equal(o.Expected, o.Hits);
        Assert.Equal(0, o.LostY);
        Assert.Equal(0, o.Duplicates);
        Assert.Equal(0, o.WrongScript);
        Assert.Equal(0, o.WrongBody);
        Assert.Equal(0, o.WrongStatus);
        Assert.Equal(0, o.Strays);
    }

    [Fact(Skip = "PHLOX-55 NEEDS JOHN: YEngine's pump (Shared/Api/Plugins/HttpRequest.cs) posts through the Shared " +
                 "AsyncCommandManager's engine list, YEngine only; a Phlox response it takes is lost (38 of 50 in " +
                 "PHLOX-55's run). Fixing it needs a YEngine or core change.")]
    public void EveryPhloxResponseReachesItsScriptWhicheverPumpTakesIt()
    {
        var o = Run("YP");
        Assert.Equal(o.Expected, o.Requests);
        Assert.Equal(0, o.LostP);
    }
}
