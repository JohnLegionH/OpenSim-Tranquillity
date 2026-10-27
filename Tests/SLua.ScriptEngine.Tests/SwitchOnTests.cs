/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine registers the ll functions in its VMs: the ones the host serves run on the real API, every other one answers a
/// script with an error that names it, and the service lane is built from the engine's settings.
/// </summary>
[Collection("native-library")]
public class SwitchOnTests
{
    private static string MarkOf(EngineRig rig, UUID item, string prefix)
    {
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Any(m => m.StartsWith(prefix))), "the script marked " + prefix);
        return rig.MarksOf(item).First(m => m.StartsWith(prefix));
    }

    // ---- the fallback ----

    [Fact]
    public void TheFallbackApiRefusesEveryCallByNameAndNeedsNoService()
    {
        ILSLSystemAPI api = UnavailableApi.Create();
        Assert.NotNull(api);

        var e = Assert.Throws<FunctionUnavailableException>(() => api.llSensorRemove());
        Assert.Contains("llSensorRemove", e.Message);
        Assert.Contains("is not available on this engine yet", e.Message);
        Assert.Equal("llSensorRemove", e.Function);
        Assert.False(api.NeedsService("llName2Key", new object[] { "x" }));
    }

    [Fact]
    public void TheUnavailableFunctionsAreTheTwentyEightWaitingOnHostMembersAndTheSixteenPhloxGaps()
    {
        var expected = new HashSet<string>();
        foreach (string row in LlServed.NotServed) expected.Add(row.Substring(0, row.IndexOf(':')));
        foreach (string name in LlCoverage.MissingFromSeam) expected.Add(name);
        foreach (string row in LlCoverage.SignatureMismatch) expected.Add(row.Substring(0, row.IndexOf(':')));

        Assert.Equal(44, expected.Count);
        Assert.Equal(expected.OrderBy(n => n), LlBindings.Unavailable.OrderBy(n => n));
        Assert.Contains("llSensorRemove", LlBindings.Unavailable);
        Assert.Contains("llRequestUsername", LlBindings.Unavailable);
        Assert.DoesNotContain("llAbs", LlBindings.Unavailable);
    }

    // ---- scripts in a region ----

    [RequiresNativeFact]
    public void AServedFunctionRunsOnTheRealApiForAnSLuaScriptAndAnLslScript()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID slua = rig.Rez(Slua("ossl.Mark('name=' .. ll.GetObjectName())"));
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"name=\" + llGetObjectName()); } }"));

        Assert.Equal("name=Example Object", MarkOf(rig, slua, "name="));
        Assert.Equal("name=Example Object", MarkOf(rig, lsl, "name="));
    }

    [RequiresNativeFact]
    public void AnUnservedFunctionCanBeCaughtWithPcallAndTheMessageNamesIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("local ok, err = pcall(function() ll.SensorRemove() end)\nossl.Mark('caught=' .. tostring(ok) .. ' ' .. tostring(err))\nossl.Mark('after')"));

        string caught = MarkOf(rig, item, "caught=");
        Assert.StartsWith("caught=false", caught);
        Assert.Contains("SensorRemove", caught);
        Assert.Contains("is not available on this engine yet", caught);
        Assert.Equal("after", MarkOf(rig, item, "after"));
        Assert.Empty(rig.Messages.Shouts);
    }

    [RequiresNativeFact]
    public void AFunctionTheEngineDoesNotBindAnswersTheSameWay()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("local ok, err = pcall(function() ll.RequestUsername('00000000-0000-0000-0000-000000000000') end)\nossl.Mark('caught=' .. tostring(ok) .. ' ' .. tostring(err))"));

        string caught = MarkOf(rig, item, "caught=");
        Assert.StartsWith("caught=false", caught);
        Assert.Contains("RequestUsername", caught);
        Assert.Contains("is not available on this engine yet", caught);
    }

    [RequiresNativeFact]
    public void AnUncaughtUnservedCallStopsAnSLuaScriptAndTellsTheOwnerOnce()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("ossl.Mark('before')\nll.SensorRemove()\nossl.Mark('never')"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1));
        Assert.Contains("SensorRemove", rig.Messages.Shouts[0].Text);
        Assert.Contains("is not available on this engine yet", rig.Messages.Shouts[0].Text);
        Assert.False(rig.ItemOf(item).ScriptRunning);

        Thread.Sleep(400); // a window in which a second message would have come
        Assert.Single(rig.Messages.Shouts);
        Assert.Equal(new[] { "before" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AnUncaughtUnservedCallStopsAnLslScriptAndTellsTheOwnerOnce()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"before\"); llSensorRemove(); llOwnerSay(\"never\"); } }"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1));
        Assert.Contains("SensorRemove", rig.Messages.Shouts[0].Text);
        Assert.Contains("is not available on this engine yet", rig.Messages.Shouts[0].Text);
        Assert.False(rig.ItemOf(item).ScriptRunning);

        Thread.Sleep(400);
        Assert.Single(rig.Messages.Shouts);
        Assert.Equal(new[] { "before" }, rig.MarksOf(item));
    }

    // ---- the lane ----

    [RequiresNativeFact]
    public void TheLaneIsBuiltFromTheEnginesSettings()
    {
        using (var rig = new EngineRig())
        {
            Assert.NotNull(rig.Engine.Lane);
            Assert.Equal(4, rig.Engine.Lane.MaxThreads);
            Assert.Equal(35000, rig.Engine.Lane.TimeoutMs);
            Assert.True(rig.Engine.Lane.Defers);
        }
        using (var rig = new EngineRig(new Dictionary<string, string>
        {
            ["ServiceCallThreads"] = "2",
            ["ServiceCallTimeoutMs"] = "1234",
            ["ServiceCallDeferral"] = "never",
        }))
        {
            Assert.NotNull(rig.Engine.Lane);
            Assert.Equal(2, rig.Engine.Lane.MaxThreads);
            Assert.Equal(1234, rig.Engine.Lane.TimeoutMs);
            Assert.False(rig.Engine.Lane.Defers);
        }
    }

    [RequiresNativeFact]
    public void AnAsyncFunctionCalledByAScriptRunsOnTheLane()
    {
        using var rig = new EngineRig(productionFunctions: true);
        rig.Rez(Slua("ll.InstantMessage('00000000-0000-0000-0000-000000000001', 'hello')"));

        Assert.NotNull(rig.Engine.Lane);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.Lane.Submitted == 1), "the call was given to the lane");
    }

    // ---- the log and the cache ----

    [RequiresNativeFact]
    public void TheEngineLogsOneStartLineWithTheCountsAndNoLinePerFunction()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("pcall(function() ll.SensorRemove() end)\npcall(function() ll.RequestUsername('00000000-0000-0000-0000-000000000000') end)\nossl.Mark('done')"));
        MarkOf(rig, item, "done");

        string[] counts = rig.Log.Lines.Where(l => l.Contains("485") && l.Contains("44")).ToArray();
        Assert.Single(counts);
        Assert.StartsWith("INFO ", counts[0]);
        Assert.Empty(rig.Log.Warnings);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("llSensorRemove") || l.Contains("does not bind"));
    }

    [Fact]
    public void TheCompileCacheStampNamesTheBoundFunctions()
    {
        Assert.NotEqual("none", SLuaEngine.BoundFunctionHash);
        Assert.Matches("^[0-9a-f]{16}$", SLuaEngine.BoundFunctionHash);
    }
}
