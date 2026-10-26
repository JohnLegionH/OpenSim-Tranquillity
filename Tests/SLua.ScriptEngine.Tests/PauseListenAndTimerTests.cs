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
/// Two things a held script keeps, both as Phlox keeps them. A script held only by its object's crossing does not hear
/// chat: what a listen would deliver is dropped and everything else is kept in order
/// (PhloxExecutionScheduler.ProcessEventQueue, the IsHeldOnlyForCrossing branch, "if (pe.Evt.EventType ==
/// SupportedEventList.Events.LISTEN) continue"). A paused script's timer keeps the time it had left and carries on with
/// it when the script runs again (PhloxExecutionScheduler.PauseForParcel and ResumeForParcel, KeepTimerLeftForParcel
/// and ResumeTimerWithTimeLeft).
/// </summary>
[Collection("native-library")]
public class PauseListenAndTimerTests
{
    private const string ListenAndData =
        "LLEvents:on(\"listen\", function(channel, name, id, message) ossl.Count() end)\n" +
        "LLEvents:on(\"dataserver\", function(query, data) ossl.Mark('data', data) end)";

    private const string LslTimer10 = "default { state_entry() { llSetTimerEvent(10.0); } timer() { llOwnerSay(\"t\"); } }";

    private static void Listen(SchedulerHarness h, SchedulerScript s) =>
        h.Scheduler.Post(s, "listen", Value.Int(0), Value.Str("Example"), Value.Key(UUID.Random().ToString()), Value.Str("hello"));

    // ---- a crossing hold drops listens ----

    [RequiresNativeFact]
    public void AListenForAScriptHeldForACrossingIsDroppedAndItsOtherEventsAreKept()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(ListenAndData);
        h.Pump();

        h.Scheduler.Suspend(s, dropEvents: false, dropListens: true);
        h.Pump();
        Listen(h, s);
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("7"));
        h.Pump();
        h.Scheduler.Resume(s);
        h.Pump();

        Assert.Equal(0, h.CountOf(s));
        Assert.Equal(new[] { "data:7" }, h.MarksOf(s));
        Assert.Equal(1, s.DroppedWhilePaused);
        Listen(h, s);
        h.Pump();
        Assert.Equal(1, h.CountOf(s)); // it hears again once it runs
    }

    [RequiresNativeFact]
    public void AListenForAScriptTheRegionSuspendedWithNoCrossingIsStillKept()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(ListenAndData);
        h.Pump();

        h.Scheduler.Suspend(s, dropEvents: false, dropListens: false);
        h.Pump();
        Listen(h, s);
        h.Pump();
        h.Scheduler.Resume(s);
        h.Pump();

        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(0, s.DroppedWhilePaused);
    }

    [RequiresNativeFact]
    public void AnObjectsCrossingDropsTheListensOfItsScriptsAndKeepsTheirOtherEventsInOrder()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(
            "default { listen(integer c, string n, key i, string m) { llOwnerSay(\"l\" + m); } changed(integer c) { llOwnerSay(\"c\" + (string)c); } }"));
        SchedulerScript script = rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        Assert.Equal(PauseReason.Crossing, rig.Engine.PauseReasonsOf(item));
        Assert.True(rig.Engine.PostScriptEvent(item, "listen", new object[] { 5, "Example Speaker", UUID.Random(), "early" }));
        rig.PostProbe(item, 1);
        Assert.True(rig.Engine.PostScriptEvent(item, "listen", new object[] { 5, "Example Speaker", UUID.Random(), "middle" }));
        rig.PostProbe(item, 2);
        Assert.True(rig.StaysSilent(item, 1));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, false);

        Assert.True(rig.Heard(item, 2));
        Assert.Equal(new[] { "c1", "c2" }, rig.MarksOf(item)); // the changed events in order, no listen
        Assert.Equal(2, script.DroppedWhilePaused);
        Assert.True(rig.Engine.PostScriptEvent(item, "listen", new object[] { 5, "Example Speaker", UUID.Random(), "late" }));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("llate")));
    }

    // ---- a paused script's timer keeps the time it had left ----

    [RequiresNativeFact]
    public void APausedScriptsTimerCarriesOnWithTheTimeItHadLeft()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer10, lsl: true);
        h.Pump();
        h.Advance(7.0); // three seconds left
        Assert.Empty(h.MarksOf(s));

        h.Scheduler.Suspend(s);
        h.Pump();
        h.Advance(100.0);
        Assert.Empty(h.MarksOf(s)); // nothing ticks while it is held

        h.Scheduler.Resume(s);
        h.Pump();
        h.Advance(2.5);
        Assert.Empty(h.MarksOf(s));
        h.Advance(0.6);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s)); // three seconds after the resume, not ten
    }

    [RequiresNativeFact]
    public void ATimerWithTheFullIntervalLeftStillWaitsTheFullIntervalAfterAPause()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer10, lsl: true);
        h.Pump();

        h.Scheduler.Suspend(s);
        h.Pump();
        h.Advance(50.0);
        h.Scheduler.Resume(s);
        h.Pump();
        h.Advance(9.0);
        Assert.Empty(h.MarksOf(s));
        h.Advance(1.2);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ASecondPauseKeepsWhatWasLeftWhenTheFirstBegan()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer10, lsl: true);
        h.Pump();
        h.Advance(8.0); // two seconds left

        h.Scheduler.Suspend(s);
        h.Pump();
        h.Advance(30.0);
        h.Scheduler.Suspend(s); // asked again while held
        h.Pump();
        h.Advance(30.0);
        h.Scheduler.Resume(s);
        h.Pump();

        h.Advance(1.5);
        Assert.Empty(h.MarksOf(s));
        h.Advance(0.6);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
    }
}
