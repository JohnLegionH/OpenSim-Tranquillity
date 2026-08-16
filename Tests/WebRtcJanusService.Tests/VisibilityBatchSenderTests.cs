/*
 * Unit tests for VisibilityBatchSender — the peer_ctl_batch orchestrator (region module).
 *
 * The sender is fire-and-forget on the tick thread; these tests drive it through the awaitable
 * PumpAsync core so each tick's send completes deterministically (and the single-flight flag is
 * cleared) before the next Pump. A FakeSink records (op, excl) and returns a scripted result; a
 * FakeFeed injects Current (for the snapshot/delta path) and per-listener columns (for the
 * pending-join path). Covers: disabled/no-sink no-ops, snapshot-then-delta bootstrap, delta
 * add+remove ordering, clear-tracking (§3.3.1), result mapping (Transport/Protocol), the
 * disjoint-bug guard, never-throw, and the bounded blind re-send + loud give-up (correction 1).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityBatchSenderTests
    {
        private const int Room = -999;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Excl(params (int listener, int[] sources)[] rows)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (var (listener, sources) in rows)
                d[Id(listener)] = sources.Select(Id).ToList();
            return d;
        }

        // ---- fakes -------------------------------------------------------------------------------

        private sealed class FakeSink : IPeerCtlBatchSink
        {
            public PeerCtlSendResult NextResult = PeerCtlSendResult.Ok;
            public bool Throw;
            public readonly List<(VisOp op, Dictionary<UUID, List<UUID>> excl)> Calls = new();
            private readonly object _lock = new object();

            public Task<PeerCtlSendResult> SendAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl)
            {
                if (Throw)
                    throw new InvalidOperationException("sink boom");
                lock (_lock)
                {
                    var copy = new Dictionary<UUID, List<UUID>>();
                    foreach (var kv in excl)
                        copy[kv.Key] = new List<UUID>(kv.Value);
                    Calls.Add((op, copy));
                }
                return Task.FromResult(NextResult);
            }

            public int Count { get { lock (_lock) return Calls.Count; } }
        }

        private sealed class FakeFeed : IVisibilityFeed
        {
            public VisibilityMatrix Current { get; set; } = VisibilityMatrix.Empty;
            public readonly Dictionary<UUID, IReadOnlyCollection<UUID>> Columns = new();

#pragma warning disable CS0067 // required by the interface; the sender does not subscribe
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067

            public VisibilityBatch SnapshotFor(UUID listener)
            {
                IReadOnlyCollection<UUID> col = Columns.TryGetValue(listener, out IReadOnlyCollection<UUID> c)
                    ? c : Array.Empty<UUID>();
                return VisibilityBatch.Snapshot(Room, listener, col);
            }
        }

        // A minimal world producing a mutual-exclusion (banned) pair: `a` is banned from the parcel
        // `b` stands on, so the matrix excludes a<->b (both become listeners). Mirrors the feeder
        // test's ban scenario; used to get a non-empty Current without a live Scene.
        private sealed class BanWorld : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public readonly Dictionary<UUID, ParcelView> ByGid = new();
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents;
            public ParcelView GetParcelAt(Vector3 p) => ByGid.Values.First();
            public ParcelView GetParcelByGlobalId(UUID id) => ByGid[id];
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        private static VisibilityMatrix BannedPairMatrix(UUID a, UUID b)
        {
            var w = new BanWorld();
            UUID pGid = Id(201), qGid = Id(202);
            ParcelView p = new ParcelView(pGid, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: x => x == a, isRestrictedFromLand: _ => false);   // P bans `a`
            ParcelView q = new ParcelView(qGid, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: _ => false, isRestrictedFromLand: _ => false);
            w.ByGid[pGid] = p;
            w.ByGid[qGid] = q;
            w.Agents.Add(new AgentView(a, false, Vector3.Zero, qGid, false));   // a on Q (banned from P)
            w.Agents.Add(new AgentView(b, false, Vector3.Zero, pGid, false));   // b on P
            return VisibilityMatrix.Build(w);
        }

        // ---- no-op paths -------------------------------------------------------------------------

        [Test]
        public async Task Disabled_SendsNothing()
        {
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(new FakeFeed(), sink, enabled: false);
            sender.OnListenerProvisioned(Id(1));
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void NullSink_NeverThrows_AndSendsNothing()
        {
            var sender = new VisibilityBatchSender(new FakeFeed(), null, enabled: true);
            Assert.That(async () => await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room)), Throws.Nothing);
        }

        // ---- bootstrap: first tick snapshots, then deltas flow -----------------------------------

        [Test]
        public async Task EmptyMatrix_FirstTickSyncsWithoutSending_ThenDeltaFlows()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            // Tick 1: unsynced -> snapshot path, but the empty matrix has nothing to send -> synced.
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(0), "empty-matrix snapshot sends nothing and preempts the delta");

            // Tick 2: now synced -> the delta is sent as an Add.
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(1));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Add));
            Assert.That(sink.Calls[0].excl[Id(1)], Is.EquivalentTo(new[] { Id(2) }));
        }

        [Test]
        public async Task NonEmptyMatrix_FirstTickSendsSnapshotReplace()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            Assert.That(sink.Count, Is.EqualTo(1));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Replace));
            Assert.That(sink.Calls[0].excl[a], Is.EquivalentTo(new[] { b }), "a excludes b in a banned pair");
            Assert.That(sink.Calls[0].excl[b], Is.EquivalentTo(new[] { a }), "symmetric");
        }

        [Test]
        public async Task Delta_SendsAtMostOneAddAndOneRemove_InOrder()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // sync

            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 3 })), Excl((2, new[] { 4 }))));

            Assert.That(sink.Count, Is.EqualTo(2));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Add));
            Assert.That(sink.Calls[0].excl[Id(1)], Is.EquivalentTo(new[] { Id(3) }));
            Assert.That(sink.Calls[1].op, Is.EqualTo(VisOp.Remove));
            Assert.That(sink.Calls[1].excl[Id(2)], Is.EquivalentTo(new[] { Id(4) }));
        }

        // ---- clear-tracking (§3.3.1): a dropped listener gets an EXPLICIT empty replace ----------

        [Test]
        public async Task ClearTracking_DroppedListenerGetsExplicitEmptyReplace()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            // Tick 1: snapshot Ok -> known listeners = {a, b}.
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            // Tick 2: a delta whose send fails with TransportError -> _synced flips false.
            sink.NextResult = PeerCtlSendResult.TransportError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((3, new[] { 4 })), null));

            // Tick 3: matrix now empty; unsynced -> re-snapshot. a and b are gone but were known,
            // so they must appear as EXPLICIT empty lists (omission would not clear them).
            sink.NextResult = PeerCtlSendResult.Ok;
            feed.Current = VisibilityMatrix.Empty;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            var replace = sink.Calls.Last();
            Assert.That(replace.op, Is.EqualTo(VisOp.Replace));
            Assert.That(replace.excl.ContainsKey(a), Is.True, "dropped listener a present in the clearing replace");
            Assert.That(replace.excl[a], Is.Empty, "a cleared with an explicit empty list");
            Assert.That(replace.excl.ContainsKey(b), Is.True);
            Assert.That(replace.excl[b], Is.Empty);
        }

        // ---- result mapping ----------------------------------------------------------------------

        [Test]
        public async Task TransportError_OnDelta_ForcesSnapshotNextTick()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));      // snapshot #1 (Ok)
            sink.NextResult = PeerCtlSendResult.TransportError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((5, new[] { 6 })), null));  // delta -> TransportError
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));      // must be a snapshot, not a delta

            Assert.That(sink.Calls.First().op, Is.EqualTo(VisOp.Replace));
            Assert.That(sink.Calls.Last().op, Is.EqualTo(VisOp.Replace), "after a TransportError the next tick re-snapshots");
        }

        [Test]
        public async Task ProtocolError_TwoThenOk_DoesNotLatch()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { NextResult = PeerCtlSendResult.ProtocolError };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #1 (snapshot, stays unsynced)
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #2
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // Ok -> resets the consecutive run
            Assert.That(sink.Count, Is.EqualTo(3));

            // Not latched: a further send still reaches the sink (a latch would block it).
            sink.NextResult = PeerCtlSendResult.ProtocolError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(4),
                "two ProtocolErrors then an Ok must NOT latch — the Ok reset the run, emission continues");
        }

        [Test]
        public async Task ProtocolError_ThreeConsecutive_Latches()
        {
            Assert.That(VisibilityBatchSender.ProtocolErrorLatchThreshold, Is.EqualTo(3), "guard: the test assumes K=3");

            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { NextResult = PeerCtlSendResult.ProtocolError };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #1
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #2
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #3 -> LATCH
            Assert.That(sink.Count, Is.EqualTo(3));

            // Latched off: further pumps and even a fresh provision do nothing.
            sink.NextResult = PeerCtlSendResult.Ok;
            sender.OnListenerProvisioned(Id(9));
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
            Assert.That(sink.Count, Is.EqualTo(3), "three consecutive ProtocolErrors latch — emission stays disabled");
        }

        // ---- disjoint-bug guard ------------------------------------------------------------------

        [Test]
        public async Task Delta_WithOverlappingAddAndRemove_IsCaught_NoThrow_NoSend()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // sync

            // (listener 1, source 2) in BOTH add and remove -> EnsureDisjoint throws inside the
            // never-throw guard; the tick swallows it and sends nothing.
            var bad = VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), Excl((1, new[] { 2 })));
            Assert.That(async () => await sender.PumpAsync(bad), Throws.Nothing);
            Assert.That(sink.Count, Is.EqualTo(0), "a disjoint-violating tick emits nothing");
        }

        [Test]
        public void SinkThrows_PumpNeverThrows()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { Throw = true };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            Assert.That(async () => await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room)), Throws.Nothing);
        }

        // ---- correction 1: bounded blind re-send of a joining listener's full column -------------

        [Test]
        public async Task PendingJoin_ResendsFullColumnBoundedTimes_ThenGivesUp()
        {
            UUID listener = Id(10), source = Id(11);
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };   // main path stays a no-op after sync
            feed.Columns[listener] = new List<UUID> { source };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            sender.OnListenerProvisioned(listener);

            // Pump well past the bound; the join column must be re-sent exactly PendingJoinMaxAttempts
            // times (Ok != applied — we cannot confirm presence), then give up silently to the sink.
            for (int i = 0; i < VisibilityBatchSender.PendingJoinMaxAttempts + 3; i++)
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            Assert.That(sink.Count, Is.EqualTo(VisibilityBatchSender.PendingJoinMaxAttempts),
                "exactly the bounded number of re-sends, then it stops");
            Assert.That(sink.Calls.All(c => c.op == VisOp.Replace), Is.True, "join uses a listener-scoped Replace");
            Assert.That(sink.Calls[0].excl.Keys, Is.EquivalentTo(new[] { listener }), "listener-scoped, not the whole room");
            Assert.That(sink.Calls[0].excl[listener], Is.EquivalentTo(new[] { source }), "the listener's full column");
        }

        [Test]
        public async Task PendingJoin_DoesNotAffectMainSnapshotState()
        {
            // The pending path must not touch _synced/_knownListeners. With an empty Current, the main
            // path syncs on tick 1 and thereafter sends nothing; the ONLY sends are the join re-sends.
            // (If the pending path had marked the main path synced/known, the counts below would differ.)
            UUID listener = Id(20), source = Id(21);
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            feed.Columns[listener] = new List<UUID> { source };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            sender.OnListenerProvisioned(listener);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // join send #1 + main snapshot (empty, no send)
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // join send #2 + main delta (empty, no send)

            Assert.That(sink.Count, Is.EqualTo(2), "only the two join re-sends; the main path emitted nothing");
            Assert.That(sink.Calls.All(c => c.op == VisOp.Replace && c.excl.ContainsKey(listener)), Is.True);
        }
    }
}
