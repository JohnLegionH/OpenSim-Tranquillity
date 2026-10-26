/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// The engine with the real loader, the real scheduler on its own threads and the real library, in a test scene. A
/// compiled script is held as bytes by the loader and made by a worker when it starts; the region's start, stop,
/// reset and remove events drive it; a fault stops it and tells the owner. Threads and real time are involved, so
/// the tests poll for results with a generous cap and use fixed windows only to show that something did not happen.
/// </summary>
[Collection("native-library")]
public class EngineLifecycleTests
{
    private const string Counting = "while true do ossl.Count() coroutine.yield() end";

    private static string Slua(string body) => EngineRig.Slua(body);

    // ---- the join ----

    [RequiresNativeFact]
    public void AReadyScriptIsMadeAndStartedByAWorkerFromItsBytes()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));

        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 3), "the script ran");
        Assert.Equal(1, rig.Scheduler.GetStats().Scripts);
        Assert.True(rig.Engine.HasScript(item, out bool running));
        Assert.True(running);
        Assert.True(rig.Engine.GetScriptState(item));
    }

    [RequiresNativeFact]
    public void AScriptWhoseRunningFlagIsOffIsMadeButNotStarted()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting), running: false);
        SchedulerScript s = rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => s.State != ScriptRunState.Loading));

        Thread.Sleep(300); // a window in which a script that was going to run would have run
        Assert.Equal(0, rig.CountOf(item));
        Assert.True(rig.Engine.HasScript(item, out bool running));
        Assert.False(running);
        Assert.False(rig.Engine.GetScriptState(item));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 0), "it ran once its Running flag was set");
    }

    [RequiresNativeFact]
    public void ASecondSaveReplacesTheScriptInTheScheduler()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        SchedulerScript first = rig.WaitScheduled(item);

        rig.Rez(Slua("ossl.Mark(\"second\", 0) " + Counting), item: item);

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item) != first), "the new script replaced the old");
        Assert.True(SchedulerHarness.WaitFor(() => first.State == ScriptRunState.Removed));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("second")));
        Assert.Equal(1, rig.Scheduler.GetStats().Scripts);
    }

    [RequiresNativeFact]
    public void AScriptThatMovesToAnotherEngineLeavesTheScheduler()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        SchedulerScript s = rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 0));

        rig.Rez("//InWorldz.Phlox:\ndefault {}", item: item);

        Assert.True(SchedulerHarness.WaitFor(() => s.State == ScriptRunState.Removed));
        Assert.Null(rig.Engine.SchedulerScriptFor(item));
        Assert.False(rig.Engine.HasScript(item, out _));
        Assert.Equal(0, rig.Scheduler.GetStats().Scripts);
    }

    [RequiresNativeFact]
    public void TheEngineHandsOutTheSchedulersCounters()
    {
        var bare = new LuauEngine(new RecordingLog(), (s, l) => true);
        Assert.Null(bare.GetStats()); // no region, no scheduler

        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 0));

        SchedulerStats stats = rig.Engine.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(1, stats.Scripts);
        Assert.Equal(1, stats.Workers.Length);
    }

    // ---- lifecycle ----

    [RequiresNativeFact]
    public void StoppingAScriptFromTheRegionStopsItAndStartingContinuesIt()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 3));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).State == ScriptRunState.Stopped));
        Assert.False(rig.Engine.GetScriptState(item));
        Assert.True(rig.Engine.HasScript(item, out bool running));
        Assert.False(running);
        long frozen = rig.CountOf(item);
        Thread.Sleep(300);
        Assert.Equal(frozen, rig.CountOf(item));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > frozen + 3), "it carried on");
        Assert.True(rig.Engine.GetScriptState(item));
    }

    [RequiresNativeFact]
    public void AResetFromTheRegionStartsTheScriptOver()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua("ossl.Mark(\"start\", 0) " + Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 3));
        Assert.Equal(new[] { "start:0" }, rig.MarksOf(item));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length == 2), "main ran again from the top");
    }

    [RequiresNativeFact]
    public void AResetOfAStoppedScriptLeavesItStopped()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua("ossl.Mark(\"start\", 0) " + Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 3));
        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).State == ScriptRunState.Stopped));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);
        Thread.Sleep(300);
        Assert.Equal(1, rig.MarksOf(item).Length);
        Assert.False(rig.Engine.GetScriptState(item));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length == 2));
    }

    [RequiresNativeFact]
    public void SuspendAndResumeThroughTheModuleHoldAndReleaseTheScript()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 3));

        Assert.True(rig.Engine.SuspendScript(item));
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).State == ScriptRunState.Suspended));
        Assert.False(rig.Engine.GetScriptState(item));
        long frozen = rig.CountOf(item);
        Thread.Sleep(300);
        Assert.Equal(frozen, rig.CountOf(item));

        Assert.True(rig.Engine.ResumeScript(item));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > frozen + 3));
        Assert.True(rig.Engine.GetScriptState(item));
        Assert.False(rig.Engine.SuspendScript(UUID.Random())); // not ours
        Assert.False(rig.Engine.ResumeScript(UUID.Random()));
    }

    [RequiresNativeFact]
    public void RemovingAScriptFreesItAndTellsTheRegion()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Counting));
        SchedulerScript s = rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 0));
        var removed = new List<UUID>();
        rig.Engine.OnScriptRemoved += id => { lock (removed) removed.Add(id); };

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => s.State == ScriptRunState.Removed));
        Assert.Equal(new[] { item }, removed.ToArray());
        Assert.False(rig.Engine.HasScript(item, out _));
        Assert.Equal(0, rig.Scheduler.GetStats().Scripts);
        Assert.False(rig.Loader.IsReady(item));
    }

    // ---- faults and the owner ----

    [RequiresNativeFact]
    public void ARuntimeErrorStopsTheScriptAndTellsTheOwnerOnDebugChannelOnce()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua("ossl.Mark(\"start\", 0) error('boom')"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1));
        var shout = rig.Messages.Shouts[0];
        Assert.Equal(rig.LocalId, shout.Local);
        Assert.Equal(item, shout.Item);
        Assert.StartsWith("Script " + rig.ItemOf(item).AssetID + " encountered a problem and was stopped: ", shout.Text);
        Assert.Contains("boom", shout.Text);
        Assert.False(rig.ItemOf(item).ScriptRunning); // the Running flag goes off, as the viewer's checkbox does
        Assert.False(rig.Engine.GetScriptState(item));

        Thread.Sleep(400); // a window in which a restart, and a second message, would have come
        Assert.Single(rig.Messages.Shouts);
        Assert.Equal(new[] { "start:0" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AStoppedByFaultScriptStartsAgainOnlyOnAResetOrWhenItsRunningFlagIsSet()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua("ossl.Mark(\"start\", 0) error('boom')"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length == 1));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length == 2), "a reset starts it again");
        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length == 2));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length == 3), "so does its Running flag set again");
        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length == 3));
    }

    [RequiresNativeFact]
    public void AScriptOverItsMemoryLimitIsStoppedAndTheOwnerIsTold()
    {
        using var rig = new EngineRig(new Dictionary<string, string> { ["MemoryLimitKiB"] = "16" });
        UUID item = rig.Rez(Slua("local t = {} for i = 1, 1000000 do t[i] = i end"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1));
        Assert.Contains("was stopped", rig.Messages.Shouts[0].Text);
        Assert.False(rig.ItemOf(item).ScriptRunning);
        Assert.Equal(FaultKindOf(rig, item), Bindings.FaultKind.OutOfMemory);
    }

    private static Bindings.FaultKind FaultKindOf(EngineRig rig, UUID item) => rig.Engine.SchedulerScriptFor(item).Fault;

    [RequiresNativeFact]
    public void ARunawayThePunishmentCannotCureIsKilledAndTheOwnerIsTold()
    {
        using var rig = new EngineRig(new Dictionary<string, string> { ["KillAfterPunishments"] = "3", ["QuantaMs"] = "1" });
        UUID item = rig.Rez(Slua("while true do ossl.Burn(20) end"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1));
        Assert.Contains("3 turns in a row", rig.Messages.Shouts[0].Text);
        Assert.False(rig.ItemOf(item).ScriptRunning);
        Assert.Equal(1, rig.Engine.GetStats().Kills);
    }

    // ---- compile failures ----

    [RequiresNativeFact]
    public void ACompileFailureNobodyCollectsReachesTheOwner()
    {
        using var rig = new EngineRig(graceMs: 100);
        UUID item = rig.Rez(Slua("local a = = 2"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Failures.Length == 1));
        var failure = rig.Messages.Failures[0];
        Assert.Equal(rig.LocalId, failure.Local);
        Assert.Equal(item, failure.Item);
        Assert.StartsWith("(2,0) Error: ", Assert.Single(failure.Errors));
        Assert.Null(rig.Engine.SchedulerScriptFor(item)); // nothing to run
    }

    [RequiresNativeFact]
    public void ACompileFailureTheEditorCollectsDoesNotReachTheOwnerAgain()
    {
        using var rig = new EngineRig(graceMs: 400);
        UUID item = rig.Rez(Slua("local a = = 2"));

        Assert.Single(rig.Engine.GetScriptErrors(item).Cast<object>()); // the editor's call

        Thread.Sleep(900); // well past the grace
        Assert.Empty(rig.Messages.Failures);
    }

    [RequiresNativeFact]
    public void AGoodScriptNeverReachesTheOwnerAsAFailure()
    {
        using var rig = new EngineRig(graceMs: 100);
        UUID item = rig.Rez(Slua(Counting));
        Assert.True(SchedulerHarness.WaitFor(() => rig.CountOf(item) > 0));

        Thread.Sleep(400);
        Assert.Empty(rig.Messages.Failures);
        Assert.Empty(rig.Messages.Shouts);
    }
}
