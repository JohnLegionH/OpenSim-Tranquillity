/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using InWorldz.Phlox.VM;
using SLua.ScriptEngine.Bindings;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
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
    public partial class SLuaEngine : INonSharedRegionModule, IScriptModule, IScriptStateProvider, IParcelScriptPolicyEngine, IEventPoster, IPolicyHost
    {
        public event ScriptRemoved OnScriptRemoved;

#pragma warning disable CS0067 // raised once scripts run on the engine
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore CS0067

        public SLuaEngine() : this(new RegionLog(), NativeRuntime.GateForEngine) { m_makesItsStore = true; }

        private readonly IEngineLog m_log;
        private readonly RuntimeLoader m_loadRuntime;
        private SLuaSettings m_settings = new SLuaSettings();
        private bool m_enabled;
        private Scene m_scene;

        private ISavedStateStore m_store;

        /// <summary>The engine's own database of saved script state, when it keeps one (the store it was given, or the one it made).</summary>
        private SqliteStateStore m_stateStore;

        /// <summary>The database this engine made itself and so closes when the region goes; null for a store it was given.</summary>
        private SqliteStateStore m_ownedStateStore;

        /// <summary>The simulator is shutting down: a script removed now is not deleted, so its saved state is still there for the restart.</summary>
        private volatile bool m_shuttingDown;

        private readonly bool m_makesItsStore;

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

            /// <summary>The state the script was last seen in, to tell a change of state from the first.</summary>
            public int LastState = -1;

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

        /// <summary>The script functions of each script, for the ends of a script (reset, change of state, unload) that release what the functions hold.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, Phlox.ScriptEngine.LSLSystemAPI> m_realApis = new System.Collections.Concurrent.ConcurrentDictionary<UUID, Phlox.ScriptEngine.LSLSystemAPI>();

        private Phlox.ScriptEngine.LSLSystemAPI RealApiOf(UUID itemId) => m_realApis.TryGetValue(itemId, out Phlox.ScriptEngine.LSLSystemAPI api) ? api : null;

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

        /// <summary>Why the engine holds the script back at its start (its saved state could not be read), or null.</summary>
        internal string HeldReasonOf(UUID itemId) => RecordOf(itemId)?.Script?.StateHeld;

        /// <summary>Whether the engine holds a load record for the item (a rez it accepted and has not forgotten).</summary>
        internal bool IsTracking(UUID itemID)
        {
            lock (m_tracked) return m_tracked.ContainsKey(itemID);
        }

        private readonly Dictionary<UUID, uint> m_tracked = new Dictionary<UUID, uint>();

        internal SLuaSettings Settings => m_settings;

        /// <summary>The lane the async and service functions run on; null before the region is added.</summary>
        internal ServiceLane Lane => m_lane;

        private ServiceLane m_lane;

        /// <summary>What each script's ll functions run against, by scheduler script id: its API, its item and the lane.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, ScriptContext> m_contexts = new System.Collections.Concurrent.ConcurrentDictionary<long, ScriptContext>();

        /// <summary>The host side of the script functions for the region; null before the region is added.</summary>
        internal EngineHostServices HostServices { get; private set; }

        private IConfigSource m_configSource;

        /// <summary>
        /// The script functions of one script: the real LSLSystemAPI, built on the engine's host services, for the functions the
        /// host serves (<see cref="LlServed"/>), and <paramref name="fallback"/> for the rest.
        /// </summary>
        internal Phlox.ScriptEngine.ILSLSystemAPI CreateScriptApi(SchedulerScript script, UUID itemId, uint localId, Phlox.ScriptEngine.ILSLSystemAPI fallback)
        {
            EngineHostServices host = HostServices ?? throw new InvalidOperationException("the engine has no region yet");
            var real = new Phlox.ScriptEngine.LSLSystemAPI(host, m_scene?.GetSceneObjectPart(localId), localId, itemId)
            {
                RuntimeAccess = new EngineScriptAccess(script),
                // An error the script functions speak on DEBUG_CHANNEL is told once and then as a count, like the engine's own errors.
                ShoutErrorFilter = text => AllowErrorShout(itemId, text),
            };
            m_realApis[itemId] = real;
            return ApiRouter.Create(real, fallback);
        }

        /// <summary>What the script functions ask of the engine: the facilities the engine has, wired to its own members.</summary>
        private EngineHostBackend BuildHostBackend() => new EngineHostBackend
        {
            PostScriptEvent = PostScriptEvent,
            PostObjectEvent = PostObjectEvent,
            HasScript = item => (HasScript(item, out bool running), running),
            SetTimer = HostSetTimer,
            SetItemRunningFlag = SetItemRunningFlag,
            ResetScript = item =>
            {
                ScriptRecord record = RecordOf(item);
                if (record != null) OnScriptReset(record.LocalID, item);
            },
            RequestParcelCheck = RecheckParcel,
            PhysicsFrameTimeAvg = () => PhysicsFrameTimeAvg,
            PostDataserverToPrim = PostDataserverToPrim,
            SetMinEventDelay = HostSetMinEventDelay,
            EventQueueFreeSpacePercentage = HostEventQueueFreeSpace,
        };

        /// <summary>
        /// llSetTimerEvent: the script's timer is set on its worker, so this is valid while the script is inside a call (the API
        /// runs inside the script's host function); a timer asked for from any other thread is not set.
        /// </summary>
        private void HostSetTimer(UUID itemId, float seconds)
        {
            SchedulerScript script = RecordOf(itemId)?.Script;
            if (script != null && ReferenceEquals(script, SchedulerScript.Current))
                script.SetTimer(seconds);
        }

        /// <summary>The region's listen registry (Phlox's manager, owned by this engine); null before the region is added.</summary>
        internal Phlox.ScriptEngine.PhloxListenManager Listens => m_chat?.Listens;

        private EngineChat m_chat;

        /// <summary>Where the engine sets each script's event flags on its prim; tests replace it before the region is added.</summary>
        internal IPrimEventFlags PrimFlags { get; set; }

        public string Name => EngineIdentity.EngineName;
        public string ScriptEngineName => EngineIdentity.EngineName;
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_settings = SLuaSettings.Read(source, m_log);
            m_configSource = source;
            if (!m_settings.Enabled)
                return;

            // The native runtime is needed before any script can run. If it cannot be used the engine stays out of
            // the region: it logs one line and registers nothing, and the other engines carry on.
            m_enabled = m_loadRuntime(m_settings, m_log);
            if (m_enabled)
                m_log.Info($"ll functions: {LlServed.Served.Length} run on the script API; {LlBindings.Unavailable.Count} are refused ({LlServed.NotServed.Length} wait on host members this engine lacks (\"not available on this engine yet\"), {LlCoverage.PrivateInSl.Length} are private in SL and intentionally absent (\"private in SL and not available to scripts\"), {LlCoverage.PhloxGaps.Length} are Phlox gaps)");
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            m_scene = scene;
            OpenStateStore();
            HostServices = new EngineHostServices(m_settings, scene, m_configSource, this, BuildHostBackend());
            m_chat = new EngineChat(scene, m_configSource, m_settings, PostScriptEvent, TellOwnerOf);
            HostServices.Chat = m_chat;
            HostServices.ScriptEngine = this;
            HostServices.AsyncCommands = new Phlox.ScriptEngine.AsyncCommand.AsyncCommandManager(this) { HostServices = HostServices }; // the plugins read the engine's settings (the HTTP caps) from its host services
            m_chat.Subscribe();
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
                m_lane = new ServiceLane(m_scheduler, m_settings.ServiceCallDeferral, m_settings.ServiceCallThreads, m_settings.ServiceCallTimeoutMs);
            }
            catch (Exception e)
            {
                m_log.Error("The script scheduler could not be started; scripts given to this engine will not run: " + e);
            }
            m_scene.EventManager.OnRezScript += OnRezScript;
            m_scene.EventManager.OnGetScriptRunning += OnGetScriptRunning;
            m_scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_scene.EventManager.OnObjectBeingRemovedFromScene += OnObjectLeavingScene;
            m_scene.EventManager.OnScriptReset += OnScriptReset;
            m_scene.EventManager.OnStartScript += OnStartScript;
            m_scene.EventManager.OnStopScript += OnStopScript;
            m_scene.EventManager.OnShutdown += OnShutdown;
            RegisterConsoleCommands();
            if (m_settings.WarmUp)
                ApiWarmUp.Start(m_log);
            m_hooks = new SceneHooksA(scene, this, m_settings.PhysicsThrottle, Clock);
            m_hooks.Subscribe();
            m_policy = new SceneHooksB(scene, this, m_grants, new LiveClock(this));
            m_policy.Subscribe();
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        /// <summary>
        /// The stamp names the bound-function list and the functions that answer "not available"; it changes with them, which
        /// makes the cache entries of an earlier list unused.
        /// </summary>
        internal static readonly string BoundFunctionHash = ComputeBoundFunctionHash();

        private static string ComputeBoundFunctionHash()
        {
            var text = new System.Text.StringBuilder("ll1");
            foreach (BoundFunction f in LlBindings.All)
                text.Append('|').Append(f.ApiName).Append(':').Append(f.Class);
            text.Append("|unavailable");
            foreach (string name in LlBindings.Unavailable)
                text.Append('|').Append(name);
            text.Append("|os1");
            foreach (OsFunction f in OsBindings.All)
                foreach (BoundFunction o in f.Overloads)
                    text.Append('|').Append(o.ApiName).Append(':').Append(o.Class).Append(':').Append(string.Join(",", o.ParamTypes));
            text.Append("|unavailable");
            foreach (string name in OsBindings.Unavailable)
                text.Append('|').Append(name);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>
        /// The simulator is shutting down: the scheduler stops, which saves every script, and the database is written out and closed.
        /// The region's modules are not always removed on the way out, so the save does not wait for RemoveRegion.
        /// </summary>
        private void OnShutdown()
        {
            StopErrorSweep();
            m_shuttingDown = true; // the scripts the region takes out on the way down keep their rows: the restart restores from them
            SLuaScheduler scheduler = m_scheduler;
            m_scheduler = null;
            try { scheduler?.Dispose(); }
            catch (Exception e) { m_log.Error("The scripts could not be stopped and saved at shutdown: " + e.Message); }
            CloseStateStore();
        }

        /// <summary>
        /// Opens the engine's database of saved script state: the store the engine was given, or (in a region) its own file under the
        /// engine's state folder. With SaveScriptState off there is none, and nothing is saved or restored.
        /// </summary>
        private void OpenStateStore()
        {
            m_stateStore = null;
            if (m_settings == null || !m_settings.SaveScriptState)
                return;
            if (m_store is SqliteStateStore given)
            {
                m_stateStore = given;
                return;
            }
            if (!m_makesItsStore)
                return;
            try
            {
                string file = Path.Combine(m_settings.StateRoot, "state", "script_state.db");
                m_ownedStateStore = new SqliteStateStore(file, EngineIdentity.EngineName, m_log, 5000);
                m_stateStore = m_ownedStateStore;
                m_store = m_ownedStateStore;
                m_log.Info("Saved script state: " + file);
            }
            catch (Exception e)
            {
                m_log.Error("The database of saved script state could not be opened; scripts of this region start fresh after a restart and are not saved: " + e.Message);
            }
        }

        /// <summary>Writes what the scheduler handed over and closes the database the engine made.</summary>
        private void CloseStateStore()
        {
            try { m_stateStore?.Flush(); }
            catch (Exception e) { m_log.Error("The saved script state could not be written out: " + e.Message); }
            if (m_ownedStateStore != null)
            {
                m_ownedStateStore.Dispose();
                m_ownedStateStore = null;
                m_store = new EmptyStateStore();
            }
            m_stateStore = null;
        }

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
                VmFactory = () =>
                {
                    var vm = new Vm(watchdog);
                    LlBindings.Register(vm, ContextOf, serviceLane: true, unavailable: true);
                    OsBindings.Register(vm, ContextOf, serviceLane: true, unavailable: true);
                    return vm;
                },
                Log = m_log,
                StateSink = m_stateStore,
                EngineName = EngineIdentity.EngineName,
                StateSaveIntervalMs = m_settings.StateSaveIntervalMs,
                ScriptFaulted = OnScriptFaulted,
                ScriptFaultRestored = OnScriptFaultRestored,
                StateChanged = OnStateChanged,
                HandlersChanged = OnHandlersChanged,
                ScriptError = OnScriptError,
                WorkerStopped = OnWorkerStopped,
            };
        }

        /// <summary>The context of the script a function was called from, made when the script joined the scheduler.</summary>
        private ScriptContext ContextOf(SchedulerScript script)
        {
            ApiWarmUp.WaitDone(); // a first call that comes while the API is still being compiled waits for it
            return m_contexts.TryGetValue(script.Id, out ScriptContext context) ? context : throw new InvalidOperationException("the script is not known to the engine");
        }

        /// <summary>How long GetScriptErrors waits for a compile; the region's own wait for a save is the same 15 seconds.</summary>
        internal static TimeSpan ErrorWaitTimeout = TimeSpan.FromSeconds(15);

        public void RemoveRegion(Scene scene)
        {
            if (m_scene == null) return;
            m_scene.EventManager.OnRezScript -= OnRezScript;
            m_scene.EventManager.OnGetScriptRunning -= OnGetScriptRunning;
            m_scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_scene.EventManager.OnObjectBeingRemovedFromScene -= OnObjectLeavingScene;
            m_scene.EventManager.OnScriptReset -= OnScriptReset;
            m_scene.EventManager.OnStartScript -= OnStartScript;
            m_scene.EventManager.OnStopScript -= OnStopScript;
            m_scene.EventManager.OnShutdown -= OnShutdown;
            StopErrorSweep();
            m_advertisedLua?.RemoveFeature(LuaScriptsEnabledFeature);
            m_advertisedLua = null;
            m_chat?.Unsubscribe();
            m_chat = null;
            HostServices?.AsyncCommands?.Shutdown();
            m_hooks?.Unsubscribe();
            m_hooks = null;
            m_policy?.Unsubscribe();
            m_policy = null;
            lock (m_tracked) m_tracked.Clear();
            lock (m_starts) m_starts.Clear();
            lock (m_deferred) m_deferred.Clear();
            m_loader?.Dispose();
            m_loader = null;
            m_lane?.Dispose();
            m_lane = null;
            m_scheduler?.Dispose(); // saves every script's state
            m_scheduler = null;
            CloseStateStore();
            m_contexts.Clear();
            lock (m_records) m_records.Clear();
            HostServices = null;
            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            m_policy?.SubscribeEstate();
            // The region's world communication may not be registered when the engine is added, so it is looked up here. Without it
            // chat cannot pass between script engines, so the engine disables itself with one line, as the Phlox engine does: it
            // takes no script, and the region and the other engines carry on.
            IWorldComm worldComm = scene?.RequestModuleInterface<IWorldComm>();
            if (worldComm == null)
            {
                lock (m_deferred)
                {
                    if (m_disabled)
                        return;
                    m_disabled = true;
                }
                m_log.Error("No IWorldComm module found, the SLua script engine is disabled");
                return;
            }
            m_chat?.Connect(worldComm);
            AdvertiseLuaScripts(scene);
        }

        /// <summary>
        /// Tells viewers the region runs Lua scripts: the simulator feature "LuaScriptsEnabled" is what a viewer reads before it
        /// offers Lua in its script editor and creates new scripts with the Lua subtype. Said only by an engine that is on and
        /// working; an engine that is off, or has disabled itself, leaves the region's features as they were.
        /// </summary>
        private void AdvertiseLuaScripts(Scene scene)
        {
            if (!m_enabled || m_disabled || scene is null)
                return;
            ISimulatorFeaturesModule features = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (features is null)
                return;
            features.AddFeature(LuaScriptsEnabledFeature, OSD.FromBoolean(true));
            m_advertisedLua = features;
        }

        /// <summary>The simulator feature a viewer reads to know that the region runs Lua scripts.</summary>
        internal const string LuaScriptsEnabledFeature = "LuaScriptsEnabled";

        private ISimulatorFeaturesModule m_advertisedLua;

        /// <summary>
        /// Every engine receives every rez with the engine name the region selected. The first-line header still takes
        /// priority, so a script is ours exactly when <see cref="EngineHeader.Owner"/> gives it to this engine.
        /// </summary>
        private void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
            // A script saved with a viewer's Luau target belongs to the engine the region chose for it, whatever its first
            // line says: the region put the target ahead of the first line, and this test must not undo that.
            TaskInventoryItem rezItem = m_scene?.GetSceneObjectPart(localID)?.Inventory?.GetInventoryItem(itemID);
            ScriptCompileTarget target = rezItem is null ? ScriptCompileTarget.Unspecified : ScriptCompileTargets.FromFlags(rezItem.Flags);
            string owner = target != ScriptCompileTarget.Unspecified ? engine : EngineHeader.Owner(script, engine, LoadedEngineNames());
            if (owner != ScriptEngineName)
            {
                Disown(itemID);
                return;
            }

            if (m_disabled)
                return;

            lock (m_tracked) m_tracked[itemID] = localID;
            lock (m_starts) m_starts[itemID] = new StartInfo { StartParam = startParam, PostOnRez = postOnRez, StateSource = stateSource };

            SceneObjectPart part = m_scene?.GetSceneObjectPart(localID);
            m_loader?.Post(new LoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                AssetID = part?.Inventory?.GetInventoryItem(itemID)?.AssetID ?? UUID.Zero,
                Target = target,
                Source = script,
            });
        }

        /// <summary>
        /// The viewer's editor asks whether a script runs and which compiler entry to show. Every engine hears the
        /// question, so this one answers only for a script it holds. The target saved with the item is reported even when
        /// the compile failed; a script with none is described by the language it was compiled as.
        /// </summary>
        private void OnGetScriptRunning(IClientAPI client, UUID objectID, UUID itemID)
        {
            if (m_disabled || !IsTracking(itemID))
                return;

            TaskInventoryItem item = m_scene?.GetSceneObjectPart(objectID)?.Inventory?.GetInventoryItem(itemID);
            ScriptCompileTarget target = item is null ? ScriptCompileTarget.Unspecified : ScriptCompileTargets.FromFlags(item.Flags);
            ScriptRecord record = RecordOf(itemID);
            if (target == ScriptCompileTarget.Unspecified && record != null)
                target = record.Script.Language == ScriptLanguage.Slua ? ScriptCompileTarget.Luau : ScriptCompileTarget.LslOnLuau;

            client.SendScriptRunningReply(objectID, itemID, record?.Script.IsRunning ?? false, target);
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
            UnloadApi(itemID);
            DropRecord(itemID);
            m_chat?.Listens.Forget(itemID); // an unload ends the script's listens, after the script has stopped
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

            // The script's saved state, if this engine owns one for this item and asset (a record of another engine is left alone; one the
            // store cannot read holds the script back with its row untouched).
            byte[] carried = TakeCarried(ready.ItemID, ready.AssetID);
            StateLoadResult saved = null;
            if (carried != null || (m_stateStore != null && ready.AssetID != UUID.Zero))
                saved = StateJoin.Load(m_stateStore, EngineIdentity.EngineName, ready.ItemID, ready.AssetID, m_settings.StateLoadRetries, m_settings.StateLoadRetryDelayMs, System.Threading.Thread.Sleep, m_log, carried);
            if (saved?.Kind == StateLoadKind.Held)
                running = false; // the script is not started over a state it could not read

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
            if (m_stateStore != null && ready.AssetID != UUID.Zero)
            {
                script.ItemId = ready.ItemID;
                script.AssetId = ready.AssetID;
            }
            if (saved?.Kind == StateLoadKind.Restore)
            {
                script.RestoreRecord = saved.Record;
                script.RestoredFromCarried = saved.Carried;
                if (saved.Record.Faulted)
                {
                    script.Fault = (Bindings.FaultKind)saved.Record.FaultKind;
                    script.FaultMessage = saved.Record.FaultMessage;
                }
                else
                    script.CurrentState = saved.Record.LslState;
            }
            else if (saved?.Kind == StateLoadKind.Held)
                script.StateHeld = saved.Reason;
            var record = new ScriptRecord { ItemID = ready.ItemID, LocalID = ready.LocalID, AssetID = ready.AssetID, Script = script, Start = start, StartupPending = true, LastState = script.CurrentState };
            m_contexts[script.Id] = new ScriptContext(script, CreateScriptApi(script, ready.ItemID, ready.LocalID, UnavailableApi.Create()), ready.ItemID, m_lane);
            WireListens(record);
            WireExtras(record);
            // The estate switch and the parcel rule decide before the script is visible or started: a script they hold back starts paused, never refused.
            record.Pause = m_policy?.ReasonsFor(ready.ItemID, ready.LocalID) ?? PauseReason.None;
            if (record.Pause != PauseReason.None)
            {
                foreach (PauseReason reason in new[] { PauseReason.Estate, PauseReason.Parcel })
                    if ((record.Pause & reason) != 0)
                        PolicyStats.Count(reason, true);
                scheduler.Suspend(script, (record.Pause & (PauseReason.Estate | PauseReason.Parcel)) != 0);
            }
            ScriptRecord replaced;
            lock (m_records)
            {
                m_records.TryGetValue(ready.ItemID, out replaced);
                m_records[ready.ItemID] = record;
            }
            if (replaced != null)
            {
                scheduler.Remove(replaced.Script);
                m_contexts.TryRemove(replaced.Script.Id, out _);
            }

            m_policy?.Resync(ready.ItemID); // closes the window between the decision above and the record being visible

            if (saved?.Kind == StateLoadKind.Held)
            {
                m_log.Error($"Script {ready.ItemID} (asset {ready.AssetID}) is held and not started: {script.StateHeld}");
                TellOwner(record, $"Script {ready.AssetID} was not started: {script.StateHeld}");
            }

            if (running)
            {
                // The flags are set before the script can hear anything, so its first events are not lost.
                ApplyEventFlags(record, 0, true);
                scheduler.Start(script);
                BeginRunning(record);
            }
            else
            {
                // A script a fault stopped is put back stopped even though its Running flag is off (the fault turned it off): the restore marks
                // it faulted and quiet, so a later "set running" resets it as it does for any script a fault stopped.
                if (saved?.Kind == StateLoadKind.Restore && saved.Record.Faulted)
                    scheduler.Start(script);
                scheduler.SetRunning(script, false);
                ApplyEventFlags(record, 0, false);
            }
        }

        /// <summary>
        /// Lets the scheduler save a script's listens with its state and put them back: the listens a script holds are in its runtime state
        /// (ActiveListens), and the region's listen registry is told of each one again, with the handle the script keeps, before the script runs.
        /// </summary>
        private void WireListens(ScriptRecord record)
        {
            UUID item = record.ItemID;
            record.Script.ListenSnapshot = () =>
            {
                RuntimeState state = RealApiOf(item)?.RuntimeAccess?.ScriptState;
                if (state?.ActiveListens == null)
                    return null;
                var list = new List<ActiveListen>(state.ActiveListens.Values);
                return list.ToArray();
            };
            record.Script.ListenRestore = listens =>
            {
                RuntimeState state = RealApiOf(item)?.RuntimeAccess?.ScriptState;
                SceneObjectPart part = m_scene?.GetSceneObjectPart(record.LocalID);
                foreach (ActiveListen l in listens)
                {
                    state?.AddActiveListen(l);
                    UUID.TryParse(l.Key ?? "", out UUID key);
                    UUID host = part != null ? part.UUID : UUID.Zero;
                    if (!string.IsNullOrEmpty(l.HostKey) && UUID.TryParse(l.HostKey, out UUID bot))
                        host = bot;
                    m_chat?.Listens.Restore(record.LocalID, item, host, key, l);
                }
            };
        }

        /// <summary>
        /// Lets the scheduler save what the API keeps for a script in its runtime state, as Second Life keeps it across a restart, a take
        /// and a crossing: the repeating sensor and the taken controls (attributes), and the permission record. The record is taken on the
        /// worker, where the API runs. A restored script gets them back before it runs: the grant is decided as Phlox decides it (a state
        /// that came with an object is checked again, one from the database comes back whole), a record of taken controls stays only
        /// while the grant it rests on does, and a script that runs sets its repeating sensor again.
        /// </summary>
        private void WireExtras(ScriptRecord record)
        {
            UUID item = record.ItemID;
            record.Script.ExtrasFill = rec =>
            {
                Phlox.ScriptEngine.LSLSystemAPI api = RealApiOf(item);
                RuntimeState state = api?.RuntimeAccess?.ScriptState;
                if (state == null)
                    return;
                api.NoteGrantForCarry();
                if (state.MiscAttributes != null)
                {
                    foreach (KeyValuePair<int, object[]> kv in new List<KeyValuePair<int, object[]>>(state.MiscAttributes))
                    {
                        if (kv.Value != null && Array.TrueForAll(kv.Value, ScriptStateRecord.CanHold))
                            rec.Misc.Add(new SavedMisc { Key = kv.Key, Values = kv.Value });
                        else
                            m_log.Warn($"Script {item}: its attribute {kv.Key} holds a value that cannot be saved and is left out");
                    }
                }
                rec.PermsGranter = state.PermsGranter;
                rec.PermsMask = state.GrantedPermsMask;
                rec.PermsOwner = state.PermsOwner;
                rec.PermsExperience = state.PermsExperience;
                rec.PermsUnverified = state.PermsUnverified;
            };
            record.Script.ExtrasRestore = (rec, carried) =>
            {
                Phlox.ScriptEngine.LSLSystemAPI api = RealApiOf(item);
                RuntimeState state = api?.RuntimeAccess?.ScriptState;
                if (state == null)
                    return;
                foreach (SavedMisc m in rec.Misc)
                    state.MiscAttributes[m.Key] = m.Values;
                state.PermsGranter = rec.PermsGranter;
                state.GrantedPermsMask = rec.PermsMask;
                state.PermsOwner = rec.PermsOwner;
                state.PermsExperience = rec.PermsExperience;
                state.PermsUnverified = rec.PermsUnverified;
                api.RestoreSavedGrant(carried);
                api.DropGrantRecordsWithoutGrant();
                // A script that runs takes up its sensor and its controls again; a stopped one does when it is started (OnStartScript).
                if (record.Script.Running)
                    api.OnScriptInjected(record.Start?.StateSource == (int)StateSource.PrimCrossing);
            };
        }

        // ---- the events each script asks the region for ----

        /// <summary>
        /// Tells the region which events the script handles, so it raises touches, collisions and the like for the prim.
        /// An LSL script's flags come from the handler mask of its state in the compiled asset (the union over the scripts
        /// of a prim is the region's own, kept per script item); a stopped script asks for nothing. An SLua script registers
        /// its handlers while it runs, so its flags come from the handler set the runtime reports, and are set again each
        /// time the runtime reports a change. Serialized per script so a stop and a state change cannot cross.
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
                else if (running && record.Script.Language == ScriptLanguage.Slua)
                    flags = EventFlagMap.ForMask(record.Script.HandlerMask);
                try { sink.Set(record.LocalID, record.ItemID, flags); }
                catch (Exception e) { m_log.Error($"The event flags of script {record.ItemID} could not be set: {e.Message}"); }
            }
        }

        /// <summary>An SLua script's handler set changed (on its worker's thread): the region is told the events it handles now.</summary>
        private void OnHandlersChanged(SchedulerScript script)
        {
            ScriptRecord record = FindRecord(script);
            if (record != null)
                ApplyEventFlags(record, script.CurrentState, script.Running);
        }

        /// <summary>A script changed state (on its worker's thread): the new state's handlers decide what the region raises.</summary>
        private void OnStateChanged(SchedulerScript script)
        {
            ScriptRecord record = FindRecord(script);
            if (record == null)
                return;
            // A change to another state releases the script's listens (wiki: State); the first state is not a change.
            int was = Interlocked.Exchange(ref record.LastState, script.CurrentState);
            if (was != -1 && was != script.CurrentState)
            {
                m_chat?.Listens.Remove(record.ItemID);
                RealApiOf(record.ItemID)?.OnStateChange(); // a sensor goes with the state; a request and a URL stay (wiki State, as Phlox)
            }
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
                {
                    // An object that arrived with riders is held by the region (its scripts start paused, PauseReason.Arrival), so the event queues until the region releases it.
                    scheduler.Post(record.Script, "changed", Value.Int(change));
                }
            }
            FlushDeferred(record);
        }

        /// <summary>
        /// The changed() bits a start brings: CHANGED_REGION_START for a region start (https://wiki.secondlife.com/wiki/CHANGED_REGION_START);
        /// CHANGED_REGION for a crossing or a teleport, plus CHANGED_TELEPORT for a worn object that teleported
        /// (https://wiki.secondlife.com/wiki/CHANGED_REGION, https://wiki.secondlife.com/wiki/CHANGED_TELEPORT), to a
        /// root prim only. Phlox's rule (PhloxExecutionScheduler.ArrivalChange); the wait for riders is the region's own hold.
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

        /// <summary>The script is leaving the engine: its sensors, requests and URLs end, and the controls it took are let go.</summary>
        private void UnloadApi(UUID itemID)
        {
            if (m_realApis.TryRemove(itemID, out Phlox.ScriptEngine.LSLSystemAPI api))
                api.OnScriptUnloaded(InWorldz.Phlox.Types.ScriptUnloadReason.Unloaded, InWorldz.Phlox.VM.RuntimeState.LocalDisableFlag.None);
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
            m_contexts.TryRemove(record.Script.Id, out _);
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
            UnloadApi(itemID);
            DropRecord(itemID);
            m_chat?.Listens.Forget(itemID); // an unload ends the script's listens
            ForgetCarried(itemID);
            // The script is gone, from its prim or with its object: its row in the engine's own database goes with it. (A take carries the
            // state in the object, a crossing in the next region's copy.) Not at shutdown, where the rows are what the restart restores from.
            DeleteRowOrWaitForObject(localID, itemID);
            OnScriptRemoved?.Invoke(itemID);
        }

        /// <summary>
        /// Items whose scripts were taken out while their item is still in its prim, by the object they are in: "scripts off"
        /// (Scene.ScriptsEnabled = false) takes every script out and the items stay, and Phlox keeps their rows then. The rows go when the
        /// object leaves the scene (<see cref="OnObjectLeavingScene"/>).
        /// </summary>
        private readonly Dictionary<UUID, HashSet<UUID>> m_removedWithObject = new Dictionary<UUID, HashSet<UUID>>();

        /// <summary>
        /// A script was taken out. If its item left its prim (deleted, moved, given away) the row goes now. If the item is still there the
        /// script was only stopped (scripts switched off) or its object is being deleted, so the row waits for the object to leave the scene.
        /// Not at shutdown, where the rows are what the restart restores from.
        /// </summary>
        private void DeleteRowOrWaitForObject(uint localID, UUID itemID)
        {
            if (m_shuttingDown || !m_settings.SaveScriptState)
                return;
            SceneObjectPart part = m_scene?.GetSceneObjectPart(localID);
            SceneObjectGroup group = part?.ParentGroup;
            if (group == null || part.Inventory.GetInventoryItem(itemID) == null)
            {
                m_store?.Delete(itemID);
                return;
            }
            lock (m_removedWithObject)
            {
                if (!m_removedWithObject.TryGetValue(group.UUID, out HashSet<UUID> items))
                    m_removedWithObject[group.UUID] = items = new HashSet<UUID>();
                items.Add(itemID);
            }
        }

        /// <summary>An object is leaving the scene (deleted, taken, crossed): the rows of the scripts taken out of it go.</summary>
        private void OnObjectLeavingScene(SceneObjectGroup group)
        {
            HashSet<UUID> items;
            lock (m_removedWithObject)
            {
                if (!m_removedWithObject.Remove(group.UUID, out items))
                    return;
            }
            if (m_shuttingDown || !m_settings.SaveScriptState)
                return;
            foreach (UUID itemID in items)
                m_store?.Delete(itemID);
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            m_chat?.Listens.Remove(itemID); // a reset removes the script's listens
            RealApiOf(itemID)?.OnScriptReset(); // and its sensors and requests, and ends its permissions and the controls taken under them
            if (record.Script.Killed)
                SetItemRunningFlag(record.LocalID, itemID, true); // a reset is what starts a script a fault stopped
            m_scheduler?.Reset(record.Script);
        }

        private void OnStartScript(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            // A script that was stopped takes up the sensor repeat and the controls its stop let go, as Phlox's start does (OnScriptInjected).
            // A script a fault stopped starts fresh instead, and one still held by its object's crossing or a pause gets them back when that ends.
            bool injected = !record.Script.Running && !record.Script.Killed && !SensorHeld(record);
            SetItemRunningFlag(record.LocalID, itemID, true);
            m_scheduler?.SetRunning(record.Script, true);
            if (injected)
                RealApiOf(itemID)?.OnScriptInjected(false);
            ApplyEventFlags(record, record.Script.CurrentState, true);
            BeginRunning(record);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return;
            bool wasRunning = record.Script.Running;
            m_scheduler?.SetRunning(record.Script, false);
            // The sensor repeat stops, owed dataserver replies are dropped and taken controls are let go; their records stay, so a
            // start carries on with them (Phlox: LSLSystemAPI.OnScriptStopped, from its stop).
            if (wasRunning)
                RealApiOf(itemID)?.OnScriptStopped();
            ApplyEventFlags(record, record.Script.CurrentState, false);
        }

        /// <summary>The pauses that stop a script's sensor repeat: the parcel and the estate switch, as Phlox's parcel pause, and the object's crossing.</summary>
        private const PauseReason SensorHolds = PauseReason.Parcel | PauseReason.Estate | PauseReason.Crossing;

        private static bool SensorHeld(ScriptRecord record)
        {
            lock (record.PauseLock)
                return (record.Pause & SensorHolds) != 0;
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

        /// <summary>
        /// A script a fault had stopped was put back stopped from its saved state (a restart, a rez, a crossing). The owner was told when the fault
        /// happened, so there is one line in the log and the item's Running flag is set off, and no message.
        /// </summary>
        private void OnScriptFaultRestored(SchedulerScript script)
        {
            ScriptRecord record = FindRecord(script);
            if (record == null)
                return;
            m_log.Info($"Script {record.ItemID} (asset {record.AssetID}) stays stopped by an earlier fault: {script.FaultMessage}");
            SetItemRunningFlag(record.LocalID, record.ItemID, false);
            ApplyEventFlags(record, script.CurrentState, false);
        }

        /// <summary>A host function reported an error for the script (on its worker's thread); the scheduler pauses the script for the error pause.</summary>
        private void OnScriptError(SchedulerScript script, string text)
        {
            ScriptRecord record = FindRecord(script);
            if (record != null)
                TellRuntimeError(record, text);
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

        private void TellOwnerOf(UUID itemId, string text)
        {
            ScriptRecord record = RecordOf(itemId);
            if (record != null)
                TellOwner(record, text);
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

        // ---- the status command ----

        /// <summary>The most scripts the status command lists before it asks for a filter.</summary>
        private const int StatusListLimit = 40;

        /// <summary>
        /// The lines of the status command for the scripts that match <paramref name="filter"/>: an item id, or part of an object's or a
        /// script's name (empty: all). A header of the engine's figures, then two lines for each script: its name and item, then its run state
        /// (running, stopped, FAULTED with the reason, HELD with the reason), why it is paused, the events waiting, the events dropped while
        /// it was stopped, its memory, its minimum event delay and its waived overruns.
        /// </summary>
        internal IReadOnlyList<string> StatusLines(string filter)
        {
            var lines = new List<string>();
            SchedulerStats stats = m_scheduler?.GetStats();
            var shown = new List<(string Object, string Script, ScriptRecord Record)>();
            long waived = 0;
            lock (m_records)
            {
                foreach (ScriptRecord r in m_records.Values)
                {
                    waived += r.Script.WaivedOverruns;
                    SceneObjectPart part = m_scene?.GetSceneObjectPart(r.LocalID);
                    string objectName = part?.ParentGroup?.Name ?? part?.Name ?? "(unknown object)";
                    string scriptName = part?.Inventory?.GetInventoryItem(r.ItemID)?.Name ?? "(not in a prim)";
                    if (StatusMatches(filter, r.ItemID, objectName, scriptName))
                        shown.Add((objectName, scriptName, r));
                }
            }
            shown.Sort((a, b) => { int c = string.Compare(a.Object, b.Object, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.Compare(a.Script, b.Script, StringComparison.OrdinalIgnoreCase); });

            lines.Add($"{EngineIdentity.EngineName} engine: scripts {stats?.Scripts ?? 0}, stopped {stats?.Stopped ?? 0}, paused {stats?.Suspended ?? 0}, faulted {stats?.Faulted ?? 0}, workers {stats?.Workers.Length ?? 0}, failed workers {stats?.FailedWorkers ?? 0}");
            lines.Add($"  events dropped: while stopped {stats?.DroppedWhileStopped ?? 0}, unhandled {stats?.DroppedUnhandled ?? 0}, queue full {stats?.DroppedFull ?? 0}; kills {stats?.Kills ?? 0}; punishments {stats?.Punishments ?? 0}; waived overruns {waived}");
            lines.Add($"  policy: parcel paused {PolicyStats.ParcelPaused}, resumed {PolicyStats.ParcelResumed}; estate paused {PolicyStats.EstatePaused}, resumed {PolicyStats.EstateResumed}; crossing held {PolicyStats.CrossingHeld}, released {PolicyStats.CrossingReleased}");
            lines.Add(m_stateStore == null
                ? "  saved state: off"
                : $"  saved state: state writes done {m_stateStore.WritesDone}, held scripts {CountHeld()}");

            if (shown.Count == 0)
            {
                lines.Add(string.IsNullOrWhiteSpace(filter) ? "No scripts." : $"No script matches '{filter}'.");
                return lines;
            }
            for (int i = 0; i < shown.Count && i < StatusListLimit; i++)
            {
                (string objectName, string scriptName, ScriptRecord r) = shown[i];
                SchedulerScript s = r.Script;
                lines.Add($"{objectName} / {scriptName}  {r.ItemID}");
                string state;
                PauseReason paused = PauseReasonsOf(r.ItemID);
                if (s.StateHeld != null) state = "HELD: " + s.StateHeld;
                else if (s.Killed) state = "FAULTED: " + FirstLine(s.FaultMessage);
                else if (!s.Running) state = "stopped";
                else state = "running";
                var detail = new System.Text.StringBuilder("  ").Append(state);
                if (paused != PauseReason.None) detail.Append(", paused: ").Append(paused);
                detail.Append($", queued {s.QueuedEvents}, dropped while stopped {s.DroppedWhileStopped}, mem {s.MemoryUsed} B");
                if (s.MinEventDelay > 0) detail.Append($", min event delay {s.MinEventDelay:0.###} s");
                if (s.WaivedOverruns > 0) detail.Append($", waived overruns {s.WaivedOverruns}");
                lines.Add(detail.ToString());
            }
            if (shown.Count > StatusListLimit)
                lines.Add($"... {shown.Count - StatusListLimit} more scripts; give an object name, a script name or an item id as a filter to see them");
            return lines;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            int end = text.IndexOf('\n');
            if (end > 0 && text[end - 1] == '\r')
                end--;
            return end < 0 ? text : text.Substring(0, end);
        }

        private int CountHeld()
        {
            int held = 0;
            lock (m_records)
                foreach (ScriptRecord r in m_records.Values)
                    if (r.Script.StateHeld != null) held++;
            return held;
        }

        private static bool StatusMatches(string filter, UUID item, string objectName, string scriptName)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;
            filter = filter.Trim();
            if (UUID.TryParse(filter, out UUID id))
                return item == id;
            return objectName.Contains(filter, StringComparison.OrdinalIgnoreCase) || scriptName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The region's console: slua status, suspend and resume, with the guard that keeps a command for another region's engine from answering.</summary>
        private void RegisterConsoleCommands()
        {
            if (MainConsole.Instance == null)
                return;
            string name = EngineIdentity.EngineName.ToLowerInvariant();
            MainConsole.Instance.Commands.AddCommand(EngineIdentity.EngineName, false, name + " status", name + " status [object name | script name | item id]",
                "Read-only: the engine's figures and, for each matching script, its run state, why it is held or paused, its queue, the events dropped while it was stopped, its memory and its minimum event delay.",
                (module, args) => HandleStatus(args));
            MainConsole.Instance.Commands.AddCommand(EngineIdentity.EngineName, false, name + " suspend", name + " suspend <object name | script name | item id>",
                "Pause the matching scripts without dropping their events; not saved, a restart clears it. The Running flag is not touched.",
                (module, args) => HandleSuspendResume(args, true));
            MainConsole.Instance.Commands.AddCommand(EngineIdentity.EngineName, false, name + " resume", name + " resume <object name | script name | item id>",
                "Let the scripts paused by the suspend command go on; the events that waited are delivered.",
                (module, args) => HandleSuspendResume(args, false));
        }

        private bool WrongConsoleScene() => !(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == m_scene);

        private void HandleStatus(string[] args)
        {
            if (WrongConsoleScene())
                return;
            string filter = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : string.Empty;
            foreach (string line in StatusLines(filter))
                MainConsole.Instance.Output(line);
        }

        private void HandleSuspendResume(string[] args, bool suspend)
        {
            if (WrongConsoleScene())
                return;
            if (args.Length < 3)
            {
                MainConsole.Instance.Output($"Usage: {args[0]} {args[1]} <object name | script name | item id>");
                return;
            }
            string filter = string.Join(" ", args, 2, args.Length - 2);
            int done = 0;
            var items = new List<UUID>();
            lock (m_records)
            {
                foreach (ScriptRecord r in m_records.Values)
                {
                    SceneObjectPart part = m_scene?.GetSceneObjectPart(r.LocalID);
                    if (StatusMatches(filter, r.ItemID, part?.ParentGroup?.Name ?? part?.Name ?? string.Empty, part?.Inventory?.GetInventoryItem(r.ItemID)?.Name ?? string.Empty))
                        items.Add(r.ItemID);
                }
            }
            foreach (UUID item in items)
                if (suspend ? SuspendScript(item) : ResumeScript(item))
                    done++;
            MainConsole.Instance.Output($"{(suspend ? "Suspended" : "Resumed")} {done} script(s).");
        }

        // ---- errors told to the owner ----

        private readonly ErrorRateLimiter m_errors = new ErrorRateLimiter();
        private System.Threading.Timer m_errorSweep;
        private readonly object m_errorSweepLock = new object();

        /// <summary>The runtime error of a script is told to its owner once; the same message again is counted and told as a count after a quiet interval.</summary>
        private void TellRuntimeError(ScriptRecord record, string text)
        {
            if (!m_settings.ErrorRateLimit)
            {
                TellOwner(record, text);
                return;
            }
            if (m_errors.Offer(record.ItemID, text, Clock.Now))
            {
                TellOwner(record, text);
                return;
            }
            EnsureErrorSweep();
        }

        /// <summary>True when the error is to be spoken now; a repeat is counted and told by the sweep.</summary>
        private bool AllowErrorShout(UUID itemId, string text)
        {
            if (!m_settings.ErrorRateLimit || m_errors.Offer(itemId, text, Clock.Now))
                return true;
            EnsureErrorSweep();
            return false;
        }

        private void EnsureErrorSweep()
        {
            lock (m_errorSweepLock)
            {
                if (m_errorSweep != null || m_shuttingDown)
                    return;
                int period = (int)Math.Max(50, Math.Min(1000, m_settings.ErrorQuietSeconds * 500));
                m_errorSweep = new System.Threading.Timer(_ => SweepErrors(), null, period, period);
            }
        }

        /// <summary>Tells the owners the counts of the errors that have stopped repeating.</summary>
        internal void SweepErrors()
        {
            foreach ((UUID item, string text, int count) in m_errors.TakeQuiet(Clock.Now, m_settings.ErrorQuietSeconds))
                TellOwnerOf(item, $"{text} (repeated {count} more time{(count == 1 ? "" : "s")})");
            if (!m_errors.HasPending)
                StopErrorSweep();
        }

        private void StopErrorSweep()
        {
            lock (m_errorSweepLock)
            {
                m_errorSweep?.Dispose();
                m_errorSweep = null;
            }
        }

        // ---- the module's lifecycle calls ----

        /// <summary>The region asks every engine to start; this one compiles as scripts arrive, so the queue is empty now (Phlox answers the same way).</summary>
        public void StartProcessing()
        {
            m_scene?.EventManager.TriggerEmptyScriptCompileQueue(0, string.Empty);
        }

        /// <summary>Every started script's state is captured on the workers and written to the database before this returns.</summary>
        public void SaveAllState()
        {
            SLuaScheduler scheduler = m_scheduler;
            SqliteStateStore sink = m_stateStore;
            if (scheduler == null || sink == null)
                return;
            var records = new List<ScriptRecord>();
            lock (m_records)
                foreach (ScriptRecord r in m_records.Values)
                    if (r.Script.StateHeld == null && r.AssetID != UUID.Zero)
                        records.Add(r);
            var scripts = new List<SchedulerScript>(records.Count);
            foreach (ScriptRecord r in records)
                scripts.Add(r.Script);
            Dictionary<long, byte[]> captured = scheduler.CaptureForCarry(scripts, CarriedStateTimeoutMs, out bool timedOut);
            if (timedOut)
                m_log.Error($"SaveAllState timed out after {CarriedStateTimeoutMs} ms; the scripts that were captured are saved");
            foreach (ScriptRecord r in records)
                if (captured.TryGetValue(r.Script.Id, out byte[] state))
                    sink.Save(r.ItemID, r.AssetID, state);
            sink.Flush(CarriedStateTimeoutMs);
        }

        /// <summary>The bytes the heaps of the given scripts used after their last step.</summary>
        public int GetScriptsMemory(List<UUID> itemIDs)
        {
            if (itemIDs == null)
                return 0;
            long total = 0;
            foreach (UUID item in itemIDs)
                total += RecordOf(item)?.Script.MemoryUsed ?? 0;
            return (int)Math.Min(int.MaxValue, total);
        }

        // ---- the module's interface ----

        public bool HasSavedScriptState(UUID itemID, UUID assetID) => HasCarried(itemID, assetID) || m_store.HasSavedState(itemID, assetID);

        /// <summary>The version of the carried-state envelope this build writes and reads.</summary>
        internal const string CarriedStateVersion = "1";

        /// <summary>The most bytes of a script's record an object carries; checked before anything is decoded.</summary>
        internal const int MaxCarriedStateBytes = 16 * 1024 * 1024;

        /// <summary>The longest envelope read: the base64 of <see cref="MaxCarriedStateBytes"/> and room for the attributes.</summary>
        internal const int MaxCarriedStateXmlChars = (MaxCarriedStateBytes + 2) / 3 * 4 + 1024;

        /// <summary>How long a region thread waits for the workers to capture an object's scripts.</summary>
        internal int CarriedStateTimeoutMs = 10 * 1000;

        /// <summary>State that came with an object and waits for its script to load, by item: the asset it was saved for, the record, and when it came.</summary>
        private readonly Dictionary<UUID, (UUID Asset, byte[] Record, long Taken)> m_carried = new Dictionary<UUID, (UUID, byte[], long)>();

        /// <summary>Records captured for the other scripts of an object, until the core asks for them one at a time (null: that script travels without state).</summary>
        private readonly Dictionary<UUID, (byte[] Record, UUID Asset, long Taken)> m_capturedForObject = new Dictionary<UUID, (byte[], UUID, long)>();

        private int m_capturePasses;

        /// <summary>How many times the workers were asked to capture the scripts of an object (one pass takes all of them).</summary>
        internal int CarriedCapturePasses => Volatile.Read(ref m_capturePasses);

        /// <summary>The script functions of the item's script, or null; for the tests.</summary>
        internal Phlox.ScriptEngine.LSLSystemAPI RealApiFor(UUID itemId) => RealApiOf(itemId);

        /// <summary>
        /// The state of the script for its object to carry (a take, a derez, a detach, a crossing), as the envelope
        /// <c>&lt;State Engine="SLua" UUID=item Asset=asset Version="1"&gt;&lt;ScriptState&gt;base64&lt;/ScriptState&gt;&lt;/State&gt;</c>, or "" when the
        /// item is not a script of this engine here, is held because its row could not be read, cannot be captured now, or is larger than an
        /// object carries. The capture is taken on the workers between steps, for all the scripts of the object in one pass: the core asks
        /// one script at a time, and the others are answered from the pass (the shape of Phlox's and YEngine's envelope).
        /// </summary>
        public string GetXMLState(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            SLuaScheduler scheduler = m_scheduler;
            if (record == null || scheduler == null || record.Script.StateHeld != null)
                return string.Empty;
            long now = Environment.TickCount64;
            lock (m_capturedForObject)
            {
                foreach (UUID stale in new List<UUID>(m_capturedForObject.Keys))
                    if (now - m_capturedForObject[stale].Taken > Math.Max(CarriedStateTimeoutMs, 1000) * 2L)
                        m_capturedForObject.Remove(stale);
                if (m_capturedForObject.Remove(itemID, out var got))
                    return CarriedEnvelope(itemID, got.Asset, got.Record);
            }

            var records = new List<ScriptRecord> { record };
            SceneObjectGroup group = m_scene?.GetSceneObjectPart(record.LocalID)?.ParentGroup;
            if (group != null)
            {
                foreach (SceneObjectPart part in group.Parts)
                {
                    foreach (TaskInventoryItem other in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    {
                        ScriptRecord r = other.ItemID == itemID ? null : RecordOf(other.ItemID);
                        if (r != null && r.Script.StateHeld == null)
                            records.Add(r);
                    }
                }
            }
            Interlocked.Increment(ref m_capturePasses);
            var scripts = new List<SchedulerScript>(records.Count);
            foreach (ScriptRecord r in records)
                scripts.Add(r.Script);
            Dictionary<long, byte[]> captured = scheduler.CaptureForCarry(scripts, CarriedStateTimeoutMs, out bool timedOut);
            if (timedOut)
            {
                m_log.Warn($"Timed out after {CarriedStateTimeoutMs} ms capturing the {records.Count} script(s) of {group?.Name ?? itemID.ToString()}; they travel without their state and start fresh where they arrive");
                captured.Clear();
            }
            lock (m_capturedForObject)
            {
                for (int i = 1; i < records.Count; i++)
                    m_capturedForObject[records[i].ItemID] = (captured.TryGetValue(records[i].Script.Id, out byte[] other) ? other : null, records[i].AssetID, now);
            }
            captured.TryGetValue(record.Script.Id, out byte[] mine);
            return CarriedEnvelope(itemID, record.AssetID, mine);
        }

        private string CarriedEnvelope(UUID itemID, UUID asset, byte[] record)
        {
            if (record == null)
            {
                m_log.Warn($"Script {itemID} could not be captured for its object; it travels without its state");
                return string.Empty;
            }
            if (record.Length > MaxCarriedStateBytes)
            {
                m_log.Warn($"The state of script {itemID} is {record.Length} bytes, above the {MaxCarriedStateBytes} an object carries; it travels without it");
                return string.Empty;
            }
            var doc = new System.Xml.XmlDocument();
            System.Xml.XmlElement state = doc.CreateElement("", "State", "");
            doc.AppendChild(state);
            state.SetAttribute("Engine", EngineIdentity.EngineName);
            state.SetAttribute("UUID", itemID.ToString());
            state.SetAttribute("Asset", asset.ToString());
            state.SetAttribute("Version", CarriedStateVersion);
            System.Xml.XmlElement data = doc.CreateElement("", "ScriptState", "");
            data.InnerText = Convert.ToBase64String(record);
            state.AppendChild(data);
            return doc.OuterXml;
        }

        /// <summary>
        /// The object brought this script's state. True only for this engine's own envelope of this version whose record reads: it is held
        /// for the item's load, which uses it before the database's row. Anything else is false, so the core offers the state to the next
        /// engine: another engine's state, another envelope version, XML that does not parse, a record that does not read, one above the
        /// size cap (checked before anything is decoded). A script given no state starts fresh.
        /// </summary>
        public bool SetXMLState(UUID itemID, string xml)
        {
            if (string.IsNullOrEmpty(xml) || m_scheduler == null)
                return false;
            if (xml.Length > MaxCarriedStateXmlChars)
            {
                m_log.Warn($"Script {itemID} brought {xml.Length} characters of state, above the {MaxCarriedStateXmlChars} read; it starts fresh");
                return false;
            }
            System.Xml.XmlElement state;
            try
            {
                var doc = new System.Xml.XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                state = doc.DocumentElement;
            }
            catch (System.Xml.XmlException) { return false; }
            if (state == null || state.Name != "State" || state.GetAttribute("Engine") != EngineIdentity.EngineName)
                return false;
            if (state.GetAttribute("Version") != CarriedStateVersion)
            {
                m_log.Warn($"Script {itemID} brought state in envelope version '{state.GetAttribute("Version")}', which this build does not read; it starts fresh");
                return false;
            }
            if (!UUID.TryParse(state.GetAttribute("Asset"), out UUID asset))
                return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(state["ScriptState"]?.InnerText ?? string.Empty); }
            catch (FormatException) { return false; }
            if (ScriptStateRecord.Inspect(bytes, EngineIdentity.EngineName, out _, out string problem) != ScriptStateRecord.RecordReading.Ok)
            {
                m_log.Warn($"Script {itemID} brought state that cannot be read ({problem}); it starts fresh");
                return false;
            }
            long now = Environment.TickCount64;
            lock (m_carried)
            {
                foreach (UUID stale in new List<UUID>(m_carried.Keys))
                    if (now - m_carried[stale].Taken > CarriedStateKeepMs)
                        m_carried.Remove(stale);
                m_carried[itemID] = (asset, bytes, now);
            }
            return true;
        }

        /// <summary>State that came with an object and was never used is forgotten after this long (the script never loaded).</summary>
        private const long CarriedStateKeepMs = 10 * 60 * 1000;

        /// <summary>The state the object brought for this item, once: null when none came, or when it was saved for another asset (the script was edited since), which is dropped.</summary>
        private byte[] TakeCarried(UUID itemId, UUID assetId)
        {
            (UUID Asset, byte[] Record, long Taken) c;
            lock (m_carried)
            {
                if (!m_carried.Remove(itemId, out c))
                    return null;
            }
            if (c.Asset != assetId)
            {
                m_log.Info($"Dropping the state script {itemId} brought for asset {c.Asset}; the script is now asset {assetId}");
                return null;
            }
            return c.Record;
        }

        private bool HasCarried(UUID itemId, UUID assetId)
        {
            lock (m_carried)
                return m_carried.TryGetValue(itemId, out var c) && c.Asset == assetId;
        }

        private void ForgetCarried(UUID itemId)
        {
            lock (m_carried) m_carried.Remove(itemId);
            lock (m_capturedForObject) m_capturedForObject.Remove(itemId);
        }

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
        internal IScriptGrants Grants
        {
            get => m_grants;
            set
            {
                m_grants = value ?? NoGrants.Instance;
                if (m_policy != null)
                    m_policy.Grants = m_grants;
            }
        }

        private IScriptGrants m_grants = NoGrants.Instance;

        /// <summary>The engine's clock as the policy hooks read it, so a clock set after the region was added is the one they use.</summary>
        private sealed class LiveClock : ISchedulerClock
        {
            private readonly SLuaEngine m_engine;

            public LiveClock(SLuaEngine engine) { m_engine = engine; }

            public double Now => m_engine.Clock.Now;
        }

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
                // The parcel and the estate switch drop what reaches the script, as Phlox does for a script it has paused for the parcel
                // (events other than the script's own state_entry and state_exit, which the runtime runs inside the script and are never posted to it);
                // the region's own suspend queues.
                record.Script.DropsEventsWhileSuspended = (after & (PauseReason.Estate | PauseReason.Parcel)) != 0;
                // A script held by its object crossing does not hear chat: a listen is dropped, its other events are kept (Phlox, ProcessEventQueue, IsHeldOnlyForCrossing).
                record.Script.DropsListensWhileSuspended = (after & PauseReason.Crossing) != 0;
                HoldOrReleaseSensor(record, before, after);
                if (before == PauseReason.None)
                    scheduler.Suspend(record.Script);
                else if (after == PauseReason.None)
                    scheduler.Resume(record.Script);
            }
        }

        /// <summary>
        /// A script held by its object's crossing, or paused by the parcel or the estate switch, stops its sensor repeat; the sensor
        /// comes back when the last of those pauses ends and the script is running (Phlox: HoldForCrossing and PauseForParcel call
        /// OnCrossingHold and PauseSensorForParcel, and the end of either calls RestoreSensorAfterParcel). The crossing hold also
        /// drops the replies the script is owed. Called with the record's pause lock held.
        /// </summary>
        private void HoldOrReleaseSensor(ScriptRecord record, PauseReason before, PauseReason after)
        {
            Phlox.ScriptEngine.LSLSystemAPI api = RealApiOf(record.ItemID);
            if (api == null)
                return;
            bool heldBefore = (before & SensorHolds) != 0;
            bool heldAfter = (after & SensorHolds) != 0;
            if (!heldBefore && heldAfter)
            {
                if ((after & PauseReason.Crossing) != 0)
                    api.OnCrossingHold();
                else
                    api.PauseSensorForParcel();
            }
            else if (heldBefore && !heldAfter && record.Script.Running && !record.Script.Killed)
                api.RestoreSensorAfterParcel();
        }

        /// <summary>
        /// Called by a function that changes a physical property, as Phlox's PhySleep is: when the region's physics frame
        /// time (average of the last ten) is over 30 ms, the calling script is delayed by that many milliseconds. Needs
        /// the physics throttle; a no-op outside a script's call.
        /// </summary>
        internal void PhySleep()
        {
            SchedulerScript script = SchedulerScript.Current;
            if (script == null || !m_settings.PhysicsThrottle)
                return;
            int frameMs = PhysicsFrameTimeAvg;
            if (frameMs > MaxPhysicsTimeBeforeDilationMs)
                script.AddSleep(frameMs / 1000.0);
        }

        /// <summary>The physics frame time over which a physical change delays its script (Halcyon MAX_PHYSICS_TIME_BEFORE_DILATION, as Phlox's PhySleep uses it).</summary>
        internal const int MaxPhysicsTimeBeforeDilationMs = 30;

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

        /// <summary>
        /// Lifts the region's suspend. True also for a script still loading: it is not suspended, which is the same as
        /// resumed, and the region posts changed(CHANGED_OWNER) after a resume only to an engine that answers yes (Phlox
        /// answers the same). False for a script of another engine.
        /// </summary>
        public bool ResumeScript(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record == null)
                return IsTracking(itemID);
            if (m_scheduler == null)
                return false;
            ((IPolicyHost)this).SetPaused(itemID, PauseReason.Core, false);
            m_policy?.EvaluateArrival(itemID); // the region's release of an arrived object ends the hold it kept for its riders
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

        /// <summary>The execution times, in milliseconds, of the given scripts that are running, added together (a stopped script adds nothing).</summary>
        public float GetScriptExecutionTime(List<UUID> itemIDs)
        {
            if (itemIDs == null)
                return 0f;
            double total = 0;
            foreach (UUID item in itemIDs)
            {
                SchedulerScript s = RecordOf(item)?.Script;
                if (s != null && s.Running)
                    total += s.ExecutionTime.Milliseconds;
            }
            return (float)total;
        }

        /// <summary>The scripts' execution times in milliseconds added up per linkset, by the local id of the linkset's root part.</summary>
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes()
        {
            var times = new Dictionary<uint, float>();
            foreach ((uint root, SchedulerScript s) in ScriptsByRoot())
            {
                times.TryGetValue(root, out float t);
                times[root] = t + (float)s.ExecutionTime.Milliseconds;
            }
            return times;
        }

        /// <summary>
        /// The region's Top Scripts report: per linkset the scripts' execution time (ms) and memory (bytes), kept when either is over its
        /// minimum, and the totals over every script, kept or not.
        /// </summary>
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            var top = new Dictionary<uint, ScriptTopStatsData>();
            foreach ((uint root, SchedulerScript s) in ScriptsByRoot())
            {
                float time = (float)s.ExecutionTime.Milliseconds;
                int memory = s.MemoryUsed;
                totaltime += time;
                totalmemory += memory;
                if (time > mintime || memory > minmemory)
                {
                    if (top.TryGetValue(root, out ScriptTopStatsData data))
                    {
                        data.time += time;
                        data.memory += memory;
                    }
                    else
                        top[root] = new ScriptTopStatsData { localID = root, time = time, memory = memory };
                }
            }
            return top.Values;
        }

        /// <summary>Every script of the engine with the local id of the root part of its linkset; a script whose object is gone is left out.</summary>
        private List<(uint Root, SchedulerScript Script)> ScriptsByRoot()
        {
            var found = new List<(uint, SchedulerScript)>();
            Scene scene = m_scene;
            if (scene == null)
                return found;
            ScriptRecord[] records;
            lock (m_records)
            {
                records = new ScriptRecord[m_records.Count];
                m_records.Values.CopyTo(records, 0);
            }
            foreach (ScriptRecord r in records)
            {
                SceneObjectGroup group = scene.GetSceneObjectPart(r.LocalID)?.ParentGroup;
                if (group == null || group.IsDeleted || r.Script == null)
                    continue;
                found.Add((group.RootPart.LocalId, r.Script));
            }
            return found;
        }
    }
}
