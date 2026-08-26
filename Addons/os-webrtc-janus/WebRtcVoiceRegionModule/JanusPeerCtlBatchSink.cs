/*
 * Janus-side implementation of IPeerCtlBatchSink (Phase 3a option C). Partitions each send by the
 * room its listeners are actually in and sends one peer_ctl_batch per room via JanusAdminClient
 * (Admin API message_plugin, admin_secret in the body). One instance per region; owns its
 * JanusAdminClient (and its HttpClient) for its lifetime.
 *
 * Per-room emission (per-room-visibility-emission-design-brief.md §8 step S3b). Until S3b the sink
 * stamped ONE room on every batch - the estate room - so exclusions for agents in per-parcel rooms
 * were addressed to a room those agents are not in and were silently dropped at the mixer. Now:
 *   - PeerCtlBatchPartitioner (S3a) splits the map by roomOf(listener), keeping for each listener
 *     only the sources in that same room (inert cross-room sources cost cap and nothing else);
 *   - one admin message goes to each room, issued in parallel under a small cap (OQ3);
 *   - the per-room results aggregate into ONE PeerCtlSendResult in §2a's severity order, so the
 *     sender's contract - and its consecutive-ProtocolError latch arithmetic - is unchanged.
 *
 * The constructor-computed estate room is now the FALLBACK: the room an agent with no record
 * resolves to, for listeners and sources alike (§7). It is still computed by the same
 * JanusAudioBridge.CalcRoomNumber the mixer uses, and still logged once at Info so it is
 * eyeball-comparable against handle_info - a wrong room is invisible on the wire.
 *
 * RoomOf is a SETTABLE property, not a ctor argument, because construction order forces it: the
 * region module builds this sink and then passes it INTO VoiceVisibilityService's constructor
 * (WebRtcVoiceRegionModule.cs:174-176), so the service - which owns the table - does not exist yet
 * when the sink does. The service assigns it in its own constructor, before Start(). While it is
 * null the partitioner reads it as "no agent has a record": every agent resolves to the fallback
 * room, one message goes out exactly as before S3b, and both fallback counters read the full
 * population, which is the loud version of that state rather than a crash on the send path.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public sealed class JanusPeerCtlBatchSink : IPeerCtlBatchSink, IDisposable
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[JANUS PEERCTL SINK]";
        private const string SlvoicePlugin = "janus.plugin.slvoice";

        /// <summary>Rooms addressed concurrently within ONE SendAsync ([WebRtcVoice]
        /// VisibilityRoomSendConcurrency). Small by design: this is a latency budget, not a
        /// throughput knob. R rooms sequentially at t ms per admin round-trip fit the 250 ms tick
        /// only while 2R.t &lt; 250; past that the sender's single-flight skips a tick, the skip
        /// forces a snapshot of R messages, and the storm sustains itself (§3). c = 4 turns a
        /// 64-parcel region's 128 round-trips into 32 rounds.</summary>
        public const int DefaultRoomSendConcurrency = 4;

        private readonly JanusAdminClient _admin;               // null when a send delegate was injected
        private readonly Func<OSDMap, Task<AdminSendResult>> _sendOne;
        private readonly SemaphoreSlim _roomGate;
        private readonly int _fallbackRoom;
        private readonly string _region;

        private volatile int _lastSendRooms;
        private volatile int _lastSendFallbackListeners;
        private volatile int _lastSendFallbackSources;

        /// <param name="roomSendConcurrency">Rooms in flight at once within one send. Absent, zero
        /// or negative is not honoured: zero would make SemaphoreSlim block every send forever and
        /// wedge emission until the sender's staleness guard fired, every tick, so a non-positive
        /// value is clamped to 1 (sequential) with a loud warning. There is no upper clamp - a large
        /// value simply means no throttling, and the real parallelism is bounded by the number of
        /// occupied rooms either way.</param>
        /// <param name="sendOne">Per-message transport, injectable for tests (the house pattern -
        /// cf. VisibilityBatchSender's nowMs). Null in production, where it posts through this
        /// sink's own JanusAdminClient.</param>
        public JanusPeerCtlBatchSink(string adminUri, string adminToken, TimeSpan timeout,
                                     UUID regionId, string regionName,
                                     int roomSendConcurrency = DefaultRoomSendConcurrency,
                                     Func<OSDMap, Task<AdminSendResult>> sendOne = null)
        {
            _region = regionName;
            if (sendOne == null)
            {
                _admin = new JanusAdminClient(adminUri, adminToken, timeout);
                _sendOne = req => _admin.SendPluginMessageAsync(SlvoicePlugin, req);
            }
            else
            {
                _sendOne = sendOne;
            }

            int concurrency = roomSendConcurrency;
            if (concurrency < 1)
            {
                m_log.LogWarning("{LogHeader} region {RegionName}: [WebRtcVoice] VisibilityRoomSendConcurrency is {Configured}, " +
                    "which would stall every per-room send; using 1 (sequential). Set a small positive value ({Default} is the default).",
                    LogHeader, regionName, concurrency, DefaultRoomSendConcurrency);
                concurrency = 1;
            }
            _roomGate = new SemaphoreSlim(concurrency, concurrency);

            // Estate/shared channel room: the "local" channel at REGION_ROOM_ID (-999), hashed by
            // the identical CalcRoomNumber the mixer computes on the Janus side. Since S3b this is
            // the FALLBACK room, not the only room - see the class comment.
            _fallbackRoom = JanusAudioBridge.CalcRoomNumber(
                regionId.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            m_log.LogInformation("{LogHeader} region {RegionName} ({RegionId}) -> peer_ctl_batch FALLBACK room {RoomNumber} " +
                "(the estate/local room; compare vs handle_info). Each listener's own room is addressed per send; this number is " +
                "used only for an agent with no recorded room. Room send concurrency {Concurrency}.",
                LogHeader, regionName, regionId, _fallbackRoom, concurrency);
        }

        /// <summary>Recorded room per agent, or null for "no record" - AgentRoomTable.Resolve, handed
        /// over by VoiceVisibilityService's constructor. Settable because this sink is built BEFORE
        /// that service (see the class comment). Null until then, and read as "nothing is recorded".</summary>
        public Func<UUID, int?> RoomOf { get; set; }

        /// <summary>The room an agent with no record is addressed at, both as listener and as source.</summary>
        public int FallbackRoom => _fallbackRoom;

        /// <summary>Rooms addressed by the most recent send (§3's "rooms addressed per tick").</summary>
        public int LastSendRooms => _lastSendRooms;

        /// <summary>Distinct listeners in the most recent send with no room record (OQ4's evidence).</summary>
        public int LastSendFallbackListeners => _lastSendFallbackListeners;

        /// <summary>Distinct sources in the most recent send with no room record. Zero on a fully
        /// upgraded deployment; non-zero says some voice service is not yet reporting its joined room.</summary>
        public int LastSendFallbackSources => _lastSendFallbackSources;

        public void Dispose()
        {
            _admin?.Dispose();
            _roomGate?.Dispose();
        }

        public async Task<PeerCtlSendResult> SendAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl)
        {
            PeerCtlBatchPartition part = PeerCtlBatchPartitioner.Partition(excl, RoomOf, _fallbackRoom);
            _lastSendRooms = part.RoomCount;
            _lastSendFallbackListeners = part.FallbackListeners;
            _lastSendFallbackSources = part.FallbackSources;

            // Build EVERY body BEFORE sending any of them. The serializer's invariant throw stays
            // all-or-nothing as it was when there was one message: a zero UUID in any room aborts the
            // whole tick with nothing on the wire, rather than leaving some rooms updated and others not.
            var requests = new List<OSDMap>(part.RoomCount);
            foreach (KeyValuePair<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> room in part.Rooms)
            {
                OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, room.Value);
                request["room"] = new OSDInteger(room.Key);   // the sink stamps the room, per room
                requests.Add(request);
            }

            LogRoomsAddressed(op, part);

            if (requests.Count == 0)
                return PeerCtlSendResult.Ok;                 // nothing to address is not a failure

            // One room: no gate, one round-trip - byte-for-byte the pre-S3b send.
            if (requests.Count == 1)
                return Map(await _sendOne(requests[0]).ConfigureAwait(false));

            var sends = new Task<PeerCtlSendResult>[requests.Count];
            for (int i = 0; i < requests.Count; i++)
                sends[i] = SendGatedAsync(requests[i]);
            PeerCtlSendResult[] results = await Task.WhenAll(sends).ConfigureAwait(false);

            // §2a: aggregate in severity order, most severe wins. Every room is attempted - a failure
            // in one must not suppress the others, because rooms are independent and `replace` is
            // per-listener idempotent, so the sender's re-snapshot repairs a partial send safely.
            PeerCtlSendResult worst = PeerCtlSendResult.Ok;
            foreach (PeerCtlSendResult r in results)
            {
                if (Severity(r) > Severity(worst))
                    worst = r;
            }
            return worst;
        }

        private async Task<PeerCtlSendResult> SendGatedAsync(OSDMap request)
        {
            await _roomGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return Map(await _sendOne(request).ConfigureAwait(false));
            }
            finally
            {
                _roomGate.Release();
            }
        }

        /// <summary>§2a severity order: ProtocolError beats TransportError beats NotApplied beats Ok.
        /// NotApplied (OQ5) arrives in S4 and slots in at 1, renumbering nothing.</summary>
        private static int Severity(PeerCtlSendResult r)
        {
            switch (r)
            {
                case PeerCtlSendResult.Ok: return 0;
                case PeerCtlSendResult.TransportError: return 2;
                case PeerCtlSendResult.ProtocolError: return 3;
                default: return 3;
            }
        }

        private static PeerCtlSendResult Map(AdminSendResult r)
        {
            switch (r)
            {
                case AdminSendResult.Ok:
                    return PeerCtlSendResult.Ok;
                case AdminSendResult.ProtocolError:
                    return PeerCtlSendResult.ProtocolError;
                case AdminSendResult.TransportError:
                default:
                    return PeerCtlSendResult.TransportError;
            }
        }

        // Which rooms a tick actually addressed is the one thing no other instrument reports: the
        // mixer's reply cannot say (§3.3.1) and the wire carries no per-region view. Debug, because
        // it is per tick.
        private void LogRoomsAddressed(VisOp op, PeerCtlBatchPartition part)
        {
            if (!m_log.IsEnabled(LogLevel.Debug))
                return;

            var sb = new StringBuilder();
            foreach (KeyValuePair<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> room in part.Rooms)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(room.Key).Append(':').Append(room.Value.Count);
            }
            if (sb.Length == 0)
                sb.Append("none");

            m_log.LogDebug("{LogHeader} region {RegionName}: {Op} addressed {RoomCount} room(s) [room:listeners {Rooms}]; " +
                "fallback listeners {FallbackListeners}, sources {FallbackSources} (fallback room {FallbackRoom})",
                LogHeader, _region, PeerCtlBatchSerializer.OpString(op), part.RoomCount, sb.ToString(),
                part.FallbackListeners, part.FallbackSources, _fallbackRoom);
        }
    }
}
