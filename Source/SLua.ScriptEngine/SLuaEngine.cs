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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>Loads the native runtime for the engine; false means it is unusable and the engine must not register.</summary>
    internal delegate bool RuntimeLoader(SLuaSettings settings, IEngineLog log);

    /// <summary>
    /// The SLua script engine as a region module. It does nothing unless <c>[SLua] Enabled = true</c>: off, it reads its
    /// settings and stops there, without looking for the native library. On, it loads the native runtime library and checks
    /// its API version; only when that succeeds does it register as one of the region's script engines, under the name
    /// "SLua", so the region lists it to viewers with the others. A library that is missing or of another API version
    /// keeps the engine out of the region with one log line, and the other engines carry on.
    /// <para>
    /// Switched on, the engine takes the scripts that are its own (see <see cref="EngineHeader.Owner"/> and
    /// <see cref="ClaimsScript"/>), compiles them off the region's threads, keeps the compiled form in its compile cache,
    /// answers the viewer's editor with the compile errors, and looks up each compiled script's saved state in its own
    /// database. It does not start scripts yet: running them comes with the scheduler, in the part that follows.
    /// </para>
    /// </summary>
    public partial class SLuaEngine : INonSharedRegionModule, IScriptModule, IScriptStateProvider
    {
        public event ScriptRemoved OnScriptRemoved;
#pragma warning disable CS0067 // raised once the engine runs scripts
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore CS0067

        /// <summary>The engine as the region server makes it: the region log, the native library from the configured places, and its own state database.</summary>
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

        private readonly LoaderFactory m_loaderFactory;
        private IScriptLoader m_loader;

        /// <summary>The scripts this engine has accepted from a rez, by item, with their prim.</summary>
        private readonly Dictionary<UUID, uint> m_tracked = new Dictionary<UUID, uint>();

        /// <summary>What the state lookup found for each compiled script; the scheduler starts the script from it.</summary>
        private readonly Dictionary<UUID, StateLoadResult> m_joined = new Dictionary<UUID, StateLoadResult>();

        /// <summary>Scripts taken out of an object that is still in the scene, by object: their rows go when the object leaves.</summary>
        private readonly Dictionary<UUID, HashSet<UUID>> m_removedWithObject = new Dictionary<UUID, HashSet<UUID>>();

        internal SLuaEngine(IEngineLog log, RuntimeLoader loadRuntime, ISavedStateStore store = null, LoaderFactory loaderFactory = null)
        {
            m_log = log;
            m_loadRuntime = loadRuntime;
            m_store = store ?? new EmptyStateStore();
            m_loaderFactory = loaderFactory;
        }

        internal SLuaSettings Settings => m_settings;

        /// <summary>Whether the engine holds a load record for the item (a rez it accepted and has not forgotten).</summary>
        internal bool IsTracking(UUID itemID)
        {
            lock (m_tracked) return m_tracked.ContainsKey(itemID);
        }

        /// <summary>What the state lookup found for the item's compiled script, or null when it has not compiled here.</summary>
        internal StateLoadResult JoinedStateOf(UUID itemID)
        {
            lock (m_joined) return m_joined.TryGetValue(itemID, out StateLoadResult r) ? r : null;
        }

        public string Name => EngineIdentity.EngineName;
        public string ScriptEngineName => EngineIdentity.EngineName;
        /// <summary>
        /// An interface no module registers or implements. The region server adds a module that names a replaceable interface
        /// after every module that names none, and skips it only when another module has registered that interface; so the
        /// engine is always loaded, and always added to the region after the other script engines (which name none). It thus
        /// registers behind them, and a module that asks the region for its first script engine gets the one it had before.
        /// </summary>
        public Type ReplaceableInterface => typeof(IAfterOtherScriptEngines);

        /// <summary>The marker <see cref="ReplaceableInterface"/> names. Nothing implements it.</summary>
        internal interface IAfterOtherScriptEngines { }

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
            OpenStateStore();
            m_scene.RegisterModuleInterface<IScriptModule>(this);
            m_scene.StackModuleInterface<IScriptModule>(this);
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
            m_scene.EventManager.OnRezScript += OnRezScript;
            m_scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_scene.EventManager.OnObjectBeingRemovedFromScene += OnObjectLeavingScene;
            m_scene.EventManager.OnShutdown += OnShutdown;
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene == null) return;
            m_scene.EventManager.OnRezScript -= OnRezScript;
            m_scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_scene.EventManager.OnObjectBeingRemovedFromScene -= OnObjectLeavingScene;
            m_scene.EventManager.OnShutdown -= OnShutdown;
            lock (m_tracked) m_tracked.Clear();
            lock (m_joined) m_joined.Clear();
            m_loader?.Dispose();
            m_loader = null;
            CloseStateStore();
            m_scene = null;
        }

        public void RegionLoaded(Scene scene) { }

        /// <summary>
        /// The simulator is shutting down: the database is written out and closed. The region's modules are not always removed
        /// on the way out, so this does not wait for RemoveRegion. Scripts the region takes out on the way down keep their rows.
        /// </summary>
        private void OnShutdown()
        {
            m_shuttingDown = true;
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

        /// <summary>Writes out what is queued and closes the database the engine made.</summary>
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
                ? new CompileCache(CompileCache.DirectoryFor(settings), CompileCache.MakeStamp(Host.Version(), CompileCache.NoBoundFunctions), log)
                : null;
            var compiler = new ScriptCompiler(null, null, log, cache);
            return new ScriptLoader(compiler, new SceneTextSource(scene.AssetService), log, ownsCompiler: true);
        }

        /// <summary>How long GetScriptErrors waits for a compile; the region's own wait for a save is the same 15 seconds.</summary>
        internal static TimeSpan ErrorWaitTimeout = TimeSpan.FromSeconds(15);

        // ---- the region's script events ----

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

            lock (m_tracked) m_tracked[itemID] = localID;
            lock (m_joined) m_joined.Remove(itemID);

            m_loader?.Post(new LoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                AssetID = rezItem?.AssetID ?? UUID.Zero,
                Target = target,
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
            lock (m_joined) m_joined.Remove(itemID);
            m_loader?.Disown(itemID);
        }

        /// <summary>
        /// Called on the loader's thread when a script has compiled: its saved state is looked up, if this engine owns one for this
        /// item and asset. A record of another engine is left alone; one the store cannot read holds the script back with its row
        /// untouched. The script is not started: the scheduler, which runs scripts, is not part of this engine yet.
        /// </summary>
        private void OnScriptReady(ReadyScript ready)
        {
            if (!IsTracking(ready.ItemID))
                return;

            StateLoadResult saved = new StateLoadResult { Kind = StateLoadKind.Fresh };
            if (m_stateStore != null && ready.AssetID != UUID.Zero)
                saved = StateJoin.Load(m_stateStore, EngineIdentity.EngineName, ready.ItemID, ready.AssetID,
                    m_settings.StateLoadRetries, m_settings.StateLoadRetryDelayMs, System.Threading.Thread.Sleep, m_log);

            lock (m_joined) m_joined[ready.ItemID] = saved;
            string state = saved.Kind switch
            {
                StateLoadKind.Restore => "its saved state was found",
                StateLoadKind.Held => "it is held: " + saved.Reason,
                _ => "no saved state",
            };
            m_log.Info($"Script {ready.ItemID} compiled as {ready.Language}{(ready.FromCache ? " (from the compile cache)" : "")}; {state}; scripts do not run on this engine yet");
        }

        /// <summary>A compile failed and no editor collected the errors: the owner cannot be told yet, so the log is.</summary>
        private void OnFailureUnclaimed(LoadFailure failure)
        {
            if (!IsTracking(failure.ItemID))
                return;
            m_log.Warn($"Script {failure.ItemID} did not compile: {string.Join("; ", failure.Errors)}");
        }

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            if (!IsTracking(itemID))
                return; // not ours
            Disown(itemID);
            // The script is gone, from its prim or with its object: its row in the engine's own database goes with it. Not at
            // shutdown, where the rows are what the restart restores from.
            DeleteRowOrWaitForObject(localID, itemID);
            OnScriptRemoved?.Invoke(itemID);
        }

        /// <summary>
        /// The row of a script deleted from its prim goes now. A script removed while its item is still in the prim is being taken
        /// out with its object (a take, a delete, a crossing): its row goes when the object leaves the scene.
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

        // ---- the module's interface ----

        /// <summary>Whether this engine's database holds state for the item and asset; the region gives such a script back to this engine.</summary>
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => m_store.HasSavedState(itemID, assetID);

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

        /// <summary>True when this engine holds the script compiled and ready. It has not started, so it is not running.</summary>
        public bool HasScript(UUID itemID, out bool running)
        {
            running = false;
            return m_loader != null && IsTracking(itemID) && m_loader.IsReady(itemID);
        }

        // ---- IScriptModule: the answers of an engine that runs no script yet ----

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
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
