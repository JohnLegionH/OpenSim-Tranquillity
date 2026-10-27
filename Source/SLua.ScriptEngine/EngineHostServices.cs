/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Glue;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The host side of the script functions for the SLua engine: what LSLSystemAPI reads of its engine (the engine's
    /// settings, the region) and the calls it makes into it (post an event, set a timer, reset the script, return a
    /// call's result). The settings come from <see cref="SLuaSettings"/>; the calls are served by the
    /// <see cref="EngineHostBackend"/> the engine fills in.
    ///
    /// The services the Phlox engine owns and the SLua engine does not have yet (the OSSL gate, the listen manager,
    /// the state manager, the asynchronous command plugins, the notecard cache) are null, which the interface allows;
    /// the script functions that need one are the ones not yet served by the real API.
    /// </summary>
    internal sealed class EngineHostServices : IScriptHostServices
    {
        private readonly SLuaSettings m_settings;
        private readonly EngineHostBackend m_backend;

        public EngineHostServices(SLuaSettings settings, Scene world, IConfigSource config, IScriptModule module, EngineHostBackend backend)
        {
            m_settings = settings ?? new SLuaSettings();
            World = world;
            ConfigSource = config;
            ScriptModule = module;
            m_backend = backend ?? new EngineHostBackend();
        }

        public string Name => EngineIdentity.EngineName;
        public Scene World { get; }
        public IConfigSource ConfigSource { get; }
        public IScriptModule ScriptModule { get; }

        /// <summary>The engine as the asynchronous command plugins know it (their interface is Phlox's); the SLua engine has none.</summary>
        public IScriptEngine ScriptEngine => null;

        public ServiceCallDeferralMode ServiceCallDeferral => m_settings.ServiceCallDeferral switch
        {
            "always" => ServiceCallDeferralMode.Always,
            "never" => ServiceCallDeferralMode.Never,
            _ => ServiceCallDeferralMode.Auto,
        };

        public bool AllowGodFunctions => m_settings.AllowGodFunctions;
        public bool AutomaticLinkPermission => false;
        public float MinTimerInterval => (float)m_settings.MinTimerInterval;

        public bool ResetThrottle => m_settings.ResetThrottle;
        public bool ChatThrottle => m_settings.ChatThrottle;
        public bool BotThrottle => m_settings.BotThrottle;
        public bool PhysicsThrottle => m_settings.PhysicsThrottle;
        public bool LinkMessageThrottle => m_settings.LinkMessageThrottle;
        public bool NotecardThrottle => m_settings.NotecardThrottle;
        public bool FormatStringThrottle => m_settings.FormatStringThrottle;
        public bool HttpInFlightThrottle => m_settings.HttpInFlightThrottle;
        public bool NotecardHalcyonTiming => m_settings.NotecardPause == "halcyon";
        public bool NotecardCacheEnabled => m_settings.NotecardCache;
        public PhloxNotecardCache NotecardCache => null;
        public int PhysicsFrameTimeAvg => m_backend.PhysicsFrameTimeAvg?.Invoke() ?? 0;

        public IScriptOsslChecks Ossl => null;
        /// <summary>The engine's chat: its listen registry and the way to the region's world communication; set once the region is added.</summary>
        internal EngineChat Chat { get; set; }

        public IScriptListenService ListenManager => Chat?.Listens;
        public IScriptStateTracker StateManager => null;
        public AsyncCommandManager AsyncCommands => null;

        /// <summary>
        /// Inside a call that runs for this script on the service lane, the result is recorded on the call's context and
        /// the call's single return is made when its body ends (Phlox: PhloxEngine.SysReturn). Anywhere else it goes to the engine.
        /// </summary>
        public void SysReturn(UUID itemId, object retValue, int delay)
        {
            SyscallContext ctx = SyscallContext.Current;
            if (ctx != null && ctx.ItemId == itemId)
            {
                ctx.SetResult(retValue, delay);
                return;
            }
            m_backend.SyscallReturn?.Invoke(itemId, retValue, delay, -1);
        }

        public void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq) =>
            m_backend.SyscallReturn?.Invoke(itemId, retValue, delay, seq);

        public bool PostScriptEvent(UUID itemID, EventParams parms) => m_backend.PostScriptEvent?.Invoke(itemID, parms) ?? false;

        public bool PostObjectEvent(uint localID, EventParams parms) => m_backend.PostObjectEvent?.Invoke(localID, parms) ?? false;

        /// <summary>SL fires linkset_data in every script of the linkset, so the event goes to every part of the group (PhloxEngine.PostObjectLinksetDataEvent).</summary>
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            var parms = new EventParams("linkset_data", new object[] { action, name.ToString(), value.ToString() }, null);
            SceneObjectGroup group = World?.GetSceneObjectPart(localID)?.ParentGroup;
            if (group == null)
                return PostObjectEvent(localID, parms);

            bool any = false;
            foreach (SceneObjectPart part in group.Parts)
                any |= PostObjectEvent(part.LocalId, parms);
            return any;
        }

        /// <summary>The dataserver answer to other scripts in a prim is not served by the SLua engine yet.</summary>
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => 0;

        public bool HasScript(UUID itemID, out bool running)
        {
            (bool exists, bool isRunning) = m_backend.HasScript?.Invoke(itemID) ?? (false, false);
            running = isRunning;
            return exists;
        }

        public void SetTimerEvent(uint localID, UUID itemID, float sec) => m_backend.SetTimer?.Invoke(itemID, sec);

        public void SetMinEventDelay(UUID itemID, float seconds) => m_backend.SetMinEventDelay?.Invoke(itemID, seconds);

        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) => m_backend.SetItemRunningFlag?.Invoke(localId, itemId, running);

        public void ResetScript(UUID itemID) => m_backend.ResetScript?.Invoke(itemID);

        public void ApiResetScript(UUID itemID) => m_backend.ResetScript?.Invoke(itemID);

        public void RequestParcelCheck(UUID itemId) => m_backend.RequestParcelCheck?.Invoke(itemId);

        /// <summary>Chat is offered to the listens of other engines through the region's world communication.</summary>
        public void SendToWorldComm(Action<IWorldComm> send) => Chat?.SendToWorldComm(send);

        /// <summary>1.0 for a script the engine does not run, as Halcyon's EngineInterface.GetEventQueueFreeSpacePercentage.</summary>
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => m_backend.EventQueueFreeSpacePercentage?.Invoke(itemID) ?? 1.0f;

        public void ExperienceGrantRestored() { }

        public void ExperienceCannotRun(UUID experience, int code) { }
    }
}
