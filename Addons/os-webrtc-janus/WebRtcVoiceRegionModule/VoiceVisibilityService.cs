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
        private readonly VoiceStateFeeder m_feeder;
        private readonly ManualResetEventSlim m_wake = new ManualResetEventSlim(false);

        private Thread m_thread;
        private volatile bool m_running;
        private IEstateModule m_estateModule;

        public VoiceVisibilityService(Scene scene, int cadenceMs)
        {
            m_scene = scene;
            m_cadenceMs = cadenceMs;
            m_feeder = new VoiceStateFeeder(new FeederWorldFromScene(scene), EstateRoomPlaceholder, OnDerivationError);
            m_feeder.BatchProduced += OnBatch;
        }

        /// The produced feed — the boundary the later Janus sender will consume.
        public IVisibilityFeed Feed => m_feeder;

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
            m_running = true;
            m_thread = new Thread(RunLoop)
            {
                Name = "VoiceVisibilityFeeder:" + m_scene.RegionInfo.RegionName,
                IsBackground = true
            };
            m_thread.Start();
            m_log.Info($"{logHeader} feeder started for {m_scene.RegionInfo.RegionName} @ {m_cadenceMs}ms");
        }

        public void Stop()
        {
            m_running = false;
            m_wake.Set();

            UnwireEvents();

            Thread t = m_thread;
            m_thread = null;
            if (t != null && !t.Join(JoinTimeoutMs))
                m_log.Warn($"{logHeader} feeder thread for {m_scene.RegionInfo.RegionName} did not stop within {JoinTimeoutMs}ms");
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
                try
                {
                    m_feeder.Tick();
                }
                catch (Exception e)
                {
                    // Tick already hardens derivation; this is a last-resort guard so the loop
                    // itself never dies.
                    m_log.Error($"{logHeader} tick failed", e);
                }

                m_wake.Wait(m_cadenceMs);
                m_wake.Reset();
            }
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
