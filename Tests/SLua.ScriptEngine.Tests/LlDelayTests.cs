/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The forced delay of a call (the call delay of the Second Life wiki) and how a bound function behaves toward the worker.
/// A delayed call that returns nothing ends the script's turn; one that returns values keeps them and its delay still counts.
/// </summary>
[Collection("native-library")]
public class LlDelayTests
{
    private static BoundFunction Fn(string name) => LlBindings.All.Single(f => f.Name == name);

    // ---- the classification, read from the Phlox source by the generator ----

    [Fact]
    public void EveryBoundFunctionHasAClassAndTheTableHoldsNoDelayFigures()
    {
        Assert.NotEmpty(LlBindings.All);
        foreach (BoundFunction f in LlBindings.All)
            Assert.True(Enum.IsDefined(f.Class), f.ApiName + " has no class");
        Assert.All(typeof(BoundFunction).GetProperties(), p => Assert.DoesNotContain("Delay", p.Name));
    }

    [Fact]
    public void EveryClassHasFunctions()
    {
        Assert.NotEmpty(LlBindings.All);
        foreach (FunctionClass c in Enum.GetValues<FunctionClass>())
            Assert.True(LlBindings.All.Any(f => f.Class == c), "no function is classed " + c);
    }

    [Fact]
    public void KnownFunctionsAreClassedAsTheirPhloxSourceHasThem()
    {
        Assert.NotEmpty(LlBindings.All);
        Assert.Equal(FunctionClass.NonBlocking, Fn("GetPos").Class);
        Assert.Equal(FunctionClass.NonBlocking, Fn("Abs").Class);

        // llSleep and llSetPos ask for a delay
        Assert.Equal(FunctionClass.Delayed, Fn("Sleep").Class);
        Assert.Equal(FunctionClass.Delayed, Fn("SetPos").Class);

        // Phlox runs llRezObject off the script's thread and may send llRequestPermissions to the service lane
        Assert.Equal(FunctionClass.Async, Fn("RezObject").Class);
        Assert.Equal(FunctionClass.Service, Fn("RequestPermissions").Class);
    }

    [Fact]
    public void ADelayThatDependsOnASettingIsUnplacedAndListedWithItsReason()
    {
        Assert.NotEmpty(LlBindings.All);
        // chat is slowed by a setting in Phlox, so its delay is not guessed
        Assert.Equal(FunctionClass.Unplaced, Fn("Say").Class);

        string[] listed = LlCoverage.Unplaced;
        foreach (BoundFunction f in LlBindings.All.Where(f => f.Class == FunctionClass.Unplaced))
            Assert.Contains(listed, entry => entry.StartsWith(f.ApiName + ": ", StringComparison.Ordinal));
        Assert.Contains(listed, entry => entry.StartsWith("llSay: ", StringComparison.Ordinal) && entry.Contains("ChatSleep"));
        Assert.All(listed, entry => Assert.Contains(": ", entry));
    }

    // ---- the delay in a running script ----

    [RequiresNativeFact]
    public void AFunctionWithNoDelayDoesNotSleepTheScript()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("local n = ll.Abs(-3) ossl.Mark('n', n) ossl.Count()");
        rig.H.Pump();
        Assert.Equal(1, rig.H.CountOf(s));
        Assert.NotEqual(ScriptRunState.Sleeping, s.State);
        Assert.Equal(1, rig.CallsOf("llAbs"));
    }

    [RequiresNativeFact]
    public void LlSleepEndsTheTurnAndTheScriptWakesAfterItsSeconds()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("ll.Sleep(1.0) ossl.Count()");
        rig.H.Pump();
        Assert.Equal(0, rig.H.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);
        rig.H.Advance(0.9);
        Assert.Equal(0, rig.H.CountOf(s));
        rig.H.Advance(0.2);
        Assert.Equal(1, rig.H.CountOf(s));
        Assert.Equal(1, rig.CallsOf("llSleep"));
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void AnLslScriptsLlSleepWakesAfterItsSeconds()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart(
            "default { state_entry() { llGetPos(); llSleep(2.0); llGetKey(); } }", lsl: true);
        rig.H.Pump();
        Assert.Equal(1, rig.CallsOf("llGetPos"));
        Assert.Equal(0, rig.CallsOf("llGetKey"));
        Assert.Equal(ScriptRunState.Sleeping, s.State);
        rig.H.Advance(2.1);
        Assert.Equal(1, rig.CallsOf("llGetKey"));
    }

    [RequiresNativeFact]
    public void ADelayedFunctionThatReturnsNothingEndsTheTurn()
    {
        // llPreloadSound: void, string; the fake asks for its 1 second as LSLSystemAPI does
        using var rig = new BindingRig();
        rig.Fake.Delays("llPreloadSound", 1000);
        SchedulerScript s = rig.H.AddAndStart("ll.PreloadSound('s') ossl.Count()");
        rig.H.Pump();
        Assert.Equal(0, rig.H.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);
        rig.H.Advance(1.05);
        Assert.Equal(1, rig.H.CountOf(s));
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void ADelayedFunctionThatReturnsValuesKeepsThemAndTwoCallsAddUpTheirDelays()
    {
        // llClearLinkMedia: integer, two integers; the fake asks for its 1 second as LSLSystemAPI does
        Assert.Equal("integer", Fn("ClearLinkMedia").ReturnType);
        using var rig = new BindingRig();
        rig.Fake.Delays("llClearLinkMedia", 1000);
        rig.Fake.Returns("llClearLinkMedia", 7);
        SchedulerScript s = rig.H.AddAndStart(
            "local a = ll.ClearLinkMedia(1, 2)\n" +
            "local b = ll.ClearLinkMedia(1, 2)\n" +
            "for _ = 1, 2 do end\n" + // an interrupt point, where the delay takes effect
            "ossl.Mark('got', a .. ':' .. b)\n" +
            "ossl.Count()");
        rig.H.Pump();
        Assert.Equal(0, rig.H.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        double elapsed = 0;
        while (elapsed < 5 && rig.H.CountOf(s) == 0)
        {
            rig.H.Advance(0.05);
            elapsed += 0.05;
        }
        Assert.InRange(elapsed, 1.99, 2.2);
        Assert.Equal(new[] { "got:7:7" }, rig.H.MarksOf(s));
        Assert.Equal(2, rig.CallsOf("llClearLinkMedia"));
        Assert.Equal(0, s.Punishments);
    }
}
