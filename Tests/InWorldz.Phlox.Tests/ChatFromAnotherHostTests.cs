/*
 * Phlox Script Engine tests
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Chat spoken through an LSLSystemAPI that another engine hosts (IScriptHostServices: an engine that is not PhloxEngine
/// runs its scripts on the same API) reaches a Phlox listen once. Such a script's llSay and llRegionSay go out as scene
/// chat (Scene.SimChat), which Phlox's listens hear, and are then offered to the region's IWorldComm, whose
/// OnMessageDelivered Phlox also takes for region chat; the second copy must not be delivered again.
/// </summary>
// Serial with the other YEngine tests through SchedulerHarness's own YEngine gate (the harness with YEngine carries the core
// WorldComm module the test needs); nothing else here is process-wide.
public class ChatFromAnotherHostTests
{
    private const int ReportChannel = 99;

    /// <summary>The engine a test stands in for: it hosts the API and offers its chat to the region's WorldComm, as SLua does.</summary>
    private sealed class OtherHost : IScriptHostServices
    {
        private readonly Scene m_scene;
        private readonly IWorldComm m_worldComm;
        [ThreadStatic] private static bool t_sending;

        public OtherHost(Scene scene, IWorldComm worldComm) { m_scene = scene; m_worldComm = worldComm; }

        public string Name => "Other";
        public Scene World => m_scene;
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
        public bool PostScriptEvent(UUID itemID, EventParams parms) => true;
        public bool PostObjectEvent(uint localID, EventParams parms) => true;
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => true;
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => 0;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public void SetTimerEvent(uint localID, UUID itemID, float sec) { }
        public void SetMinEventDelay(UUID itemID, float seconds) { }
        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) { }
        public void ResetScript(UUID itemID) { }
        public void ApiResetScript(UUID itemID) { }
        public void RequestParcelCheck(UUID itemId) { }
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => 100f;
        public void ExperienceGrantRestored() { }
        public void ExperienceCannotRun(UUID experience, int code) { }

        /// <summary>As SLua's engine does: its own listens have had the chat, so it marks its own sends only.</summary>
        public void SendToWorldComm(Action<IWorldComm> send)
        {
            t_sending = true;
            try { send(m_worldComm); }
            finally { t_sending = false; }
        }
    }

    private static string Listener(string tag, int channel)
        => "default { state_entry() { llListen(" + channel + ", \"\", NULL_KEY, \"\"); llSay(" + ReportChannel + ", \"ready " + tag + "\"); } " +
           "listen(integer c, string n, key k, string m) { llSay(" + ReportChannel + ", \"" + tag + " heard \" + (string)c + \":\" + m); } }";

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private static int Times(SchedulerHarness h, string tag, string what) => h.Said.Count(s => s == tag + " heard " + what);

    private static (SchedulerHarness H, LSLSystemAPI Speaker, SceneObjectGroup Listening) Rig()
    {
        var h = new SchedulerHarness(withYEngine: true);
        SceneObjectGroup listening = SceneHelpers.AddSceneObject(h.Scene, "listening", UUID.Random());
        listening.AbsolutePosition = new Vector3(10, 128, 30);
        h.RezScriptInto(listening.RootPart, Listener("PHLOX", 5));
        WaitFor(h, said => said.Contains("ready PHLOX"), 60);
        Assert.True(h.Said.Contains("ready PHLOX"), "the Phlox listener never started: [" + string.Join(" | ", h.Said) + "]");

        SceneObjectGroup speaking = SceneHelpers.AddSceneObject(h.Scene, "speaking", UUID.Random());
        speaking.AbsolutePosition = new Vector3(12, 128, 30);
        var api = new LSLSystemAPI(new OtherHost(h.Scene, h.WorldComm), speaking.RootPart, speaking.RootPart.LocalId, UUID.Random());
        return (h, api, listening);
    }

    /// <summary>A real wait after the first copy: a second one would come at once, on the same thread or the next pump.</summary>
    private static void Settle(SchedulerHarness h) => h.PumpFor(TimeSpan.FromMilliseconds(1000));

    [Fact]
    public void ARegionSayOfAScriptAnotherEngineHostsIsHeardByAPhloxListenOnce()
    {
        var (h, api, _) = Rig();
        using (h)
        {
            api.llRegionSay(5, "everyone");
            WaitFor(h, _ => Times(h, "PHLOX", "5:everyone") > 0, 20);
            Settle(h);
            Assert.True(Times(h, "PHLOX", "5:everyone") == 1, "heard " + Times(h, "PHLOX", "5:everyone") + " times: [" + string.Join(" | ", h.Said) + "]");
        }
    }

    [Fact]
    public void ASayOfAScriptAnotherEngineHostsIsHeardByAPhloxListenOnce()
    {
        var (h, api, _) = Rig();
        using (h)
        {
            api.llSay(5, "near");
            WaitFor(h, _ => Times(h, "PHLOX", "5:near") > 0, 20);
            Settle(h);
            Assert.True(Times(h, "PHLOX", "5:near") == 1, "heard " + Times(h, "PHLOX", "5:near") + " times: [" + string.Join(" | ", h.Said) + "]");
        }
    }

    /// <summary>An addressed message is not scene chat: it reaches another engine's listen only through WorldComm, and must still arrive.</summary>
    [Fact]
    public void ARegionSayToAPhloxObjectFromAScriptAnotherEngineHostsIsHeardOnce()
    {
        var (h, api, listening) = Rig();
        using (h)
        {
            api.llRegionSayTo(listening.RootPart.UUID.ToString(), 5, "you");
            WaitFor(h, _ => Times(h, "PHLOX", "5:you") > 0, 20);
            Settle(h);
            Assert.True(Times(h, "PHLOX", "5:you") == 1, "heard " + Times(h, "PHLOX", "5:you") + " times: [" + string.Join(" | ", h.Said) + "]");
        }
    }
}
