/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The engine as one of the region's script engines (<see cref="IScriptEngine"/>). The Phlox script functions offer an
    /// event they raise in every script of some prims (a link message, a dataserver answer, an email, an object_rez) to each
    /// other script engine of the region that is an <see cref="IScriptEngine"/>; an engine that is only a script module
    /// would never be offered one. The members that matter to them are the two ways to post an event
    /// (<see cref="PostScriptEvent(UUID, EventParams)"/> and <see cref="PostObjectEvent(uint, EventParams)"/>); the rest
    /// belong to the way the old C# script engines compile and run a script and have nothing to do here (as Phlox's own
    /// answers for them).
    /// </summary>
    public partial class SLuaEngine : IScriptEngine
    {
        Scene IScriptEngine.World => m_scene;

        IScriptModule IScriptEngine.ScriptModule => this;

        IConfig IScriptEngine.Config => m_configSource?.Configs[EngineIdentity.ConfigSection];

        IConfigSource IScriptEngine.ConfigSource => m_configSource;

        string IScriptEngine.ScriptEnginePath => string.Empty;
        string IScriptEngine.ScriptClassName => string.Empty;
        string IScriptEngine.ScriptBaseClassName => string.Empty;
        string[] IScriptEngine.ScriptReferencedAssemblies => Array.Empty<string>();
        ParameterInfo[] IScriptEngine.ScriptBaseClassParameters => null;

        IScriptWorkItem IScriptEngine.QueueEventHandler(object parms) => null;
        void IScriptEngine.CancelScriptEvent(UUID itemID, string eventName) { }
        DetectParams IScriptEngine.GetDetectParams(UUID item, int number) => null;
        int IScriptEngine.GetStartParameter(UUID itemID) => 0;
        void IScriptEngine.SetScriptState(UUID itemID, bool state, bool self) { }
        void IScriptEngine.SetState(UUID itemID, string newState) { }
        IScriptApi IScriptEngine.GetApi(UUID itemID, string name) => null;
        void IScriptEngine.SleepScript(UUID itemID, int delay) { }
        void IScriptEngine.SetMinEventDelay(UUID itemID, double delay) => HostSetMinEventDelay(itemID, (float)delay);

        void IScriptEngine.ApiResetScript(UUID itemID) => ResetByItem(itemID);

        void IScriptEngine.ResetScript(UUID itemID) => ResetByItem(itemID);

        bool IScriptEngine.PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) =>
            HostServices?.PostObjectLinksetDataEvent(localID, action, name, value) ?? false;

        /// <summary>
        /// llMinEventDelay: the floor between the starts of the script's handlers, kept to whole milliseconds as Phlox keeps it.
        /// Set on the worker: directly when the script is inside its own call, through the worker's queue from any other thread.
        /// </summary>
        private void HostSetMinEventDelay(UUID itemId, float seconds)
        {
            SchedulerScript script = RecordOf(itemId)?.Script;
            if (script == null)
                return;
            double floor = seconds <= 0f ? 0 : (int)(seconds * 1000f) / 1000.0;
            if (ReferenceEquals(script, SchedulerScript.Current))
                script.SetMinEventDelay(floor);
            else
                m_scheduler?.SetMinEventDelay(script, floor);
        }

        /// <summary>
        /// The share of the script's event queue that is free, as Halcyon's EngineInterface.GetEventQueueFreeSpacePercentage
        /// reports it: 1 for a script this engine does not run, 0 when the queue is full, else 1 less the share queued
        /// (the queue holds <c>MaxEventQueue</c> events, 64 as in Second Life).
        /// </summary>
        private float HostEventQueueFreeSpace(UUID itemId)
        {
            SchedulerScript script = RecordOf(itemId)?.Script;
            if (script == null)
                return 1.0f;
            int max = m_settings.MaxEventQueue;
            int queued = script.QueuedEvents;
            return queued >= max ? 0.0f : 1.0f - (float)queued / max;
        }

        private void ResetByItem(UUID itemID)
        {
            ScriptRecord record = RecordOf(itemID);
            if (record != null)
                OnScriptReset(record.LocalID, itemID);
        }

        /// <summary>
        /// A dataserver answer goes to every script in the asking script's prim, as SL does (wiki Dataserver: a request raises
        /// the event in every script of the prim it was made from) and as Phlox delivers it: this engine's scripts in the prim in
        /// a stable order (script name, then item id), except <paramref name="skip"/> (the asker, when it is no longer owed
        /// the answer), and then each other script engine of the region once, for the scripts it runs. Never to another prim.
        /// Returns how many of this engine's scripts it was posted to.
        /// </summary>
        internal int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data)
        {
            if (part?.ParentGroup == null || part.ParentGroup.IsDeleted)
                return 0;

            var scripts = new List<TaskInventoryItem>();
            foreach (TaskInventoryItem item in ((TaskInventoryDictionary)part.TaskInventory.Clone()).Values)
                if (item.Type == (int)AssetType.LSLText || item.Type == 10)
                    scripts.Add(item);
            scripts.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.Name, b.Name);
                return byName != 0 ? byName : a.ItemID.CompareTo(b.ItemID);
            });

            int posted = 0;
            foreach (TaskInventoryItem item in scripts)
            {
                if (item.ItemID == skip)
                    continue;
                if (PostScriptEvent(item.ItemID, new EventParams("dataserver", new object[] { queryId, data }, new DetectParams[0])))
                    posted++;
            }

            // An exception from another engine is logged and goes no further: the engines after it are still offered the
            // answer, and it never reaches the asking script's request.
            var seen = new List<IScriptEngine>();
            foreach (IScriptModule module in m_scene?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>())
            {
                if (ReferenceEquals(module, this) || module is not IScriptEngine other || seen.Contains(other))
                    continue;
                seen.Add(other);
                try
                {
                    other.PostObjectEvent(part.LocalId, new EventParams("dataserver", new object[] { queryId, data }, new DetectParams[0]));
                }
                catch (Exception e)
                {
                    m_log.Error("another script engine failed to take a dataserver event: " + e.Message);
                }
            }
            return posted;
        }
    }
}
