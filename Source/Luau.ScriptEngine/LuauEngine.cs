/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using Luau.ScriptEngine.Bindings;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;

namespace Luau.ScriptEngine
{
    /// <summary>Loads the native runtime for the engine; false means it is unusable and the engine must not register.</summary>
    internal delegate bool RuntimeLoader(LuauSettings settings, IEngineLog log);

    /// <summary>
    /// The Luau script engine as a region module. It registers with the region only when
    /// <c>[NGC.Luau] Enabled = true</c>; it owns a script only when the script's first line names it
    /// or the region's saved-state routing gives it the script. A script it owns is compiled by the loader, which
    /// keeps the compiled bytes; a worker of the scheduler makes the script object from them when the script starts.
    /// The region's start, stop, reset and remove events drive the script; a script that faults is stopped and its
    /// owner is told.
    /// </summary>
    public class LuauEngine : INonSharedRegionModule, IScriptModule, IScriptStateProvider
    {
        public event ScriptRemoved OnScriptRemoved;

#pragma warning disable CS0067 // raised once scripts run on the engine
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore CS0067

        public LuauEngine() : this(new RegionLog(), NativeRuntime.GateForEngine) { }

        private readonly IEngineLog m_log;
        private readonly RuntimeLoader m_loadRuntime;
        private LuauSettings m_settings = new LuauSettings();
        private bool m_enabled;
        private Scene m_scene;

        private readonly ISavedStateStore m_store;

        /// <summary>Builds the loader for a region once the engine is enabled; tests substitute their own.</summary>
        internal delegate IScriptLoader LoaderFactory(LuauSettings settings, IEngineLog log, Scene scene);

        /// <summary>Builds the scheduler from the options the engine made; tests change the options (host functions, clock) first.</summary>
        internal delegate LuauScheduler SchedulerFactory(SchedulerOptions options);

        private readonly LoaderFactory m_loaderFactory;
        private readonly SchedulerFactory m_schedulerFactory;
        private IScriptMessages m_messages;
        private IScriptLoader m_loader;
        private LuauScheduler m_scheduler;

        /// <summary>The pause a script pays for its own error report, as a script's chat does: 15 ms.</summary>
        private const double ErrorPauseSeconds = 0.015;

        /// <summary>What the engine knows of a script that has been made: where it lives and the scheduler's record of it.</summary>
        private sealed class ScriptRecord
        {
            public UUID ItemID;
            public uint LocalID;
            public UUID AssetID;
            public SchedulerScript Script;
        }

        private readonly Dictionary<UUID, ScriptRecord> m_records = new Dictionary<UUID, ScriptRecord>();

        internal LuauEngine(IEngineLog log, RuntimeLoader loadRuntime, ISavedStateStore store = null, LoaderFactory loaderFactory = null,
            SchedulerFactory schedulerFactory = null, IScriptMessages messages = null)
        {
            m_log = log;
            m_loadRuntime = loadRuntime;
            m_store = store ?? new EmptyStateStore();
            m_loaderFactory = loaderFactory;
            m_schedulerFactory = schedulerFactory;
            m_messages = messages;
        }

        /// <summary>Whether the engine holds a load record for the item (a rez it accepted and has not forgotten).</summary>
        internal bool IsTracking(UUID itemID)
        {
            lock (m_tracked) return m_tracked.ContainsKey(itemID);
        }

        private readonly Dictionary<UUID, uint> m_tracked = new Dictionary<UUID, uint>();
        private bool m_warnedNoFunctions;

        internal LuauSettings Settings => m_settings;

        public string Name => EngineIdentity.EngineName;
        public string ScriptEngineName => EngineIdentity.EngineName;
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_settings = LuauSettings.Read(source, m_log);
            if (!m_settings.Enabled)
                return;

            // The native runtime is needed before any script can run. If it cannot be used the engine stays out of
            // the region: it logs one line and registers nothing, and the other engines carry on.
            m_enabled = m_loadRuntime(m_settings, m_log);
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            m_scene = scene;
            m_scene.RegisterModuleInterface<IScriptModule>(this);
            m_scene.StackModuleInterface<IScriptModule>(this);
            m_messages ??= new SceneScriptMessages(scene);
            try
            {
                m_loader = (m_loaderFactory ?? DefaultLoader)(m_settings, m_log, scene);
                m_loader.ScriptReady += OnScriptReady;
                m_loader.FailureUnclaimed += OnFailureUnclaimed;
            }
            catch (Exception e)
            {
                // The engine stays registered but cannot load: the region and the other engines carry on.
                m_log.Error("The script loader could not be started; scripts given to this engine will not load: " + e);
            }
            try
            {
                m_scheduler = (m_schedulerFactory ?? (o => new LuauScheduler(o)))(BuildSchedulerOptions());
            }
            catch (Exception e)
            {
                m_log.Error("The script scheduler could not be started; scripts given to this engine will not run: " + e);
            }
            m_scene.EventManager.OnRezScript += OnRezScript;
            m_scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_scene.EventManager.OnScriptReset += OnScriptReset;
            m_scene.EventManager.OnStartScript += OnStartScript;
            m_scene.EventManager.OnStopScript += OnStopScript;
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        /// <summary>
        /// The bound-function list is empty until host functions are bound, so the stamp carries a fixed marker; it
        /// changes with that list, which makes the cache entries of an earlier list unused.
        /// </summary>
        internal const string BoundFunctionHash = "none";

        /// <summary>The loader the engine runs with: the compile cache in the engine's folder (unless switched off), the region's asset service for text, scripts held as compiled bytes.</summary>
        private static IScriptLoader DefaultLoader(LuauSettings settings, IEngineLog log, Scene scene)
        {
            CompileCache cache = settings.CompileCache
                ? new CompileCache(CompileCache.DirectoryFor(settings), CompileCache.MakeStamp(Host.Version(), BoundFunctionHash), log)
                : null;
            var compiler = new ScriptCompiler(null, null, log, cache);
            return new ScriptLoader(compiler, new SceneTextSource(scene.AssetService), log, ownsCompiler: true);
        }

        /// <summary>The scheduler's options from the engine's settings, with the engine's own callbacks for faults, script errors and a worker that gave up.</summary>
        private SchedulerOptions BuildSchedulerOptions()
        {
            Bindings.Watchdog watchdog = m_settings.Watchdog switch
            {
                "resident" => Bindings.Watchdog.Resident,
                "threaded" => Bindings.Watchdog.Threaded,
                _ => Bindings.Watchdog.Default,
            };
            return new SchedulerOptions
            {
                WorkerThreads = m_settings.WorkerThreads,
                ThreadPriority = m_settings.SchedulerThreadPriority,
                QuantaMs = m_settings.QuantaMs,
                MinTimerInterval = m_settings.MinTimerInterval,
                MaxEventQueue = m_settings.MaxEventQueue,
                MemoryLimitBytes = m_settings.MemoryLimitKiB * 1024,
                KillAfterPunishments = m_settings.KillAfterPunishments,
                ErrorPauseSeconds = m_settings.ChatThrottle ? ErrorPauseSeconds : 0,
                VmFactory = () => new Vm(watchdog),
                Log = m_log,
                ScriptFaulted = OnScriptFaulted,
                ScriptError = OnScriptError,
                WorkerStopped = OnWorkerStopped,
            };
        }

        /// <summary>How long GetScriptErrors waits for a compile; the region's own wait for a save is the same 15 seconds.</summary>
        internal static TimeSpan ErrorWaitTimeout = TimeSpan.FromSeconds(15);

        public void RemoveRegion(Scene scene)
        {
            if (m_scene == null) return;
            m_scene.EventManager.OnRezScript -= OnRezScript;
            m_scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_scene.EventManager.OnScriptReset -= OnScriptReset;
            m_scene.EventManager.OnStartScript -= OnStartScript;
            m_scene.EventManager.OnStopScript -= OnStopScript;
            lock (m_tracked) m_tracked.Clear();
            m_loader?.Dispose();
            m_loader = null;
            m_scheduler?.Dispose();
            m_scheduler = null;
            lock (m_records) m_records.Clear();
            m_scene = null;
        }

        public void RegionLoaded(Scene scene) { }

        /// <summary>
        /// Every engine receives every rez with the engine name the region selected. The first-line header still takes
        /// priority, so a script is ours exactly when <see cref="EngineHeader.Owner"/> gives it to this engine.
        /// </summary>
        private void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
            if (EngineHeader.Owner(script, engine, LoadedEngineNames()) != ScriptEngineName)
            {
                Disown(itemID);
                return;
            }

            lock (m_tracked) m_tracked[itemID] = localID;
            if (!m_warnedNoFunctions)
            {
                m_warnedNoFunctions = true;
                m_log.Warn("A script was given to this engine, which runs scripts but does not bind the ll, os, iw or bot functions yet; a script that calls one stops with an error");
            }

            SceneObjectPart part = m_scene?.GetSceneObjectPart(localID);
            m_loader?.Post(new LoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                AssetID = part?.Inventory?.GetInventoryItem(itemID)?.AssetID ?? UUID.Zero,
                Source = script,
            });
        }

        private IEnumerable<string> LoadedEngineNames()
        {
            foreach (IScriptModule module in m_scene?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>())
                if (module != null)
                    yield return module.ScriptEngineName;
        }

        /// <summary>
        /// The script belongs to another engine: forget this item's load record and let go of the script. The saved
        /// state row is kept, so the script resumes if it returns here with the same asset; an edited script has a new
        /// asset and starts fresh.
        /// </summary>
        private void Disown(UUID itemID)
        {
            lock (m_tracked) m_tracked.Remove(itemID);
            m_loader?.Disown(itemID);
            DropRecord(itemID);
        }

        // ---- the join: a ready script becomes a scheduler script ----

        /// <summary>Called on the loader's thread when a script has compiled: a worker makes the script from its bytes, and it starts if its Running flag is on.</summary>
        private void OnScriptReady(ReadyScript ready)
        {
            LuauScheduler scheduler = m_scheduler;
            if (scheduler == null || !IsTracking(ready.ItemID))
                return;

            TaskInventoryItem item = m_scene?.GetSceneObjectPart(ready.LocalID)?.Inventory?.GetInventoryItem(ready.ItemID);
            bool running = item?.ScriptRunning ?? true;

            SchedulerScript script;
            try
            {
                script = scheduler.Add(ready.Asset, ready.Language);
            }
            catch (Exception e)
            {
                m_log.Error($"Script {ready.ItemID} could not be given to a worker: {e.Message}");
                return;
            }

            var record = new ScriptRecord { ItemID = ready.ItemID, LocalID = ready.LocalID, AssetID = ready.AssetID, Script = script };
            ScriptRecord replaced;
            lock (m_records)
            {
                m_records.TryGetValue(ready.ItemID, out replaced);
                m_records[ready.ItemID] = record;
            }
            if (replaced != null)
                scheduler.Remove(replaced.Script);

            if (running)
                scheduler.Start(script);
            else
                scheduler.SetRunning(script, false);
        }

        private ScriptRecord RecordOf(UUID itemID)
        {
            lock (m_records) return m_records.TryGetValue(itemID, out ScriptRecord r) ? r : null;
        }

        private void DropRecord(UUID itemID)
        {
            ScriptRecord record;
            lock (m_records)
            {
                if (!m_records.Remove(itemID, out record))
                    return;
            }
            m_scheduler?.Remove(record.Script);
        }

        /// <summary>The scheduler's record of the item's script, or null; for the status command and the tests.</summary>
        internal SchedulerScript SchedulerScriptFor(UUID itemID) => RecordOf(itemID)?.Script;

        /// <summary>The scheduler's counters for the status console command; null when the engine has no scheduler.</summary>
        internal SchedulerStats GetStats() => m_scheduler?.GetStats();

        // ---- the region's lifecycle events ----

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            if (!IsTracking(itemID) && RecordOf(itemID) == null)
                return; // not ours
            lock (m_tracked) m_tracked.Remove(itemID);
            m_loader?.Disown(itemID);
            DropRecord(itemID);
            OnScriptRemoved?.Invoke(itemID);
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            if (record.Script.Killed)
                SetItemRunningFlag(record.LocalID, itemID, true); // a reset is what starts a script a fault stopped
            m_scheduler?.Reset(record.Script);
        }

        private void OnStartScript(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            SetItemRunningFlag(record.LocalID, itemID, true);
            m_scheduler?.SetRunning(record.Script, true);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            m_scheduler?.SetRunning(record.Script, false);
        }

        /// <summary>The item's Running flag, as the viewer's checkbox and llSetScriptState persist it.</summary>
        private void SetItemRunningFlag(uint localId, UUID itemId, bool running)
        {
            SceneObjectPart part = m_scene?.GetSceneObjectPart(localId);
            TaskInventoryItem item = part?.Inventory?.GetInventoryItem(itemId);
            if (item == null || item.ScriptRunning == running)
                return;
            item.ScriptRunning = running;
            part.Inventory.ForceInventoryPersistence();
            part.ParentGroup.HasGroupChanged = true;
        }

        // ---- faults and messages to the owner ----

        /// <summary>
        /// A script ended in a fault or was killed (on its worker's thread). It stays stopped until it is reset or its
        /// Running flag is set again, and its Running flag goes off the way the viewer's checkbox does it. The owner
        /// is told on DEBUG_CHANNEL in Halcyon's wording, which scripts that catch errors match.
        /// </summary>
        private void OnScriptFaulted(SchedulerScript script)
        {
            ScriptRecord record = FindRecord(script);
            if (record == null)
                return;
            m_log.Error($"Script {record.ItemID} (asset {record.AssetID}) was stopped: {script.FaultMessage}");
            SetItemRunningFlag(record.LocalID, record.ItemID, false);
            TellOwner(record, $"Script {record.AssetID} encountered a problem and was stopped: {script.FaultMessage}");
        }

        /// <summary>A host function reported an error for the script (on its worker's thread); the scheduler pauses the script for the error pause.</summary>
        private void OnScriptError(SchedulerScript script, string text)
        {
            ScriptRecord record = FindRecord(script);
            if (record != null)
                TellOwner(record, text);
        }

        private void OnWorkerStopped(int index, int crashes, string message) =>
            m_log.Error($"Script worker {index} stopped after {crashes} failures; its scripts no longer run. Last failure: {message}");

        private ScriptRecord FindRecord(SchedulerScript script)
        {
            lock (m_records)
            {
                foreach (ScriptRecord r in m_records.Values)
                    if (ReferenceEquals(r.Script, script))
                        return r;
            }
            return null;
        }

        private void TellOwner(ScriptRecord record, string text)
        {
            try { m_messages?.ShoutError(record.LocalID, record.ItemID, text); }
            catch (Exception e) { m_log.Error("A script error could not be sent to the owner: " + e.Message); }
        }

        /// <summary>A compile failure no editor collected in time: the owner gets a pop-up with the errors.</summary>
        private void OnFailureUnclaimed(LoadFailure failure)
        {
            if (!IsTracking(failure.ItemID))
                return;
            try { m_messages?.CompileFailed(failure.LocalID, failure.ItemID, failure.Errors); }
            catch (Exception e) { m_log.Error("A compile failure could not be shown to the owner: " + e.Message); }
        }

        // ---- the module's interface ----

        public bool HasSavedScriptState(UUID itemID, UUID assetID) => m_store.HasSavedState(itemID, assetID);

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;

        /// <summary>Posts an event to one script (the entry point other engines and the async plugins use).</summary>
        public bool PostScriptEvent(UUID itemID, EventParams parms) => false;

        /// <summary>Posts an event to every script of the prim that this engine holds.</summary>
        public bool PostObjectEvent(uint localID, EventParams parms) => false;

        /// <summary>True once every worker has failed and the engine has switched itself off.</summary>
        internal bool IsDisabled => false;

        /// <summary>Events held for an item whose script has not finished loading.</summary>
        internal int DeferredEventCount(UUID itemID) => 0;

        /// <summary>The clock the engine's deferred events expire by; tests replace it.</summary>
        internal ISchedulerClock Clock { get; set; } = new MonotonicClock();

        /// <summary>Events kept for a script that is still loading, per item (Halcyon DeferredEventManager.MAX_DEFERRED_EVENTS).</summary>
        internal const int MaxDeferredEventsPerItem = 32;

        /// <summary>How long they are kept from the first one (Halcyon DeferredEventManager.EXPIRATION_SECONDS).</summary>
        internal const double DeferredEventLifetimeSeconds = 60;

        public bool SuspendScript(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null || m_scheduler == null)
                return false;
            m_scheduler.Suspend(record.Script);
            return true;
        }

        public bool ResumeScript(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null || m_scheduler == null)
                return false;
            m_scheduler.Resume(record.Script);
            return true;
        }

        /// <summary>
        /// The compile errors of the load that follows the viewer's save, in the editor's format; empty when it
        /// compiled. Called on the caps thread that answers the save. A script this engine does not own gets an empty
        /// answer, so the engine that owns it is the one that reports.
        /// </summary>
        public ArrayList GetScriptErrors(UUID itemID)
        {
            var list = new ArrayList();
            if (m_loader == null || !IsTracking(itemID))
                return list;
            List<string> errors = m_loader.WaitForCompileErrors(itemID, ErrorWaitTimeout);
            if (errors != null)
                list.AddRange(errors);
            return list;
        }

        /// <summary>
        /// True when this engine holds the script: running or stopped in the scheduler, or compiled and held ready. A
        /// script that has not started is not running.
        /// </summary>
        public bool HasScript(UUID itemID, out bool running)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record != null)
            {
                running = record.Script.IsRunning;
                return true;
            }
            running = false;
            return m_loader != null && IsTracking(itemID) && m_loader.IsReady(itemID);
        }

        /// <summary>True when the script is running: its Running flag is on, it is not suspended and no fault has stopped it.</summary>
        public bool GetScriptState(UUID itemID) => RecordOf(itemID)?.Script.IsRunning ?? false;

        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new Dictionary<uint, float>();

        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            return new List<ScriptTopStatsData>();
        }
    }
}
