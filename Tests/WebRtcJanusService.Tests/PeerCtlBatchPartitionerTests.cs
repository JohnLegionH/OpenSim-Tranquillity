/*
 * Unit tests for PeerCtlBatchPartitioner - the per-room split that makes SLV_VIS_MAX_EXCL
 * unreachable by construction (design brief §8 S3a, §7 OQ2/OQ4 and the missing-record resolution).
 *
 * The load-bearing cases are the two fallbacks. A missing room record resolves to the estate room
 * for BOTH roles, and the pair of counters is what tells an operator which state a deployment is
 * in: fallback_listeners non-zero means agents the table does not cover, fallback_sources non-zero
 * means a service that does not yet report the room it joined.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class PeerCtlBatchPartitionerTests
    {
        private const int EstateRoom = 9000;

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
            {
                var list = new List<UUID>();
                foreach (int s in sources) list.Add(Id(s));
                d[Id(listener)] = list;
            }
            return d;
        }

        /// <summary>A resolver over a fixed agent-number -> room table; any agent not named has NO
        /// record, exactly as AgentRoomTable.Resolve returns null for one it never recorded.</summary>
        private static Func<UUID, int?> Resolver(params (int agent, int room)[] records)
        {
            var table = new Dictionary<UUID, int>();
            foreach (var (agent, room) in records) table[Id(agent)] = room;
            return a => table.TryGetValue(a, out int r) ? r : (int?)null;
        }

        private static IReadOnlyCollection<UUID> Column(PeerCtlBatchPartition p, int room, int listener)
            => p.Rooms[room][Id(listener)];

        // ---- partitioning across rooms ----

        [Test]
        public void Partition_SplitsListenersByTheirOwnRoom()
        {
            // Listeners 1 and 2 in room 100; listener 3 in room 200. Sources co-located so nothing
            // is filtered - this test is about the split alone.
            var excl = Excl((1, new[] { 2 }), (2, new[] { 1 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(2));
            Assert.That(p.Rooms[100].Count, Is.EqualTo(2));
            Assert.That(p.Rooms[200].Count, Is.EqualTo(1));
            Assert.That(p.Rooms[100].ContainsKey(Id(3)), Is.False, "listener 3 belongs to room 200 only");
            Assert.That(Column(p, 200, 3), Is.EquivalentTo(new[] { Id(4) }));
            Assert.That(p.FallbackListeners, Is.Zero);
            Assert.That(p.FallbackSources, Is.Zero);
        }

        // ---- same-room source filtering (OQ2a) ----

        [Test]
        public void Partition_KeepsOnlySourcesInTheListenersRoom()
        {
            // Listener 1 (room 100) excludes sources 2 (room 100) and 3 (room 200). The cross-room
            // source is inert at the mixer, so it must not travel.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1), "only listener 1 is a listener, so only its room is addressed");
            Assert.That(Column(p, 100, 1), Is.EquivalentTo(new[] { Id(2) }));
        }

        [Test]
        public void Partition_ListenerWhoseColumnFiltersEmpty_KeepsItsKey()
        {
            // Every source is in another room. The key must SURVIVE with an empty column: on a
            // Replace that empty array is the meaningful "clear this listener", and dropping the
            // key would silently skip the clear.
            var excl = Excl((1, new[] { 3, 4 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.Rooms[100].ContainsKey(Id(1)), Is.True);
            Assert.That(Column(p, 100, 1).Count, Is.Zero);
        }

        // ---- fallback: listener with no record (OQ4a) ----

        [Test]
        public void Partition_ListenerWithNoRecord_GoesToEstateRoom_AndIsCounted()
        {
            // Listener 5 is unknown to the table; listener 1 is in room 100.
            var excl = Excl((1, new[] { 2 }), (5, new[] { 6 }));
            var roomOf = Resolver((1, 100), (2, 100), (6, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.Rooms.ContainsKey(EstateRoom), Is.True);
            Assert.That(p.Rooms[EstateRoom].ContainsKey(Id(5)), Is.True);
            Assert.That(p.FallbackListeners, Is.EqualTo(1));
            Assert.That(p.FallbackSources, Is.Zero, "every source here has a record");
            // Source 6 IS recorded, in room 100, so it is filtered out of the estate listener's column.
            Assert.That(Column(p, EstateRoom, 5).Count, Is.Zero);
        }

        // ---- fallback: source with no record (the resolution that supersedes OQ2's draft) ----

        [Test]
        public void Partition_SourceWithNoRecord_IsEstateSource_KeptForEstateListener_FilteredForParcelListener()
        {
            // Source 9 has no record. It must NOT be dropped: it is an estate-room source, so it
            // survives for the estate-room listener (7) and is filtered out for the room-100
            // listener (1). That asymmetry-removal is the whole point of the resolution in §7.
            var excl = Excl((1, new[] { 9 }), (7, new[] { 9 }));
            var roomOf = Resolver((1, 100), (7, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(Column(p, EstateRoom, 7), Is.EquivalentTo(new[] { Id(9) }), "kept for the estate listener");
            Assert.That(Column(p, 100, 1).Count, Is.Zero, "filtered out for the per-parcel listener");
            Assert.That(p.FallbackSources, Is.EqualTo(1));
            Assert.That(p.FallbackListeners, Is.Zero, "listener 7's estate room is RECORDED, not a fallback");
        }

        [Test]
        public void Partition_CountsEachRoleSeparately_ForAnAgentThatIsBoth()
        {
            // Agent 5 has no record and appears as a listener AND as a source. The counters are
            // per-role, so it reads once in each - not once overall, and not twice in one.
            var excl = Excl((5, new[] { 1 }), (1, new[] { 5 }));
            var roomOf = Resolver((1, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.FallbackListeners, Is.EqualTo(1));
            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        [Test]
        public void Partition_CountsDistinctSources_NotOccurrences()
        {
            // The unrecorded source 9 is named in three columns. It is ONE agent missing a record;
            // counting occurrences would scale the counter with column fan-out and make "reads zero
            // on a fully upgraded deployment" the only interpretable reading it has.
            var excl = Excl((1, new[] { 9 }), (2, new[] { 9 }), (3, new[] { 9 }));
            var roomOf = Resolver((1, EstateRoom), (2, EstateRoom), (3, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        // ---- empty map ----

        [Test]
        public void Partition_EmptyMap_YieldsNoRoomsAndNoFallbacks()
        {
            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(
                new Dictionary<UUID, IReadOnlyCollection<UUID>>(), Resolver(), EstateRoom);

            Assert.That(p.RoomCount, Is.Zero);
            Assert.That(p.Rooms.Count, Is.Zero);
            Assert.That(p.FallbackListeners, Is.Zero);
            Assert.That(p.FallbackSources, Is.Zero);
        }

        // ---- single-room fast path ----

        [Test]
        public void Partition_SingleRoom_ReturnsOneBucket_WithTheInputMapItself()
        {
            // Everyone in room 100: no column can lose a source, so the input map IS the answer and
            // is handed back uncopied.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1));
            Assert.That(p.Rooms[100], Is.SameAs(excl), "fast path must not copy");
        }

        [Test]
        public void Partition_NoRecordsAtAll_IsTodaysBehaviour_OneEstateBatchWithFullColumns()
        {
            // The connector-topology skew state: nobody has a record. Both roles fall back to the
            // estate room, so this is the single-room fast path - one batch, full columns, exactly
            // what a pre-S3 sink sent. This is the no-regression guarantee, as a test.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1, 3 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, Resolver(), EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1));
            Assert.That(p.Rooms[EstateRoom], Is.SameAs(excl));
            Assert.That(p.FallbackListeners, Is.EqualTo(2));
            Assert.That(p.FallbackSources, Is.EqualTo(3), "sources 1, 2 and 3, counted once each");
        }

        [Test]
        public void Partition_NullResolver_ReadsAsNoRecordsAtAll()
        {
            // An unwired sink must not throw on the send path; it must degrade to today's behaviour
            // with both counters shouting.
            var excl = Excl((1, new[] { 2 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, null, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1));
            Assert.That(p.Rooms[EstateRoom], Is.SameAs(excl));
            Assert.That(p.FallbackListeners, Is.EqualTo(1));
            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        // ---- guards and non-mutation ----

        [Test]
        public void Partition_NullExcl_Throws()
        {
            Assert.That(() => PeerCtlBatchPartitioner.Partition(null, Resolver(), EstateRoom),
                Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void Partition_NullColumn_ReadsAsAnEmptyColumn()
        {
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Id(1)] = null, [Id(2)] = new List<UUID> { Id(1) } };
            var roomOf = Resolver((1, 100), (2, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(Column(p, 100, 1).Count, Is.Zero);
        }

        [Test]
        public void Partition_DoesNotMutateTheInput()
        {
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(excl.Count, Is.EqualTo(1));
            Assert.That(excl[Id(1)], Is.EquivalentTo(new[] { Id(2), Id(3) }), "the caller's column is untouched");
        }

        [Test]
        public void Partition_ResultColumnsAreWhatTheSerializerAccepts()
        {
            // The contract with S3b: each per-room map goes straight into BuildRequest, which stays
            // room-agnostic - the sink stamps the room key alongside.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            foreach (KeyValuePair<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> room in p.Rooms)
            {
                var body = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, room.Value);
                Assert.That(body["request"].AsString(), Is.EqualTo("peer_ctl_batch"));
                Assert.That(body.ContainsKey("room"), Is.False);
            }
        }
    }
}
