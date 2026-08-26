/*
 * Per-region owner of a VoiceStateFeeder.
 *
 * Wires sim events (dirty-flag only — never mutates the matrix on an event/sim thread), and
 * drives Tick() on ONE dedicated, named background thread at the configured cadence. It is
 * deliberately NOT a System.Threading.Timer: pool callbacks hop threads and would trip the
 * feeder's Debug single-thread guard. Stop() signals the loop and joins the thread with a
 * timeout.
 *
 * No Janus sender exists yet (out of scope); produced batches are logged so the in-world DEBUG
 * smoke check can watch the feed run while the guard stays quiet.
 */

using System;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class VoiceVisibilityService
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string logHeader = "[VOICE VISIBILITY]";

        // Matrix scope placeholder until the (later) sender wires real Janus room numbers.
        private const int EstateRoomPlaceholder = -999;
        private const int JoinTimeoutMs = 2000;

        private readonly Scene m_scene;
        private readonly int m_cadenceMs;
        private readonly bool m_emitEnabled;
        private readonly IPeerCtlBatchSink m_sink;   // injected by the region module (option c-new); may be null
        private readonly TimeSpan m_adminTimeout;    // for the sender's in-flight staleness self-heal
        private readonly VoiceStateFeeder m_feeder;
        private readonly ManualResetEventSlim m_wake = new ManualResetEventSlim(false);

        private Thread m_thread;
        private volatile bool m_running;
        private IEstateModule m_estateModule;
        private VisibilityBatchSender m_sender;   // built in StartLoop from the injected sink
        private readonly AgentRoomTable m_rooms = new AgentRoomTable();   // S2: agent -> joined mixer room (newest wins)

        // The sink is passed in directly (NOT resolved via scene.RequestModuleInterface): the sink
        // and the sender live in this module's AssemblyLoadContext, so IPeerCtlBatchSink identity
        // matches. Routing it through the scene module-interface registry crossed an ALC boundary
        // with a non-shared type (VoiceVisibility.dll) and the two Types never matched. A null sink
        // is tolerated — the sender runs matrix-only and logs once.
        public VoiceVisibilityService(Scene scene, int cadenceMs, bool emitEnabled = false,
            IPeerCtlBatchSink sink = null, TimeSpan? adminTimeout = null)
        {
            m_scene = scene;
            m_cadenceMs = cadenceMs;
            m_emitEnabled = emitEnabled;
            m_sink = sink;
            m_adminTimeout = adminTimeout ?? TimeSpan.FromSeconds(5);
            // Pass the moderation store to the adapter so ToParcelView can populate the source-side
            // moderation predicate. Moderation is an auto-property initialiser, so it is already set
            // before this constructor body runs.
            m_feeder = new VoiceStateFeeder(new FeederWorldFromScene(scene, Moderation), EstateRoomPlaceholder, OnDerivationError);
            m_feeder.BatchProduced += OnBatch;
            // S2: the resolver the sink consumes (S3b). Null = no record; the SINK maps that to the
            // estate room (OQ4, one policy for listeners and sources) — this service never guesses.
            RoomOf = m_rooms.Resolve;
            // S3b: hand it to the sink HERE. The sink was constructed before this service
            // (WebRtcVoiceRegionModule.cs:174-176), so the resolver cannot be a ctor argument;
            // this assignment closes the window before Start() lets any tick emit. Concrete
            // type on purpose: IPeerCtlBatchSink stays a pure transport seam, so a sink that is
            // not the Janus one (a test double) needs no room knowledge at all.
            if (m_sink is JanusPeerCtlBatchSink janusSink)
                janusSink.RoomOf = RoomOf;
        }

        /// The produced feed — the boundary the later Janus sender will consume.
        public IVisibilityFeed Feed => m_feeder;

        /// Sticky per-parcel voice-moderation state (slice 1, in-memory / NON-PERSISTENT). Written
        /// by the region module's SpatialVoiceModerationRequest CAP handler via this per-region
        /// service; purged here on parcel removal (OnLandObjectRemoved). Read by the matrix via the
        /// FeederWorldFromScene constructed below — ToParcelView folds IsModerated plus the
        /// moderator exemption into ParcelView.IsVoiceModerated (rule 2b, source-side).
        public VoiceModerationStore Moderation { get; } = new VoiceModerationStore();

        /// Observability for the event→dirty wiring (used by tests).
        public bool HasPendingInvalidation => m_feeder.HasPendingInvalidation;

        public void Start()
        {
            WireEvents();
            StartLoop();
        }

        // Split from Start() so tests can assert event→dirty wiring without a running loop
        // consuming the flag.
        public void WireEvents()
        {
            m_scene.EventManager.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
            m_scene.EventManager.OnLandObjectAdded += OnLandObjectAdded;
            m_scene.EventManager.OnLandObjectRemoved += OnLandObjectRemoved;
            m_estateModule = m_scene.RequestModuleInterface<IEstateModule>();
            if (m_estateModule != null)
                m_estateModule.OnEstateInfoChange += OnEstateInfoChange;
        }

        public void StartLoop()
        {
            // Build the sender from the injected sink (same ALC — no registry resolve). Null-tolerant:
            // a null sink makes the sender run matrix-only and log once. The sender's own
            // VisibilityEmitEnabled gate decides whether it emits at all.
            m_sender = new VisibilityBatchSender(m_feeder, m_sink, m_emitEnabled,
                m_adminTimeout, m_scene.RegionInfo.RegionName);

            m_running = true;
            // Register with the OpenSim Watchdog so a dead or non-heartbeating tick thread is
            // reported instead of failing silently. StartThread creates, names, registers, and
            // starts the thread. 5000ms timeout = 20x the 250ms cadence; Pump is fire-and-forget
            // so the tick thread never blocks on a send, leaving that headroom safe.
            m_thread = WorkManager.StartThread(
                RunLoop,
                "VoiceVisibilityFeeder:" + m_scene.RegionInfo.RegionName,
                ThreadPriority.Normal,
                isBackground: true,
                alarmIfTimeout: true,
                alarmMethod: null,
                timeout: 5000);
            m_log.LogInformation($"{logHeader} feeder started for {m_scene.RegionInfo.RegionName} @ {m_cadenceMs}ms (emit={m_emitEnabled})");
        }

        /// Step S2: the mixer room each agent's latest successful provision joined, as a resolver for
        /// the sink (S3b). Null means no record here; the sink resolves null to the estate room
        /// (OQ4 / §7 "one policy for a missing room record"). Newest provision wins (OQ7).
        public Func<UUID, int?> RoomOf { get; }

        /// Forward a WebRTC provisioning result for a listener. If the result carried the joined
        /// room (the success map, S1) record it; a failure or logout map has no room, so the caller
        /// passes null and the record is left untouched. Then hand the agent to the sender's
        /// pending-join path (correction 1) exactly as before — that queueing is deliberately
        /// unchanged here. Safe if the sender is not yet built or emission is disabled.
        public void OnListenerProvisioned(UUID listener, int? room)
        {
            if (room.HasValue)
                m_rooms.Record(listener, room.Value);
            m_sender?.OnListenerProvisioned(listener);
        }

        /// Pre-S2 overload: no room information. Delegates with null, so the record is untouched.
        public void OnListenerProvisioned(UUID listener)
            => OnListenerProvisioned(listener, null);

        public void Stop()
        {
            m_running = false;
            m_wake.Set();

            UnwireEvents();

            Thread t = m_thread;
            m_thread = null;
            if (t != null && !t.Join(JoinTimeoutMs))
                m_log.LogWarning($"{logHeader} feeder thread for {m_scene.RegionInfo.RegionName} did not stop within {JoinTimeoutMs}ms");

            // The service owns the injected sink's lifetime (it holds a JanusAdminClient/HttpClient).
            // Dispose AFTER the tick thread has joined so no in-flight send races the dispose.
            (m_sink as IDisposable)?.Dispose();
        }

        private void UnwireEvents()
        {
            m_scene.EventManager.OnAvatarEnteringNewParcel -= OnAvatarEnteringNewParcel;
            m_scene.EventManager.OnLandObjectAdded -= OnLandObjectAdded;
            m_scene.EventManager.OnLandObjectRemoved -= OnLandObjectRemoved;
            if (m_estateModule != null)
                m_estateModule.OnEstateInfoChange -= OnEstateInfoChange;
        }

        private void RunLoop()
        {
            while (m_running)
            {
                VisibilityBatch batch = null;
                try
                {
                    batch = m_feeder.Tick();
                }
                catch (Exception e)
                {
                    // Tick already hardens derivation; this is a last-resort guard so the loop
                    // itself never dies.
                    m_log.LogError(e, $"{logHeader} tick failed");
                }

                // Drive emission off the tick: fire-and-forget, never awaited here, never throws.
                // Pump runs every tick (even an empty batch) so the pending-join path and a
                // snapshot-on-a-quiet-tick still get a chance.
                if (batch != null)
                    m_sender?.Pump(batch);

                m_wake.Wait(m_cadenceMs);
                m_wake.Reset();

                // Heartbeat on the always-executed path (even an idle tick with a null batch), so
                // the Watchdog only alarms when this loop truly stops ticking.
                Watchdog.UpdateThread();
            }

            // Deregister on clean exit so shutdown does not leave a stale tracked thread.
            Watchdog.RemoveThread();
        }

        // --- Event handlers: run on sim threads. Only flip the feeder's dirty flag (NOT the
        //     matrix) and wake the tick thread for an early recompute. Matrix mutation happens
        //     exclusively on RunLoop's thread, keeping the Debug single-thread guard quiet. ---

        private void OnAvatarEnteringNewParcel(ScenePresence sp, int localLandID, UUID regionID)
        {
            m_feeder.OnAvatarEnteringNewParcel();
            m_wake.Set();
        }

        private void OnLandObjectAdded(OpenSim.Framework.ILandObject parcel)
        {
            m_feeder.OnLandChanged();
            m_wake.Set();
        }

        private void OnLandObjectRemoved(UUID globalID)
        {
            // Slice 1: purge any moderation state for a parcel that no longer exists (join /
            // delete) so orphaned GlobalIDs self-heal. Reuses this existing subscription.
            Moderation.Remove(globalID);
            m_feeder.OnLandChanged();
            m_wake.Set();
        }

        private void OnEstateInfoChange(UUID regionID)
        {
            if (regionID != m_scene.RegionInfo.RegionID)
                return;
            m_feeder.OnEstateChanged();
            m_wake.Set();
        }

        private void OnBatch(VisibilityBatch b)
        {
            if (!b.IsEmpty)
                m_log.LogDebug("{LogHeader} {RegionName}: +{AddedListenerCount} listeners / -{RemovedListenerCount} listeners",
                    logHeader, m_scene.RegionInfo.RegionName, b.Added.Count, b.Removed.Count);
        }

        private void OnDerivationError(Exception e)
            => m_log.LogError(e, $"{logHeader} matrix derivation error (kept last matrix)");
    }
}
