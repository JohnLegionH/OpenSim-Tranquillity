/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using SLua.ScriptEngine.Bindings;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>Loads the native runtime for the engine; false means it is unusable and the engine must not register.</summary>
    internal delegate bool RuntimeLoader(SLuaSettings settings, IEngineLog log);

    /// <summary>
    /// The SLua script engine as a region module. It registers with the region only when
    /// <c>[SLua] Enabled = true</c>; it owns a script only when the script's first line names it
    /// or the region's saved-state routing gives it the script. A script it owns is compiled by the loader, which
    /// keeps the compiled bytes; a worker of the scheduler makes the script object from them when the script starts.
    /// The region's start, stop, reset and remove events drive the script; a script that faults is stopped and its
    /// owner is told.
    /// </summary>
    public class SLuaEngine : INonSharedRegionModule, IScriptModule, IScriptStateProvider, IParcelScriptPolicyEngine, IEventPoster, IPolicyHost
    {
        public event ScriptRemoved OnScriptRemoved;

#pragma warning disable CS0067 // raised once scripts run on the engine
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore CS0067

        public SLuaEngine() : this(new RegionLog(), NativeRuntime.GateForEngine) { }

        private readonly IEngineLog m_log;
        private readonly RuntimeLoader m_loadRuntime;
        private SLuaSettings m_settings = new SLuaSettings();
        private bool m_enabled;
        private Scene m_scene;

        private readonly ISavedStateStore m_store;

        /// <summary>Builds the loader for a region once the engine is enabled; tests substitute their own.</summary>
        internal delegate IScriptLoader LoaderFactory(SLuaSettings settings, IEngineLog log, Scene scene);

        /// <summary>Builds the scheduler from the options the engine made; tests change the options (host functions, clock) first.</summary>
        internal delegate SLuaScheduler SchedulerFactory(SchedulerOptions options);

        private readonly LoaderFactory m_loaderFactory;
        private readonly SchedulerFactory m_schedulerFactory;
        private IScriptMessages m_messages;
        private IScriptLoader m_loader;
        private SLuaScheduler m_scheduler;

        /// <summary>The pause a script pays for its own error report, as a script's chat does: 15 ms.</summary>
        private const double ErrorPauseSeconds = 0.015;

        /// <summary>What the engine knows of a script that has been made: where it lives and the scheduler's record of it.</summary>
        private sealed class ScriptRecord
        {
            public UUID ItemID;
            public uint LocalID;
            public UUID AssetID;
            public SchedulerScript Script;
            public StartInfo Start;
            public bool StartupPending;

            /// <summary>Why the script is held back; the scheduler's Suspend is on while any reason is set. Guarded by <see cref="PauseLock"/>.</summary>
            public PauseReason Pause;

            public readonly object PauseLock = new object();

            /// <summary>Serializes the calls that set the item's flags on its prim.</summary>
            public readonly object FlagsLock = new object();
        }

        /// <summary>What the rez that started a script asked for: its start parameter, whether it wants on_rez, and where its state came from.</summary>
        private sealed class StartInfo
        {
            public int StartParam;
            public bool PostOnRez;
            public int StateSource;
        }

        private readonly Dictionary<UUID, StartInfo> m_starts = new Dictionary<UUID, StartInfo>();

        /// <summary>Events held for items whose script has not finished loading, as Halcyon's DeferredEventManager does.</summary>
        private sealed class DeferredList
        {
            public double Started;
            public readonly List<(string Name, Value[] Args, DetectedRecord[] Detected)> Events = new List<(string, Value[], DetectedRecord[])>();
        }

        private readonly Dictionary<UUID, DeferredList> m_deferred = new Dictionary<UUID, DeferredList>();
        private volatile bool m_disabled;

        private readonly Dictionary<UUID, ScriptRecord> m_records = new Dictionary<UUID, ScriptRecord>();

        internal SLuaEngine(IEngineLog log, RuntimeLoader loadRuntime, ISavedStateStore store = null, LoaderFactory loaderFactory = null,
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

        internal SLuaSettings Settings => m_settings;

        /// <summary>Where the engine sets each script's event flags on its prim; tests replace it before the region is added.</summary>
        internal IPrimEventFlags PrimFlags { get; set; }

        public string Name => EngineIdentity.EngineName;
        public string ScriptEngineName => EngineIdentity.EngineName;
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_settings = SLuaSettings.Read(source, m_log);
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
            PrimFlags ??= new ScenePrimEventFlags(scene);
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
                m_scheduler = (m_schedulerFactory ?? (o => new SLuaScheduler(o)))(BuildSchedulerOptions());
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
            m_hooks = new SceneHooksA(scene, this, m_settings.PhysicsThrottle, Clock);
            m_hooks.Subscribe();
            m_policy = new SceneHooksB(scene, this, Grants, Clock);
            m_policy.Subscribe();
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        /// <summary>
        /// The bound-function list is empty until host functions are bound, so the stamp carries a fixed marker; it
        /// changes with that list, which makes the cache entries of an earlier list unused.
        /// </summary>
        internal const string BoundFunctionHash = "none";

        /// <summary>The loader the engine runs with: the compile cache in the engine's folder (unless switched off), the region's asset service for text, scripts held as compiled bytes.</summary>
        private static IScriptLoader DefaultLoader(SLuaSettings settings, IEngineLog log, Scene scene)
        {
            CompileCache cache = settings.CompileCache
                ? new CompileCache(CompileCache.DirectoryFor(settings), CompileCache.MakeStamp(Host.Version(), BoundFunctionHash), log)
                : null;
            var compiler = new ScriptCompiler(null, null, log, cache);
            return new ScriptLoader(compiler, new SceneTextSource(scene.AssetService), log, ownsCompiler: true);
        }

        /// <summary>The scheduler's options from the engine's settings, with the engine's own callbacks for faults, script errors and a worker that gave up.</summary>
        internal SchedulerOptions BuildSchedulerOptions()
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
                FrontOfRing = m_settings.EventFrontOfRing,
                MemoryLimitBytes = m_settings.MemoryLimitKiB * 1024,
                KillAfterPunishments = m_settings.KillAfterPunishments,
                ErrorPauseSeconds = m_settings.ChatThrottle ? ErrorPauseSeconds : 0,
                VmFactory = () => new Vm(watchdog),
                Log = m_log,
                ScriptFaulted = OnScriptFaulted,
                StateChanged = OnStateChanged,
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
            m_hooks?.Unsubscribe();
            m_hooks = null;
            m_policy?.Unsubscribe();
            m_policy = null;
            lock (m_tracked) m_tracked.Clear();
            lock (m_starts) m_starts.Clear();
            lock (m_deferred) m_deferred.Clear();
            m_loader?.Dispose();
            m_loader = null;
            m_scheduler?.Dispose();
            m_scheduler = null;
            lock (m_records) m_records.Clear();
            m_scene = null;
        }

        public void RegionLoaded(Scene scene) => m_policy?.SubscribeEstate();

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

            if (m_disabled)
                return;

            lock (m_tracked) m_tracked[itemID] = localID;
            lock (m_starts) m_starts[itemID] = new StartInfo { StartParam = startParam, PostOnRez = postOnRez, StateSource = stateSource };
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
            ForgetPending(itemID);
            m_loader?.Disown(itemID);
            DropRecord(itemID);
        }

        private void ForgetPending(UUID itemID)
        {
            lock (m_starts) m_starts.Remove(itemID);
            lock (m_deferred) m_deferred.Remove(itemID);
        }

        // ---- the join: a ready script becomes a scheduler script ----

        /// <summary>Called on the loader's thread when a script has compiled: a worker makes the script from its bytes, and it starts if its Running flag is on.</summary>
        private void OnScriptReady(ReadyScript ready)
        {
            SLuaScheduler scheduler = m_scheduler;
            if (scheduler == null || m_disabled || !IsTracking(ready.ItemID))
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
                if (!DisableIfEveryWorkerFailed())
                    m_log.Error($"Script {ready.ItemID} could not be given to a worker: {e.Message}");
                return;
            }

            StartInfo start;
            lock (m_starts) m_starts.TryGetValue(ready.ItemID, out start);
            var record = new ScriptRecord { ItemID = ready.ItemID, LocalID = ready.LocalID, AssetID = ready.AssetID, Script = script, Start = start, StartupPending = true };
            ScriptRecord replaced;
            lock (m_records)
            {
                m_records.TryGetValue(ready.ItemID, out replaced);
                m_records[ready.ItemID] = record;
            }
            if (replaced != null)
                scheduler.Remove(replaced.Script);

            // The estate switch and the parcel rule decide before the script starts: a script they hold back starts paused, never refused.
            m_policy?.EvaluateNew(ready.ItemID);

            if (running)
            {
                // The flags are set before the script can hear anything, so its first events are not lost.
                ApplyEventFlags(record, 0, true);
                scheduler.Start(script);
                BeginRunning(record);
            }
            else
            {
                scheduler.SetRunning(script, false);
                ApplyEventFlags(record, 0, false);
            }
        }

        // ---- the events each script asks the region for ----

        /// <summary>
        /// Tells the region which events the script handles, so it raises touches, collisions and the like for the prim.
        /// An LSL script's flags come from the handler mask of its state in the compiled asset (the union over the scripts
        /// of a prim is the region's own, kept per script item); a stopped script asks for nothing. An SLua script's
        /// handler set is known only to the runtime, which does not export it yet (API version 4): its flags are computed
        /// here from that export once it exists, in place of the zero below. Serialized per script so a stop and a
        /// state change cannot cross.
        /// </summary>
        private void ApplyEventFlags(ScriptRecord record, int state, bool running)
        {
            IPrimEventFlags sink = PrimFlags;
            if (sink == null)
                return;
            lock (record.FlagsLock)
            {
                ulong flags = 0;
                ulong[] masks = record.Script.StateMasks;
                if (running && masks != null && masks.Length > 0)
                    flags = EventFlagMap.ForMask(masks[Math.Min(state, masks.Length - 1)]);
                try { sink.Set(record.LocalID, record.ItemID, flags); }
                catch (Exception e) { m_log.Error($"The event flags of script {record.ItemID} could not be set: {e.Message}"); }
            }
        }

        /// <summary>A script changed state (on its worker's thread): the new state's handlers decide what the region raises.</summary>
        private void OnStateChanged(SchedulerScript script)
        {
            ScriptRecord record = FindRecord(script);
            if (record != null)
                ApplyEventFlags(record, script.CurrentState, script.Running);
        }

        private void ForgetEventFlags(ScriptRecord record)
        {
            IPrimEventFlags sink = PrimFlags;
            if (sink == null)
                return;
            lock (record.FlagsLock)
            {
                try { sink.Remove(record.LocalID, record.ItemID); }
                catch (Exception e) { m_log.Error($"The event flags of script {record.ItemID} could not be removed: {e.Message}"); }
            }
        }

        /// <summary>
        /// The events a script's first start brings, after the state_entry the runtime runs itself, in the order Phlox
        /// posts them: on_rez when the rez asked for it, attach for a worn object, changed for a region start or an
        /// arrival; then whatever arrived while the script was loading. A script that starts stopped gets them when it is started.
        /// </summary>
        private void BeginRunning(ScriptRecord record)
        {
            lock (record)
            {
                if (!record.StartupPending)
                    return;
                record.StartupPending = false;
            }
            SLuaScheduler scheduler = m_scheduler;
            if (scheduler == null)
                return;
            StartInfo start = record.Start;
            if (start != null)
            {
                SceneObjectPart part = m_scene?.GetSceneObjectPart(record.LocalID);
                if (start.PostOnRez)
                    scheduler.Post(record.Script, "on_rez", Value.Int(start.StartParam));
                if (start.StateSource == (int)StateSource.AttachedRez && part?.ParentGroup != null && part.ParentGroup.AttachedAvatar != UUID.Zero)
                    scheduler.Post(record.Script, "attach", Value.Key(part.ParentGroup.AttachedAvatar.ToString()));
                int change = ArrivalChange(start.StateSource, part);
                if (change != 0)
                    scheduler.Post(record.Script, "changed", Value.Int(change));
            }
            FlushDeferred(record);
        }

        /// <summary>
        /// The changed() bits a start brings: CHANGED_REGION_START for a region start (https://wiki.secondlife.com/wiki/CHANGED_REGION_START);
        /// CHANGED_REGION for a crossing or a teleport, plus CHANGED_TELEPORT for a worn object that teleported
        /// (https://wiki.secondlife.com/wiki/CHANGED_REGION, https://wiki.secondlife.com/wiki/CHANGED_TELEPORT), to a
        /// root prim only. Phlox's rule (PhloxExecutionScheduler.ArrivalChange); the wait for a rider is not copied.
        /// </summary>
        private static int ArrivalChange(int stateSource, SceneObjectPart part)
        {
            const int ChangedRegion = 0x100, ChangedTeleport = 0x200, ChangedRegionStart = 0x400;
            switch ((StateSource)stateSource)
            {
                case StateSource.RegionStart:
                    return ChangedRegionStart;
                case StateSource.PrimCrossing:
                case StateSource.Teleporting:
                    if (part == null || !part.IsRoot)
                        return 0;
                    bool worn = part.ParentGroup?.IsAttachment == true;
                    return (StateSource)stateSource == StateSource.Teleporting && worn ? ChangedRegion | ChangedTeleport : ChangedRegion;
                default:
                    return 0;
            }
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
            ForgetEventFlags(record);
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
            ForgetPending(itemID);
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
            ApplyEventFlags(record, record.Script.CurrentState, true);
            BeginRunning(record);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            m_scheduler?.SetRunning(record.Script, false);
            ApplyEventFlags(record, record.Script.CurrentState, false);
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
            ApplyEventFlags(record, script.CurrentState, false);
            TellOwner(record, $"Script {record.AssetID} encountered a problem and was stopped: {script.FaultMessage}");
        }

        /// <summary>A host function reported an error for the script (on its worker's thread); the scheduler pauses the script for the error pause.</summary>
        private void OnScriptError(SchedulerScript script, string text)
        {
            ScriptRecord record = FindRecord(script);
            if (record != null)
                TellOwner(record, text);
        }

        private void OnWorkerStopped(int index, int crashes, string message)
        {
            m_log.Error($"Script worker {index} stopped after {crashes} failures; its scripts no longer run. Last failure: {message}");
            DisableIfEveryWorkerFailed();
        }

        /// <summary>
        /// Phlox stops its engine the same way when its scheduler gives up: one log line, and the region and the other
        /// engines carry on. True when the engine is (now) disabled.
        /// </summary>
        private bool DisableIfEveryWorkerFailed()
        {
            SchedulerStats stats = m_scheduler?.GetStats();
            if (stats == null || stats.FailedWorkers < stats.Workers.Length)
                return m_disabled;
            lock (m_deferred)
            {
                if (m_disabled)
                    return true;
                m_disabled = true;
            }
            m_log.Error("The SLua script engine is disabled: every script worker has failed, so no script of this engine runs until the region restarts");
            return true;
        }

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
        public bool PostScriptEvent(UUID itemID, string name, object[] args) =>
            PostScriptEvent(itemID, new EventParams(name, args, null));

        public bool PostObjectEvent(UUID itemID, string name, object[] args)
        {
            uint localId;
            lock (m_tracked)
            {
                if (!m_tracked.TryGetValue(itemID, out localId))
                    return false;
            }
            return PostObjectEvent(localId, new EventParams(name, args, null));
        }

        /// <summary>
        /// Posts an event to one script: the entry point other engines and the async plugins use. True when the script
        /// is ours and the event is one the runtime knows. A script that is still loading keeps the event
        /// (32 per item, for 60 seconds from the first) and gets it once it has started.
        /// </summary>
        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (!TryConvert(parms, out string name, out Value[] args, out DetectedRecord[] detected))
                return false;
            return PostToItem(itemID, name, args, detected);
        }

        /// <summary>Posts an event to every script of the prim that this engine holds or is loading.</summary>
        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            if (!TryConvert(parms, out string name, out Value[] args, out DetectedRecord[] detected))
                return false;
            var items = new List<UUID>();
            lock (m_tracked)
            {
                foreach (KeyValuePair<UUID, uint> kv in m_tracked)
                    if (kv.Value == localID)
                        items.Add(kv.Key);
            }
            bool any = false;
            foreach (UUID item in items)
                any |= PostToItem(item, name, args, detected);
            return any;
        }

        private bool PostToItem(UUID itemID, string name, Value[] args, DetectedRecord[] detected)
        {
            SLuaScheduler scheduler = m_scheduler;
            if (scheduler == null || m_disabled)
                return false;
            ScriptRecord record = RecordOf(itemID);
            if (record != null)
            {
                scheduler.Post(record.Script, name, args, detected);
                return true;
            }
            if (!IsTracking(itemID))
                return false;
            Defer(itemID, name, args, detected);
            return true;
        }

        private void Defer(UUID itemID, string name, Value[] args, DetectedRecord[] detected)
        {
            double now = Clock.Now;
            lock (m_deferred)
            {
                if (m_deferred.TryGetValue(itemID, out DeferredList list) && now - list.Started >= DeferredEventLifetimeSeconds)
                {
                    m_deferred.Remove(itemID);
                    list = null;
                }
                if (list == null)
                    m_deferred[itemID] = list = new DeferredList { Started = now };
                if (list.Events.Count < MaxDeferredEventsPerItem)
                    list.Events.Add((name, args, detected)); // the first 32 are kept
            }
        }

        private void FlushDeferred(ScriptRecord record)
        {
            DeferredList list;
            lock (m_deferred)
            {
                if (!m_deferred.Remove(record.ItemID, out list))
                    return;
            }
            if (Clock.Now - list.Started >= DeferredEventLifetimeSeconds)
                return; // a script that took more than a minute to load lost them
            foreach ((string name, Value[] args, DetectedRecord[] detected) in list.Events)
                m_scheduler?.Post(record.Script, name, args, detected);
        }

        /// <summary>Events held for an item whose script has not finished loading.</summary>
        internal int DeferredEventCount(UUID itemID)
        {
            lock (m_deferred) return m_deferred.TryGetValue(itemID, out DeferredList list) ? list.Events.Count : 0;
        }

        /// <summary>The scene hooks of the region the engine is in; null outside one.</summary>
        internal SceneHooksA Hooks => m_hooks;

        private SceneHooksA m_hooks;

        /// <summary>The policy hooks (parcel rules, estate switch, crossings, grants) of the region the engine is in; null outside one.</summary>
        internal SceneHooksB Policy => m_policy;

        private SceneHooksB m_policy;

        /// <summary>What the engine's scripts hold from the region; replaced when the ll functions that make grants are bound. Set before the region is added.</summary>
        internal IScriptGrants Grants { get; set; } = NoGrants.Instance;

        /// <summary>The pauses the policy hooks made.</summary>
        internal PolicyStats PolicyStats { get; } = new PolicyStats();

        /// <summary>The scripts the engine holds back, and why; none for an unknown item.</summary>
        internal PauseReason PauseReasonsOf(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return PauseReason.None;
            lock (record.PauseLock) return record.Pause;
        }

        /// <summary>The core leaves the parcel rule to this engine, which pauses and resumes its scripts live.</summary>
        public bool EnforcesParcelScriptRules => true;

        PolicyScript[] IPolicyHost.AllScripts()
        {
            lock (m_records)
            {
                var all = new PolicyScript[m_records.Count];
                int i = 0;
                foreach (ScriptRecord r in m_records.Values)
                    all[i++] = new PolicyScript(r.ItemID, r.LocalID);
                return all;
            }
        }

        bool IPolicyHost.TryGetScript(UUID itemID, out PolicyScript script)
        {
            ScriptRecord record = RecordOf(itemID);
            script = record == null ? default : new PolicyScript(record.ItemID, record.LocalID);
            return record != null;
        }

        /// <summary>
        /// Sets or clears one reason a script is held back. The scheduler's suspend is on while any reason is set, so a
        /// held script keeps its queued events and its prim's event flags, and the pause shows in the scheduler's counts.
        /// Each reason is cleared only by its own cause: the parcel's resume does not lift a console suspend.
        /// </summary>
        void IPolicyHost.SetPaused(UUID itemID, PauseReason reason, bool paused)
        {
            ScriptRecord record = RecordOf(itemID);
            SLuaScheduler scheduler = m_scheduler;
            if (record == null || scheduler == null)
                return;
            lock (record.PauseLock)
            {
                PauseReason before = record.Pause;
                PauseReason after = paused ? before | reason : before & ~reason;
                if (after == before)
                    return;
                record.Pause = after;
                PolicyStats.Count(reason, paused);
                if (before == PauseReason.None)
                    scheduler.Suspend(record.Script);
                else if (after == PauseReason.None)
                    scheduler.Resume(record.Script);
            }
        }

        /// <summary>
        /// Called by a function that changes a physical property, as Phlox's PhySleep is: when the region's physics frame
        /// time (average of the last ten) is over 30 ms, the calling script is delayed by that many milliseconds. Needs
        /// the physics throttle; a no-op outside a script's call.
        /// </summary>
        internal void PhySleep() { }

        /// <summary>A script took or released controls: whether the parcel lets it run is asked again.</summary>
        internal void RecheckParcel(UUID itemID) => m_policy?.EvaluateItem(itemID);

        /// <summary>Average of the last ten physics frame times in milliseconds (for the physics slowdowns the function binding will apply); 0 when the throttle is off.</summary>
        internal int PhysicsFrameTimeAvg => m_hooks?.PhysicsFrameTimeAvg ?? 0;

        /// <summary>True once every worker has failed and the engine has switched itself off.</summary>
        internal bool IsDisabled => m_disabled;

        /// <summary>The clock the engine's deferred events expire by; tests replace it.</summary>
        internal ISchedulerClock Clock { get; set; } = new MonotonicClock();

        /// <summary>Events kept for a script that is still loading, per item (Halcyon DeferredEventManager.MAX_DEFERRED_EVENTS).</summary>
        internal const int MaxDeferredEventsPerItem = 32;

        /// <summary>How long they are kept from the first one (Halcyon DeferredEventManager.EXPIRATION_SECONDS).</summary>
        internal const double DeferredEventLifetimeSeconds = 60;

        private static bool TryConvert(EventParams parms, out string name, out Value[] args, out DetectedRecord[] detected)
        {
            name = null;
            args = null;
            detected = null;
            if (parms == null || string.IsNullOrEmpty(parms.EventName))
                return false;
            try
            {
                Host.EventIndex(parms.EventName);
            }
            catch (SluaException)
            {
                return false; // not an event the runtime knows
            }
            name = parms.EventName;
            args = EventArguments.Convert(name, parms.Params);
            detected = DetectedRecord.From(parms.DetectParams);
            return true;
        }

        public bool SuspendScript(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null || m_scheduler == null)
                return false;
            ((IPolicyHost)this).SetPaused(itemID, PauseReason.Core, true);
            return true;
        }

        public bool ResumeScript(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null || m_scheduler == null)
                return false;
            ((IPolicyHost)this).SetPaused(itemID, PauseReason.Core, false);
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
        public bool GetScriptState(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return false;
            SchedulerScript script = record.Script;
            lock (record.PauseLock)
                return script.Running && !script.Killed && script.State != ScriptRunState.Removed && (record.Pause & PauseReason.Core) == 0;
        }

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
