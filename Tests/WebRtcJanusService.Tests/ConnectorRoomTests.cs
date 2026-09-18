/*
 * Slice 0.8c (ledger O-93, and the connector half of O-92): a connector's room is resolved the way an avatar's is,
 * the sim makes sure it exists, and an unknown_room retry backs off.
 *
 * T4 lives here first, because it is the one that measures the defect: with the 0.8 build a listener whose re-arm
 * request never clears is re-armed EVERY tick, so a room that stays unknown is hammered at tick rate. The other
 * cases live in ConnectorRoomResolverTests / the endpoint tests; this file is the authority-level behaviour.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ConnectorRoomTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            return new UUID(b, 0);
        }

        private static readonly UUID L = Id(1);
        private const int Room = 226001844;
        private const string Epoch = "0000018f00000001";

        /// A one-agent world, so the matrix population is exactly the connector NPC.
        private sealed class OneAgentWorld : IFeederWorld
        {
            private readonly UUID _id;
            public OneAgentWorld(UUID id) { _id = id; }
            public IReadOnlyList<AgentView> SnapshotAgents()
                => new List<AgentView> { new AgentView(_id, false, Vector3.Zero, Id(200), false) };
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(Id(200));
            public ParcelView GetParcelByGlobalId(UUID id)
                => new ParcelView(Id(200), true, true, _ => false, _ => false, _ => false);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        private sealed class Feed : IVisibilityFeed
        {
            private readonly IFeederWorld _world;
            public Feed(IFeederWorld world) { _world = world; Current = VisibilityMatrix.Build(world); }
            public VisibilityMatrix Current { get; private set; }
#pragma warning disable CS0067
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067
            public VisibilityBatch SnapshotFor(UUID listener) => DeltaComputer.SnapshotFor(Current, listener, -999);
            public VisibilityBatch Tick() { Current = VisibilityMatrix.Build(_world); return VisibilityBatch.EmptyDelta(-999); }
        }

        /// T4: a room the mixer never knows must not be retried at tick rate. The REAL sender and authority, 120
        /// simulated seconds at a tick every 250 ms, every batch answered unknown_room: at most 10 attempts.
        [Test]
        public async Task T4_ARoomThatStaysUnknown_DrawsAtMostTenAttemptsInTwoMinutes()
        {
            long clock = 10_000;
            int attempts = 0;
            Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                if (request["request"].AsString() == "peer_ctl_batch")
                    attempts++;
                string reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\"," +
                    "\"status\":\"unknown_room\",\"vis_protocol\":2,\"mixer_instance\":\"m1\"}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }
            var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "backoff",
                sendOne: Send);
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            sink.Authority = auth;
            sink.RoomOf = _ => Room;
            var feed = new Feed(new OneAgentWorld(L));
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "backoff", () => clock,
                auth, _ => Room);
            auth.RequestArm(L);   // the connector's registration, or any provision: one standing re-arm request

            for (int tick = 0; tick < 480; tick++)   // 480 ticks x 250 ms = 120 s
            {
                clock += 250;
                await sender.PumpAsync(feed.Tick());
            }

            TestContext.Out.WriteLine($"attempts in 120 s: {attempts}");
            Assert.That(attempts, Is.LessThanOrEqualTo(10),
                "a room that stays unknown for 120 s draws at most 10 attempts (1 s doubling to a 30 s cap)");
            Assert.That(attempts, Is.GreaterThanOrEqualTo(5), "and it does keep trying");
        }

        /// The backoff resets once the room answers: the next failure starts at 1 s again, not at the cap.
        [Test]
        public void T4b_TheBackoffResetsOnSuccess()
        {
            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            var named = new List<UUID> { L };
            for (int i = 0; i < 5; i++)   // climb the backoff
            {
                auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                    new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
                clock += 60_000;
            }
            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" });
            Assert.That(auth.IsArmed(Room, L), Is.True, "the room answered: armed");

            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.That(auth.CanArmNow(L), Is.False, "backing off again");
            clock += 1_100;
            Assert.That(auth.CanArmNow(L), Is.True, "from 1 s again, not from the cap it had climbed to");
        }

        /// F1, pinned: a standing re-arm request must not bypass the backoff. This is what made the live mixer log
        /// 8,933 unknown_room lines at ~3.8/s for one connector: the NPC never leaves the population, so its request
        /// never expired, and the arming pass short-circuited on it every tick.
        [Test]
        public void T4c_AStandingRearmRequest_DoesNotBypassTheBackoff()
        {
            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            auth.RequestArm(L);
            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), new List<UUID> { L }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });

            Assert.That(auth.IsRearmRequested(L), Is.True, "the request is still outstanding");
            Assert.That(auth.CanArmNow(L), Is.False, "the authority's own backoff was always right");
            // The sender's arming condition is what bypassed it. This is that condition, as VisibilityBatchSender
            // evaluates it: with the fix nothing arms while the backoff holds, standing request or not.
            bool snapshot = false;
            bool wouldArm = (snapshot || auth.IsRearmRequested(L) || !auth.IsArmed(Room, L)) && auth.CanArmNow(L);
            Assert.That(wouldArm, Is.False,
                "and the sender does not arm through it: a re-arm request is not a licence to retry every tick");
        }
    }
}
