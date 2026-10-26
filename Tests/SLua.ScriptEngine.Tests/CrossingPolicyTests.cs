/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Region crossings, as Phlox handles them: an object's scripts are held while it crosses, keeping the events that reach
/// them; they carry on if the object stays and are left to the region's removal if it left. A crossing object's
/// changed(CHANGED_REGION) waits for a rider still to come, for ten seconds at most. The engine neither loses a script nor
/// runs one twice across a crossing.
/// </summary>
[Collection("native-library")]
public class CrossingPolicyTests
{
    private const string ChangedMarker = "default { changed(integer c) { llOwnerSay(\"c\" + (string)c); } }";

    private static int Count(EngineRig rig, UUID item, string mark) => rig.MarksOf(item).Count(m => m == mark);

    // ---- the hold while the object crosses (hooks 33 and 34) ----

    [RequiresNativeFact]
    public void AnObjectsScriptsAreHeldWhileItCrossesAndKeepTheEventsThatReachThem()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        Assert.Equal(PauseReason.Crossing, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 1);
        rig.PostProbe(item, 2);
        Assert.True(rig.StaysSilent(item, 1));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, false);

        Assert.True(rig.Heard(item, 2));
        Assert.Equal(new[] { "c1", "c2" }, rig.MarksOf(item)); // in the order they came, each once
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        Assert.Equal(1, rig.Engine.PolicyStats.CrossingHeld);
        Assert.Equal(1, rig.Engine.PolicyStats.CrossingReleased);
    }

    [RequiresNativeFact]
    public void AScriptOfAnObjectThatLeftStaysHeldUntilTheRegionRemovesItAndIsNeitherLostNorRunTwice()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), stateSource: (int)StateSource.NewRez);
        SchedulerScript script = rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        rig.PostProbe(item, 1);
        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, true); // the object left

        Assert.Equal(PauseReason.Crossing, rig.Engine.PauseReasonsOf(item));
        Assert.True(rig.StaysSilent(item, 1));
        Assert.Same(script, rig.Engine.SchedulerScriptFor(item)); // still the one script, still here until the region removes it

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);

        Assert.Null(rig.Engine.SchedulerScriptFor(item));
        Assert.Empty(rig.MarksOf(item)); // the held event never ran here
        Assert.False(rig.Engine.PostScriptEvent(item, "changed", new object[] { 3 }));
    }

    [RequiresNativeFact]
    public void AWornObjectIsNotHeldWhenItsAvatarCrosses()
    {
        using var rig = new EngineRig();
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = UUID.Random();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AParcelPauseSurvivesTheEndOfACrossing()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        Assert.Equal(PauseReason.Parcel | PauseReason.Crossing, rig.Engine.PauseReasonsOf(item));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, false);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
    }

    // ---- the changed event of a crossing: the region's own hold is the only wait ----

    [RequiresNativeFact]
    public void WithNoRiderToWaitForTheChangedEventIsPostedAtOnce()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.PrimCrossing);

        Assert.True(rig.Heard(item, 256));
    }

    [RequiresNativeFact]
    public void AGrantWaitingForARiderDoesNotDelayTheChangedEventTheEngineHasNoWaitOfItsOwn()
    {
        var grants = new RecordingGrants { RiderPending = true };
        using var rig = new EngineRig();
        rig.Engine.Grants = grants;
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.PrimCrossing);

        Assert.True(rig.Heard(item, 256));
    }

    [RequiresNativeFact]
    public void AStartForTheRegionStartBringsRegionStart()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.RegionStart);

        Assert.True(rig.Heard(item, 1024));
    }

    /// <summary>
    /// Puts an object into the region's hold for riders as an arrival with riders does (Scene.IncomingCreateObject, which needs the
    /// entity transfer module this test scene does not have): the hold table is the scene's own private one.
    /// </summary>
    private static void HoldForRiders(Scene scene, SceneObjectGroup group, UUID rider, double now)
    {
        object table = typeof(Scene).GetField("m_arrivalHolds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
        Type holdType = typeof(Scene).GetNestedType("ArrivalHold", BindingFlags.NonPublic)!;
        object hold = Activator.CreateInstance(holdType, true)!;
        holdType.GetField("Group")!.SetValue(hold, group);
        holdType.GetField("Waiting")!.SetValue(hold, new HashSet<UUID> { rider });
        holdType.GetField("StartedMs")!.SetValue(hold, now);
        holdType.GetField("DeadlineMs")!.SetValue(hold, now + 10000);
        table.GetType().GetMethod("set_Item")!.Invoke(table, new object[] { group.UUID, hold });
    }

    [RequiresNativeFact]
    public void TheChangedEventOfAnObjectHeldForRidersRunsOnlyAfterTheRidersHaveArrivedWithTheRegionsHoldAlone()
    {
        using var rig = new EngineRig();
        UUID rider = UUID.Random();
        double now = 1000;
        rig.Scene.CrossingClock = () => now;
        SceneObjectGroup arrived = rig.AddObject("Example Arrival", new Vector3(128, 128, 25), EngineRig.Owner);
        arrived.HeldForRiders = true;
        HoldForRiders(rig.Scene, arrived, rider, now);
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.PrimCrossing, part: arrived.RootPart);
        rig.WaitScheduled(item);

        Assert.True(rig.Scene.HoldsArrival(arrived.UUID));
        Assert.True(rig.StaysSilent(item, 256), "the rider has not arrived");

        SceneHelpers.AddScenePresence(rig.Scene, rider); // the rider becomes a root agent: the region releases the object

        Assert.False(rig.Scene.HoldsArrival(arrived.UUID));
        Assert.True(rig.Heard(item, 256));
        Thread.Sleep(200); // a real wait: it runs once
        Assert.Equal(1, Count(rig, item, "c256"));
    }

    [RequiresNativeFact]
    public void TheRegionEndingItsHoldAfterTheTimeLimitReleasesTheChangedEvent()
    {
        using var rig = new EngineRig();
        double now = 1000;
        rig.Scene.CrossingClock = () => now;
        SceneObjectGroup arrived = rig.AddObject("Example Arrival", new Vector3(128, 128, 25), EngineRig.Owner);
        arrived.HeldForRiders = true;
        HoldForRiders(rig.Scene, arrived, UUID.Random(), now);
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.PrimCrossing, part: arrived.RootPart);
        rig.WaitScheduled(item);
        Assert.True(rig.StaysSilent(item, 256));

        now += 10001; // the region's wait for riders is over
        rig.Scene.ReleaseExpiredCrossingHolds();

        Assert.True(rig.Heard(item, 256));
    }

    [RequiresNativeFact]
    public void AScriptHeldForRidersTakesItsOtherEventsAfterTheRelease()
    {
        using var rig = new EngineRig();
        double now = 1000;
        rig.Scene.CrossingClock = () => now;
        SceneObjectGroup arrived = rig.AddObject("Example Arrival", new Vector3(128, 128, 25), EngineRig.Owner);
        arrived.HeldForRiders = true;
        HoldForRiders(rig.Scene, arrived, UUID.Random(), now);
        UUID item = rig.Rez(EngineRig.Lsl(ChangedMarker), stateSource: (int)StateSource.PrimCrossing, part: arrived.RootPart);
        rig.WaitScheduled(item);
        rig.PostProbe(item, 3);

        now += 10001;
        rig.Scene.ReleaseExpiredCrossingHolds();

        Assert.True(rig.Heard(item, 3));
        Assert.True(rig.Heard(item, 256));
    }

    // ---- the region's resume reaches a script that is still loading ----

    private sealed class Gate
    {
        public readonly ManualResetEventSlim Open = new(false);
        public byte[] Compile(string text, bool lsl)
        {
            Open.Wait(TimeSpan.FromSeconds(60));
            return Host.Compile(text, lsl);
        }
    }

    [RequiresNativeFact]
    public void TheRegionsResumeIsAnsweredYesForAScriptStillLoadingSoItsOwnerChangeIsPosted()
    {
        var gate = new Gate();
        using var rig = new EngineRig(compile: gate.Compile);
        try
        {
            UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));

            Assert.True(rig.Engine.ResumeScript(item)); // the region posts changed(OWNER) only for an engine that answers yes
            Assert.False(rig.Engine.ResumeScript(UUID.Random())); // another engine's script
        }
        finally
        {
            gate.Open.Set();
        }
    }
}
