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

/// <summary>Stands in for the region's flag API: remembers every call, and the flags each script item was given.</summary>
internal sealed class RecordingPrimFlags : IPrimEventFlags
{
    private readonly object m_lock = new();
    private readonly List<(string Kind, uint Local, UUID Item, ulong Flags)> m_calls = new();

    public void Set(uint localId, UUID itemId, ulong flags)
    {
        lock (m_lock) m_calls.Add(("set", localId, itemId, flags));
    }

    public void Remove(uint localId, UUID itemId)
    {
        lock (m_lock) m_calls.Add(("remove", localId, itemId, 0));
    }

    public (string Kind, uint Local, UUID Item, ulong Flags)[] Calls
    {
        get { lock (m_lock) return m_calls.ToArray(); }
    }

    /// <summary>The flags of the item's set calls, in order.</summary>
    public ulong[] History(UUID item) => Calls.Where(c => c.Item == item && c.Kind == "set").Select(c => c.Flags).ToArray();

    /// <summary>True when the item's latest call was a set with exactly these flags.</summary>
    public bool HasLast(UUID item, ulong flags)
    {
        var mine = Calls.Where(c => c.Item == item).ToArray();
        return mine.Length > 0 && mine[^1].Kind == "set" && mine[^1].Flags == flags;
    }
}

/// <summary>
/// The region raises touch, collision, at_target and the like only for a prim whose script-event flags include them,
/// so the engine sets each LSL script's flags from the handler masks in its compiled asset: when the script starts,
/// on every state change, when it is stopped, reset or removed. An SLua script's flags wait for the runtime to
/// export the handler set (API version 4).
/// </summary>
[Collection("native-library")]
public class PrimEventFlagsTests
{
    private static ulong Of(params scriptEvents[] events) => events.Aggregate(0UL, (all, e) => all | (ulong)e);

    private static readonly ulong FirstState = Of(scriptEvents.touch_start, scriptEvents.collision_start);

    private static readonly ulong SecondState = Of(scriptEvents.at_target, scriptEvents.moving_start);

    private const string TwoStates =
        "default { touch_start(integer n) { state other; } collision_start(integer n) { } }\n" +
        "state other { at_target(integer h, vector a, vector b) { } moving_start() { } }";

    [RequiresNativeFact]
    public void AScriptStartsWithTheFlagsOfTheEventsItsFirstStateHandles()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
        Assert.Equal(rig.LocalId, flags.Calls.First(c => c.Item == item).Local);
    }

    [RequiresNativeFact]
    public void AHandlerForTouchAlsoAsksForTouchStartAndTouchEnd()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl("default { touch(integer n) { } }"));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, Of(scriptEvents.touch, scriptEvents.touch_start, scriptEvents.touch_end))));
    }

    [RequiresNativeFact]
    public void AStateChangeSetsTheFlagsOfTheNewState()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));

        Assert.True(rig.Engine.PostScriptEvent(item, "touch_start", new object[] { 1 }));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, SecondState)));
        Assert.Equal(new[] { FirstState, SecondState }, flags.History(item));
    }

    [RequiresNativeFact]
    public void AStoppedScriptHasNoFlagsAndAStartedOneHasThemAgain()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
    }

    [RequiresNativeFact]
    public void AScriptThatStartsStoppedHasNoFlagsUntilItIsStarted()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates), running: false);
        rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
    }

    [RequiresNativeFact]
    public void AResetGoesBackToTheFlagsOfTheFirstState()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
        rig.Engine.PostScriptEvent(item, "touch_start", new object[] { 1 });
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, SecondState)));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
    }

    [RequiresNativeFact]
    public void AResetOfAStoppedScriptLeavesItWithoutFlags()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));
        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        // a window in which flags set by the reset would have arrived
        Assert.False(SchedulerHarness.WaitFor(() => !flags.HasLast(item, 0), 400), "a stopped script stays without flags after a reset");
    }

    [RequiresNativeFact]
    public void ARemovedScriptIsRemovedFromThePrim()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl(TwoStates));
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, FirstState)));

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => flags.Calls.Any(c => c.Kind == "remove" && c.Item == item && c.Local == rig.LocalId)));
    }

    [RequiresNativeFact]
    public void AScriptThatFaultsHasNoFlags()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Lsl("default { state_entry() { llDie(); } touch_start(integer n) { } }"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length >= 1), "the script faulted");
        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));
    }

    [RequiresNativeFact]
    public void EachScriptOnAPrimSetsItsOwnFlags()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID a = rig.Rez(EngineRig.Lsl("default { touch_start(integer n) { } }"));
        UUID b = rig.Rez(EngineRig.Lsl("default { collision_start(integer n) { } }"));

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(a, Of(scriptEvents.touch_start)) && flags.HasLast(b, Of(scriptEvents.collision_start))));
    }

    [RequiresNativeFact]
    public void ASluaScriptLeavesTheFlagsAtZeroUntilTheRuntimeExportsItsHandlers()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on(\"touch_start\", function() end)"));
        rig.WaitScheduled(item);

        Assert.True(SchedulerHarness.WaitFor(() => flags.HasLast(item, 0)));
        Thread.Sleep(300); // a window in which flags from a handler set would have arrived
        Assert.All(flags.History(item), f => Assert.Equal(0UL, f));
    }

    // ---- with the region's own part ----

    [RequiresNativeFact]
    public void TheRegionsPartHoldsTheUnionOfTheScriptsOnIt()
    {
        using var rig = new EngineRig();
        SceneObjectPart part = rig.Object.RootPart;
        UUID a = rig.Rez(EngineRig.Lsl("default { touch_start(integer n) { } }"));
        UUID b = rig.Rez(EngineRig.Lsl("default { collision_start(integer n) { } }"));

        Assert.True(SchedulerHarness.WaitFor(() => (part.ScriptEvents & (scriptEvents.touch_start | scriptEvents.collision_start)) == (scriptEvents.touch_start | scriptEvents.collision_start)),
            "both scripts' handlers are on the part");

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, a);
        Assert.True(SchedulerHarness.WaitFor(() => (part.ScriptEvents & scriptEvents.touch_start) == 0), "the removed script's flags are gone");
        Assert.NotEqual(0UL, (ulong)(part.ScriptEvents & scriptEvents.collision_start));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, b);
        Assert.True(SchedulerHarness.WaitFor(() => (part.ScriptEvents & scriptEvents.collision_start) == 0), "a stopped script's flags are gone");
    }
}
