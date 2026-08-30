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
        // Reused empty excl slice for a room that has only mute changes this op.
        private static readonly IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> EmptyChannel
            = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
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
        private readonly Func<OSDMap, Task<(AdminSendResult Result, string Body)>> _sendOne;
        private readonly SemaphoreSlim _roomGate;
        private readonly int _fallbackRoom;
        private readonly string _region;

        private volatile int _lastSendRooms;
        private volatile int _lastSendFallbackListeners;
        private volatile int _lastSendFallbackSources;
        // Q4 counter fix: the MUTE channel's fallback counts, parallel to the excl ones above. Kept
        // SEPARATE (not summed into the excl fields) because the partition exposes counts, not listener
        // sets, so a distinct cross-channel union cannot be computed without changing the partitioner
        // (out of scope). _lastSendRooms below becomes the room UNION (rooms addressed by either channel).
        private volatile int _lastSendMuteFallbackListeners;
        private volatile int _lastSendMuteFallbackSources;

        // S4 inner-reply stats for the most recent send, summed across rooms (see LastSendStats).
        // Volatile ints mirror the LastSend* pattern above; SendAsync is single-flight from the sender.
        private volatile int _statRepliesParsed;
        private volatile int _statDeferred;
        private volatile int _statSkipped;
        private volatile int _statEntries;
        private volatile int _statMuteEntries;
        private volatile int _statAnomalies;

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
                                     Func<OSDMap, Task<(AdminSendResult Result, string Body)>> sendOne = null)
        {
            _region = regionName;
            if (sendOne == null)
            {
                _admin = new JanusAdminClient(adminUri, adminToken, timeout);
                _sendOne = req => _admin.SendPluginMessageWithReplyAsync(SlvoicePlugin, req);
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
            // The grid id (S-A2A-4) only enters the "multiagent" arm; the local derivation ignores it.
            _fallbackRoom = JanusAudioBridge.CalcRoomNumber(
                string.Empty, regionId.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
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

        /// <summary>Q4: distinct MUTE-channel listeners/sources in the most recent send with no room
        /// record (fallback), parallel to the excl fallback figures. Non-zero on a mute-only op whose
        /// listener has no AgentRoomTable record; zero otherwise.</summary>
        public int LastSendMuteFallbackListeners => _lastSendMuteFallbackListeners;

        /// <summary>Q4: see <see cref="LastSendMuteFallbackListeners"/> — the mute-channel source companion.</summary>
        public int LastSendMuteFallbackSources => _lastSendMuteFallbackSources;

        /// <summary>S4: inner-reply stats from the most recent SendAsync (see <see cref="PeerCtlSendStats"/>),
        /// summed across the rooms the send addressed. Default/all-zero when the mixer reply carried no such
        /// fields (old mixer). PLUMBING only -- read by nobody today; surfaced for a future decision.</summary>
        public PeerCtlSendStats LastSendStats => new PeerCtlSendStats
        {
            RepliesParsed = _statRepliesParsed,
            DeferredListeners = _statDeferred,
            Skipped = _statSkipped,
            Entries = _statEntries,
            MuteEntries = _statMuteEntries,
            Anomalies = _statAnomalies,
        };

        public void Dispose()
        {
            _admin?.Dispose();
            _roomGate?.Dispose();
        }

        public async Task<PeerCtlSendResult> SendAsync(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
        {
            PeerCtlBatchPartition part = PeerCtlBatchPartitioner.Partition(excl, RoomOf, _fallbackRoom);
            // Exclusion-channel fallback counts keep their exact meaning; _lastSendRooms is set from the
            // CHANNEL UNION further below (a mute-only op addresses rooms the excl partition never sees).
            _lastSendFallbackListeners = part.FallbackListeners;
            _lastSendFallbackSources = part.FallbackSources;

            // ADDITIVE mute channel: partition it with the SAME per-room policy (the partitioner is
            // untouched — called a second time). Rooms present only in the mute partition are unioned
            // in below, so a mute-only op still addresses its room(s). Empty/null mute => no second
            // partition and the excl-only path is byte-for-byte the pre-mute behaviour. mutePart is
            // captured IN FULL (not just .Rooms) so its fallback counts feed the mute companions and the
            // log; muteRooms keeps its exact pre-fix value (null when empty), so the SEND LOOP below is
            // untouched (Q4: counters/logging only, no wire change).
            PeerCtlBatchPartition mutePart = (mute != null && mute.Count > 0)
                ? PeerCtlBatchPartitioner.Partition(mute, RoomOf, _fallbackRoom)
                : PeerCtlBatchPartition.Empty;
            IReadOnlyDictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> muteRooms
                = (mute != null && mute.Count > 0) ? mutePart.Rooms : null;
            _lastSendMuteFallbackListeners = mutePart.FallbackListeners;
            _lastSendMuteFallbackSources = mutePart.FallbackSources;

            // Union the room keys across both channels, preserving order-independence.
            var roomKeys = new HashSet<int>();
            foreach (int r in part.Rooms.Keys) roomKeys.Add(r);
            if (muteRooms != null)
                foreach (int r in muteRooms.Keys) roomKeys.Add(r);
            _lastSendRooms = roomKeys.Count;   // Q4: rooms addressed by EITHER channel (the true count)

            // Build EVERY body BEFORE sending any of them. The serializer's invariant throw stays
            // all-or-nothing as it was when there was one message: a zero UUID in any room aborts the
            // whole tick with nothing on the wire, rather than leaving some rooms updated and others not.
            var requests = new List<OSDMap>(roomKeys.Count);
            foreach (int roomKey in roomKeys)
            {
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> exclSlice =
                    part.Rooms.TryGetValue(roomKey, out var es) ? es : EmptyChannel;
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteSlice =
                    (muteRooms != null && muteRooms.TryGetValue(roomKey, out var ms)) ? ms : null;
                OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, exclSlice, muteSlice);
                request["room"] = new OSDInteger(roomKey);   // the sink stamps the room, per room
                requests.Add(request);
            }

            LogRoomsAddressed(op, part, mutePart, roomKeys.Count);

            if (requests.Count == 0)
            {
                RecordStats(Array.Empty<SlvoiceReply>());    // an empty send resets last stats
                return PeerCtlSendResult.Ok;                 // nothing to address is not a failure
            }

            // One room: no gate, one round-trip - byte-for-byte the pre-S3b send (plus the reply parse).
            if (requests.Count == 1)
            {
                (PeerCtlSendResult only, SlvoiceReply onlyReply) = await SendAndReadAsync(requests[0]).ConfigureAwait(false);
                RecordStats(new[] { onlyReply });
                return only;
            }

            var sends = new Task<(PeerCtlSendResult Result, SlvoiceReply Reply)>[requests.Count];
            for (int i = 0; i < requests.Count; i++)
                sends[i] = SendGatedAsync(requests[i]);
            (PeerCtlSendResult Result, SlvoiceReply Reply)[] results = await Task.WhenAll(sends).ConfigureAwait(false);

            // §2a: aggregate in severity order, most severe wins. Every room is attempted - a failure
            // in one must not suppress the others, because rooms are independent and `replace` is
            // per-listener idempotent, so the sender's re-snapshot repairs a partial send safely.
            PeerCtlSendResult worst = PeerCtlSendResult.Ok;
            var replies = new SlvoiceReply[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                replies[i] = results[i].Reply;
                if (Severity(results[i].Result) > Severity(worst))
                    worst = results[i].Result;
            }
            RecordStats(replies);
            return worst;
        }

        private async Task<(PeerCtlSendResult Result, SlvoiceReply Reply)> SendGatedAsync(OSDMap request)
        {
            await _roomGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await SendAndReadAsync(request).ConfigureAwait(false);
            }
            finally
            {
                _roomGate.Release();
            }
        }

        // Send one room's request, map the transport outcome, and (only on a janus:"success") parse and
        // LOG that room's inner slvoice reply. The PeerCtlSendResult is UNCHANGED by the inner reply: a
        // successful transport is Ok even if the inner reply reports skipped items or a non-applied
        // status -- that anomaly is reported (WARN) and surfaced in stats, never converted into a
        // transport/protocol failure (which would wrongly trip the sender's ProtocolError latch or
        // staleness guard, i.e. change behaviour). See ClassifyReply for the severity policy.
        private async Task<(PeerCtlSendResult Result, SlvoiceReply Reply)> SendAndReadAsync(OSDMap request)
        {
            (AdminSendResult admin, string body) = await _sendOne(request).ConfigureAwait(false);
            PeerCtlSendResult result = Map(admin);
            SlvoiceReply reply = default;
            if (admin == AdminSendResult.Ok)   // only a janus:"success" carries an inner slvoice reply
            {
                reply = ParseInnerReply(body);
                int room = request.TryGetValue("room", out OSD ro) ? ro.AsInteger() : 0;
                LogReply(room, in reply);
            }
            return (result, reply);
        }

        // Sum this send's per-room inner replies into the LastSendStats snapshot (volatile ints,
        // single-writer: SendAsync is single-flight from the sender).
        private void RecordStats(IReadOnlyList<SlvoiceReply> replies)
        {
            int parsed = 0, deferred = 0, skipped = 0, entries = 0, muteEntries = 0, anomalies = 0;
            for (int i = 0; i < replies.Count; i++)
            {
                SlvoiceReply r = replies[i];
                if (r.Present) parsed++;
                deferred += r.DeferredListeners;
                skipped += r.Skipped;
                entries += r.Entries;
                muteEntries += r.MuteEntries;
                (bool warn, bool _) = ClassifyReply(in r);
                if (warn) anomalies++;
            }
            _statRepliesParsed = parsed;
            _statDeferred = deferred;
            _statSkipped = skipped;
            _statEntries = entries;
            _statMuteEntries = muteEntries;
            _statAnomalies = anomalies;
        }

        // Deliberate severities (S4): deferred_listeners>0 is the join-window deferral self-heal working
        // as designed -> INFO, not a fault. skipped>0, a non-applied inner status, or a malformed inner
        // reply is real loss or protocol drift -> WARN. An absent inner reply (old mixer) or an all-zero
        // applied reply (quiet steady state) logs NOTHING -- behaviourally identical to before S4.
        public static (bool Warn, bool Info) ClassifyReply(in SlvoiceReply reply)
        {
            if (!reply.Present && !reply.Malformed)
                return (false, false);   // absent: no info, no log (old mixer / empty)
            bool applied = reply.Present && string.Equals(reply.Status, "applied", StringComparison.Ordinal);
            bool warn = reply.Malformed || !applied || reply.Skipped > 0;
            bool info = reply.DeferredListeners > 0;
            return (warn, info);
        }

        private void LogReply(int room, in SlvoiceReply reply)
        {
            (bool warn, bool info) = ClassifyReply(in reply);
            if (warn)
                m_log.LogWarning("{LogHeader} region {RegionName} room {Room}: peer_ctl_batch inner-reply anomaly " +
                    "(status={Status}, skipped={Skipped}, malformed={Malformed}) [{Raw}]",
                    LogHeader, _region, room, reply.Status ?? "(none)", reply.Skipped, reply.Malformed, reply.RawSummary);
            if (info)
                m_log.LogInformation("{LogHeader} region {RegionName} room {Room}: mixer deferred {Deferred} listener " +
                    "entr(y/ies) for not-yet-joined listener(s) (join-window self-heal; replays on join, not a fault)",
                    LogHeader, _region, room, reply.DeferredListeners);
        }

        /// <summary>Parsed mixer inner reply for one room (S4). All-default = absent (old mixer): logs
        /// nothing, contributes zero. Public so the pure ParseInnerReply / ClassifyReply are unit-testable.</summary>
        public struct SlvoiceReply
        {
            public bool Present;              // an inner "response" object was found
            public bool Malformed;            // janus:success but the inner reply is not the expected shape
            public string Status;             // inner "slvoice" value ("applied"/"error"/"empty"/...), or null
            public int Entries;
            public int MuteEntries;
            public int Skipped;
            public int DeferredListeners;
            public string RawSummary;         // compact inner payload for the WARN log
        }

        /// <summary>Parse the mixer's peer_ctl_batch inner reply. The plugin response is nested under the
        /// top-level "response" key: {janus:success, response:{slvoice, op, room, entries, mute_entries,
        /// skipped, deferred_listeners}} (mixer janus_slvoice.c:1552-1557 + deferred_listeners from 27977c8).
        /// EVERY inner field is optional: absent (old / pre-mute mixer) yields a default (Present=false)
        /// reply that logs nothing and contributes zero -- behaviourally identical to before S4. A
        /// janus:"success" with no/!object "response", or a "response" with no "slvoice", is flagged
        /// Malformed (protocol drift). Pure and unit-testable.</summary>
        public static SlvoiceReply ParseInnerReply(string body)
        {
            OSDMap top;
            try { top = OSDParser.DeserializeJson(body ?? string.Empty) as OSDMap; }
            catch { top = null; }
            if (top == null)
                return default;   // unparseable/empty at the top level: the transport layer already handled it
            if (!(top.TryGetValue("response", out OSD ro) && ro is OSDMap resp))
                return new SlvoiceReply { Present = false, Malformed = true, RawSummary = Summarize(top) };
            string status = resp.TryGetValue("slvoice", out OSD st) ? st.AsString() : null;
            return new SlvoiceReply
            {
                Present = true,
                Malformed = string.IsNullOrEmpty(status),
                Status = status,
                Entries = resp.TryGetValue("entries", out OSD e) ? e.AsInteger() : 0,
                MuteEntries = resp.TryGetValue("mute_entries", out OSD me) ? me.AsInteger() : 0,
                Skipped = resp.TryGetValue("skipped", out OSD sk) ? sk.AsInteger() : 0,
                DeferredListeners = resp.TryGetValue("deferred_listeners", out OSD dl) ? dl.AsInteger() : 0,
                RawSummary = Summarize(resp),
            };
        }

        // Compact, capped one-line summary of an inner reply map for the WARN log (a surprise payload
        // must not flood the log).
        private static string Summarize(OSDMap map)
        {
            if (map == null) return "(none)";
            string str = map.ToString();
            const int capLen = 300;
            return str.Length <= capLen ? str : str.Substring(0, capLen) + "\u2026";
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
        // Q4: report BOTH channels. "addressed N" is the room UNION (rooms that got any channel); the
        // per-room breakdown shows excl+mute listener counts, and both channels' fallback counts are
        // logged. Without this a mute-only op logged "addressed 0 rooms / fallback 0" while the mute
        // was delivered to its room (the 2026-08-28 trace).
        private void LogRoomsAddressed(VisOp op, PeerCtlBatchPartition part, PeerCtlBatchPartition mutePart, int roomsAddressed)
        {
            if (!m_log.IsEnabled(LogLevel.Debug))
                return;

            var roomKeys = new SortedSet<int>();
            foreach (int r in part.Rooms.Keys) roomKeys.Add(r);
            foreach (int r in mutePart.Rooms.Keys) roomKeys.Add(r);

            var sb = new StringBuilder();
            foreach (int roomKey in roomKeys)
            {
                int exclCount = part.Rooms.TryGetValue(roomKey, out var es) ? es.Count : 0;
                int muteCount = mutePart.Rooms.TryGetValue(roomKey, out var ms) ? ms.Count : 0;
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(roomKey).Append(":excl").Append(exclCount).Append("+mute").Append(muteCount);
            }
            if (sb.Length == 0)
                sb.Append("none");

            m_log.LogDebug("{LogHeader} region {RegionName}: {Op} addressed {RoomCount} room(s) [room:excl+mute {Rooms}]; " +
                "fallback excl listeners {FbExclL}/sources {FbExclS}, mute listeners {FbMuteL}/sources {FbMuteS} (fallback room {FallbackRoom})",
                LogHeader, _region, PeerCtlBatchSerializer.OpString(op), roomsAddressed, sb.ToString(),
                part.FallbackListeners, part.FallbackSources, mutePart.FallbackListeners, mutePart.FallbackSources, _fallbackRoom);
        }
    }
}
