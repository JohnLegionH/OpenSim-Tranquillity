/*
 * VisibilityBatchSender — orchestrates emission of the feeder's per-tick VisibilityBatch to the
 * mixer through an IPeerCtlBatchSink. Backend-agnostic (no Janus / no room number here — the sink
 * stamps the room). Driven off the VoiceVisibilityService tick via Pump(): fire-and-forget, never
 * awaited on the tick thread, never throws into it, single-flight.
 *
 * Emission paths (mixer-feed-protocol.md §3.2 / §3.3.1):
 *  - steady-state DELTA: <=1 add message + 1 remove message per tick (the bound is per-op §3.3.1);
 *  - SNAPSHOT (replace-all) on (re)connect / full-rebuild: sets _synced + _knownListeners, with
 *    clear-tracking — a listener dropped from Current gets an explicit empty replace, because op
 *    scoping is per-listener and omission is NOT a clear (§3.3.1);
 *  - per-listener JOIN replace (pending set): a DISTINCT path — it does NOT set _synced, NOT reset
 *    _knownListeners, NOT send the room. Triggered by WebRTC provisioning-success. Because the mixer
 *    silently drops a batch entry for a listener not yet in the room (:958) and exposes no admin
 *    room-membership query, this uses BOUNDED BLIND RE-SEND: the listener's replace re-sent once per
 *    tick up to PendingJoinMaxAttempts, then one loud give-up log (the silent-drop failure, made
 *    loud on our side). replace is listener-scoped + idempotent, so re-sends are safe.
 *
 * A ProtocolError from the sink is a config/format error (e.g. wrong AdminAPIToken / wrong plugin
 * name / broken transport in front). It latches emission off (per-region, cleared only by a service
 * rebuild / region-server restart) with one loud log — but only after ProtocolErrorLatchThreshold
 * CONSECUTIVE ProtocolErrors, not the first. Any Ok resets the run to zero; a persistent fault trips
 * all K within a second, while a lone transient stray-200 no longer permanently disables a region.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VisibilityBatchSender
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(VisibilityBatchSender));
        private const string LogHeader = "[VISIBILITY SENDER]";

        /// <summary>Bounded blind re-sends of a joining listener's replace — no admin membership
        /// query exists to confirm presence, so we re-send this many ticks then give up loudly.</summary>
        public const int PendingJoinMaxAttempts = 6;

        /// <summary>Consecutive ProtocolErrors before emission latches off. Not the first: a single
        /// transient stray-200 must not permanently disable a region. Every fault that actually
        /// reaches ProtocolError (wrong admin_secret / wrong plugin name / broken transport in front)
        /// is persistent, so it trips all K within a second (a few ticks); a one-off does not.</summary>
        public const int ProtocolErrorLatchThreshold = 3;

        /// <summary>Self-heal a send stuck in-flight after this many admin-timeouts. Set to 8x so it
        /// sits strictly beyond JanusAdminClient's 4x HttpClient backstop — a send still in flight at
        /// 8x means the completion path itself failed (the finally never ran), not just a slow send.
        /// This turns the permanent silent wedge into a bounded, logged hiccup.</summary>
        public const int StaleInFlightMultiple = 8;

        private readonly IVisibilityFeed _feed;
        private readonly IPeerCtlBatchSink _sink;   // null => no sink registered; no-op (logged once)
        private readonly bool _enabled;

        // Single-flight is now epoch-based: 0 = idle; otherwise the epoch id of the in-flight send.
        // The finally clears the flag via CAS-against-its-own-epoch, so a late-completing abandoned
        // send (force-cleared by the staleness guard) can never clear a NEWER send's ownership.
        private long _sendInFlight;                 // 0 or the in-flight send's epoch (Interlocked)
        private long _sendEpochSeq;                 // monotonic; each acquire claims ++ as its epoch id
        private long _sendStartedAtMs;              // clock when the current send acquired (advisory; see guard)
        private volatile bool _synced;
        private volatile bool _protocolFailed;      // latched after K consecutive ProtocolErrors; stops emission
        private int _consecutiveProtocolErrors;     // touched only on RunAsync's thread (single-flight), like _knownListeners
        private bool _loggedNoSink;

        private readonly long _staleThresholdMs;    // force-heal an in-flight send after this long (StaleInFlightMultiple x admin)
        private readonly long _adminTimeoutMs;      // for the guard log
        private readonly string _region;            // for the guard log
        private readonly Func<long> _nowMs;         // monotonic clock (injectable for tests)

        private readonly HashSet<UUID> _knownListeners = new HashSet<UUID>();   // touched only on RunAsync's thread
        private readonly object _pendingLock = new object();
        private readonly Dictionary<UUID, int> _pending = new Dictionary<UUID, int>();   // listener -> attempts left

        public VisibilityBatchSender(IVisibilityFeed feed, IPeerCtlBatchSink sink, bool enabled,
            TimeSpan? adminTimeout = null, string region = null, Func<long> nowMs = null)
        {
            _feed = feed;
            _sink = sink;
            _enabled = enabled;
            _region = region ?? "?";
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            TimeSpan admin = (adminTimeout is TimeSpan t && t > TimeSpan.Zero) ? t : TimeSpan.FromSeconds(5);
            _adminTimeoutMs = (long)admin.TotalMilliseconds;
            _staleThresholdMs = _adminTimeoutMs * StaleInFlightMultiple;
        }

        /// <summary>Trigger (correction 1): call on WebRTC provisioning-success for a listener. Adds
        /// it to the pending-join set so its full column is (re)sent until present / attempts exhaust.
        /// Distinct from the recovery triggers.</summary>
        public void OnListenerProvisioned(UUID listener)
        {
            if (!_enabled || _protocolFailed || listener == UUID.Zero)
                return;
            lock (_pendingLock)
                _pending[listener] = PendingJoinMaxAttempts;
        }

        /// <summary>Called once per feeder tick with that tick's batch. Fire-and-forget; never blocks
        /// or throws on the tick thread. Single-flight: while a send is in flight, skip this tick and
        /// force a snapshot next (a skipped delta must not cause drift).</summary>
        public void Pump(VisibilityBatch batch) => _ = PumpAsync(batch);   // fire-and-forget on the tick thread

        /// <summary>The awaitable core of Pump — production uses the fire-and-forget void overload;
        /// tests await this for determinism. Returns a completed task when it no-ops or is skipped by
        /// single-flight; otherwise the send task (which clears the in-flight flag in its finally).</summary>
        public Task PumpAsync(VisibilityBatch batch)
        {
            if (!_enabled || _protocolFailed)
                return Task.CompletedTask;
            if (_sink == null)
            {
                LogNoSinkOnce();
                return Task.CompletedTask;
            }
            long myEpoch = Interlocked.Increment(ref _sendEpochSeq);   // >=1, unique to this attempt
            if (Interlocked.CompareExchange(ref _sendInFlight, myEpoch, 0L) != 0L)
            {
                // A send is already in flight. Self-heal if it has been stuck far longer than
                // possible (FIX 2) — neither the per-call token nor the HttpClient backstop resolved it.
                ForceClearStalledSend();
                _synced = false;   // a skipped tick -> snapshot next
                return Task.CompletedTask;
            }
            // Acquired this epoch. Record the start AFTER claiming the flag (still on the pump/tick
            // thread, so no other Pump interleaves before this write).
            Volatile.Write(ref _sendStartedAtMs, _nowMs());
            return RunAsync(batch, myEpoch);   // NOT awaited by Pump (the void wrapper); tests may await it
        }

        private async Task RunAsync(VisibilityBatch batch, long epoch)
        {
            try
            {
                await DrainPendingAsync().ConfigureAwait(false);
                await EmitMainAsync(batch).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Never throw into the tick. EnsureDisjoint's feeder-bug throw lands here too — its
                // message names the offending (listener, source).
                m_log.Error($"{LogHeader} emit failed (matrix kept, will re-derive)", e);
            }
            finally
            {
                // Release ONLY if we still own the flag. If the staleness guard force-cleared us and a
                // newer send took over, _sendInFlight holds a different epoch and this CAS no-ops — so
                // a late completion of an abandoned send cannot clear a newer send's ownership.
                Interlocked.CompareExchange(ref _sendInFlight, 0L, epoch);
            }
        }

        // FIX 2: self-heal a send stuck in-flight far longer than possible. Called on a skipped Pump
        // (a send is already in flight). Logs once per stall episode — the CAS-clear resolves the
        // stall, so the next detection is a fresh episode — never once per tick.
        private void ForceClearStalledSend()
        {
            long inflight = Interlocked.Read(ref _sendInFlight);
            if (inflight == 0L)
                return;   // raced to idle; nothing in flight
            long elapsed = _nowMs() - Volatile.Read(ref _sendStartedAtMs);
            if (elapsed <= _staleThresholdMs)
                return;   // slow but within budget; a normal single-flight skip

            // Torn-read safety: the timestamp read above may belong to a NEWER send if the in-flight
            // send rotated between our two reads. We neutralise that by force-clearing with a CAS
            // against the epoch we measured — if the epoch moved, the CAS fails and we do nothing (no
            // erroneous clear, no log). The flag+epoch is a single Interlocked long, so there is no
            // torn STATE; the separate timestamp is advisory and any staleness is caught by this CAS.
            if (Interlocked.CompareExchange(ref _sendInFlight, 0L, inflight) != inflight)
                return;

            m_log.ErrorFormat("{0} region {1}: peer_ctl_batch send stuck in-flight {2}ms (self-heal " +
                "threshold {3}ms = {4}x the {5}ms admin timeout). Emission was STALLED — neither the " +
                "per-call token nor the HttpClient backstop resolved it. Force-clearing the in-flight " +
                "flag and re-syncing (snapshot next); the abandoned send is left to complete or hang " +
                "harmlessly.", LogHeader, _region, elapsed, _staleThresholdMs, StaleInFlightMultiple, _adminTimeoutMs);
            _synced = false;   // abandoned send's applied-state is unknown -> full snapshot next
        }

        // ---- per-listener JOIN path (bounded blind re-send; distinct from _synced/_knownListeners) ----
        private async Task DrainPendingAsync()
        {
            List<UUID> due;
            lock (_pendingLock)
                due = new List<UUID>(_pending.Keys);
            foreach (UUID listener in due)
            {
                var one = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = ColumnFor(listener) };
                PeerCtlSendResult r = await _sink.SendAsync(VisOp.Replace, one).ConfigureAwait(false);
                if (r == PeerCtlSendResult.ProtocolError)
                {
                    NoteProtocolError("per-listener join replace");   // may or may not latch (K consecutive)
                    return;   // stop this tick's drain either way; do not count the attempt down (it did not apply)
                }
                if (r == PeerCtlSendResult.Ok)
                    NoteOk();
                bool giveUp = false;
                lock (_pendingLock)
                {
                    if (!_pending.TryGetValue(listener, out int left))
                        continue;   // removed elsewhere
                    // Ok != applied (listener may not be in the room yet); count down regardless.
                    left--;
                    if (left <= 0)
                    {
                        _pending.Remove(listener);
                        giveUp = true;
                    }
                    else
                    {
                        _pending[listener] = left;
                    }
                }
                if (giveUp)
                    m_log.WarnFormat("{0} listener {1}: full column re-sent {2}x but never confirmed in the room; " +
                        "GIVING UP — its exclusions may be absent at the mixer (silent-drop, made loud here)",
                        LogHeader, listener, PendingJoinMaxAttempts);
            }
        }

        // ---- snapshot / delta main path ----
        private async Task EmitMainAsync(VisibilityBatch batch)
        {
            if (!_synced)
            {
                await SendSnapshotAsync().ConfigureAwait(false);
                return;
            }

            // Steady-state delta: at most one add + one remove message (per-op bound, §3.3.1).
            PeerCtlBatchSerializer.EnsureDisjoint(batch.Added, batch.Removed);
            bool ok = true;
            if (batch.Added.Count > 0)
                ok = await SendMappedAsync(VisOp.Add, batch.Added).ConfigureAwait(false);
            if (ok && batch.Removed.Count > 0)
                ok = await SendMappedAsync(VisOp.Remove, batch.Removed).ConfigureAwait(false);
            if (ok)
                RefreshKnownListeners();
        }

        private async Task SendSnapshotAsync()
        {
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            VisibilityMatrix cur = _feed.Current;
            var nowListeners = new HashSet<UUID>();
            foreach (UUID listener in cur.Listeners)
            {
                excl[listener] = new List<UUID>(cur.ExcludedFor(listener));
                nowListeners.Add(listener);
            }
            // Clear-tracking (load-bearing, §3.3.1): a listener we previously sent that is no longer
            // excluded must be reset with an EXPLICIT empty list — omission is not a clear.
            foreach (UUID listener in _knownListeners)
                if (!nowListeners.Contains(listener))
                    excl[listener] = Array.Empty<UUID>();

            if (excl.Count == 0)
            {
                _synced = true;   // nothing to (re)send and nothing to clear
                return;
            }

            PeerCtlSendResult r = await _sink.SendAsync(VisOp.Replace, excl).ConfigureAwait(false);
            switch (r)
            {
                case PeerCtlSendResult.Ok:
                    NoteOk();
                    _synced = true;
                    _knownListeners.Clear();
                    foreach (UUID l in nowListeners)
                        _knownListeners.Add(l);
                    break;
                case PeerCtlSendResult.TransportError:
                    _synced = false;   // stay unsynced; retry snapshot next tick
                    break;
                case PeerCtlSendResult.ProtocolError:
                default:
                    NoteProtocolError("snapshot replace");   // latches only on the Kth consecutive
                    _synced = false;   // until latched, retry next tick so consecutive faults accrue
                    break;
            }
        }

        // Ok -> true. TransportError -> _synced=false (snapshot next), false. ProtocolError -> latch+stop, false.
        private async Task<bool> SendMappedAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl)
        {
            PeerCtlSendResult r = await _sink.SendAsync(op, excl).ConfigureAwait(false);
            switch (r)
            {
                case PeerCtlSendResult.Ok:
                    NoteOk();
                    return true;
                case PeerCtlSendResult.TransportError:
                    _synced = false;
                    return false;
                case PeerCtlSendResult.ProtocolError:
                default:
                    NoteProtocolError("delta " + PeerCtlBatchSerializer.OpString(op));   // latches only on the Kth consecutive
                    _synced = false;   // until latched, snapshot next tick so consecutive faults accrue
                    return false;
            }
        }

        private IReadOnlyCollection<UUID> ColumnFor(UUID listener)
        {
            // The listener's full current exclusion column, as a Replace payload.
            VisibilityBatch snap = _feed.SnapshotFor(listener);
            return snap.Added.TryGetValue(listener, out IReadOnlyCollection<UUID> col)
                ? col : Array.Empty<UUID>();
        }

        private void RefreshKnownListeners()
        {
            _knownListeners.Clear();
            foreach (UUID l in _feed.Current.Listeners)
                _knownListeners.Add(l);
        }

        // A successful send clears the consecutive-ProtocolError run. Called on every Ok, on any path.
        private void NoteOk() => _consecutiveProtocolErrors = 0;

        // Count a ProtocolError; latch only once K have arrived back-to-back. TransportError does NOT
        // call this AND does NOT reset the counter — it LEAVES it unchanged: transport has its own
        // snapshot-recovery path, and a persistent config fault that only surfaces when transport
        // briefly works (P, T, P, T, P) must still accrue toward the latch rather than be masked by
        // flapping transport. So only an actual Ok resets the run.
        private void NoteProtocolError(string where)
        {
            _consecutiveProtocolErrors++;
            if (_consecutiveProtocolErrors >= ProtocolErrorLatchThreshold)
                LatchProtocolFailure(where, _consecutiveProtocolErrors);
        }

        private void LatchProtocolFailure(string where, int consecutive)
        {
            if (_protocolFailed)
                return;
            _protocolFailed = true;   // stop emission — K consecutive config/format errors are not transient
            m_log.ErrorFormat("{0} {1} rejected as ProtocolError {2}x consecutively (config/format — e.g. wrong " +
                "AdminAPIToken, wrong plugin name, or broken transport in front). Emission DISABLED for this " +
                "region until fixed and the region server is restarted. NOT entering an unbounded snapshot-retry loop.",
                LogHeader, where, consecutive);
        }

        private void LogNoSinkOnce()
        {
            if (_loggedNoSink)
                return;
            _loggedNoSink = true;
            m_log.WarnFormat("{0} no IPeerCtlBatchSink registered for this region; feeder runs matrix-only " +
                "(no emission). Is the Janus service module enabled?", LogHeader);
        }
    }
}
