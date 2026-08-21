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
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class VoiceVisibilityService
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(VoiceVisibilityService));
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
            m_feeder = new VoiceStateFeeder(new FeederWorldFromScene(scene), EstateRoomPlaceholder, OnDerivationError);
            m_feeder.BatchProduced += OnBatch;
        }

        /// The produced feed — the boundary the later Janus sender will consume.
        public IVisibilityFeed Feed => m_feeder;

        /// Sticky per-parcel voice-moderation state (slice 1, in-memory / NON-PERSISTENT). Written
        /// by the region module's SpatialVoiceModerationRequest CAP handler via this per-region
        /// service; purged here on parcel removal (OnLandObjectRemoved). Nothing reads it yet — the
        /// matrix enforcement rule is a later commit.
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
            m_log.Info($"{logHeader} feeder started for {m_scene.RegionInfo.RegionName} @ {m_cadenceMs}ms (emit={m_emitEnabled})");
        }

        /// Forward a WebRTC provisioning-success for a listener to the sender's pending-join path
        /// (correction 1). Called from the region module's provisioning handler; safe if the sender
        /// is not yet built or emission is disabled.
        public void OnListenerProvisioned(UUID listener)
            => m_sender?.OnListenerProvisioned(listener);

        public void Stop()
        {
            m_running = false;
            m_wake.Set();

            UnwireEvents();

            Thread t = m_thread;
            m_thread = null;
            if (t != null && !t.Join(JoinTimeoutMs))
                m_log.Warn($"{logHeader} feeder thread for {m_scene.RegionInfo.RegionName} did not stop within {JoinTimeoutMs}ms");

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
                    m_log.Error($"{logHeader} tick failed", e);
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
                m_log.DebugFormat("{0} {1}: +{2} listeners / -{3} listeners",
                    logHeader, m_scene.RegionInfo.RegionName, b.Added.Count, b.Removed.Count);
        }

        private void OnDerivationError(Exception e)
            => m_log.Error($"{logHeader} matrix derivation error (kept last matrix)", e);
    }
}
