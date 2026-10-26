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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace Luau.ScriptEngine
{
    /// <summary>Loads the native runtime for the engine; false means it is unusable and the engine must not register.</summary>
    internal delegate bool RuntimeLoader(LuauSettings settings, IEngineLog log);

    /// <summary>
    /// The Luau script engine as a region module. It registers with the region only when
    /// <c>[NGC.Luau] Enabled = true</c>; it owns a script only when the script's first line names it
    /// or the region's saved-state routing gives it the script. This is the skeleton: it holds no scripts yet.
    /// </summary>
    public class LuauEngine : INonSharedRegionModule, IScriptModule, IScriptStateProvider
    {
#pragma warning disable CS0067 // raised once scripts run on the engine
        public event ScriptRemoved OnScriptRemoved;
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

        private readonly LoaderFactory m_loaderFactory;
        private IScriptLoader m_loader;

        internal LuauEngine(IEngineLog log, RuntimeLoader loadRuntime, ISavedStateStore store = null, LoaderFactory loaderFactory = null)
        {
            m_log = log;
            m_loadRuntime = loadRuntime;
            m_store = store ?? new EmptyStateStore();
            m_loaderFactory = loaderFactory;
        }

        /// <summary>Whether the engine holds a load record for the item (a rez it accepted and has not forgotten).</summary>
        internal bool IsTracking(UUID itemID)
        {
            lock (m_tracked) return m_tracked.ContainsKey(itemID);
        }

        private readonly Dictionary<UUID, uint> m_tracked = new Dictionary<UUID, uint>();
        private bool m_warnedNoLoader;

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
            m_scene.EventManager.OnRezScript += OnRezScript;
            try
            {
                m_loader = (m_loaderFactory ?? DefaultLoader)(m_settings, m_log, scene);
            }
            catch (Exception e)
            {
                // The engine stays registered but cannot load: the region and the other engines carry on.
                m_log.Error("The script loader could not be started; scripts given to this engine will not load: " + e);
            }
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        /// <summary>
        /// The bound-function list is empty until host functions are bound, so the stamp carries a fixed marker; it
        /// changes with that list, which makes the cache entries of an earlier list unused.
        /// </summary>
        internal const string BoundFunctionHash = "none";

        /// <summary>The loader the engine runs with: the compile cache in the engine's folder (unless switched off), the region's asset service for text, one VM.</summary>
        private static IScriptLoader DefaultLoader(LuauSettings settings, IEngineLog log, Scene scene)
        {
            CompileCache cache = settings.CompileCache
                ? new CompileCache(CompileCache.DirectoryFor(settings), CompileCache.MakeStamp(Host.Version(), BoundFunctionHash), log)
                : null;
            var compiler = new ScriptCompiler(null, null, log, cache);
            Bindings.Watchdog watchdog = settings.Watchdog switch
            {
                "resident" => Bindings.Watchdog.Resident,
                "threaded" => Bindings.Watchdog.Threaded,
                _ => Bindings.Watchdog.Default,
            };
            return new ScriptLoader(compiler, new SceneTextSource(scene.AssetService), log, settings.MemoryLimitKiB * 1024,
                () => new Vm(watchdog), ownsCompiler: true);
        }

        /// <summary>How long GetScriptErrors waits for a compile; the region's own wait for a save is the same 15 seconds.</summary>
        internal static TimeSpan ErrorWaitTimeout = TimeSpan.FromSeconds(15);

        public void RemoveRegion(Scene scene)
        {
            if (m_scene == null) return;
            m_scene.EventManager.OnRezScript -= OnRezScript;
            lock (m_tracked) m_tracked.Clear();
            m_loader?.Dispose();
            m_loader = null;
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
            if (!m_warnedNoLoader)
            {
                m_warnedNoLoader = true;
                m_log.Warn("A script was given to this engine, which compiles and loads scripts but cannot run them yet; the script will not run");
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
        /// The script belongs to another engine: forget this item's load record. The saved state row is kept, so the
        /// script resumes if it returns here with the same asset; an edited script has a new asset and starts fresh.
        /// </summary>
        private void Disown(UUID itemID)
        {
            lock (m_tracked) m_tracked.Remove(itemID);
            m_loader?.Disown(itemID);
        }

        public bool HasSavedScriptState(UUID itemID, UUID assetID) => m_store.HasSavedState(itemID, assetID);

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
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
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
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
