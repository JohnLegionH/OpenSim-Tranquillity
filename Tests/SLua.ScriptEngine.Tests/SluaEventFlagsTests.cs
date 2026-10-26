/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An SLua script registers its handlers while it runs (LLEvents:on and :off), so the runtime reports a change of its
/// handler set and the engine tells the region the events the script now handles. The moments are the LSL ones: start,
/// a change while running, stop, start, reset, fault and removal.
/// </summary>
[Collection("native-library")]
public class SluaEventFlagsTests
{
    private static ulong Of(params scriptEvents[] events) => events.Aggregate(0UL, (all, e) => all | (ulong)e);

    private static readonly ulong Touch = Of(scriptEvents.touch_start);
    private static readonly ulong TouchAndCollision = Of(scriptEvents.touch_start, scriptEvents.collision_start);

    private const string TouchOnly = "LLEvents:on(\"touch_start\", function() end)";

    // A touch adds a collision handler, then another touch takes the touch handler off.
    private const string GrowsThenShrinks =
        "local onTouch\n" +
        "onTouch = function()\n" +
        "  if not added then added = true LLEvents:on(\"collision_start\", function() end) else LLEvents:off(\"touch_start\", onTouch) end\n" +
        "end\n" +
        "LLEvents:on(\"touch_start\", onTouch)";

    private static bool Touched(EngineRig rig, UUID item) => rig.Engine.PostScriptEvent(item, "touch_start", new object[] { 1 });

    [RequiresNativeFact]
    public void ASluaScriptGetsTheFlagsOfTheHandlersItRegistersWhenItStarts()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(TouchOnly));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));
        Assert.Equal(rig.LocalId, flags.Calls.First(c => c.Item == item && c.Flags == Touch).Local);
    }

    [RequiresNativeFact]
    public void TouchAlsoAsksForTouchStartAndTouchEnd()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on(\"touch\", function() end)"));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Of(scriptEvents.touch, scriptEvents.touch_start, scriptEvents.touch_end))));
    }

    [RequiresNativeFact]
    public void AHandlerRegisteredWhileTheScriptRunsAddsItsFlagAndOneTakenOffRemovesIt()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(GrowsThenShrinks));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));

        Assert.True(Touched(rig, item));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, TouchAndCollision)));

        Assert.True(Touched(rig, item));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Of(scriptEvents.collision_start))));
        Assert.Equal(new[] { Touch, TouchAndCollision, Of(scriptEvents.collision_start) }, flags.History(item).Distinct().ToArray());
    }

    [RequiresNativeFact]
    public void AStoppedSluaScriptHasNoFlagsAndAStartedOneHasThemAgain()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(TouchOnly));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));
    }

    [RequiresNativeFact]
    public void ASluaScriptThatStartsStoppedHasNoFlagsUntilItIsStarted()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(TouchOnly), running: false);
        rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));
    }

    [RequiresNativeFact]
    public void AResetRunsTheScriptAgainAndTheFlagsComeBackFromWhatItRegistersThen()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(GrowsThenShrinks));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));
        Assert.True(Touched(rig, item));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, TouchAndCollision)));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        // back to what the fresh run registers: touch alone, not the collision handler of the run before
        Assert.True(SchedulerHarness.WaitFor(() => flags.History(item).Length > 2 && flags.HasLast(item, Touch)));
    }

    [RequiresNativeFact]
    public void ARemovedSluaScriptIsRemovedFromThePrim()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua(TouchOnly));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => flags.Calls.Any(c => c.Kind == "remove" && c.Item == item && c.Local == rig.LocalId)));
    }

    [RequiresNativeFact]
    public void ASluaScriptThatFaultsHasNoFlags()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on(\"touch_start\", function() error(\"boom\") end)"));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Touch)));

        Assert.True(Touched(rig, item));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));
    }

    [RequiresNativeFact]
    public void EachSluaScriptOnAPrimSetsItsOwnFlagsAndTheRegionsPartHoldsTheUnion()
    {
        using var rig = new EngineRig();
        SceneObjectPart part = rig.Object.RootPart;
        UUID a = rig.Rez(EngineRig.Slua(TouchOnly));
        UUID b = rig.Rez(EngineRig.Slua("LLEvents:on(\"collision_start\", function() end)"));

        Assert.True(SchedulerHarness.WaitFor(() => (part.ScriptEvents & (scriptEvents.touch_start | scriptEvents.collision_start)) == (scriptEvents.touch_start | scriptEvents.collision_start)),
            "both scripts' handlers are on the part");

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, a);
        Assert.True(SchedulerHarness.WaitFor(() => (part.ScriptEvents & scriptEvents.touch_start) == 0), "the removed script's flags are gone");
        Assert.NotEqual(0UL, (ulong)(part.ScriptEvents & scriptEvents.collision_start));
    }
}
