/*
 * Slice 0.2 golden-file proof: with [WebRtcVoice] VisibilityArmingEnabled OFF (the default), the sim's
 * peer_ctl_batch payloads and its room-create body are byte-for-byte what they were before 0.2.
 *
 * The golden file (Golden/visibility-knob-off.golden.txt) was CAPTURED FROM THE CODE BEFORE 0.2
 * CHANGED ANYTHING (tranq-ais feature/ais-v3 at 2fa978c254). It was written by this same test with
 * VIS_GOLDEN_UPDATE=1, and is compared exactly on every run since. One scenario runs through the real
 * VisibilityBatchSender and the real JanusPeerCtlBatchSink, with a recording transport in place of the
 * admin HTTP call:
 *   tick 1  snapshot; listener D has an empty column (today: skipped);
 *   join    OnListenerProvisioned for A (non-empty, re-sent) and D (empty, today: skipped);
 *   tick 2  C joins moderated: a multi-room add, including an unrecorded (fallback-room) listener;
 *   tick 3  the ban is lifted: a remove;
 *   tick 4  C leaves, and the transport fails: nothing applied, unsynced;
 *   tick 5  the re-snapshot, with clear-tracking empty replaces;
 * plus the AudioBridgeCreateRoomReq body a spatial room create sends.
 * Each tick's bodies are sorted ordinally, because a multi-room send issues its rooms concurrently.
 * Every body is otherwise the exact serialized JSON.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityKnobOffGoldenTests
    {
        private const int RoomAB = 1001;
        private const int RoomD = 1002;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static readonly UUID A = Id(1), B = Id(2), C = Id(3), D = Id(4);
        private static readonly UUID ParcelP = Id(201), ParcelQ = Id(202);
        private static readonly UUID GoldenRegion = Id(77);
        private static readonly UUID ConnectorNpc = Id(90);

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public bool BanAOnP = true;
            public bool ModerateC = false;
            // Slice 0.7a T6: an id listed here is admitted only while it holds a voice session in GoldenRegion,
            // FeederWorldFromScene's IsAgentInRegion gate. Empty in the golden scenario itself.
            public readonly HashSet<UUID> Gated = new();
            public IReadOnlyList<AgentView> SnapshotAgents()
                => Agents.Where(a => !Gated.Contains(a.Id) || VoiceViewerSession.IsAgentInRegion(GoldenRegion, a.Id)).ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelQ);
            public ParcelView GetParcelByGlobalId(UUID id) => id == ParcelP
                ? new ParcelView(ParcelP, seeAVs: true, allowVoiceChat: true,
                    isBannedFromLand: x => BanAOnP && x == A, isRestrictedFromLand: _ => false,
                    isVoiceModerated: x => ModerateC && x == C)
                : new ParcelView(ParcelQ, seeAVs: true, allowVoiceChat: true,
                    isBannedFromLand: _ => false, isRestrictedFromLand: _ => false,
                    isVoiceModerated: x => ModerateC && x == C);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        /// Stands in for VoiceStateFeeder without its single-tick-thread assert (awaits resume elsewhere).
        private sealed class MatrixFeed : IVisibilityFeed
        {
            public VisibilityMatrix Current { get; private set; } = VisibilityMatrix.Empty;
#pragma warning disable CS0067
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067
            public VisibilityBatch SnapshotFor(UUID listener) => DeltaComputer.SnapshotFor(Current, listener, -999);

            public VisibilityBatch Tick(IFeederWorld world)
            {
                VisibilityMatrix next = VisibilityMatrix.Build(world);
                VisibilityBatch batch = DeltaComputer.Diff(Current, next, -999);
                Current = next;
                return batch;
            }
        }

        private sealed class Transport
        {
            private readonly object _lock = new object();
            public readonly List<string> Bodies = new();
            public AdminSendResult Result = AdminSendResult.Ok;

            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (_lock)
                    Bodies.Add(OSDParser.SerializeJsonString(request, true));
                string reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"room\":" +
                    request["room"].AsInteger() + ",\"entries\":1}}";
                return Task.FromResult((Result, reply));
            }

            public List<string> Take()
            {
                lock (_lock)
                {
                    var taken = Bodies.OrderBy(s => s, StringComparer.Ordinal).ToList();
                    Bodies.Clear();
                    return taken;
                }
            }
        }

        private static string GoldenPath([CallerFilePath] string here = "")
            => Path.Combine(Path.GetDirectoryName(here), "Golden", "visibility-knob-off.golden.txt");

        /// The scenario, run against whatever the default (knob-off) construction is in this build.
        /// <paramref name="withConnector"/> (slice 0.7a T6) threads a voice connector through it via the real
        /// VoiceConnectorRegistrar: registered before tick 1, unregistered before tick 2, registered again after tick 5
        /// for two more ticks. Anything it put on the wire lands in the transcript, so the golden comparison catches it.
        internal static async Task<string> RunScenarioAsync(bool withConnector = false)
        {
            var world = new World();
            world.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelQ, false));   // A on Q, banned from P
            world.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelP, false));   // B on P
            world.Agents.Add(new AgentView(D, false, Vector3.Zero, ParcelQ, false));   // D on Q: an empty column

            var rooms = new Dictionary<UUID, int> { [A] = RoomAB, [B] = RoomAB, [D] = RoomD };   // C: no record
            var transport = new Transport();
            var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), GoldenRegion, "golden",
                sendOne: transport.SendAsync);
            sink.RoomOf = a => rooms.TryGetValue(a, out int r) ? r : (int?)null;
            var feed = new MatrixFeed();
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "golden");

            var sb = new StringBuilder();
            void Record(string step)
            {
                sb.Append("== ").Append(step).Append('\n');
                foreach (string body in transport.Take())
                    sb.Append(body).Append('\n');
            }

            var connector = new VoiceConnectorRecord("Speaker", true, "Speaker", "Connector", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, true, "Operator", null);
            void RegisterConnector()
            {
                // VoiceConnectorModule.StartRecord's shape: the NPC presence exists, the session gates it into the
                // population, pRecordRoom is the service's OnListenerProvisioned (room record + sender trigger).
                bool ok = VoiceConnectorRegistrar.Register(connector, sink.FallbackRoom,
                    r => ConnectorNpc,
                    id => new VoiceViewerSession(null, GoldenRegion, id),
                    (id, room) => { rooms[id] = room; sender.OnListenerProvisioned(id); },
                    id => { },
                    null);
                if (!ok)
                    throw new InvalidOperationException("connector registration failed");
            }
            void UnregisterConnector() => VoiceConnectorRegistrar.Unregister(connector, id => { }, null);
            if (withConnector)
            {
                world.Gated.Add(ConnectorNpc);
                world.Agents.Add(new AgentView(ConnectorNpc, false, Vector3.Zero, ParcelQ, false));
                RegisterConnector();
            }

            await sender.PumpAsync(feed.Tick(world));
            Record("tick 1: snapshot, D's column empty");

            sender.OnListenerProvisioned(A);
            sender.OnListenerProvisioned(D);
            await sender.PumpAsync(feed.Tick(world));
            Record("join: A and D provisioned (quiet tick)");

            if (withConnector)
                UnregisterConnector();

            world.ModerateC = true;
            world.Agents.Add(new AgentView(C, false, Vector3.Zero, ParcelP, false));
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 2: C joins moderated (multi-room add, C unrecorded)");

            world.BanAOnP = false;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 3: ban lifted (remove)");

            world.Agents.RemoveAll(a => a.Id == C);
            transport.Result = AdminSendResult.TransportError;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 4: C leaves, transport fails");

            transport.Result = AdminSendResult.Ok;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 5: re-snapshot with clear-tracking");

            var create = new AudioBridgeCreateRoomReq(RoomAB, true, "golden");
            create.ToJson();
            sb.Append("== room create body (spatial)\n")
              .Append(OSDParser.SerializeJsonString(create.RawBody["body"], true)).Append('\n');

            if (withConnector)
            {
                try
                {
                    RegisterConnector();
                    await sender.PumpAsync(feed.Tick(world));
                    await sender.PumpAsync(feed.Tick(world));
                    if (transport.Bodies.Count > 0)
                        Record("connector registered again after tick 5 (knob-off must send nothing)");
                }
                finally
                {
                    UnregisterConnector();
                }
            }
            return sb.ToString();
        }

        /// Slice 0.7a T6: with the knob off, registering (and unregistering) a MayInject=true connector changes no byte.
        /// A MayInject=false connector is outside this comparison by design: its moderation mute is enforced knob-off
        /// too (brief Amendment 2 D2, since S-CON-2), so it legitimately adds mute entries the pre-0.2 golden never had.
        [Test]
        public async Task KnobOff_WithAConnectorRegistered_PayloadsAreByteIdenticalToTheGoldenFile()
        {
            string actual = await RunScenarioAsync(withConnector: true);
            string golden = File.ReadAllText(GoldenPath()).Replace("\r\n", "\n");
            Assert.That(actual, Is.EqualTo(golden));
        }

        [Test]
        public async Task KnobOff_PayloadsAndRoomCreate_AreByteIdenticalToTheGoldenFile()
        {
            string actual = await RunScenarioAsync();
            string path = GoldenPath();
            if (Environment.GetEnvironmentVariable("VIS_GOLDEN_UPDATE") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                Assert.Pass("golden written: " + path);
            }
            Assert.That(File.Exists(path), Is.True, "golden file missing: " + path);
            string golden = File.ReadAllText(path).Replace("\r\n", "\n");   // a CRLF checkout changes line ends only
            Assert.That(actual, Is.EqualTo(golden));
        }
    }
}
