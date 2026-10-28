/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A timer() handler that sets the timer (a new interval, or none) while its own tick is being dispatched. The tick
/// is still at the head of the script's queue then, and setting the timer must not take it out from under the dispatch.
/// </summary>
[Collection("native-library")]
public class TimerHandlerTests
{
    private const string Retimes = "default { state_entry() { llSetTimerEvent(1.0); } timer() { llOwnerSay(\"t\"); llSetTimerEvent(2.0); } }";
    private const string StopsItself = "default { state_entry() { llSetTimerEvent(1.0); } timer() { llOwnerSay(\"t\"); llSetTimerEvent(0.0); } }";
    private const string SetsFirst = "default { state_entry() { llSetTimerEvent(1.0); } timer() { llSetTimerEvent(2.0); llOwnerSay(\"t\"); } }";

    [RequiresNativeFact]
    public void ATimerHandlerThatSetsANewIntervalRunsOnceAndTheNextTickComesAtTheNewInterval()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Retimes, lsl: true);
        h.Pump();

        h.Advance(1.1);
        Assert.Equal(0, h.FaultsSeen);
        Assert.Equal(ScriptRunState.Idle, s.State);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
        Assert.Equal(2.0, s.TimerInterval);

        h.Advance(1.5);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s)); // the new interval is 2 s, not the old 1 s
        h.Advance(0.6);
        Assert.Equal(new[] { "say:t", "say:t" }, h.MarksOf(s));
        Assert.Equal(0, h.FaultsSeen);
    }

    [RequiresNativeFact]
    public void ATimerHandlerThatStopsItsOwnTimerRunsOnceAndNoTickFollows()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(StopsItself, lsl: true);
        h.Pump();

        h.Advance(1.1);
        Assert.Equal(0, h.FaultsSeen);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
        Assert.Equal(0.0, s.TimerInterval);

        h.Advance(10.0);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ATimerHandlerThatSetsTheTimerBeforeItDoesAnythingElseStillFinishesItsHandler()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(SetsFirst, lsl: true);
        h.Pump();

        h.Advance(1.1);
        Assert.Equal(0, h.FaultsSeen);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
        Assert.Equal(2.0, s.TimerInterval);
        Assert.Equal(0, s.Events.Count); // the tick left the queue exactly once
    }
}
