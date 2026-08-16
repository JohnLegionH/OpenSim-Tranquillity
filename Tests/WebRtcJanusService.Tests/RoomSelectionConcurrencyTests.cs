/*
 * Concurrency/regression tests for room selection (JanusAudioBridge).
 *
 * Live defect: three near-simultaneous ProvisionVoiceAccountRequests from one agent
 * (all region/estate room, parcel -999) raced. Because the AudioBridge (and its room
 * cache) was per viewer session, nothing coalesced the concurrent Janus room creates;
 * one create came back non-486 ("room selection failed") while the retry succeeded.
 *
 * Part 1 (CreateWithRecheck): an inconclusive first create is re-attempted once, so a
 *   cross-process create that won the race resolves to 486 -> reuse.
 * Part 2 (SelectRoomCoalesced): a process-wide per-room lock + existence hint collapse
 *   concurrent same-process creates of one room number to a single Janus create.
 *
 * Both helpers are Func-based (no Janus transport) so they unit-test per the house
 * pattern. Each test uses a distinct room number because the coalescing state is static
 * (process-wide by design).
 */

using System.Threading;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RoomSelectionConcurrencyTests
    {
        // JanusRoom's ctor only stores the plugin handle (used later by Join/Leave); a
        // null handle is fine for these transport-free tests.
        private static JanusRoom FakeRoom(int id) => new JanusRoom(null, id);

        // --- Part 1: CreateWithRecheck ---

        // Non-486 / inconclusive first attempt, room exists on the retry -> reuse.
        [Test]
        public async Task CreateWithRecheck_InconclusiveThenExists_ReusesOnRetry()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () =>
            {
                attempts++;
                // 1st attempt: inconclusive (null). 2nd: room now exists (486 -> reuse).
                return Task.FromResult(attempts == 1 ? null : FakeRoom(4242));
            };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(2), "must re-check exactly once after an inconclusive first attempt");
            Assert.That(result, Is.Not.Null);
            Assert.That(result.RoomId, Is.EqualTo(4242));
        }

        [Test]
        public async Task CreateWithRecheck_FirstSucceeds_NoRetry()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () => { attempts++; return Task.FromResult(FakeRoom(1)); };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public async Task CreateWithRecheck_BothInconclusive_ReturnsNull()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () => { attempts++; return Task.FromResult<JanusRoom>(null); };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(result, Is.Null);
        }

        // --- Part 2: SelectRoomCoalesced ---

        // N concurrent SelectRooms for one room number -> exactly one Janus create.
        [Test]
        public async Task SelectRoomCoalesced_ConcurrentSameRoom_CreatesOnce()
        {
            const int room = 900001;   // distinct per test (static process-wide state)
            int creates = 0, existing = 0;

            Func<Task<JanusRoom>> create = async () =>
            {
                Interlocked.Increment(ref creates);
                await Task.Delay(25);          // widen the race window
                return FakeRoom(room);
            };
            Func<JanusRoom> makeExisting = () => { Interlocked.Increment(ref existing); return FakeRoom(room); };

            const int N = 8;
            var tasks = new Task<JanusRoom>[N];
            for (int i = 0; i < N; i++)
            {
                tasks[i] = JanusAudioBridge.SelectRoomCoalesced(room, create, makeExisting);
            }
            JanusRoom[] results = await Task.WhenAll(tasks);

            Assert.That(creates, Is.EqualTo(1), "concurrent SelectRooms for one room number must create exactly once");
            Assert.That(existing, Is.EqualTo(N - 1), "the other racers reuse the created room");
            Assert.That(results, Has.All.Not.Null);
        }

        // A room already known to exist in this process must not be created again.
        [Test]
        public async Task SelectRoomCoalesced_AlreadyKnown_SkipsCreate()
        {
            const int room = 900002;
            int creates = 0, existing = 0;

            Func<Task<JanusRoom>> create = () => { Interlocked.Increment(ref creates); return Task.FromResult(FakeRoom(room)); };
            Func<JanusRoom> makeExisting = () => { Interlocked.Increment(ref existing); return FakeRoom(room); };

            await JanusAudioBridge.SelectRoomCoalesced(room, create, makeExisting);   // establishes existence
            await JanusAudioBridge.SelectRoomCoalesced(room, create, makeExisting);   // must skip create

            Assert.That(creates, Is.EqualTo(1));
            Assert.That(existing, Is.EqualTo(1));
        }

        // Stale-hint recovery: once a room is known, ForgetRoom (called on a JoinRoom
        // failure) clears the hint so the next SelectRoom re-creates instead of looping.
        [Test]
        public async Task ForgetRoom_AfterKnown_NextSelectRecreates()
        {
            const int room = 900004;
            int creates = 0;
            Func<Task<JanusRoom>> create = () => { Interlocked.Increment(ref creates); return Task.FromResult(FakeRoom(room)); };
            Func<JanusRoom> makeExisting = () => FakeRoom(room);

            await JanusAudioBridge.SelectRoomCoalesced(room, create, makeExisting);   // create #1, marks known
            JanusAudioBridge.ForgetRoom(room);                                        // join failed -> drop hint
            await JanusAudioBridge.SelectRoomCoalesced(room, create, makeExisting);   // must create again

            Assert.That(creates, Is.EqualTo(2), "after ForgetRoom, the room must be re-created, not skipped");
        }

        // If the create is inconclusive (null), existence is NOT recorded -> a later
        // attempt still tries to create.
        [Test]
        public async Task SelectRoomCoalesced_FailedCreate_NotMarkedKnown()
        {
            const int room = 900003;
            int creates = 0;
            Func<Task<JanusRoom>> failingCreate = () => { Interlocked.Increment(ref creates); return Task.FromResult<JanusRoom>(null); };
            Func<JanusRoom> makeExisting = () => FakeRoom(room);

            JanusRoom first = await JanusAudioBridge.SelectRoomCoalesced(room, failingCreate, makeExisting);
            JanusRoom second = await JanusAudioBridge.SelectRoomCoalesced(room, failingCreate, makeExisting);

            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            Assert.That(creates, Is.EqualTo(2), "a failed create must not mark the room known");
        }
    }
}
