/*
 * Slice 0.8f-sim (ledger O-98): the process-wide "room exists" hint must never beat the mixer.
 *
 * THE DEFECT, LIVE (0.8d). JanusAudioBridge._knownRooms is set when this process creates a room and cleared only by a
 * sim-side destroy, a join that fails THROUGH the sim, or a 10-minute idle sweep. The mixer's 60 s empty-room grace
 * destroys rooms without the sim knowing, so the hint goes stale:
 *   (a) a connector's EnsureSpatialRoom found the hint, created nothing, reported success, and that false success fired
 *       RoomExists and reset the unknown_room backoff - both injectors then got 485 No such room;
 *   (b) a viewer whose join hit the stale hint got 485, the sim forgot the hint, and the VIEWER's own retry re-created
 *       the room: 5.2-5.6 s for three of John's nine provisions, against 13-67 ms for the clean six.
 *
 * THE RULING. R1 the ensure never trusts the hint - it always asks the mixer, already-exists is success, and RoomExists
 * follows only a truthful answer. R2 a viewer join answered 485 forgets the hint, re-creates and joins ONCE more inline
 * before failing back. R3 an unknown_room reply forgets the hint. (R4, the backoff cap, is ConnectorRoomTests H7.)
 *
 * The mixer here is a fake with one piece of state - which rooms exist - driven through the real coalescing statics.
 * Each test uses its own room numbers, because the hint is process-wide.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // the hint and the create gates are process-wide statics
    public class RoomHintTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            return new UUID(b, 0);
        }

        /// The mixer, as far as rooms go: create is idempotent (already-exists answers 486, which the sim treats as
        /// success), a join into a room it does not have answers 485, and the grace destroy happens behind the sim's back.
        private sealed class FakeMixer
        {
            private readonly object _lock = new object();
            public readonly HashSet<int> Rooms = new HashSet<int>();
            public int Creates;
            public bool FailCreates;
            public bool JoinAlways485;

            public Task<JanusRoom> Create(int room)
            {
                lock (_lock)
                {
                    Creates++;
                    if (FailCreates)
                        return Task.FromResult<JanusRoom>(null);   // inconclusive / error: CreateRoom answers null
                    Rooms.Add(room);                                // "created", or 486 already exists: both a room
                    return Task.FromResult(new JanusRoom(null, room));
                }
            }

            public (bool Joined, int ErrorCode) JoinNow(JanusRoom r)
            {
                lock (_lock)
                    return !JoinAlways485 && Rooms.Contains(r.RoomId)
                        ? (true, 0)
                        : (false, WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE);
            }

            public void GraceDestroy(int room) { lock (_lock) Rooms.Remove(room); }
        }

        private static Func<Task<JanusRoom>> CreateOf(FakeMixer m, int room) => () => m.Create(room);
        private static Func<JanusRoom> Existing(int room) => () => new JanusRoom(null, room);

        /// A room this process created, whose hint is therefore set, and which the mixer has since destroyed.
        private static async Task StaleHint(FakeMixer m, int room)
        {
            JanusAudioBridge.ForgetRoom(room);
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room), Existing(room));
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.True, "precondition: the hint says the room exists");
            m.GraceDestroy(room);
        }

        /// The viewer provision's room step, as WebRtcJanusService does it: select, join, and on 485 the R2 recreate.
        private static Task<(JanusRoom Room, bool Joined, int ErrorCode, int Joins)> Provision(FakeMixer m, int room,
            Func<JanusRoom, Task<(bool, int)>> join)
            => ProvisionAsync(m, room, join);

        private static async Task<(JanusRoom Room, bool Joined, int ErrorCode, int Joins)> ProvisionAsync(FakeMixer m,
            int room, Func<JanusRoom, Task<(bool, int)>> join)
        {
            JanusRoom first = await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room), Existing(room));
            return await WebRtcJanusService.JoinWithOneRecreate(first, join,
                stale => JanusAudioBridge.RecreateAfterMissingCoalesced(room, stale.HintStamp, CreateOf(m, room),
                    Existing(room)));
        }

        // ---- R1: the ensure asks the mixer ---------------------------------------------------------------------

        /// <summary>H1: the hint says the room exists, the mixer has destroyed it. The ensure must create it - this is
        /// 0.8d's PROOF 1 failure, one layer down.</summary>
        [Test]
        public async Task H1_HintSaysExists_MixerHasNoRoom_TheEnsureCreatesIt()
        {
            const int room = 880101;
            var m = new FakeMixer();
            await StaleHint(m, room);
            int before = m.Creates;

            JanusRoom ensured = await JanusAudioBridge.EnsureRoomCoalesced(room, CreateOf(m, room), Existing(room));

            Assert.That(m.Creates, Is.EqualTo(before + 1), "the ensure asked the mixer to create");
            Assert.That(m.Rooms, Does.Contain(room), "and the mixer has the room now");
            Assert.That(ensured?.RoomId, Is.EqualTo(room));
        }

        /// <summary>H2: the room is really there. Asking again is not an error - already-exists is success - and
        /// RoomExists fires exactly once.</summary>
        [Test]
        public async Task H2_EnsureWithTheRoomPresent_NoError_RoomExistsFiresOnce()
        {
            const int room = 880201;
            var m = new FakeMixer();
            JanusAudioBridge.ForgetRoom(room);
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room), Existing(room));   // really exists
            int before = m.Creates, fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.EnsureRoomCoalesced(room, CreateOf(m, room), Existing(room)).Result?.RoomId,
                r => fired++);

            Assert.That(ensured, Is.EqualTo(room), "no error: already-exists is success");
            Assert.That(m.Creates, Is.EqualTo(before + 1), "the mixer was asked even though the hint was fresh (R1)");
            Assert.That(fired, Is.EqualTo(1), "RoomExists fired exactly once");
        }

        /// <summary>H3: the create fails. Nothing is proven, so RoomExists does NOT fire and the backoff is NOT reset.
        /// Today a stale hint answers for the mixer, the ensure "succeeds", and the reset fires on a room that does not
        /// exist - the live O-98 reset.</summary>
        [Test]
        public async Task H3_AFailedCreate_FiresNoRoomExists_AndLeavesTheBackoff()
        {
            const int room = 880301;
            var m = new FakeMixer();
            await StaleHint(m, room);
            m.FailCreates = true;

            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "h3", () => clock);
            UUID l = Id(1);
            auth.OnBatchOutcome(room, VisOp.Replace, auth.NextGeneration(room), new List<UUID> { l }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.That(auth.CanArmNow(l), Is.False, "precondition: backing off");
            int fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.EnsureRoomCoalesced(room, CreateOf(m, room), Existing(room)).Result?.RoomId,
                r => { fired++; auth.RoomExists(r); });

            Assert.That(ensured, Is.Null, "a failed create is not a room");
            Assert.That(fired, Is.Zero, "RoomExists did not fire");
            Assert.That(auth.CanArmNow(l), Is.False, "the backoff was not reset");
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.False, "and the stale hint is gone");
        }

        // ---- R2: the viewer's join retries once, inline -------------------------------------------------------

        /// <summary>H4: the viewer's join is answered 485 once. The provision succeeds without the viewer retrying:
        /// exactly one create and two joins. TODAY the provision fails after one join and no create, and the viewer's
        /// own retry does the rest - a second CAP round trip, 5.2-5.6 s live.</summary>
        [Test]
        public async Task H4_Join485Once_TheProvisionSucceeds_OneCreate_TwoJoins()
        {
            const int room = 880401;
            var m = new FakeMixer();
            await StaleHint(m, room);
            int before = m.Creates;

            var r = await Provision(m, room, jr => Task.FromResult(m.JoinNow(jr)));

            TestContext.Out.WriteLine($"joined={r.Joined} error={r.ErrorCode} joins={r.Joins} creates={m.Creates - before}");
            Assert.That(r.Joined, Is.True, "joined inside ONE provision");
            Assert.That(r.Joins, Is.EqualTo(2), "two joins");
            Assert.That(m.Creates - before, Is.EqualTo(1), "one create");
        }

        /// <summary>H5: 485 on both joins. The provision fails as it does today, and the hint is forgotten.</summary>
        [Test]
        public async Task H5_Join485Twice_FailsAsToday_HintForgotten()
        {
            const int room = 880501;
            var m = new FakeMixer();
            await StaleHint(m, room);
            m.JoinAlways485 = true;

            var r = await Provision(m, room, jr => Task.FromResult(m.JoinNow(jr)));

            Assert.That(r.Joined, Is.False);
            Assert.That(r.ErrorCode, Is.EqualTo(WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE));
            Assert.That(r.Joins, Is.EqualTo(2), "one retry, never a loop");
            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.False, "the hint is forgotten");
        }

        // ---- R3: the mixer saying unknown_room is believed ------------------------------------------------------

        /// <summary>H6: an unknown_room reply - to a batch or a heartbeat - forgets the hint for that room, so the next
        /// select or ensure creates.</summary>
        [TestCase(false, TestName = "H6_UnknownRoomReplyToABatch_ForgetsTheHint")]
        [TestCase(true, TestName = "H6_UnknownRoomReplyToAHeartbeat_ForgetsTheHint")]
        public async Task H6_UnknownRoomReply_ForgetsTheHint_NextSelectCreates(bool heartbeat)
        {
            int room = heartbeat ? 880602 : 880601;
            var m = new FakeMixer();
            await StaleHint(m, room);
            var auth = new VisAuthority(0x0000018f00000001UL, "h6", () => 10_000);

            if (heartbeat)
            {
                var reply = new VisAuthority.HeartbeatReply { IsHeartbeat = true, VisProtocol = 2, MixerInstance = "m1" };
                reply.Rooms[room] = new VisAuthority.HeartbeatRoomReply { Status = "unknown_room" };
                auth.OnHeartbeatOutcome(true, reply);
            }
            else
                auth.OnBatchOutcome(room, VisOp.Replace, auth.NextGeneration(room), new List<UUID> { Id(1) }, true,
                    new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });

            Assert.That(JanusAudioBridge.IsRoomKnown(room), Is.False, "the mixer said the room is unknown: believed");
            int before = m.Creates;
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room), Existing(room));
            Assert.That(m.Creates, Is.EqualTo(before + 1), "so the next select creates it");
        }

        // ---- concurrency ------------------------------------------------------------------------------------------

        /// <summary>H8: two provisions into the same missing room at once, both first joins answered 485 before either
        /// recreates. One create, both join: the second recreate sees the first one's fresh hint and does not create
        /// again. The retry runs outside the per-room create gate (the gate is released before any join), so it cannot
        /// deadlock on it.</summary>
        [Test]
        public async Task H8_TwoConcurrentProvisionsIntoOneMissingRoom_OneCreate_BothJoin()
        {
            const int room = 880801;
            var m = new FakeMixer();
            await StaleHint(m, room);
            int before = m.Creates;

            int arrivals = 0;
            var bothFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<(bool, int)> Join(JanusRoom jr)
            {
                if (Interlocked.Increment(ref arrivals) <= 2)
                {
                    (bool, int) first = m.JoinNow(jr);   // both first joins see the room missing
                    if (arrivals >= 2)
                        bothFailed.TrySetResult(true);
                    await bothFailed.Task.ConfigureAwait(false);
                    return first;
                }
                return m.JoinNow(jr);
            }

            var results = await Task.WhenAll(Provision(m, room, Join), Provision(m, room, Join))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(results[0].Joined && results[1].Joined, Is.True, "both joined");
            Assert.That(m.Creates - before, Is.EqualTo(1), "one create between them");
        }
    }
}
