/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An idle script that an event wakes goes to the front of its worker's ring, at most once per ring cycle, so the
/// latency of a quiet script's event does not grow with the number of busy scripts and an event flood cannot keep
/// jumping the queue. A cycle ends when a pass has given every runnable script its turn.
/// </summary>
[Collection("native-library")]
public class EventRingTests
{
    private const string Busy = "while true do ossl.Mark(\"busy\", 0) coroutine.yield() end";
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"event\", d) end)";

    private static string[] Kinds(SchedulerHarness h) =>
        h.Marks.Select(m => m.Split(':')[1]).ToArray();

    [RequiresNativeFact]
    public void AnEventThatWakesAnIdleScriptRunsItsHandlerFirstInThePass()
    {
        using var h = new SchedulerHarness();
        for (int i = 0; i < 8; i++) h.AddAndStart(Busy);
        SchedulerScript quiet = h.AddAndStart(Listener);
        h.Passes(3);
        int seen = h.Marks.Length;

        h.Scheduler.Post(quiet, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Passes(1);

        string[] pass = Kinds(h).Skip(seen).ToArray();
        Assert.Equal("event", pass[0]);
        Assert.Equal(8, pass.Count(k => k == "busy")); // the busy scripts still had their turn in that pass
        Assert.Equal(1, quiet.FrontGrants);
    }

    [RequiresNativeFact]
    public void AnEventForAScriptAlreadyInTheRingDoesNotGetAFrontPlace()
    {
        using var h = new SchedulerHarness();
        SchedulerScript slow = h.AddAndStart("LLEvents:on(\"dataserver\", function(q, d) for i = 1, 5 do ossl.Mark(\"h\", i) coroutine.yield() end end)");
        for (int i = 0; i < 3; i++) h.AddAndStart(Busy);
        h.Passes(3);

        h.Scheduler.Post(slow, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Passes(1); // woken: front place, and its handler is now in flight across passes
        h.Scheduler.Post(slow, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Passes(1);

        Assert.Equal(1, slow.FrontGrants);
        Assert.Equal(ScriptRunState.Runnable, slow.State);
    }

    [RequiresNativeFact]
    public void AScriptGetsTheFrontOnlyOncePerRingCycle()
    {
        // A pass budget of zero lets one script run per pass, so one ring cycle of 20 busy scripts spans 20 passes.
        using var h = new SchedulerHarness(passBudgetMs: 0);
        for (int i = 0; i < 20; i++) h.AddAndStart(Busy);
        SchedulerScript quiet = h.AddAndStart(Listener);
        h.Passes(60);
        long cycles = h.Scheduler.GetStats().Cycles;
        for (int i = 0; i < 100 && h.Scheduler.GetStats().Cycles == cycles; i++) h.Passes(1); // until a new cycle has just begun
        Assert.True(h.Scheduler.GetStats().Cycles > cycles, "the worker counts ring cycles");
        int seen = h.Marks.Length;

        h.Scheduler.Post(quiet, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Passes(1); // front: the event runs now
        h.Scheduler.Post(quiet, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Passes(1); // same cycle: no second front, the event waits its turn

        string[] marks = Kinds(h).Skip(seen).ToArray();
        Assert.Equal("event", marks[0]);
        Assert.Equal("busy", marks[1]);
        Assert.Equal(1, quiet.FrontGrants);

        h.Passes(40);
        Assert.Equal(2, Kinds(h).Count(k => k == "event")); // the second event ran in its turn
    }

    [RequiresNativeFact]
    public void AnEventFloodDoesNotStarveTheOtherScripts()
    {
        using var h = new SchedulerHarness();
        var busy = new List<SchedulerScript>();
        for (int i = 0; i < 8; i++) busy.Add(h.AddAndStart(Busy));
        SchedulerScript flooder = h.AddAndStart(Listener);
        h.Passes(3);
        long[] stepsBefore = busy.Select(b => b.Steps).ToArray();
        long dispatchesBefore = flooder.Dispatches;
        long grantsBefore = flooder.FrontGrants;

        const int passes = 60;
        for (int pass = 0; pass < passes; pass++)
        {
            for (int e = 0; e < 20; e++) h.Scheduler.Post(flooder, "dataserver", Value.Key("k"), Value.Str(e.ToString()));
            h.Passes(1);
        }

        for (int i = 0; i < busy.Count; i++)
            Assert.Equal(passes, busy[i].Steps - stepsBefore[i]); // every busy script ran every pass
        Assert.True(flooder.Dispatches - dispatchesBefore <= passes, "the flooder ran at most once per pass");
        Assert.True(flooder.FrontGrants - grantsBefore <= 1, "the flooder's queue never emptied, so it kept its one place");
        Assert.True(flooder.DroppedEvents > 0, "the flood overflowed the queue");
    }
}
