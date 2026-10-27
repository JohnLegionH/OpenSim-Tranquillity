/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
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
    /// <summary>The host side of the script functions for the SLua engine: the engine's settings and the calls LSLSystemAPI makes into its engine.</summary>
    internal sealed class EngineHostServices : IScriptHostServices
    {
        public EngineHostServices(SLuaSettings settings, Scene world, IConfigSource config, IScriptModule module, EngineHostBackend backend) { }

        public string Name => null;
        public Scene World => null;
        public IConfigSource ConfigSource => null;
        public IScriptModule ScriptModule => null;
        public IScriptEngine ScriptEngine => null;
        public ServiceCallDeferralMode ServiceCallDeferral => ServiceCallDeferralMode.Auto;
        public bool AllowGodFunctions => false;
        public bool AutomaticLinkPermission => false;
        public float MinTimerInterval => 0f;
        public bool ResetThrottle => false;
        public bool ChatThrottle => false;
        public bool BotThrottle => false;
        public bool PhysicsThrottle => false;
        public bool LinkMessageThrottle => false;
        public bool NotecardThrottle => false;
        public bool FormatStringThrottle => false;
        public bool HttpInFlightThrottle => false;
        public bool NotecardHalcyonTiming => false;
        public bool NotecardCacheEnabled => false;
        public PhloxNotecardCache NotecardCache => null;
        public int PhysicsFrameTimeAvg => 0;
        public IScriptOsslChecks Ossl => null;
        public IScriptListenService ListenManager => null;
        public IScriptStateTracker StateManager => null;
        public AsyncCommandManager AsyncCommands => null;

        public void SysReturn(UUID itemId, object retValue, int delay) { }
        public void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq) { }
        public bool PostScriptEvent(UUID itemID, EventParams parms) => false;
        public bool PostObjectEvent(uint localID, EventParams parms) => false;
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => false;
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => 0;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public void SetTimerEvent(uint localID, UUID itemID, float sec) { }
        public void SetMinEventDelay(UUID itemID, float seconds) { }
        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) { }
        public void ResetScript(UUID itemID) { }
        public void ApiResetScript(UUID itemID) { }
        public void RequestParcelCheck(UUID itemId) { }
        public void SendToWorldComm(Action<IWorldComm> send) { }
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => 0f;
        public void ExperienceGrantRestored() { }
        public void ExperienceCannotRun(UUID experience, int code) { }
    }
}
