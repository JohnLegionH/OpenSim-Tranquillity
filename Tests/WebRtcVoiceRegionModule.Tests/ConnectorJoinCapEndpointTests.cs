/*
 * Slice 0.7b (O-88, design §11.10): the connector join-capability endpoint.
 *
 * U1-U5 and U7 route every request through a real BaseHttpServer's own handler lookup (TryGetSimpleStreamHandler,
 * reached by reflection: a STARTED server needs the region's WorkManager statics, which this test host lacks), with the
 * endpoint attached exactly as VoiceConnectorModule attaches it. A path with no handler answers as the server would,
 * with its HTML 404, so "indistinguishable" covers the case the var-path registration exists for: an unknown name
 * must reach this handler's empty 404, never the server's HTML one.
 *
 * U4 verifies the capability with a C# transcription of the mixer's validator (legion-voice-mixer src/joincap.c:
 * slv_joincap_parse, then slv_joincap_check), not with the minter: signature over "v1." + payload, then agent,
 * session, room, and the lifetime.
 *
 * U6 captures every log line the endpoint and the module's config load produce (both the injected logger and the
 * process LoggerProvider factory) across the U1-U5 flows and asserts no bearer, secret or capability appears.
 */

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class ConnectorJoinCapEndpointTests
    {
        private const string Secret = "connector-bearer-secret-0123456789abcdef";          // 40 chars
        private const string MintKey = "mixer-join-cap-hmac-key-fedcba9876543210";       // [JanusWebRtcVoice] JoinCapabilitySecret
        private const int Room = 226001844;
        private static readonly UUID Npc = new UUID("0c0c0c0c-0000-4000-8000-00000000c0c0");

        // ---- log capture ----
        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<string> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
            }
            public void Dispose() { }
        }

        private Capture m_capture;
        private ILoggerFactory m_previousFactory;
        private BaseHttpServer m_server;
        private VoiceConnectorJoinCapEndpoint m_endpoint;
        private readonly List<string> m_minted = new();

        [SetUp]
        public void SetUp()
        {
            m_capture = new Capture();
            m_previousFactory = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(m_capture).SetMinimumLevel(LogLevel.Trace));
            JoinCapabilityAuthority.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            m_server = null;
            m_endpoint = null;
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = m_previousFactory;
            ours.Dispose();
            m_capture.Dispose();
            JoinCapabilityAuthority.Clear();
        }

        private BaseHttpServer StartServer()
        {
            m_server = new BaseHttpServer(9);   // never started: only its handler table is used
            return m_server;
        }

        private static readonly MethodInfo s_lookup = typeof(BaseHttpServer).GetMethod("TryGetSimpleStreamHandler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly byte[] ServerHtml404 = Encoding.UTF8.GetBytes("<the server's own HTML 404>");

        private static VoiceConnectorRecord Record(string name = "Recorder", string secret = Secret, bool active = true)
        {
            var r = new VoiceConnectorRecord(name, true, name, "NPC", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, false, "Operator", null, null, secret);
            if (active)
            {
                r.NpcId = Npc;
                r.ViewerSessionId = "5e551011-0000-4000-8000-000000005e55";
                r.Room = Room;
            }
            return r;
        }

        /// One request as the server would route it: its own lookup, then the endpoint's answer, or the HTML 404.
        private (int Status, byte[] Body, string ContentType) Post(string name, string bearer, string method = "POST")
        {
            string path = $"/voice/connector/{name}/join-cap";
            Assert.That(s_lookup, Is.Not.Null, "BaseHttpServer.TryGetSimpleStreamHandler exists");
            object[] args = { path, null };
            if (!(bool)s_lookup.Invoke(m_server, args))
                return (404, ServerHtml404, "text/html");
            Assert.That(((ISimpleStreamHandler)args[1]).Path, Is.EqualTo(VoiceConnectorJoinCapEndpoint.PathRoot));
            VoiceConnectorJoinCapEndpoint.Response r = m_endpoint.Handle(method, path, bearer is null ? null : "Bearer " + bearer);
            return (r.Status, r.Body, r.ContentType);
        }

        private VoiceConnectorJoinCapEndpoint Endpoint(IHttpServer server)
            => m_endpoint = new VoiceConnectorJoinCapEndpoint(server, LoggerProvider.CreateLogger("test.joincap"));

        // ---- the mixer's validator, transcribed (src/joincap.c slv_joincap_parse + slv_joincap_check) ----
        private static string[] VerifyLikeTheMixer(string cap, string key)
        {
            string[] parts = cap.Split('.');
            Assert.That(parts.Length, Is.EqualTo(3), "v1.<payload>.<sig>");
            Assert.That(parts[0], Is.EqualTo("v1"));
            string signing = parts[0] + "." + parts[1];
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            byte[] want = mac.ComputeHash(Encoding.UTF8.GetBytes(signing));
            Assert.That(CryptographicOperations.FixedTimeEquals(want, B64UrlDecode(parts[2])), Is.True, "signature verifies under the key");
            string[] f = Encoding.UTF8.GetString(B64UrlDecode(parts[1])).Split('|');
            Assert.That(f.Length, Is.EqualTo(8), "agent|session|room|epoch|generation|iat|exp|nonce");
            return f;
        }

        private static byte[] B64UrlDecode(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
        }

        // ---- flows (each U test runs one; U6 runs them all) ----

        private void FlowU1()
        {
            // The config default: no CapabilitySecret. Loaded through the real registry, attached as the module attaches.
            var cfg = new IniConfigSource();
            IConfig s = cfg.AddConfig("VoiceConnector.Recorder");
            s.Set("Enabled", "true"); s.Set("NpcFirstName", "Recorder"); s.Set("NpcLastName", "NPC"); s.Set("Position", "<128,128,25>");
            VoiceConnectorLoadResult load = VoiceConnectorRegistry.LoadFrom(cfg, "NPC");
            Assert.That(load.Registry.Snapshot().Single().CapabilitySecret, Is.Null, "unset is the default");
            Assert.That(load.Warnings, Is.Empty);

            BaseHttpServer server = StartServer();
            int before = server.GetSimpleStreamHandlerKeys().Count;
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => load.Registry.Snapshot(), true, MintKey));
            Assert.That(ep.IsRegistered, Is.False, "U1: no handler registered");
            Assert.That(server.GetSimpleStreamHandlerKeys(), Has.Count.EqualTo(before).And.No.Contains("/voice"));
            // What a peer then sees is the server's own 404, exactly as before this slice.
            Assert.That(Post("Recorder", Secret).Body, Is.EqualTo(ServerHtml404));
        }

        private void FlowU2()
        {
            var cfg = new IniConfigSource();
            IConfig s = cfg.AddConfig("VoiceConnector.Recorder");
            s.Set("Enabled", "true"); s.Set("NpcFirstName", "Recorder"); s.Set("NpcLastName", "NPC"); s.Set("Position", "<128,128,25>");
            s.Set("CapabilitySecret", "short-secret-31-chars-abcdefghi");
            Assert.That("short-secret-31-chars-abcdefghi".Length, Is.EqualTo(31));
            // The module's own Initialise: it logs the load warnings.
            var module = new VoiceConnectorModule();
            cfg.AddConfig("WebRtcVoice");
            module.Initialise(cfg);
            lock (m_capture.Lines)
                Assert.That(m_capture.Lines.Any(l => l.StartsWith("Warning:") && l.Contains("CapabilitySecret is shorter than 32")),
                    Is.True, "U2: WARN");

            VoiceConnectorLoadResult load = VoiceConnectorRegistry.LoadFrom(cfg, "NPC");
            VoiceConnectorRecord r = load.Registry.Snapshot().Single();
            Assert.That(r.CapabilitySecret, Is.Null, "a weak key is dropped, never enforced with");
            r.NpcId = Npc; r.ViewerSessionId = "5e551011-0000-4000-8000-000000005e55"; r.Room = Room;   // even active

            BaseHttpServer server = StartServer();
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => load.Registry.Snapshot(), true, MintKey));
            Assert.That(ep.IsRegistered, Is.False, "U2: no handler");
            Assert.That(server.GetSimpleStreamHandlerKeys(), Has.No.Member("/voice"));
        }

        private void FlowU3()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord active = Record("Recorder");
            VoiceConnectorRecord inactive = Record("Injector", active: false);
            VoiceConnectorRecord noSecret = Record("Plain", secret: null);
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { active, inactive, noSecret }, true, MintKey));
            Assert.That(ep.IsRegistered, Is.True);

            var answers = new Dictionary<string, (int Status, byte[] Body, string ContentType)>
            {
                ["wrong bearer"] = Post("Recorder", "not-the-secret-not-the-secret-not-the-secret"),
                ["no bearer"] = Post("Recorder", null),
                ["unknown name"] = Post("Nobody", Secret),
                ["inactive record"] = Post("Injector", Secret),
                ["record without a secret"] = Post("Plain", Secret),
                ["GET with the right bearer"] = Post("Recorder", Secret, "GET"),
            };
            foreach (var (what, a) in answers)
            {
                Assert.That(a.Status, Is.EqualTo(404), what);
                Assert.That(a.Body, Is.Empty, what + ": empty body");
            }
            var distinct = answers.Values.Select(a => (a.Status, Convert.ToBase64String(a.Body), a.ContentType)).Distinct().ToList();
            Assert.That(distinct, Has.Count.EqualTo(1), "U3: every pre-authentication failure is the same answer");
        }

        private void FlowU4()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            JoinCapabilityAuthority.Publish(Room, "0000018f00000001", 7);   // an armed room, as VisAuthority publishes it
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey));

            var a = Post("Recorder", Secret);
            Assert.That(a.Status, Is.EqualTo(200), "U4: right bearer");
            var body = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(a.Body));
            string cap = body["join_cap"].AsString();
            m_minted.Add(cap);
            Assert.That(body["display"].AsString(), Is.EqualTo(Npc.ToString()));
            Assert.That(body["room"].AsInteger(), Is.EqualTo(Room));
            Assert.That(body["session_id"].AsString(), Is.EqualTo(r.ViewerSessionId));

            string[] f = VerifyLikeTheMixer(cap, MintKey);
            Assert.That(f[0], Is.EqualTo(Npc.ToString()), "agent = the NPC id");
            Assert.That(f[1], Is.EqualTo(r.ViewerSessionId), "session = the record's ViewerSessionId");
            Assert.That(int.Parse(f[2]), Is.EqualTo(Room), "room = the recorded room");
            Assert.That(f[3], Is.EqualTo("0000018f00000001"), "epoch from JoinCapabilityAuthority, as an avatar's join");
            Assert.That(f[4], Is.EqualTo("7"), "generation from JoinCapabilityAuthority, as an avatar's join");
            Assert.That(long.Parse(f[6]) - long.Parse(f[5]), Is.EqualTo(JoinCapability.LifetimeSeconds));
            Assert.That(body["expires"].AsLong(), Is.EqualTo(long.Parse(f[6])));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.That(now, Is.InRange(long.Parse(f[5]) - 120, long.Parse(f[6]) + 120), "fresh under the mixer's skew rule");
        }

        private void FlowU5()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            var ep = Endpoint(server);
            var off = new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, false, MintKey);
            ep.Attach(off);
            var a = Post("Recorder", Secret);
            Assert.That(a.Status, Is.EqualTo(503), "U5: JoinCapabilityEnabled false");
            Assert.That(a.Body, Is.Empty);
            // No JoinCapabilitySecret: the module passes enabled=false then (WebRtcJanusService's own rule), and the
            // minter refuses an empty key besides.
            ep.Detach(off);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, string.Empty));
            Assert.That(Post("Recorder", Secret).Status, Is.EqualTo(503), "U5: no JoinCapabilitySecret");
            Assert.That(Post("Recorder", "wrong-wrong-wrong-wrong-wrong-wrong-wrong").Status, Is.EqualTo(404),
                "503 only after authentication");
        }

        private void FlowU7()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            r.NpcId = UUID.Zero; r.ViewerSessionId = null; r.Room = null;
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey));
            string session = null;
            VoiceConnectorRegistrar.Register(r, Room, _ => Npc,
                id => { var vs = new VoiceViewerSession(null, UUID.Random(), id); session = vs.ViewerSessionID; return vs; },
                (_, _) => { }, _ => { }, null);
            try
            {
                Assert.That(Post("Recorder", Secret).Status, Is.EqualTo(200), "registered: served");
            }
            finally
            {
                VoiceConnectorRegistrar.Unregister(r, _ => { }, null);
            }
            var after = Post("Recorder", Secret);
            Assert.That(after.Status, Is.EqualTo(404), "U7: after Unregister");
            Assert.That(after.Body, Is.Empty);
            Assert.That(after, Is.EqualTo(Post("Nobody", Secret)).Using<(int, byte[], string)>((x, y) =>
                x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2) && x.Item3 == y.Item3), "and indistinguishable from an unknown name");
            Assert.That(session, Is.Not.Null);
        }

        private void Reset()
        {
            m_server = null;
            m_endpoint = null;
            JoinCapabilityAuthority.Clear();
        }

        /// Slice 0.7c L3: an unpinned record with a secret, in an instance with two regions, is named at startup with
        /// "add Region="; the request-time 404 stays; a pinned record and a single-region instance warn nothing.
        [Test]
        public void L3_UnpinnedRecordWithSecret_InAMultiRegionInstance_WarnsAtStartup_And404Stays()
        {
            StartServer();
            var ep = Endpoint(m_server);
            VoiceConnectorRecord unpinnedA = Record("Recorder");
            VoiceConnectorRecord pinned = new VoiceConnectorRecord("Pinned", true, "Pinned", "NPC", new Vector3(1, 1, 1),
                VoiceConnectorScope.Estate, false, "Operator", null, "Ebony", Secret);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { unpinnedA, pinned }, true, MintKey));
            List<string> Warnings() { lock (m_capture.Lines) return m_capture.Lines.Where(l => l.StartsWith("Warning:")).ToList(); }
            Assert.That(Warnings(), Is.Empty, "one region: nothing to warn about");

            // The second region's instance loads the same records; the unpinned one starts there too.
            VoiceConnectorRecord unpinnedB = Record("Recorder");
            unpinnedB.NpcId = new UUID("0c0c0c0c-0000-4000-8000-00000000c0c1");
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { unpinnedB, pinned }, true, MintKey));
            List<string> warned = Warnings();
            Assert.That(warned, Has.Count.EqualTo(1), "one WARN, for the unpinned record only");
            Assert.That(warned[0], Does.Contain("Recorder").And.Contain("Add Region="), "names the record and says add Region=");
            Assert.That(warned[0], Does.Not.Contain(Secret));

            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { Record("Recorder") }, true, MintKey));
            Assert.That(Warnings(), Has.Count.EqualTo(1), "once per record, not once per region");
            Assert.That(Post("Recorder", Secret), Is.EqualTo((404, Array.Empty<byte>(), (string)null))
                .Using<(int, byte[], string)>((x, y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2) && x.Item3 == y.Item3),
                "the request-time 404 stays");
        }

        [Test] public void U1_SecretUnset_NoHandlerRegistered() => FlowU1();
        [Test] public void U2_SecretUnder32Chars_Warns_NoHandler() => FlowU2();
        [Test] public void U3_WrongBearer_UnknownName_InactiveRecord_AllTheSame404() => FlowU3();
        [Test] public void U4_RightBearer_200_CapabilityValidatesLikeTheMixer() => FlowU4();
        [Test] public void U5_RightBearer_MintingDisabled_503() => FlowU5();
        [Test] public void U7_AfterUnregister_404() => FlowU7();

        [Test]
        public void U6_LogsAcrossU1ToU5_CarryNoBearerSecretOrCapability()
        {
            FlowU1(); Reset();
            FlowU2(); Reset();
            FlowU3(); Reset();
            FlowU4(); Reset();
            FlowU5(); Reset();
            List<string> lines;
            lock (m_capture.Lines)
                lines = m_capture.Lines.ToList();
            Assert.That(lines.Any(l => l.Contains("join capability minted for room")), Is.True, "the capture saw the mint line");
            Assert.That(m_minted, Is.Not.Empty);
            var forbidden = new List<string> { Secret, MintKey, "short-secret-31-chars-abcdefghi",
                "not-the-secret-not-the-secret-not-the-secret", "wrong-wrong-wrong-wrong-wrong-wrong-wrong" };
            foreach (string cap in m_minted)
            {
                forbidden.Add(cap);
                forbidden.AddRange(cap.Split('.').Skip(1));   // the payload and the signature on their own
            }
            foreach (string f in forbidden)
                Assert.That(lines.Where(l => l.Contains(f)), Is.Empty, "U6: a log line carries a secret, bearer or capability");
        }
    }
}
