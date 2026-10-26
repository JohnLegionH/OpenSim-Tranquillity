/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What reaches a script while it is held back. During a parcel or estate pause the event is dropped (listens included), as
/// Phlox drops it for a script that is not enabled: PhloxExecutionScheduler.ProcessEventQueue, the check
/// "if (!script.ScriptState.Enabled)" that signals the event complete and moves on. A suspend asked for by the region keeps
/// the events and runs them on resume, as Phlox's m_Suspended branch queues them.
/// </summary>
[Collection("native-library")]
public class PausedEventTests
{
    private const string Listener = "LLEvents:on(\"listen\", function(channel, name, id, message) ossl.Count() end)";

    private static void Listen(SchedulerHarness h, SchedulerScript s) =>
        h.Scheduler.Post(s, "listen", Value.Int(0), Value.Str("Example"), Value.Key(UUID.Random().ToString()), Value.Str("hello"));

    [RequiresNativeFact]
    public void AListenForAScriptHeldByAPolicyPauseIsDroppedNotQueued()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();

        h.Scheduler.Suspend(s, dropEvents: true);
        h.Pump();
        Listen(h, s);
        h.Scheduler.Resume(s);
        h.Pump();

        Assert.Equal(0, h.CountOf(s));
        Listen(h, s);
        h.Pump();
        Assert.Equal(1, h.CountOf(s)); // it hears again once it runs
    }

    [RequiresNativeFact]
    public void AListenForAScriptTheRegionSuspendedIsKeptAndHeardOnResume()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();

        h.Scheduler.Suspend(s);
        h.Pump();
        Listen(h, s);
        h.Scheduler.Resume(s);
        h.Pump();

        Assert.Equal(1, h.CountOf(s));
    }

    [RequiresNativeFact]
    public void AnEstatePausedScriptDropsWhatReachesItAndRunsNewEventsAfterTheSwitchIsLifted()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        rig.PostProbe(item, 5);
        Assert.True(rig.StaysSilent(item, 5));

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        rig.Engine.Policy.RegionSettingsChanged();
        rig.PostProbe(item, 6);
        Assert.True(rig.Heard(item, 6));
        Assert.False(rig.MarksOf(item).Contains("c5"));
    }

    [RequiresNativeFact]
    public void AnAdminSuspendStillQueuesAndTheEventsRunOnResume()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        Assert.True(rig.Engine.SuspendScript(item));
        rig.PostProbe(item, 9);
        Assert.True(rig.StaysSilent(item, 9));

        Assert.True(rig.Engine.ResumeScript(item));
        Assert.True(rig.Heard(item, 9));
    }

    [RequiresNativeFact]
    public void DisablingThenEnablingScriptsLeavesAScriptItsStateRunningOnceWithNoDoubleHandling()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl("integer n; default { changed(integer c) { n = n + 1; llOwnerSay(\"n\" + (string)n); } }"));
        rig.WaitScheduled(item);
        rig.PostProbe(item, 1);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("n1")));
        SchedulerScript before = rig.Engine.SchedulerScriptFor(item);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 2);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("n2")), "the counter went on from 1: the state was kept");

        Assert.Same(before, rig.Engine.SchedulerScriptFor(item)); // the same script, not one made again
        string[] marks = rig.MarksOf(item);
        Assert.Equal(1, marks.Count(m => m == "n1"));
        Assert.Equal(1, marks.Count(m => m == "n2"));
        Assert.Equal(2, marks.Length);
    }
}
