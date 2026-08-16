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
 * A ProtocolError from the sink is a config/format error (e.g. wrong AdminAPIToken), not transient:
 * it stops emission (latched) with one loud log — it must NOT enter the snapshot-retry loop.
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

        private readonly IVisibilityFeed _feed;
        private readonly IPeerCtlBatchSink _sink;   // null => no sink registered; no-op (logged once)
        private readonly bool _enabled;

        private int _sendInFlight;                  // Interlocked single-flight (0/1)
        private volatile bool _synced;
        private volatile bool _protocolFailed;      // latched on ProtocolError; stops emission
        private bool _loggedNoSink;

        private readonly HashSet<UUID> _knownListeners = new HashSet<UUID>();   // touched only on RunAsync's thread
        private readonly object _pendingLock = new object();
        private readonly Dictionary<UUID, int> _pending = new Dictionary<UUID, int>();   // listener -> attempts left

        public VisibilityBatchSender(IVisibilityFeed feed, IPeerCtlBatchSink sink, bool enabled)
        {
            _feed = feed;
            _sink = sink;
            _enabled = enabled;
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
            if (Interlocked.CompareExchange(ref _sendInFlight, 1, 0) != 0)
            {
                _synced = false;   // a skipped tick -> snapshot next
                return Task.CompletedTask;
            }
            return RunAsync(batch);   // NOT awaited by Pump (the void wrapper); tests may await it
        }

        private async Task RunAsync(VisibilityBatch batch)
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
                Interlocked.Exchange(ref _sendInFlight, 0);
            }
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
                    LatchProtocolFailure("per-listener join replace");
                    return;
                }
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
                    LatchProtocolFailure("snapshot replace");
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
                    return true;
                case PeerCtlSendResult.TransportError:
                    _synced = false;
                    return false;
                case PeerCtlSendResult.ProtocolError:
                default:
                    LatchProtocolFailure("delta " + PeerCtlBatchSerializer.OpString(op));
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

        private void LatchProtocolFailure(string where)
        {
            if (_protocolFailed)
                return;
            _protocolFailed = true;   // stop emission — a config/format error is not transient
            m_log.ErrorFormat("{0} {1} rejected as ProtocolError (config/format — e.g. wrong AdminAPIToken " +
                "or a malformed batch). Emission DISABLED for this region until fixed and the region " +
                "server is restarted. NOT entering the snapshot-retry loop.", LogHeader, where);
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
