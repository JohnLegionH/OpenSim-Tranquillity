/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using InWorldz.Phlox.Glue;
using Nini.Config;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An os function is gated by Phlox's OsslGate exactly as for a Phlox script: the [OSSL] threat levels, the Allow_ and Creators_
/// lines and the master switch, with the same text when a call is denied. The engine's gate reads [OSSL], and the engine's
/// own section when [OSSL] is absent.
/// </summary>
[Collection("native-library")]
public class OsGateTests
{
    private const string DeniedByLevel = "OSSL Permission Error: osGetSimulatorVersion permission denied.  Allowed threat level is VeryLow but function threat level is High";

    private static Action<IConfigSource> Ossl(params (string Key, string Value)[] settings) => cfg =>
    {
        IConfig section = cfg.AddConfig("OSSL");
        foreach ((string key, string value) in settings) section.Set(key, value);
    };

    /// <summary>Calls the expression in an SLua script and gives "true|result" or "false|error".</summary>
    private static string Call(EngineApiRig rig, string expression)
    {
        SchedulerScript s = rig.H.AddAndStart("local ok, r = pcall(function() return " + expression + " end)\nossl.Mark('r', tostring(ok) .. '|' .. tostring(r))");
        Assert.True(rig.H.PumpAdvancing(() => rig.H.MarksOf(s).Any(m => m.StartsWith("r:")), -1),
            "the script said nothing; faulted: [" + string.Join("; ", rig.H.FaultedScripts.Select(f => f.FaultMessage)) + "], reported: [" + string.Join("; ", rig.H.ReportedErrors) + "]");
        return rig.H.MarksOf(s).Single(m => m.StartsWith("r:")).Substring(2);
    }

    [RequiresNativeFact]
    public void TheEngineGivesTheFunctionsPhloxsOsslGate()
    {
        using var rig = new EngineApiRig();
        Assert.IsType<OsslGate>(rig.Host.Ossl);
    }

    [RequiresNativeFact]
    public void AFunctionAboveTheDefaultThreatLevelIsDeniedWithPhloxsTextAndAnUngatedOneIsNot()
    {
        using var rig = new EngineApiRig();
        string denied = Call(rig, "ossl.GetSimulatorVersion()");
        Assert.StartsWith("false|", denied);
        Assert.Contains(DeniedByLevel, denied);
        Assert.StartsWith("true|", Call(rig, "ossl.GetGridName()"));
    }

    [RequiresNativeFact]
    public void ACallThatOverrunsTheScriptsFirstWindowIsStillAnsweredWithTheGatesText()
    {
        // The first os call in a process can overrun the script's run window (it compiles code); the runtime then punishes the
        // script with a delay on the manual clock, which a wait that only pumps never ends. Burn stands in for that overrun.
        using var rig = new EngineApiRig();
        string denied = Call(rig, "(function() ossl.Burn(60) return ossl.GetSimulatorVersion() end)()");
        Assert.StartsWith("false|", denied);
        Assert.Contains(DeniedByLevel, denied);
    }

    [RequiresNativeFact]
    public void RaisingTheThreatLevelInTheOsslSectionAllowsTheFunction()
    {
        using var rig = new EngineApiRig(Ossl(("OSFunctionThreatLevel", "High")));
        Assert.StartsWith("true|", Call(rig, "ossl.GetSimulatorVersion()"));
    }

    [RequiresNativeFact]
    public void AnAllowLineForTheFunctionAllowsItWhateverTheThreatLevel()
    {
        using var rig = new EngineApiRig(Ossl(("Allow_osGetSimulatorVersion", "true")));
        Assert.StartsWith("true|", Call(rig, "ossl.GetSimulatorVersion()"));
    }

    [RequiresNativeFact]
    public void AnAllowLineNamingAnotherOwnerDeniesTheFunctionWithoutTheThreatLevelText()
    {
        using var rig = new EngineApiRig(Ossl(("Allow_osGetSimulatorVersion", UUID.Random().ToString())));
        string denied = Call(rig, "ossl.GetSimulatorVersion()");
        Assert.StartsWith("false|", denied);
        Assert.Contains("OSSL Permission Error: osGetSimulatorVersion permission denied", denied);
        Assert.DoesNotContain("Allowed threat level", denied);
    }

    [RequiresNativeFact]
    public void ACreatorsLineThatDoesNotNameTheScriptsCreatorDeniesTheFunction()
    {
        using var rig = new EngineApiRig(Ossl(("Creators_osGetSimulatorVersion", UUID.Random().ToString())));
        string denied = Call(rig, "ossl.GetSimulatorVersion()");
        Assert.Contains("osGetSimulatorVersion permission denied. Script creator is not in the list of users allowed to execute this function and prim owner also has no permission", denied);
    }

    [RequiresNativeFact]
    public void TheMasterSwitchDeniesAFunctionThatChecksOnlyTheSwitchAndPermissionErrorToOwnerPrefixesTheText()
    {
        using (var rig = new EngineApiRig(Ossl(("AllowOSFunctions", "false"))))
            Assert.Contains("OSSL Permission Error: All unsafe OSSL funtions disabled", Call(rig, "ossl.GetRegionSize()"));
        using (var rig = new EngineApiRig(Ossl(("AllowOSFunctions", "false"), ("PermissionErrorToOwner", "true"))))
            Assert.Contains("(OWNER)OSSL Permission Error: All unsafe OSSL funtions disabled", Call(rig, "ossl.GetRegionSize()"));
    }

    [RequiresNativeFact]
    public void WithNoOsslSectionTheEnginesOwnSectionSetsTheThreatLevelAndOsslWinsWhenBothAreThere()
    {
        using (var rig = new EngineApiRig(("OSFunctionThreatLevel", "High")))
            Assert.StartsWith("true|", Call(rig, "ossl.GetSimulatorVersion()"));
        using (var rig = new EngineApiRig(Ossl(("OSFunctionThreatLevel", "VeryLow")), ("OSFunctionThreatLevel", "High")))
            Assert.Contains(DeniedByLevel, Call(rig, "ossl.GetSimulatorVersion()"));
    }

    [RequiresNativeFact]
    public void AnLslScriptIsDeniedTheSameFunctionWithTheSameText()
    {
        using var rig = new EngineApiRig();
        OsBindings.ApplyHostList();
        SchedulerScript s;
        try { s = rig.H.AddAndStart("default { state_entry() { osGetSimulatorVersion(); } }", lsl: true); }
        catch (Bindings.SluaException e) { Assert.Fail("the script did not compile: " + e.Message); return; }
        Assert.True(rig.H.PumpAdvancing(() => !rig.H.FaultedScripts.IsEmpty, -1), "the script did not fault");
        SchedulerScript faulted = Assert.Single(rig.H.FaultedScripts);
        Assert.Contains(DeniedByLevel, faulted.FaultMessage);
    }
}

/// <summary>
/// The rules the os, iw and bot functions share with the ll functions: the delay of a call is the one Phlox's code asks for,
/// a long-running or service-reaching call runs on the service lane, a function the engine cannot serve answers "not available"
/// and names itself, and positions are not shifted (Second Life defines none of these functions, so they keep Phlox's convention).
/// </summary>
[Collection("native-library")]
public class OsRulesTests
{
    private const string BotId = "11111111-2222-3333-4444-555555555555";

    private static void AssertSeconds(double expected, double actual) => Assert.InRange(actual, expected - 0.0005, expected + 0.0005);

    [RequiresNativeFact]
    public void TheDelayOfABotCallIsPhloxsDelayUnderTheBotThrottleSetting()
    {
        using (var rig = new EngineApiRig())
        {
            int phlox = rig.PhloxDelayMs(a => { a.botSay(BotId, 0, "hi"); return null; });
            Assert.Equal(15, phlox);
            AssertSeconds(0.015, rig.FirstSleep("bot.Say('" + BotId + "', 0, 'hi')\nossl.Count()"));
        }
        using (var rig = new EngineApiRig(("BotThrottle", "false")))
        {
            Assert.Equal(0, rig.PhloxDelayMs(a => { a.botSay(BotId, 0, "hi"); return null; }));
            Assert.Equal(0, rig.FirstSleep("bot.Say('" + BotId + "', 0, 'hi')\nossl.Count()"));
        }
    }

    [RequiresNativeFact]
    public void TheDelayOfAnLslCallIsThePhloxDelayToo()
    {
        using var rig = new EngineApiRig();
        OsBindings.ApplyHostList();
        AssertSeconds(0.015, rig.FirstSleep("default { state_entry() { botSay(\"" + BotId + "\", 0, \"hi\"); llSay(0, \"after\"); } }", lsl: true));
    }

    /// <summary>The scheduler harness with the os bindings, a fake API and a service lane.</summary>
    private sealed class LaneRig : IDisposable
    {
        public readonly ILSLSystemAPI Api;
        public readonly FakeLslApi Fake;
        public readonly SchedulerHarness H;
        public readonly ServiceLane Lane;
        private readonly ConcurrentDictionary<long, UUID> m_items = new();

        public LaneRig()
        {
            (Api, Fake) = FakeLslApi.Create();
            H = new SchedulerHarness(configure: o =>
            {
                Func<Bindings.Vm> inner = o.VmFactory;
                o.VmFactory = () =>
                {
                    Bindings.Vm vm = inner();
                    OsBindings.Register(vm, s => new ScriptContext(s, Api, m_items.GetOrAdd(s.Id, _ => UUID.Random()), Lane), serviceLane: true);
                    return vm;
                };
            });
            Lane = new ServiceLane(H.Scheduler, "auto", 4, 35000, 30000);
        }

        public bool PumpUntil(Func<bool> condition) => H.PumpAdvancing(condition, -1);

        public void Dispose()
        {
            Lane.Dispose();
            H.Dispose();
        }
    }

    [RequiresNativeFact]
    public void AServiceCallOfAnOsFunctionRunsOnTheLaneAndTheScriptGetsItsValue()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("osKey2Name", a => "Alice Example");
        SchedulerScript s = rig.H.AddAndStart("local n = ossl.Key2Name('" + BotId + "')\nossl.Mark('n', tostring(n))");

        Assert.True(rig.PumpUntil(() => rig.H.MarksOf(s).Contains("n:Alice Example")));

        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Fake.Calls.Single(c => c.Name == "osKey2Name").Thread);
        FakeLslApi.Call asked = rig.Fake.Calls.Single(c => c.Name == "NeedsService");
        Assert.Equal("osKey2Name", asked.Args[0]);
    }

    [RequiresNativeFact]
    public void ALongRunningCallOfABotFunctionRunsOnTheLaneAndGivesItsKeyThroughSysReturn()
    {
        using var rig = new LaneRig();
        rig.Fake.Runs("botCreateBot", _ => { SyscallContext.Current?.SetResult(BotId, 0); return null; });
        SchedulerScript s = rig.H.AddAndStart("local b = bot.CreateBot('A', 'B', 'Outfit', vector(1, 2, 3), 0)\nossl.Mark('b', tostring(b))");

        Assert.True(rig.PumpUntil(() => rig.H.MarksOf(s).Contains("b:" + BotId)));

        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Fake.Calls.Single(c => c.Name == "botCreateBot").Thread);
        Assert.DoesNotContain(rig.Fake.Calls, c => c.Name == "NeedsService");
    }

    [RequiresNativeFact]
    public void AFunctionTheEngineCannotServeAnswersNotAvailableAndNamesItselfWithoutReachingTheApi()
    {
        var (api, fake) = FakeLslApi.Create();
        using var h = new SchedulerHarness(configure: o =>
        {
            Func<Bindings.Vm> inner = o.VmFactory;
            o.VmFactory = () =>
            {
                Bindings.Vm vm = inner();
                OsBindings.Register(vm, s => new ScriptContext(s, api), unavailable: true, refuse: new[] { "osSHA256" });
                return vm;
            };
        });
        SchedulerScript script = h.AddAndStart("local ok, e = pcall(ossl.SHA256, 'abc')\nossl.Mark('r', tostring(ok) .. '|' .. tostring(e))\nossl.Mark('g', tostring(pcall(ossl.GetGridName)))");
        Assert.True(WaitForMark(h, script, "g:"), "the script said nothing");
        string[] marks = h.MarksOf(script);
        Assert.Contains(marks, m => m.StartsWith("r:false|") && m.Contains("osSHA256 is not available on this engine yet"));
        Assert.Contains("g:true", marks);
        Assert.DoesNotContain(fake.Calls, c => c.Name == "osSHA256");
    }

    /// <summary>Pumps until the script has made a mark that starts with <paramref name="prefix"/>, moving the manual clock on so a script punished for an overrun wakes.</summary>
    private static bool WaitForMark(SchedulerHarness h, SchedulerScript script, string prefix) =>
        h.PumpAdvancing(() => h.MarksOf(script).Any(m => m.StartsWith(prefix)), -1);

    [RequiresNativeFact]
    public void AnOverrunBeforeAnUnavailableFunctionIsCalledStillGetsTheNotAvailableAnswer()
    {
        // A script that overruns its first window is punished with a delay on the manual clock; the answer must still come.
        var (api, fake) = FakeLslApi.Create();
        using var h = new SchedulerHarness(configure: o =>
        {
            Func<Bindings.Vm> inner = o.VmFactory;
            o.VmFactory = () =>
            {
                Bindings.Vm vm = inner();
                OsBindings.Register(vm, s => new ScriptContext(s, api), unavailable: true, refuse: new[] { "osSHA256" });
                return vm;
            };
        });
        SchedulerScript script = h.AddAndStart("ossl.Burn(60)\nlocal ok, e = pcall(ossl.SHA256, 'abc')\nossl.Mark('r', tostring(ok) .. '|' .. tostring(e))");
        Assert.True(WaitForMark(h, script, "r:"), "the script said nothing; punishments: " + script.Punishments);
        Assert.Contains(h.MarksOf(script), m => m.StartsWith("r:false|") && m.Contains("osSHA256 is not available on this engine yet"));
        Assert.Equal(1, script.Punishments);
    }

    [Fact]
    public void TheRouterSendsOsIwAndBotCallsToTheRealApiAndKeepsTheFallbackForTheFunctionsTheEngineCannotServe()
    {
        var (real, realFake) = FakeLslApi.Create();
        var (fallback, fallbackFake) = FakeLslApi.Create();
        ILSLSystemAPI router = ApiRouter.Create(real, fallback);

        router.osSHA256("abc");
        router.iwFormatTime(0, 1, "%H");
        router.botIsBot("k");
        router.NeedsService("osKey2Name", new object[] { "k" });
        router.llStringLength("x"); // an ll function that is served still goes the same way

        Assert.Equal(new[] { "osSHA256", "iwFormatTime", "botIsBot", "NeedsService", "llStringLength" }, realFake.Calls.Select(c => c.Name));
        Assert.Empty(fallbackFake.Calls);
        foreach (string name in OsBindings.Unavailable)
            Assert.False(ApiRouter.Serves(name), name + " cannot be served and must go to the fallback");
    }

    [RequiresNativeFact]
    public void PositionsAreShiftedForAnOsFunctionAsTheLlTableShiftsThem()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("ossl.StringSubString('abcdef', 1)\nll.GetSubString('abcdef', 1, 2)");
        Assert.True(rig.H.PumpAdvancing(() => rig.CallsOf("llGetSubString") > 0, -1), "the script made no call");

        // both count from 1 for an SLua script (OsPositionTests holds the rest of the os, iw and bot positions)
        Assert.Equal(0, rig.Fake.Calls.Single(c => c.Name == "osStringSubString").Args[1]);
        Assert.Equal(0, rig.Fake.Calls.Single(c => c.Name == "llGetSubString").Args[1]);
    }
}
