/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The script module members the region calls: StartProcessing, SaveAllState and the memory figure.</summary>
[Collection("native-library")]
public class EngineModuleMembersTests
{
    private const string Quiet = "ossl.Mark('start')\n";

    [RequiresNativeFact]
    public void StartProcessingTellsTheRegionTheScriptQueueIsEmpty()
    {
        using var rig = new EngineRig();
        int told = 0;
        rig.Scene.EventManager.OnEmptyScriptCompileQueue += (failed, message) => Interlocked.Increment(ref told);

        rig.Engine.StartProcessing();

        Assert.Equal(1, Volatile.Read(ref told));
    }

    [RequiresNativeFact]
    public void SaveAllStateWritesEveryScriptBeforeItReturns()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "state.db"), "SLua");
        // the timed save is an hour away, so only SaveAllState can have written the rows
        using var rig = new EngineRig(new Dictionary<string, string> { ["StateSaveIntervalMs"] = "3600000" }, store: store);
        UUID a = UUID.Random(), b = UUID.Random(), assetA = UUID.Random(), assetB = UUID.Random();
        rig.Rez(EngineRig.Slua(Quiet), item: a, asset: assetA);
        rig.Rez(EngineRig.Slua(Quiet), item: b, asset: assetB);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(a).Contains("start") && rig.MarksOf(b).Contains("start")));
        Assert.False(store.HasSavedState(a, assetA));

        rig.Engine.SaveAllState();

        Assert.True(store.HasSavedState(a, assetA));
        Assert.True(store.HasSavedState(b, assetB));
    }

    [RequiresNativeFact]
    public void TheMemoryOfTheGivenScriptsIsTheSumOfTheirHeaps()
    {
        using var rig = new EngineRig();
        UUID a = rig.Rez(EngineRig.Slua(Quiet)), b = rig.Rez(EngineRig.Slua(Quiet));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(a).Contains("start") && rig.MarksOf(b).Contains("start")));

        int one = rig.Engine.GetScriptsMemory(new List<UUID> { a });
        int both = rig.Engine.GetScriptsMemory(new List<UUID> { a, b });

        Assert.True(one > 0);
        Assert.True(both > one);
        Assert.Equal(0, rig.Engine.GetScriptsMemory(new List<UUID> { UUID.Random() }));
    }
}
