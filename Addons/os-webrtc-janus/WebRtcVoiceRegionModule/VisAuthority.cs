/*
 * Phase 0 slice 0.2 — the sim's authority over its mixer rooms (Docs/voice/nonspatial-phase0-design.md §1-§3).
 *
 * One instance per VoiceVisibilityService, built only when [WebRtcVoice] VisibilityArmingEnabled is true (default
 * false). With it absent, the sender and the sink run exactly the pre-0.2 code paths (golden-file tested).
 *
 * It owns:
 *  - room_epoch (§1.1): one per service start, (unix_ms << 16) | random16, sent as 16 lowercase hex digits. A
 *    process-wide guard keeps it strictly increasing, so a restarted feeder can never reuse its predecessor's value;
 *  - policy_generation (§1.2): one counter per room, advanced for every batch sent to that room;
 *  - listener_generation (§1.3): per (room, listener), set when a batch naming that listener succeeds. A listener is
 *    ARMED at a room while it holds a generation there;
 *  - what the mixer told us (§2 item 4): its mixer_instance, whether it speaks vis_protocol 2 (heartbeats are sent
 *    only then, §6.3), and which listeners it reported stale or unarmed;
 *  - the heartbeat body (§3).
 *
 * The decision table for mixer replies (see ApplyOutcomeLocked / OnHeartbeatOutcome):
 *   transport failure                      -> nothing here; the sender re-snapshots, which re-arms everyone
 *   first mixer_instance seen              -> remember it
 *   mixer_instance changed                 -> the mixer restarted: disarm everything, re-arm every listener, all rooms
 *   vis_protocol >= 2                      -> heartbeats start; a heartbeat answered with anything else stops them
 *   status/reason unknown_room             -> disarm that room's listeners, retry after UnknownRoomRetryMs or at the
 *                                             listener's next provision
 *   status stale_epoch                     -> the same backoff (not in the design's table: a newer authority holds the
 *                                             room, so the sim retries rather than escalating its epoch)
 *   stale_listeners / unarmed_listeners    -> disarm and re-arm exactly those listeners
 *   applied (or no inner reply)            -> a replace arms each named listener; an add/remove advances an armed one
 *   any other status, or a malformed reply -> not armed; retried after the backoff
 *
 * Thread-safety: batch outcomes arrive on send continuations, heartbeat outcomes on their own, and the sender reads on
 * its run path, so every member takes _lock.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    /// <summary>The heartbeat transport seam (§3). Implemented by JanusPeerCtlBatchSink; the sender uses it only in
    /// arming mode, so IPeerCtlBatchSink and its test doubles are unchanged.</summary>
    public interface IPeerCtlHeartbeatSink
    {
        /// <summary>Send one peer_ctl_heartbeat body. True when the transport succeeded (janus:"success").</summary>
        Task<bool> SendHeartbeatAsync(OSDMap body);
    }

    public sealed class VisAuthority
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[VISIBILITY AUTHORITY]";

        /// <summary>§3: the heartbeat interval, a sim constant (4 feeder ticks at 250 ms).</summary>
        public const int HeartbeatIntervalMs = 1000;

        /// <summary>How long a listener whose room the mixer does not know (or refuses as stale_epoch) waits before
        /// the sender arms it again. One heartbeat interval; a provision of that listener clears it at once.</summary>
        public const int UnknownRoomRetryMs = 1000;

        /// <summary>§6.3: the reply vis_protocol at which the mixer understands peer_ctl_heartbeat.</summary>
        public const int HeartbeatProtocol = 2;

        /// <summary>A re-arm request for an agent that never shows up in the population is dropped after this long.</summary>
        public const int RearmRequestTtlMs = 60000;

        private static long s_lastEpoch;

        private readonly object _lock = new object();
        private readonly Func<long> _nowMs;
        private readonly string _region;
        private readonly Dictionary<int, uint> _roomGen = new Dictionary<int, uint>();
        private readonly Dictionary<int, Dictionary<UUID, uint>> _armed = new Dictionary<int, Dictionary<UUID, uint>>();
        private readonly Dictionary<UUID, long> _retryAt = new Dictionary<UUID, long>();
        private readonly Dictionary<UUID, long> _rearm = new Dictionary<UUID, long>();   // listener -> requested at
        private bool _rearmAll;
        private string _mixerInstance;
        private bool _heartbeatCapable;

        public VisAuthority(ulong epoch, string region = null, Func<long> nowMs = null)
        {
            Epoch = epoch;
            EpochString = FormatEpoch(epoch);
            _region = region ?? "?";
            _nowMs = nowMs ?? (() => Environment.TickCount64);
        }

        /// <summary>§1.1: a new epoch, strictly greater than any earlier one in this process.</summary>
        public static ulong NewEpoch(long unixMs, int random16)
        {
            ulong candidate = ((ulong)unixMs << 16) | (ushort)random16;
            while (true)
            {
                long last = Interlocked.Read(ref s_lastEpoch);
                ulong next = candidate > (ulong)last ? candidate : (ulong)last + 1;
                if (Interlocked.CompareExchange(ref s_lastEpoch, (long)next, last) == last)
                    return next;
            }
        }

        /// <summary>A new epoch from the wall clock and a random low 16 bits.</summary>
        public static ulong NewEpoch()
            => NewEpoch(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Random.Shared.Next(0, 65536));

        public static string FormatEpoch(ulong epoch) => epoch.ToString("x16");

        public ulong Epoch { get; }

        /// <summary>The epoch as carried on the wire: 16 lowercase hex digits.</summary>
        public string EpochString { get; }

        public bool HeartbeatCapable { get { lock (_lock) return _heartbeatCapable; } }

        public string MixerInstance { get { lock (_lock) return _mixerInstance; } }

        // ---- generations ------------------------------------------------------------------------

        /// <summary>The next policy_generation for a room (the first is 1).</summary>
        public uint NextGeneration(int room)
        {
            uint next;
            lock (_lock)
            {
                _roomGen.TryGetValue(room, out uint g);
                _roomGen[room] = ++g;
                next = g;
            }
            // Slice 0.4 (§11.5): publish the arming state this room has reached, so a join capability minted for it
            // carries the same (epoch, generation) and dies with this authority. The join happens in another
            // assembly, which is why this goes through a process-wide table rather than a reference.
            JoinCapabilityAuthority.Publish(room, EpochString, next);
            return next;
        }

        public uint CurrentGeneration(int room)
        {
            lock (_lock)
                return _roomGen.TryGetValue(room, out uint g) ? g : 0;
        }

        /// <summary>The listener's generation at a room; 0 when it is not armed there.</summary>
        public uint ListenerGeneration(int room, UUID listener)
        {
            lock (_lock)
                return _armed.TryGetValue(room, out var m) && m.TryGetValue(listener, out uint g) ? g : 0;
        }

        public bool IsArmed(int room, UUID listener) => ListenerGeneration(room, listener) != 0;

        // ---- re-arm requests --------------------------------------------------------------------

        /// <summary>§2 item 2: a provisioned listener is armed at its recorded room, even if already armed, and any
        /// unknown-room backoff for it ends.</summary>
        public void RequestArm(UUID listener)
        {
            if (listener == UUID.Zero)
                return;
            lock (_lock)
            {
                _rearm[listener] = _nowMs();
                _retryAt.Remove(listener);
            }
        }

        public bool IsRearmRequested(UUID listener)
        {
            lock (_lock)
                return _rearm.ContainsKey(listener);
        }

        /// <summary>True once after a mixer restart was detected: the sender arms every listener in every room.</summary>
        public bool TakeRearmAll()
        {
            lock (_lock)
            {
                bool all = _rearmAll;
                _rearmAll = false;
                return all;
            }
        }

        /// <summary>False while a listener is backing off after unknown_room / stale_epoch.</summary>
        public bool CanArmNow(UUID listener)
        {
            lock (_lock)
                return !_retryAt.TryGetValue(listener, out long at) || _nowMs() >= at;
        }

        /// <summary>Forget arming for agents that left the population or moved room (§2 item 5 re-arms them at the
        /// new room), and drop re-arm requests that never met an agent.</summary>
        public void PruneTo(IReadOnlyCollection<UUID> population, Func<UUID, int> resolveRoom)
        {
            var present = new HashSet<UUID>(population);
            lock (_lock)
            {
                foreach (KeyValuePair<int, Dictionary<UUID, uint>> room in _armed)
                {
                    List<UUID> gone = null;
                    foreach (UUID l in room.Value.Keys)
                        if (!present.Contains(l) || resolveRoom(l) != room.Key)
                            (gone ??= new List<UUID>()).Add(l);
                    if (gone != null)
                        foreach (UUID l in gone)
                            room.Value.Remove(l);
                }
                long now = _nowMs();
                List<UUID> stale = null;
                foreach (KeyValuePair<UUID, long> r in _rearm)
                    if (!present.Contains(r.Key) && now - r.Value > RearmRequestTtlMs)
                        (stale ??= new List<UUID>()).Add(r.Key);
                if (stale != null)
                    foreach (UUID l in stale)
                        _rearm.Remove(l);
                List<UUID> absent = null;
                foreach (UUID l in _retryAt.Keys)
                    if (!present.Contains(l))
                        (absent ??= new List<UUID>()).Add(l);
                if (absent != null)
                    foreach (UUID l in absent)
                        _retryAt.Remove(l);
            }
        }

        // ---- outcomes ---------------------------------------------------------------------------

        /// <summary>A peer_ctl_batch to one room resolved. Applies the decision table to the named listeners.</summary>
        public void OnBatchOutcome(int room, VisOp op, uint generation, IReadOnlyCollection<UUID> named, bool transportOk,
            in JanusPeerCtlBatchSink.SlvoiceReply reply)
        {
            if (!transportOk)
                return;   // the sender goes unsynced and its snapshot re-arms every listener
            lock (_lock)
            {
                ObserveMixerLocked(reply.VisProtocol, reply.MixerInstance, "peer_ctl_batch");
                string outcome = BatchOutcome(in reply);
                if (outcome != "applied")
                {
                    BackOffLocked(room, named, outcome);
                    return;
                }
                var flagged = new HashSet<UUID>();
                AddAll(flagged, reply.StaleListeners);
                AddAll(flagged, reply.UnarmedListeners);
                Dictionary<UUID, uint> armed = ArmedLocked(room);
                foreach (UUID l in named)
                {
                    if (flagged.Contains(l))
                        continue;
                    if (op == VisOp.Replace)
                    {
                        armed[l] = generation;
                        _rearm.Remove(l);
                        _retryAt.Remove(l);
                    }
                    else if (armed.ContainsKey(l))
                    {
                        armed[l] = generation;
                    }
                }
                FlagLocked(room, flagged, "peer_ctl_batch");
            }
        }

        /// <summary>A peer_ctl_heartbeat resolved (§3 reply).</summary>
        public void OnHeartbeatOutcome(bool transportOk, HeartbeatReply reply)
        {
            if (!transportOk)
                return;
            lock (_lock)
            {
                if (!reply.IsHeartbeat)
                {
                    if (_heartbeatCapable)
                        m_log.LogWarning("{LogHeader} region {Region}: the mixer did not answer peer_ctl_heartbeat as a heartbeat " +
                            "({Reply}); heartbeats stop until a reply advertises vis_protocol >= {Protocol}",
                            LogHeader, _region, reply.RawSummary ?? "(no reply)", HeartbeatProtocol);
                    _heartbeatCapable = false;
                    return;
                }
                ObserveMixerLocked(reply.VisProtocol, reply.MixerInstance, "peer_ctl_heartbeat");
                foreach (KeyValuePair<int, HeartbeatRoomReply> room in reply.Rooms)
                {
                    string status = room.Value.Status ?? "ok";
                    if (status == "unknown_room" || status == "stale_epoch")
                    {
                        List<UUID> inRoom = _armed.TryGetValue(room.Key, out var m) ? new List<UUID>(m.Keys) : new List<UUID>();
                        BackOffLocked(room.Key, inRoom, status);
                        continue;
                    }
                    var flagged = new HashSet<UUID>();
                    AddAll(flagged, room.Value.StaleListeners);
                    AddAll(flagged, room.Value.UnarmedListeners);
                    FlagLocked(room.Key, flagged, "peer_ctl_heartbeat");
                }
            }
        }

        private static string BatchOutcome(in JanusPeerCtlBatchSink.SlvoiceReply reply)
        {
            if (reply.Reason == "unknown_room" || reply.StatusField == "unknown_room")
                return "unknown_room";
            if (reply.StatusField == "stale_epoch" || reply.Reason == "stale_epoch")
                return "stale_epoch";
            if (!reply.Present && !reply.Malformed)
                return "applied";   // no inner reply at all (a transport that returns none): trust the transport
            if (reply.Present && reply.Status == "applied")
                return "applied";
            return reply.Malformed ? "malformed" : (reply.Status ?? "not applied");
        }

        private void ObserveMixerLocked(int visProtocol, string instance, string via)
        {
            if (visProtocol >= HeartbeatProtocol && !_heartbeatCapable)
            {
                _heartbeatCapable = true;
                m_log.LogInformation("{LogHeader} region {Region}: the mixer advertises vis_protocol {Protocol} (via {Via}); " +
                    "heartbeats start, every {Interval} ms, epoch {Epoch}", LogHeader, _region, visProtocol, via, HeartbeatIntervalMs, EpochString);
            }
            if (string.IsNullOrEmpty(instance))
                return;
            if (_mixerInstance == null)
            {
                _mixerInstance = instance;
                m_log.LogInformation("{LogHeader} region {Region}: mixer_instance {Instance} (first seen, via {Via})",
                    LogHeader, _region, instance, via);
            }
            else if (!string.Equals(_mixerInstance, instance, StringComparison.Ordinal))
            {
                m_log.LogWarning("{LogHeader} region {Region}: mixer_instance changed {Old} -> {New} (via {Via}): the mixer " +
                    "restarted and holds no arming; re-arming every listener in every room, epoch {Epoch}",
                    LogHeader, _region, _mixerInstance, instance, via, EpochString);
                _mixerInstance = instance;
                _armed.Clear();
                _retryAt.Clear();
                _rearmAll = true;
            }
        }

        private void BackOffLocked(int room, IEnumerable<UUID> listeners, string why)
        {
            long retryAt = _nowMs() + UnknownRoomRetryMs;
            Dictionary<UUID, uint> armed = ArmedLocked(room);
            int n = 0;
            foreach (UUID l in listeners)
            {
                armed.Remove(l);
                _retryAt[l] = retryAt;
                n++;
            }
            m_log.LogInformation("{LogHeader} region {Region} room {Room}: {Why}; {Count} listener(s) not armed, retrying in {Retry} ms " +
                "or at the listener's next provision", LogHeader, _region, room, why, n, UnknownRoomRetryMs);
        }

        private void FlagLocked(int room, HashSet<UUID> flagged, string via)
        {
            if (flagged.Count == 0)
                return;
            Dictionary<UUID, uint> armed = ArmedLocked(room);
            long now = _nowMs();
            foreach (UUID l in flagged)
            {
                armed.Remove(l);
                _rearm[l] = now;
                _retryAt.Remove(l);
            }
            m_log.LogInformation("{LogHeader} region {Region} room {Room}: the mixer reported {Count} stale/unarmed listener(s) " +
                "(via {Via}); re-arming them", LogHeader, _region, room, flagged.Count, via);
        }

        private Dictionary<UUID, uint> ArmedLocked(int room)
        {
            if (!_armed.TryGetValue(room, out Dictionary<UUID, uint> m))
                _armed[room] = m = new Dictionary<UUID, uint>();
            return m;
        }

        private static void AddAll(HashSet<UUID> set, UUID[] items)
        {
            if (items != null)
                foreach (UUID l in items)
                    set.Add(l);
        }

        // ---- heartbeat (§3) ---------------------------------------------------------------------

        /// <summary>One peer_ctl_heartbeat for every room the population resolves to, each listing every listener
        /// addressed there with its generation (0 = not armed), empty columns included.</summary>
        public OSDMap BuildHeartbeat(IReadOnlyCollection<UUID> population, Func<UUID, int> resolveRoom, bool stopping)
        {
            var byRoom = new SortedDictionary<int, List<UUID>>();
            foreach (UUID l in population)
            {
                int room = resolveRoom(l);
                if (!byRoom.TryGetValue(room, out List<UUID> list))
                    byRoom[room] = list = new List<UUID>();
                list.Add(l);
            }
            var rooms = new OSDMap();
            lock (_lock)
            {
                foreach (KeyValuePair<int, List<UUID>> room in byRoom)
                {
                    var listeners = new OSDMap();
                    _armed.TryGetValue(room.Key, out Dictionary<UUID, uint> armed);
                    foreach (UUID l in room.Value)
                        listeners[l.ToString()] = OSD.FromInteger(armed != null && armed.TryGetValue(l, out uint g) ? (int)g : 0);
                    rooms[room.Key.ToString()] = new OSDMap
                    {
                        ["policy_generation"] = OSD.FromInteger(_roomGen.TryGetValue(room.Key, out uint rg) ? (int)rg : 0),
                        ["listeners"] = listeners,
                    };
                }
            }
            var body = new OSDMap
            {
                ["request"] = OSD.FromString("peer_ctl_heartbeat"),
                ["room_epoch"] = OSD.FromString(EpochString),
                ["interval_ms"] = OSD.FromInteger(HeartbeatIntervalMs),
                ["rooms"] = rooms,
            };
            if (stopping)
                body["state"] = OSD.FromString("stopping");
            return body;
        }

        public struct HeartbeatRoomReply
        {
            public string Status;
            public UUID[] UnarmedListeners;
            public UUID[] StaleListeners;
        }

        public sealed class HeartbeatReply
        {
            public bool IsHeartbeat;
            public int VisProtocol;
            public string MixerInstance;
            public readonly Dictionary<int, HeartbeatRoomReply> Rooms = new Dictionary<int, HeartbeatRoomReply>();
            public string RawSummary;
        }

        /// <summary>Parse the inner reply to a heartbeat: {janus:success, response:{slvoice:"heartbeat", vis_protocol,
        /// mixer_instance, rooms:{R:{status, unarmed_listeners, stale_listeners}}}}. Anything else is IsHeartbeat=false.</summary>
        public static HeartbeatReply ParseHeartbeatReply(string body)
        {
            var reply = new HeartbeatReply();
            OSDMap top;
            try { top = OSDParser.DeserializeJson(body ?? string.Empty) as OSDMap; }
            catch { top = null; }
            if (top == null || !(top.TryGetValue("response", out OSD ro) && ro is OSDMap resp))
                return reply;
            string summary = resp.ToString();
            reply.RawSummary = summary.Length <= 300 ? summary : summary.Substring(0, 300) + "…";
            if (!(resp.TryGetValue("slvoice", out OSD sv) && sv.AsString() == "heartbeat"))
                return reply;
            reply.IsHeartbeat = true;
            reply.VisProtocol = resp.TryGetValue("vis_protocol", out OSD vp) ? vp.AsInteger() : 0;
            reply.MixerInstance = resp.TryGetValue("mixer_instance", out OSD mi) ? mi.AsString() : null;
            if (resp.TryGetValue("rooms", out OSD rs) && rs is OSDMap rooms)
            {
                foreach (KeyValuePair<string, OSD> kv in rooms)
                {
                    if (!int.TryParse(kv.Key, out int room) || !(kv.Value is OSDMap entry))
                        continue;
                    reply.Rooms[room] = new HeartbeatRoomReply
                    {
                        Status = entry.TryGetValue("status", out OSD st) ? st.AsString() : null,
                        UnarmedListeners = JanusPeerCtlBatchSink.ParseUuidList(entry, "unarmed_listeners"),
                        StaleListeners = JanusPeerCtlBatchSink.ParseUuidList(entry, "stale_listeners"),
                    };
                }
            }
            return reply;
        }
    }
}
