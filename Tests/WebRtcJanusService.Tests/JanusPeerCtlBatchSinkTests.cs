/*
 * Unit tests for JanusPeerCtlBatchSink's per-room emission (design brief §8 S3b).
 *
 * The sink is the only place a room number is stamped, and the mixer cannot tell us we stamped the
 * wrong one - an unknown room replies janus:"success" like any other (§3.3.1). So the room a message
 * carries is verified HERE or nowhere. The transport is injected through the ctor's sendOne hook
 * (the house pattern - cf. VisibilityBatchSender's nowMs), so no HttpClient is created.
 *
 * The load-bearing case is the estate no-regression one: where every agent resolves to one room the
 * sink must emit ONE message with the same body to the same room as it did before S3b, so those
 * tests compare the serialized body against one built the pre-S3b way rather than eyeballing fields.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusPeerCtlBatchSinkTests
    {
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

        private static Func<UUID, int?> Resolver(params (int agent, int room)[] records)
        {
            var table = new Dictionary<UUID, int>();
            foreach (var (agent, room) in records) table[Id(agent)] = room;
            return a => table.TryGetValue(a, out int r) ? r : (int?)null;
        }

        /// <summary>Records every message the sink hands the transport, and answers each one.</summary>
        private sealed class Recorder
        {
            private readonly object _lock = new object();
            public readonly List<OSDMap> Sent = new List<OSDMap>();
            public Func<OSDMap, AdminSendResult> Reply = _ => AdminSendResult.Ok;

            // S4: the sink now consumes (result, body). These tests assert PeerCtlSendResult only, so
            // the body is empty (an empty body parses to an absent inner reply -> zero stats, no log).
            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (_lock)
                    Sent.Add(request);
                return Task.FromResult((Reply(request), string.Empty));
            }

            public List<int> Rooms()
            {
                var rooms = new List<int>();
                lock (_lock)
                    foreach (OSDMap m in Sent) rooms.Add(m["room"].AsInteger());
                return rooms;
            }

            public OSDMap ForRoom(int room)
            {
                lock (_lock)
                    foreach (OSDMap m in Sent)
                        if (m["room"].AsInteger() == room) return m;
                return null;
            }
        }

        private static JanusPeerCtlBatchSink NewSink(Recorder rec, Func<UUID, int?> roomOf,
                                                     int concurrency = JanusPeerCtlBatchSink.DefaultRoomSendConcurrency)
        {
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", concurrency, rec.SendAsync);
            sink.RoomOf = roomOf;
            return sink;
        }

        // ---- estate no-regression: one room in, one message out, same body ----

        [Test]
        public async Task SendAsync_EveryAgentInTheFallbackRoom_SendsExactlyOneMessage_WithThePreS3bBody()
        {
            var rec = new Recorder();
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1 }));
            using var sink = NewSink(rec, null);   // no resolver at all: nothing is recorded
            int fallback = sink.FallbackRoom;

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, excl);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(rec.Sent.Count, Is.EqualTo(1), "one room must mean one admin round-trip, as before S3b");

            // The pre-S3b sink built exactly this: the room-less body, with the estate room stamped on.
            OSDMap expected = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, excl);
            expected["room"] = new OSDInteger(fallback);
            Assert.That(OSDParser.SerializeJsonString(rec.Sent[0]),
                Is.EqualTo(OSDParser.SerializeJsonString(expected)),
                "the estate-channel body must be byte-for-byte what it was before per-room emission");
        }

        [Test]
        public async Task SendAsync_EveryAgentRecordedInOneRoom_SendsOneMessageToThatRoom_NotTheFallback()
        {
            var rec = new Recorder();
            var excl = Excl((1, new[] { 2 }), (2, new[] { 1 }));
            using var sink = NewSink(rec, Resolver((1, 4242), (2, 4242)));

            await sink.SendAsync(VisOp.Add, excl);

            Assert.That(rec.Sent.Count, Is.EqualTo(1));
            Assert.That(rec.Sent[0]["room"].AsInteger(), Is.EqualTo(4242));
            Assert.That(sink.LastSendFallbackListeners, Is.Zero);
            Assert.That(sink.LastSendFallbackSources, Is.Zero);
        }

        // ---- the null-resolver window (construction order) ----

        [Test]
        public async Task SendAsync_BeforeTheServiceAssignsTheResolver_FallsBackToTheEstateRoom_AndCountsEveryone()
        {
            // The sink is built before VoiceVisibilityService exists, so RoomOf is null until that
            // ctor runs. A send in that window must behave exactly as it did before S3b, loudly.
            var rec = new Recorder();
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", 4, rec.SendAsync);
            using (sink)
            {
                Assert.That(sink.RoomOf, Is.Null, "unset until the service assigns it");

                PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, Excl((1, new[] { 2, 3 })));

                Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
                Assert.That(rec.Rooms(), Is.EqualTo(new[] { sink.FallbackRoom }));
                Assert.That(sink.LastSendRooms, Is.EqualTo(1));
                Assert.That(sink.LastSendFallbackListeners, Is.EqualTo(1));
                Assert.That(sink.LastSendFallbackSources, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task RoomOf_AssignedAfterConstruction_TakesEffectOnTheNextSend()
        {
            var rec = new Recorder();
            using var sink = NewSink(rec, null);
            await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 })));
            Assert.That(rec.Rooms(), Is.EqualTo(new[] { sink.FallbackRoom }));

            sink.RoomOf = Resolver((1, 700), (2, 700));
            await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 })));

            Assert.That(rec.Sent.Count, Is.EqualTo(2));
            Assert.That(rec.Sent[1]["room"].AsInteger(), Is.EqualTo(700));
        }

        // ---- partitioning and filtering, end to end through the sink ----

        [Test]
        public async Task SendAsync_ListenersInTwoRooms_SendsOneMessagePerRoom_WithSameRoomSourcesOnly()
        {
            var rec = new Recorder();
            // Listener 1 in room 100 excludes 2 (room 100) and 3 (room 200); listener 3 in room 200.
            var excl = Excl((1, new[] { 2, 3 }), (3, new[] { 2 }));
            using var sink = NewSink(rec, Resolver((1, 100), (2, 100), (3, 200)));

            await sink.SendAsync(VisOp.Replace, excl);

            Assert.That(rec.Sent.Count, Is.EqualTo(2));
            Assert.That(rec.Rooms(), Is.EquivalentTo(new[] { 100, 200 }));
            Assert.That(sink.LastSendRooms, Is.EqualTo(2));

            var a = (OSDMap)rec.ForRoom(100)["excl"];
            Assert.That(a.ContainsKey(Id(1).ToString()), Is.True);
            Assert.That(((OSDArray)a[Id(1).ToString()]).Count, Is.EqualTo(1), "source 3 is in another room");

            var b = (OSDMap)rec.ForRoom(200)["excl"];
            Assert.That(b.ContainsKey(Id(3).ToString()), Is.True);
            Assert.That(((OSDArray)b[Id(3).ToString()]).Count, Is.Zero, "source 2 is in another room");
        }

        [Test]
        public async Task SendAsync_EmptyMap_SendsNothing_AndIsOk()
        {
            var rec = new Recorder();
            using var sink = NewSink(rec, Resolver());

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Add, new Dictionary<UUID, IReadOnlyCollection<UUID>>());

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok), "nothing to address is not a failure");
            Assert.That(rec.Sent, Is.Empty);
            Assert.That(sink.LastSendRooms, Is.Zero);
        }

        [Test]
        public void SendAsync_InvariantViolationInOneRoom_SendsNothingAtAll()
        {
            // Every body is built before any is sent, so the serializer's all-or-nothing throw stays
            // all-or-nothing across rooms: no room may be updated while another aborts.
            //
            // The bad source must land in its OWN listener's room to reach the serializer at all:
            // same-room filtering drops a cross-room source first, so a zero UUID in another room is
            // filtered out rather than caught. Listener 3 and the zero source both have no record,
            // so both resolve to the fallback room while listener 1 sits in room 100 - two rooms,
            // one of which cannot be built.
            var rec = new Recorder();
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>
            {
                [Id(1)] = new List<UUID> { Id(2) },
                [Id(3)] = new List<UUID> { UUID.Zero },   // invariant violation, in the fallback room
            };
            using var sink = NewSink(rec, Resolver((1, 100), (2, 100)));

            Assert.That(async () => await sink.SendAsync(VisOp.Add, excl),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(rec.Sent, Is.Empty, "no room may have been updated before the throw");
        }

        // ---- aggregation precedence (§2a severity order) ----

        private async Task<PeerCtlSendResult> ThreeRooms(Func<int, AdminSendResult> replyByRoom, Recorder rec)
        {
            using var sink = NewSink(rec, Resolver((1, 100), (2, 200), (3, 300)));
            rec.Reply = req => replyByRoom(req["room"].AsInteger());
            return await sink.SendAsync(VisOp.Add, Excl((1, new int[0]), (2, new int[0]), (3, new int[0])));
        }

        [Test]
        public async Task Aggregate_AllOk_IsOk()
        {
            var rec = new Recorder();
            Assert.That(await ThreeRooms(_ => AdminSendResult.Ok, rec), Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(rec.Sent.Count, Is.EqualTo(3));
        }

        [Test]
        public async Task Aggregate_OkPlusTransportError_IsTransportError()
        {
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 200 ? AdminSendResult.TransportError : AdminSendResult.Ok, rec);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.TransportError));
            Assert.That(rec.Sent.Count, Is.EqualTo(3), "a failure in one room must not suppress the others");
        }

        [Test]
        public async Task Aggregate_OkPlusProtocolError_IsProtocolError()
        {
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 300 ? AdminSendResult.ProtocolError : AdminSendResult.Ok, rec);

            // Must NOT read as Ok: the sender's latch counts consecutive ProtocolErrors, and an Ok
            // here would reset that run and mask a real config/format fault forever.
            Assert.That(r, Is.EqualTo(PeerCtlSendResult.ProtocolError));
            Assert.That(rec.Sent.Count, Is.EqualTo(3));
        }

        [Test]
        public async Task Aggregate_TransportErrorPlusProtocolError_IsProtocolError()
        {
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 100 ? AdminSendResult.TransportError
                      : room == 300 ? AdminSendResult.ProtocolError
                      : AdminSendResult.Ok, rec);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.ProtocolError), "ProtocolError outranks TransportError");
        }

        [Test]
        public async Task Aggregate_AllTransportErrors_IsTransportError()
        {
            var rec = new Recorder();
            Assert.That(await ThreeRooms(_ => AdminSendResult.TransportError, rec),
                Is.EqualTo(PeerCtlSendResult.TransportError));
        }

        [Test]
        public async Task SingleRoom_PreservesTheResultUnchanged()
        {
            var rec = new Recorder { Reply = _ => AdminSendResult.ProtocolError };
            using var sink = NewSink(rec, null);

            Assert.That(await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 }))),
                Is.EqualTo(PeerCtlSendResult.ProtocolError));
        }

        // ---- the concurrency cap actually bounds ----

        /// <summary>Counts how many sends are inside the transport at once.</summary>
        private sealed class ConcurrencyProbe
        {
            private int _current;
            private int _max;
            public int Max => Volatile.Read(ref _max);
            public readonly List<OSDMap> Sent = new List<OSDMap>();

            public async Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (Sent)
                    Sent.Add(request);
                int now = Interlocked.Increment(ref _current);
                int seen;
                while (now > (seen = Volatile.Read(ref _max)))
                {
                    if (Interlocked.CompareExchange(ref _max, now, seen) == seen)
                        break;
                }
                await Task.Delay(25).ConfigureAwait(false);
                Interlocked.Decrement(ref _current);
                return (AdminSendResult.Ok, string.Empty);
            }
        }

        private static async Task<ConcurrencyProbe> EightRoomsAt(int concurrency)
        {
            var probe = new ConcurrencyProbe();
            var records = new List<(int, int)>();
            var rows = new List<(int, int[])>();
            for (int i = 1; i <= 8; i++)
            {
                records.Add((i, 100 + i));       // each listener in its own room
                rows.Add((i, new int[0]));
            }
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", concurrency, probe.SendAsync);
            using (sink)
            {
                sink.RoomOf = Resolver(records.ToArray());
                await sink.SendAsync(VisOp.Add, Excl(rows.ToArray()));
            }
            return probe;
        }

        [Test]
        public async Task Concurrency_BoundsRoomsInFlight()
        {
            ConcurrencyProbe probe = await EightRoomsAt(2);

            Assert.That(probe.Sent.Count, Is.EqualTo(8), "every room is still sent");
            Assert.That(probe.Max, Is.LessThanOrEqualTo(2), "the cap must actually bound in-flight sends");
            Assert.That(probe.Max, Is.EqualTo(2), "and must actually parallelise up to it, not run sequentially");
        }

        [Test]
        public async Task Concurrency_Zero_IsClampedToSequential_NotAStall()
        {
            // SemaphoreSlim(0) would block every send forever and wedge emission until the sender's
            // staleness guard fired - every tick. Zero must degrade to sequential instead.
            ConcurrencyProbe probe = await EightRoomsAt(0);

            Assert.That(probe.Sent.Count, Is.EqualTo(8));
            Assert.That(probe.Max, Is.EqualTo(1));
        }

        [Test]
        public async Task Concurrency_Negative_IsClampedToSequential()
        {
            ConcurrencyProbe probe = await EightRoomsAt(-4);

            Assert.That(probe.Sent.Count, Is.EqualTo(8));
            Assert.That(probe.Max, Is.EqualTo(1));
        }

        [Test]
        public void DefaultRoomSendConcurrency_IsTheBriefsSmallDefault()
        {
            Assert.That(JanusPeerCtlBatchSink.DefaultRoomSendConcurrency, Is.EqualTo(4));
        }
    }
}
