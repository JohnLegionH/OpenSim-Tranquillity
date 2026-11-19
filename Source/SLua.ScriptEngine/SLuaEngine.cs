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
    /// This part of the engine holds no scripts yet: it claims none, and the region's script requests find nothing in it.
    /// Compiling and running scripts come with the parts that follow.
    /// </para>
    /// </summary>
    public partial class SLuaEngine : INonSharedRegionModule, IScriptModule
    {
#pragma warning disable CS0067 // raised once the engine runs scripts
        public event ScriptRemoved OnScriptRemoved;
        public event ObjectRemoved OnObjectRemoved;
#pragma warning restore CS0067

        /// <summary>The engine as the region server makes it: the region log and the native library from the configured places.</summary>
        public SLuaEngine() : this(new RegionLog(), NativeRuntime.GateForEngine) { }

        private readonly IEngineLog m_log;
        private readonly RuntimeLoader m_loadRuntime;
        private SLuaSettings m_settings = new SLuaSettings();
        private bool m_enabled;
        private Scene m_scene;

        internal SLuaEngine(IEngineLog log, RuntimeLoader loadRuntime)
        {
            m_log = log;
            m_loadRuntime = loadRuntime;
        }

        internal SLuaSettings Settings => m_settings;

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
            m_scene.RegisterModuleInterface<IScriptModule>(this);
            m_scene.StackModuleInterface<IScriptModule>(this);
            m_log.Info("Added to region " + scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            m_scene = null;
        }

        public void RegionLoaded(Scene scene) { }

        // ---- IScriptModule: the answers of an engine that holds no script ----

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new ArrayList();

        public bool HasScript(UUID itemID, out bool running)
        {
            running = false;
            return false;
        }

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
