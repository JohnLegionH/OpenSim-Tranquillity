/*
 * Slice 0.8f-sim (ledger O-98), as restated by slice 0.8i: a room is made to exist by ASKING THE MIXER, every time.
 *
 * 0.8f kept a process-wide "room exists" hint and taught every path not to trust it. 0.8i deleted the hint: the mixer's
 * 60 s empty-room grace destroys rooms without telling the sim, so any cached answer goes stale, and in the 0.8g live
 * proof a stale one cost a viewer 5.25 s. What 0.8f's tests pinned about the ENSURE still holds and is kept here:
 *   H1  a room this process created and the mixer has since destroyed is created again by the ensure;
 *   H2  a room that exists is asked for anyway - already-exists is success - and RoomExists fires exactly once;
 *   H3  a create that fails proves nothing: RoomExists does not fire and the unknown_room backoff is not reset.
 * What 0.8f's tests pinned about R2, the inline same-handle retry, is GONE with R2:
 *   H4, H5, H8 (a join answered 485 re-created and re-joined inside one provision) are DELETED. They passed because
 *     their fake join had no ICE state; live, Janus core refuses the second JSEP join on the same handle with 490
 *     (0.8g PROOF D). CreateBeforeJoinTests V1, V3 and V4 replace them on the REAL provision path, against a fake
 *     gateway that models that rule, and harness S39 pins the rule against a real Janus.
 *   H6 (an unknown_room reply forgets the hint) is DELETED: there is no hint left to forget.
 *   H7 (the 300 s backoff cap) lives in ConnectorRoomTests and is unchanged.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // the per-room create gates are process-wide statics
    public class RoomHintTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            return new UUID(b, 0);
        }

        /// The mixer, as far as rooms go: create is idempotent (already-exists answers 486, which the sim treats as
        /// success), and the grace destroy happens behind the sim's back.
        private sealed class FakeMixer
        {
            private readonly object _lock = new object();
            public readonly HashSet<int> Rooms = new HashSet<int>();
            public int Creates;
            public bool FailCreates;

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

            public void GraceDestroy(int room) { lock (_lock) Rooms.Remove(room); }
        }

        private static Func<Task<JanusRoom>> CreateOf(FakeMixer m, int room) => () => m.Create(room);

        /// <summary>H1: this process created the room, the mixer has since destroyed it. The ensure creates it again -
        /// 0.8d's PROOF 1 failure, which a stale hint caused.</summary>
        [Test]
        public async Task H1_ARoomThisProcessCreated_DestroyedByTheMixer_TheEnsureCreatesItAgain()
        {
            const int room = 880101;
            var m = new FakeMixer();
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));
            m.GraceDestroy(room);
            int before = m.Creates;

            JanusRoom ensured = await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));

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
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));   // really exists
            int before = m.Creates, fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room)).Result?.RoomId,
                r => fired++);

            Assert.That(ensured, Is.EqualTo(room), "no error: already-exists is success");
            Assert.That(m.Creates, Is.EqualTo(before + 1), "the mixer was asked, even right after a create");
            Assert.That(fired, Is.EqualTo(1), "RoomExists fired exactly once");
        }

        /// <summary>H3: the create fails. Nothing is proven, so RoomExists does NOT fire and the backoff is NOT reset.</summary>
        [Test]
        public async Task H3_AFailedCreate_FiresNoRoomExists_AndLeavesTheBackoff()
        {
            const int room = 880301;
            var m = new FakeMixer();
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));
            m.GraceDestroy(room);
            m.FailCreates = true;

            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "h3", () => clock);
            UUID l = Id(1);
            auth.OnBatchOutcome(room, VisOp.Replace, auth.NextGeneration(room), new List<UUID> { l }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.That(auth.CanArmNow(l), Is.False, "precondition: backing off");
            int fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room)).Result?.RoomId,
                r => { fired++; auth.RoomExists(r); });

            Assert.That(ensured, Is.Null, "a failed create is not a room");
            Assert.That(fired, Is.Zero, "RoomExists did not fire");
            Assert.That(auth.CanArmNow(l), Is.False, "the backoff was not reset");
        }
    }
}
