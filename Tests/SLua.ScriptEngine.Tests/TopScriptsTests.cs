/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The figure in the average: a tenth of each new run window added to nine tenths of what was there.</summary>
public class ExecutionTimeAverageTests
{
    [Fact]
    public void ItStartsAtZero()
    {
        Assert.Equal(0.0, new ExecutionTimeAverage().Milliseconds);
    }

    [Fact]
    public void AFirstWindowCountsForATenth()
    {
        var a = new ExecutionTimeAverage();
        a.Add(10.0);
        Assert.Equal(1.0, a.Milliseconds, 6);
    }

    [Fact]
    public void ANewWindowKeepsNineTenthsOfTheOldFigure()
    {
        var a = new ExecutionTimeAverage();
        a.Add(10.0);
        a.Add(10.0);
        Assert.Equal(1.9, a.Milliseconds, 6);
        a.Add(0.0);
        Assert.Equal(1.71, a.Milliseconds, 6);
    }
}

/// <summary>What the region's Top Scripts report and the object's script time read from the engine: a decaying average of the run window in milliseconds, summed per script, per linkset root and for the report.</summary>
[Collection("native-library")]
public class TopScriptsTests
{
    private const string Quiet = "ossl.Mark('start')\n";
    private const string Burner = "ossl.Burn(40)\nossl.Mark('start')\n";

    private static void Started(EngineRig rig, params UUID[] items)
    {
        foreach (UUID item in items)
            Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));
    }

    [RequiresNativeFact]
    public void AScriptThatRanForAWhileShowsAnExecutionTimeAndAnIdleOneDoesNot()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner)), idle = rig.Rez(EngineRig.Slua(Quiet));
        Started(rig, busy, idle);

        float busyTime = rig.Engine.GetScriptExecutionTime(new List<UUID> { busy });
        float idleTime = rig.Engine.GetScriptExecutionTime(new List<UUID> { idle });

        Assert.True(busyTime >= 3.0f, $"a 40 ms window counts 4 ms in the average, got {busyTime}");
        Assert.True(idleTime < 1.0f, $"a script that did nothing is near zero, got {idleTime}");
    }

    [RequiresNativeFact]
    public void TheTimeOfSeveralScriptsIsTheirSumAndAnUnknownScriptAddsNothing()
    {
        using var rig = new EngineRig();
        UUID a = rig.Rez(EngineRig.Slua(Burner)), b = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, a, b);

        float one = rig.Engine.GetScriptExecutionTime(new List<UUID> { a });
        float both = rig.Engine.GetScriptExecutionTime(new List<UUID> { a, b, UUID.Random() });

        Assert.True(one >= 3.0f);
        Assert.True(both > one * 1.5f, $"two scripts of the same kind, got {one} and {both}");
        Assert.Equal(0f, rig.Engine.GetScriptExecutionTime(new List<UUID> { UUID.Random() }));
        Assert.Equal(0f, rig.Engine.GetScriptExecutionTime(null));
    }

    [RequiresNativeFact]
    public void AScriptThatIsStoppedAddsNoTime()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, busy);
        Assert.True(rig.Engine.GetScriptExecutionTime(new List<UUID> { busy }) >= 3.0f);

        rig.Scheduler.SetRunning(rig.Engine.SchedulerScriptFor(busy), false);

        Assert.Equal(0f, rig.Engine.GetScriptExecutionTime(new List<UUID> { busy }));
    }

    [RequiresNativeFact]
    public void TheScriptsOfALinksetAreSummedUnderItsRootLocalId()
    {
        using var rig = new EngineRig();
        SceneObjectGroup group = SceneHelpers.AddSceneObject(rig.Scene, 3, EngineRig.Owner, "Example Part ", 0x9000);
        UUID child1 = rig.Rez(EngineRig.Slua(Burner), part: group.Parts[1]);
        UUID child2 = rig.Rez(EngineRig.Slua(Burner), part: group.Parts[2]);
        Started(rig, child1, child2);
        float single = rig.Engine.GetScriptExecutionTime(new List<UUID> { child1 });

        Dictionary<uint, float> times = rig.Engine.GetObjectScriptsExecutionTimes();

        Assert.Equal(new[] { group.RootPart.LocalId }, times.Keys.ToArray());
        Assert.True(times[group.RootPart.LocalId] > single * 1.5f, $"the root's figure holds both children, got {times[group.RootPart.LocalId]} against {single}");
    }

    [RequiresNativeFact]
    public void TheTopObjectReportListsTheLinksetWithItsTimeAndMemoryAndTheTotals()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, busy);

        ICollection<ScriptTopStatsData> top = rig.Engine.GetTopObjectStats(0.001f, 1024, out float totalTime, out float totalMemory);

        ScriptTopStatsData entry = Assert.Single(top);
        Assert.Equal(rig.LocalId, entry.localID);
        Assert.True(entry.time >= 3.0f);
        Assert.True(entry.memory > 0);
        Assert.Equal(entry.time, totalTime, 3);
        Assert.Equal((float)entry.memory, totalMemory, 3);
    }

    [RequiresNativeFact]
    public void TheTopObjectReportLeavesOutWhatIsBelowBothMinimumsButStillCountsItInTheTotals()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, busy);

        ICollection<ScriptTopStatsData> top = rig.Engine.GetTopObjectStats(10000f, int.MaxValue, out float totalTime, out float totalMemory);

        Assert.Empty(top);
        Assert.True(totalTime >= 3.0f);
        Assert.True(totalMemory > 0);
    }

    [RequiresNativeFact]
    public void AnObjectThatIsGoneFromTheSceneIsLeftOutOfTheReport()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, busy);
        Assert.NotEmpty(rig.Engine.GetTopObjectStats(0.001f, 1024, out _, out _));
        Assert.NotEmpty(rig.Engine.GetObjectScriptsExecutionTimes());
        rig.Scene.DeleteSceneObject(rig.Object, false);

        Assert.Empty(rig.Engine.GetTopObjectStats(0.001f, 1024, out _, out _));
        Assert.Empty(rig.Engine.GetObjectScriptsExecutionTimes());
    }

    [RequiresNativeFact]
    public void TheTopObjectReportKeepsAnObjectThatIsOverEitherMinimum()
    {
        using var rig = new EngineRig();
        UUID busy = rig.Rez(EngineRig.Slua(Burner));
        Started(rig, busy);

        ICollection<ScriptTopStatsData> overMemoryOnly = rig.Engine.GetTopObjectStats(10000f, 0, out _, out _);
        ICollection<ScriptTopStatsData> overTimeOnly = rig.Engine.GetTopObjectStats(0.001f, int.MaxValue, out _, out _);

        Assert.Equal(rig.LocalId, Assert.Single(overMemoryOnly).localID);
        Assert.Equal(rig.LocalId, Assert.Single(overTimeOnly).localID);
    }

    [RequiresNativeFact]
    public void TheTopObjectReportAddsUpTheScriptsOfALinkset()
    {
        using var rig = new EngineRig();
        SceneObjectGroup group = SceneHelpers.AddSceneObject(rig.Scene, 3, EngineRig.Owner, "Example Part ", 0x9000);
        UUID child1 = rig.Rez(EngineRig.Slua(Burner), part: group.Parts[1]);
        UUID child2 = rig.Rez(EngineRig.Slua(Burner), part: group.Parts[2]);
        Started(rig, child1, child2);
        float time = rig.Engine.GetScriptExecutionTime(new List<UUID> { child1, child2 });
        int memory = rig.Engine.GetScriptsMemory(new List<UUID> { child1, child2 });

        ICollection<ScriptTopStatsData> top = rig.Engine.GetTopObjectStats(0.001f, 1024, out float totalTime, out float totalMemory);

        ScriptTopStatsData entry = Assert.Single(top);
        Assert.Equal(group.RootPart.LocalId, entry.localID);
        Assert.Equal(time, entry.time, 1);
        Assert.Equal(memory, entry.memory);
        Assert.Equal(time, totalTime, 1);
        Assert.Equal((float)memory, totalMemory, 1);
    }
}
