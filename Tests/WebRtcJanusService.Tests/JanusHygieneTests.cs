/*
 * O-60 (audit W-12/W-13/W-14): service-session reconnect, LeaveRoom's answer, the signalling fixes and the
 * bounded room-create locks.
 *
 * No live Janus: FakeJanusGateway answers the HTTP API every JanusSession in a test creates (each through
 * its own FakeJanusHandler, because disposing a session's HttpClient disposes its handler). It numbers
 * sessions, serves each session's long poll from a queue the test controls, can declare a session unknown
 * to the server (458), records trickles, and answers the plugin "list" and "leave" requests.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public sealed class FakeJanusGateway
    {
        private long _nextId = 1000;
        private readonly ConcurrentDictionary<string, Channel<(HttpStatusCode Code, string Body)>> _polls = new();

        public readonly ConcurrentQueue<string> CreatedSessions = new ConcurrentQueue<string>();
        public readonly ConcurrentDictionary<string, bool> DeadSessions = new ConcurrentDictionary<string, bool>();
        public readonly ConcurrentQueue<OSDMap> Trickles = new ConcurrentQueue<OSDMap>();

        // plugindata.data of the event that completes a "leave".
        public string LeaveReplyData = "{\"audiobridge\":\"left\",\"room\":900,\"id\":5}";

        public int CreateCount => CreatedSessions.Count;

        public void QueueGet(string pSessionId, HttpStatusCode pCode, string pBody) => Poll(pSessionId).Writer.TryWrite((pCode, pBody));

        private Channel<(HttpStatusCode Code, string Body)> Poll(string pSessionId) =>
            _polls.GetOrAdd(pSessionId, _ => Channel.CreateUnbounded<(HttpStatusCode, string)>());

        public async Task<HttpResponseMessage> Handle(HttpRequestMessage request, CancellationToken ct)
        {
            string[] segs = request.RequestUri.AbsolutePath.Trim('/').Split('/');   // voice[/session[/handle]]
            string sid = segs.Length > 1 ? segs[1] : null;

            if (request.Method == HttpMethod.Get)
            {
                if (sid is null)
                    return Json(HttpStatusCode.OK, "{\"janus\":\"server_info\"}");
                try
                {
                    (HttpStatusCode code, string body) = await Poll(sid).Reader.ReadAsync(ct);
                    return Json(code, body);
                }
                catch (OperationCanceledException)
                {
                    throw new TaskCanceledException();
                }
            }

            var msg = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
            string janus = msg["janus"].AsString();
            string txn = msg["transaction"].AsString();

            if (sid is not null && DeadSessions.ContainsKey(sid))
                return Json(HttpStatusCode.OK, "{\"janus\":\"error\",\"transaction\":\"" + txn + "\",\"error\":{\"code\":458,\"reason\":\"No such session\"}}");

            switch (janus)
            {
                case "create":
                    string id = Interlocked.Increment(ref _nextId).ToString();
                    CreatedSessions.Enqueue(id);
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + id + "}}");
                case "attach":
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + Interlocked.Increment(ref _nextId) + "}}");
                case "trickle":
                    Trickles.Enqueue(msg);
                    return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
                case "message":
                    string req = (msg["body"] as OSDMap)?["request"].AsString();
                    if (req == "list")
                        return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn +
                            "\",\"plugindata\":{\"plugin\":\"janus.plugin.slvoice\",\"data\":{\"audiobridge\":\"success\",\"list\":[]}}}");
                    if (req == "leave" && sid is not null)
                        QueueGet(sid, HttpStatusCode.OK, "{\"janus\":\"event\",\"transaction\":\"" + txn +
                            "\",\"plugindata\":{\"plugin\":\"janus.plugin.slvoice\",\"data\":" + LeaveReplyData + "}}");
                    return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
                default:   // destroy, detach, keepalive
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new HttpResponseMessage(code) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
    }

    public sealed class FakeJanusHandler : HttpMessageHandler
    {
        private readonly FakeJanusGateway _gateway;
        public FakeJanusHandler(FakeJanusGateway pGateway) { _gateway = pGateway; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _gateway.Handle(request, ct);
    }

    [TestFixture]
    public class JanusHygieneTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000a0060");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000a0060");

        private FakeJanusGateway _gw;
        private readonly List<JanusSession> _sessions = new List<JanusSession>();

        [SetUp]
        public void SetUp()
        {
            _gw = new FakeJanusGateway();
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (JanusSession s in _sessions)
            {
                if (s.IsConnected)
                    await s.DestroySession();
                s.Dispose();
            }
            _sessions.Clear();
        }

        private static IConfigSource JanusConfig()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("PluginName", "janus.plugin.slvoice");
            janus.Set("RequestTimeoutMs", "500");
            return config;
        }

        private static IConfigSource NoJanusConfig()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            return config;
        }

        private WebRtcJanusService NewService() => new WebRtcJanusService(JanusConfig(), () => new FakeJanusHandler(_gw));

        private async Task<JanusSession> NewSession()
        {
            var s = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, new FakeJanusHandler(_gw))
            {
                JanusRequestTimeout = TimeSpan.FromMilliseconds(500)
            };
            _sessions.Add(s);
            Assert.That(await s.CreateSession(), Is.True, "fake create succeeds");
            return s;
        }

        private static async Task<T> Within<T>(Task<T> task, int ms, string what)
        {
            Task done = await Task.WhenAny(task, Task.Delay(ms));
            Assert.That(done, Is.SameAs(task), what + " did not complete within " + ms + " ms");
            return await task;
        }

        private static async Task WaitUntil(Func<bool> condition, int ms, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < ms)
                await Task.Delay(10);
            Assert.That(condition(), Is.True, what);
        }

        // ---- service session reconnect (W-14) ----------------------------------------------------------

        [Test]
        public async Task ServiceSession_WhoseLongPollExited_IsReplacedOnTheNextConsoleCall()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;
            Assert.That(first, Is.Not.Null.And.Not.Empty, "the service session is created at construction");

            _gw.QueueGet(first, HttpStatusCode.NotFound, string.Empty);   // the mixer restarted: the long poll gets 404
            await WaitUntil(() => !svc.ServiceSessionIsConnected, 3000, "the lost long poll marks the service session disconnected");

            AudioBridgeResp resp = await Within(svc.ServiceListRoomsAsync(), 5000, "console list rooms");

            Assert.That(resp, Is.Not.Null);
            Assert.That(resp.isSuccess, Is.True, "the console command succeeds on the fresh session");
            Assert.That(svc.ServiceSessionId, Is.Not.EqualTo(first), "a fresh Janus session replaced the dead one");
            Assert.That(svc.ServiceSessionIsConnected, Is.True);
            Assert.That(_gw.CreateCount, Is.EqualTo(2));
        }

        [Test]
        public async Task ServiceSession_UnknownToTheServer_IsReplacedAfterTheFailedRequest()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;
            _gw.DeadSessions[first] = true;   // restarted mixer, but no disconnect event reached us

            AudioBridgeResp resp = await Within(svc.ServiceListRoomsAsync(), 5000, "console list rooms");

            Assert.That(resp?.isSuccess, Is.True, "retried once on a reconnected session");
            Assert.That(svc.ServiceSessionId, Is.Not.EqualTo(first));
            Assert.That(_gw.CreateCount, Is.EqualTo(2));
        }

        [Test]
        public async Task HealthyServiceSession_IsReused()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;

            Assert.That((await Within(svc.ServiceListRoomsAsync(), 5000, "first list"))?.isSuccess, Is.True);
            Assert.That((await Within(svc.ServiceListRoomsAsync(), 5000, "second list"))?.isSuccess, Is.True);

            Assert.That(svc.ServiceSessionId, Is.EqualTo(first));
            Assert.That(_gw.CreateCount, Is.EqualTo(1), "no reconnect for a live session");
        }

        // ---- LeaveRoom's answer (W-12) ------------------------------------------------------------------

        [TestCase("{\"audiobridge\":\"left\",\"room\":900,\"id\":5}", true)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":487,\"error\":\"Not in a room\"}", true)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room\"}", false)]
        public async Task LeaveRoom_ReportsTheMixersAnswer(string pReplyData, bool pExpected)
        {
            _gw.LeaveReplyData = pReplyData;
            JanusSession s = await NewSession();
            var bridge = new JanusAudioBridge(s, "janus.plugin.slvoice", string.Empty);
            Assert.That(await bridge.Activate(new IniConfigSource()), Is.True, "fake attach succeeds");
            var room = new JanusRoom(bridge, 900);

            bool left = await Within(room.LeaveRoom(new JanusViewerSession(null) { ParticipantId = 5 }), 3000, "leave");

            Assert.That(left, Is.EqualTo(pExpected));
        }

        // ---- signalling (W-12) --------------------------------------------------------------------------

        [Test]
        public void Signalling_OnAViewerSessionWithNoJanusSession_AnswersAnErrorMap_NotAnNre()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = (JanusViewerSession)svc.CreateViewerSession(new OSDMap(), Alice, Region);
            var req = new OSDMap { ["candidate"] = new OSDMap { ["completed"] = OSD.FromBoolean(true) } };

            OSDMap resp = null;
            Assert.DoesNotThrow(() => resp = svc.VoiceSignalingRequest(vs, req, Alice, Region));

            Assert.That(resp, Is.Not.Null);
            Assert.That(resp["response"].AsString(), Is.EqualTo("error"));
        }

        [Test]
        public async Task Signalling_SingularCandidate_IsTrickled_NotDropped()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = new JanusViewerSession(svc) { Session = await NewSession(), AgentId = Alice, RegionId = Region };
            var req = new OSDMap
            {
                ["candidate"] = new OSDMap
                {
                    ["candidate"] = OSD.FromString("candidate:1 1 udp 2122260223 192.0.2.1 50000 typ host"),
                    ["sdpMid"] = OSD.FromString("0"),
                    ["sdpMLineIndex"] = OSD.FromInteger(0),
                }
            };

            OSDMap resp = svc.VoiceSignalingRequest(vs, req, Alice, Region);

            Assert.That(_gw.Trickles.Count, Is.EqualTo(1), "one trickle request for the singular candidate (was an empty else)");
            Assert.That(resp.ContainsKey("response"), Is.False, "the Janus reply, not the error map");
        }

        // ---- bounded room-create locks (W-13) -----------------------------------------------------------

        [Test]
        public async Task RoomCreateLocks_IdleEntriesAreEvicted()
        {
            const int room = 777_000_060;
            await JanusAudioBridge.SelectRoomCoalesced(room, () => Task.FromResult(new JanusRoom(null, room)), () => new JanusRoom(null, room));
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.True);
            int before = JanusAudioBridge.RoomCreateLockCount;

            JanusAudioBridge.SweepRoomCreateLocks(Environment.TickCount64, JanusAudioBridge.RoomCreateLockIdle);
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.True, "a room used just now is kept");

            long later = Environment.TickCount64 + (long)TimeSpan.FromMinutes(11).TotalMilliseconds;
            int evicted = JanusAudioBridge.SweepRoomCreateLocks(later, JanusAudioBridge.RoomCreateLockIdle);

            Assert.That(evicted, Is.GreaterThanOrEqualTo(1));
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.False, "the idle room's hint is gone");
            Assert.That(JanusAudioBridge.RoomCreateLockCount, Is.LessThan(before), "and its gate");
        }
    }
}
