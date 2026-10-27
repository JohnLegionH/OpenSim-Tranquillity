/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine's own host services: the settings the script functions read come from the engine's settings, the calls they make
/// into their engine reach what the engine does, and a real LSLSystemAPI built on them serves a script's calls.
/// </summary>
[Collection("native-library")]
public class EngineHostServicesTests
{
    private static SLuaSettings Settings(params (string Key, string Value)[] values)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        c.Set("Enabled", "true");
        foreach ((string key, string value) in values) c.Set(key, value);
        return SLuaSettings.Read(source, new RecordingLog());
    }

    /// <summary>What the engine was asked to do.</summary>
    private sealed class Backend
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public readonly EngineHostBackend Calling;

        public Backend()
        {
            Calling = new EngineHostBackend
            {
                PostScriptEvent = (item, e) => { Calls.Enqueue("PostScriptEvent " + item + " " + e.EventName); return true; },
                PostObjectEvent = (local, e) => { Calls.Enqueue("PostObjectEvent " + local + " " + e.EventName); return true; },
                HasScript = item => { Calls.Enqueue("HasScript " + item); return (true, true); },
                SetTimer = (item, sec) => Calls.Enqueue("SetTimer " + item + " " + sec),
                SetMinEventDelay = (item, sec) => Calls.Enqueue("SetMinEventDelay " + item + " " + sec),
                SetItemRunningFlag = (local, item, on) => Calls.Enqueue("SetItemRunningFlag " + local + " " + item + " " + on),
                ResetScript = item => Calls.Enqueue("ResetScript " + item),
                RequestParcelCheck = item => Calls.Enqueue("RequestParcelCheck " + item),
                EventQueueFreeSpacePercentage = item => 0.25f,
                SyscallReturn = (item, value, delay, seq) => Calls.Enqueue("SyscallReturn " + item + " " + value + " " + delay + " " + seq),
            };
        }
    }

    // ---- what the host answers ----

    [Fact]
    public void TheHostAnswersTheEnginesSettings()
    {
        var host = new EngineHostServices(
            Settings(("ChatThrottle", "false"), ("ResetThrottle", "false"), ("BotThrottle", "false"), ("PhysicsThrottle", "false"),
                ("LinkMessageThrottle", "false"), ("NotecardThrottle", "false"), ("FormatStringThrottle", "false"),
                ("HttpInFlightThrottle", "false"), ("NotecardCache", "false"), ("NotecardPause", "halcyon"),
                ("MinTimerInterval", "0.5"), ("ServiceCallDeferral", "never"), ("AllowGodFunctions", "true")),
            null, null, null, new EngineHostBackend());

        Assert.False(host.ChatThrottle);
        Assert.False(host.ResetThrottle);
        Assert.False(host.BotThrottle);
        Assert.False(host.PhysicsThrottle);
        Assert.False(host.LinkMessageThrottle);
        Assert.False(host.NotecardThrottle);
        Assert.False(host.FormatStringThrottle);
        Assert.False(host.HttpInFlightThrottle);
        Assert.False(host.NotecardCacheEnabled);
        Assert.True(host.NotecardHalcyonTiming);
        Assert.Equal(0.5f, host.MinTimerInterval);
        Assert.Equal(ServiceCallDeferralMode.Never, host.ServiceCallDeferral);
        Assert.True(host.AllowGodFunctions);
    }

    [Fact]
    public void WithNothingSetTheHostAnswersTheEnginesDefaults()
    {
        var host = new EngineHostServices(Settings(), null, null, null, new EngineHostBackend());

        Assert.True(host.ChatThrottle);
        Assert.True(host.ResetThrottle);
        Assert.True(host.BotThrottle);
        Assert.True(host.PhysicsThrottle);
        Assert.True(host.LinkMessageThrottle);
        Assert.True(host.NotecardThrottle);
        Assert.True(host.FormatStringThrottle);
        Assert.True(host.HttpInFlightThrottle);
        Assert.True(host.NotecardCacheEnabled);
        Assert.False(host.NotecardHalcyonTiming);
        Assert.Equal(0.1f, host.MinTimerInterval);
        Assert.Equal(ServiceCallDeferralMode.Auto, host.ServiceCallDeferral);
        Assert.False(host.AllowGodFunctions);
        Assert.Equal(ServiceCallDeferralMode.Always, new EngineHostServices(Settings(("ServiceCallDeferral", "always")), null, null, null, new EngineHostBackend()).ServiceCallDeferral);
    }

    [Fact]
    public void TheHostHandsOutTheRegionTheConfigAndTheEngineItWasGiven()
    {
        Scene scene = new SceneHelpers().SetupScene();
        var config = new IniConfigSource();
        var module = new SLuaEngine(new RecordingLog(), (_, _) => true);
        var host = new EngineHostServices(Settings(), scene, config, module, new EngineHostBackend());

        Assert.Same(scene, host.World);
        Assert.Same(config, host.ConfigSource);
        Assert.Same(module, host.ScriptModule);
        Assert.Equal(EngineIdentity.EngineName, host.Name);
    }

    [Fact]
    public void TheServicesOnlyPhloxOwnsAreAbsent()
    {
        var host = new EngineHostServices(Settings(), null, null, null, new EngineHostBackend());

        Assert.Null(host.Ossl);
        Assert.Null(host.ListenManager);
        Assert.Null(host.StateManager);
        Assert.Null(host.AsyncCommands);
        Assert.Null(host.NotecardCache);
    }

    // ---- what the calls into the engine do ----

    [Fact]
    public void TheCallsIntoTheEngineReachWhatTheEngineDoes()
    {
        var b = new Backend();
        var host = new EngineHostServices(Settings(), null, null, null, b.Calling);
        UUID item = UUID.Random();
        var e = new EventParams("touch_start", new object[] { 1 }, null);

        Assert.True(host.PostScriptEvent(item, e));
        Assert.True(host.PostObjectEvent(7, e));
        Assert.True(host.HasScript(item, out bool running));
        Assert.True(running);
        host.SetTimerEvent(7, item, 2.5f);
        host.SetMinEventDelay(item, 0.3f);
        host.SetItemRunningFlag(7, item, false);
        host.ResetScript(item);
        host.ApiResetScript(item);
        host.RequestParcelCheck(item);
        Assert.Equal(0.25f, host.GetEventQueueFreeSpacePercentage(item));

        Assert.Equal(new[]
        {
            "PostScriptEvent " + item + " touch_start",
            "PostObjectEvent 7 touch_start",
            "HasScript " + item,
            "SetTimer " + item + " 2.5",
            "SetMinEventDelay " + item + " 0.3",
            "SetItemRunningFlag 7 " + item + " False",
            "ResetScript " + item,
            "ResetScript " + item,
            "RequestParcelCheck " + item,
        }, b.Calls.ToArray());
    }

    [Fact]
    public void WithNothingBehindItTheHostAnswersAsAnEmptyEngineWould()
    {
        var host = new EngineHostServices(Settings(), null, null, null, new EngineHostBackend());
        UUID item = UUID.Random();

        Assert.False(host.PostScriptEvent(item, new EventParams("timer", new object[0], null)));
        Assert.False(host.HasScript(item, out bool running));
        Assert.False(running);
        Assert.Equal(1.0f, host.GetEventQueueFreeSpacePercentage(item));
        host.SetTimerEvent(1, item, 1f);
        host.ResetScript(item);
        host.SysReturn(item, "x", 0);
    }

    [Fact]
    public void ALinksetDataEventGoesToEveryPartOfTheLinkset()
    {
        var b = new Backend();
        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 3, UUID.Random(), "Example Part ", 0x9000);
        var host = new EngineHostServices(Settings(), scene, null, null, b.Calling);

        Assert.True(host.PostObjectLinksetDataEvent(group.RootPart.LocalId, 1, "k", "v"));

        string[] posted = b.Calls.ToArray();
        Assert.Equal(3, posted.Length);
        Assert.All(group.Parts, p => Assert.Contains("PostObjectEvent " + p.LocalId + " linkset_data", posted));
    }

    // ---- how a call's result reaches the script ----

    [Fact]
    public void AReturnMadeInsideTheCallsOwnContextIsRecordedOnItFirstOneWinning()
    {
        var b = new Backend();
        var host = new EngineHostServices(Settings(), null, null, null, b.Calling);
        UUID item = UUID.Random();
        var ctx = new SyscallContext(item, SyscallContext.NextSeq());

        ctx.Enter();
        try
        {
            host.SysReturn(item, "first", 100);
            host.SysReturn(item, "second", 900);
        }
        finally { SyscallContext.Exit(); }

        Assert.True(ctx.HasResult);
        Assert.Equal("first", ctx.Result);
        Assert.Equal(100, ctx.DelayMs);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void AReturnMadeOutsideAnyContextGoesToTheEngineWithoutASequenceNumber()
    {
        var b = new Backend();
        var host = new EngineHostServices(Settings(), null, null, null, b.Calling);
        UUID item = UUID.Random();

        host.SysReturn(item, "late", 40);
        host.SysReturnSequenced(item, "numbered", 60, 17);

        Assert.Equal(new[]
        {
            "SyscallReturn " + item + " late 40 -1",
            "SyscallReturn " + item + " numbered 60 17",
        }, b.Calls.ToArray());
    }

    // ---- the script as the API sees it ----

    [RequiresNativeFact]
    public void EachScriptHasItsOwnRuntimeStateAndListsTheEventsItHandles()
    {
        Assert.True(NativeRuntime.Load(new NativeRuntimeOptions()).Ok);
        var one = new SchedulerScript(1, 0, new byte[0], ScriptLanguage.Slua);
        var two = new SchedulerScript(2, 0, new byte[0], ScriptLanguage.Slua);
        var a = new EngineScriptAccess(one);
        var b = new EngineScriptAccess(two);

        Assert.NotNull(a.ScriptState);
        Assert.NotSame(a.ScriptState, b.ScriptState);
        Assert.Empty(a.EventTypesHandledIn(0));

        ulong mask = (1UL << (Host.EventIndex("touch_start") - 1)) | (1UL << (Host.EventIndex("timer") - 1));
        one.SetHandlers(mask, known: true);

        int[] handled = a.EventTypesHandledIn(0).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { (int)SupportedEventList.Events.TIMER, (int)SupportedEventList.Events.TOUCH_START }.OrderBy(x => x), handled);
        Assert.Empty(b.EventTypesHandledIn(0));
    }

    // ---- a real LSLSystemAPI on the engine's host ----

    [Fact]
    public void ARealApiOnTheEnginesHostSendsATimerToTheEngineWithTheEnginesFloor()
    {
        var b = new Backend();
        var host = new EngineHostServices(Settings(("MinTimerInterval", "0.5")), null, null, null, b.Calling);
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(host, null, 9, item) { RuntimeAccess = new EngineScriptAccess(new SchedulerScript(1, 0, new byte[0], ScriptLanguage.Slua)) };

        api.llSetTimerEvent(0.01f);
        api.llSetTimerEvent(3f);
        api.llResetScript();

        Assert.Equal(new[] { "SetTimer " + item + " 0.5", "SetTimer " + item + " 3", "ResetScript " + item }, b.Calls.ToArray());
    }
}
