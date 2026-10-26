/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
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

        public LuauEngine() : this(new RegionLog(), (settings, log) => true) { }

        private readonly IEngineLog m_log;
        private readonly RuntimeLoader m_loadRuntime;
        private LuauSettings m_settings = new LuauSettings();
        private bool m_enabled;
        private Scene m_scene;

        private readonly ISavedStateStore m_store;

        internal LuauEngine(IEngineLog log, RuntimeLoader loadRuntime, ISavedStateStore store = null)
        {
            m_log = log;
            m_loadRuntime = loadRuntime;
            m_store = store ?? new EmptyStateStore();
        }

        /// <summary>Whether the engine holds a load record for the item (a rez it accepted and has not forgotten).</summary>
        internal bool IsTracking(UUID itemID) => false;

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
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public bool HasSavedScriptState(UUID itemID, UUID assetID) => m_store.HasSavedState(itemID, assetID);

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new ArrayList();
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
