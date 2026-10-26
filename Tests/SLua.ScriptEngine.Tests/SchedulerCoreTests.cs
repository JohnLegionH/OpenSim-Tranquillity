/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The scheduler's core with no region: workers, the ring, run windows, sleeping, timers, pausing calls and the
/// per-script event queue. The pump and the clock are the test's, so nothing here waits on real time except the
/// run window of a script that never gives up its turn. The tests load the native library, whose handle and
/// counters are process-wide, so they run one at a time with the other native tests.
/// </summary>
[Collection("native-library")]
public class SchedulerCoreTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";
    /// <summary>A loop gives the runtime a safe point to pause the script at after it has asked for a delay.</summary>
    private const string Safepoint = "for _ = 1, 2 do end";
    private const string LslSafepoint = "integer i; for (i = 0; i < 2; ++i) { }";
    private const string LslTimer = "default { state_entry() { llSetTimerEvent(1.0); } timer() { llOwnerSay(\"t\"); } }";

    [RequiresNativeFact]
    public void AScriptIsMadeOnItsWorkerAndIsNotRunUntilItIsStarted()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.Add("ossl.Count()");
        h.Pump();
        Assert.Equal(ScriptRunState.Created, s.State);
        Assert.Equal(0, h.CountOf(s));

        h.Scheduler.Start(s);
        h.Pump();

        Assert.Equal(1, h.CountOf(s));
    }

    [RequiresNativeFact]
    public void AScriptThatHasRunToTheEndIsIdleAndOutOfTheRing()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Count()");
        h.Pump();

        Assert.Equal(ScriptRunState.Idle, s.State);
        Assert.False(h.Scheduler.DoWork(h.Clock.Now));
        WorkerStats w = Assert.Single(h.Scheduler.GetStats().Workers);
        Assert.Equal(0, w.Runnable);
        long steps = s.Steps;
        h.Passes(5);
        Assert.Equal(steps, s.Steps); // an idle script is not stepped
    }

    [RequiresNativeFact]
    public void EveryRunnableScriptGetsOneTurnPerPassInRingOrder()
    {
        using var h = new SchedulerHarness();
        SchedulerScript a = h.AddAndStart(Yielder);
        SchedulerScript b = h.AddAndStart(Yielder);
        SchedulerScript c = h.AddAndStart(Yielder);
        h.Passes(1); // the first pass runs each main function to its first yield

        long a0 = h.CountOf(a), b0 = h.CountOf(b), c0 = h.CountOf(c);
        h.Passes(3);

        Assert.Equal(a0 + 3, h.CountOf(a));
        Assert.Equal(b0 + 3, h.CountOf(b));
        Assert.Equal(c0 + 3, h.CountOf(c));
    }

    [RequiresNativeFact]
    public void ARunWindowEndsAScriptThatWillNotGiveUpItsTurn()
    {
        using var h = new SchedulerHarness(quantaMs: 2.0);
        SchedulerScript spin = h.AddAndStart("while true do end");

        var clock = Stopwatch.StartNew();
        bool more = h.Scheduler.DoWork(h.Clock.Now);
        clock.Stop();

        Assert.True(more); // still runnable: it goes to the back of the ring
        Assert.Equal(ScriptRunState.Runnable, spin.State);
        Assert.Equal(1, spin.Steps);
        Assert.True(clock.ElapsedMilliseconds < 1000, "one turn took " + clock.ElapsedMilliseconds + " ms");
    }

    [RequiresNativeFact]
    public void AForcedDelayParksTheScriptUntilItIsDue()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Count() ossl.Sleep(1.0) " + Safepoint + " ossl.Count()");
        h.Pump();

        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(0.9);
        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(0.2);
        Assert.Equal(2, h.CountOf(s));
        Assert.Equal(ScriptRunState.Idle, s.State);
    }

    [RequiresNativeFact]
    public void ASleepingScriptDoesNotHoldUpTheOthers()
    {
        using var h = new SchedulerHarness();
        SchedulerScript sleeper = h.AddAndStart("ossl.Sleep(60.0) " + Safepoint + " ossl.Count()");
        SchedulerScript worker = h.AddAndStart(Yielder);
        h.Passes(1);
        long before = h.CountOf(worker);

        h.Passes(5);

        Assert.Equal(before + 5, h.CountOf(worker));
        Assert.Equal(0, h.CountOf(sleeper));
        Assert.Equal(ScriptRunState.Sleeping, sleeper.State);
    }

    [RequiresNativeFact]
    public void ALslSleepAndTheCallsAfterItRunInOrder()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("default { state_entry() { llOwnerSay(\"a\"); llSleep(2.0); " + LslSafepoint + " llOwnerSay(\"b\"); } }", lsl: true);
        h.Pump();
        Assert.Equal(new[] { "say:a" }, h.MarksOf(s));

        h.Advance(1.0);
        Assert.Equal(new[] { "say:a" }, h.MarksOf(s));
        h.Advance(1.5);
        Assert.Equal(new[] { "say:a", "say:b" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ALslTimerFiresAtItsIntervalAndNeverBursts()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer, lsl: true);
        h.Pump();
        Assert.Empty(h.MarksOf(s));
        Assert.Equal(1.0, s.TimerInterval);

        h.Advance(0.99);
        Assert.Empty(h.MarksOf(s));
        h.Advance(0.02);
        Assert.Single(h.MarksOf(s));

        h.Advance(1.0);
        Assert.Equal(2, h.MarksOf(s).Length);

        h.Advance(30.0); // a long gap is one tick, not thirty
        Assert.Equal(3, h.MarksOf(s).Length);
    }

    [RequiresNativeFact]
    public void ATimerIsRaisedToTheConfiguredFloor()
    {
        using var h = new SchedulerHarness(minTimer: 0.5);
        SchedulerScript s = h.AddAndStart("default { state_entry() { llSetTimerEvent(0.01); } timer() { llOwnerSay(\"t\"); } }", lsl: true);
        h.Pump();
        Assert.Equal(0.5, s.TimerInterval);

        h.Advance(0.3);
        Assert.Empty(h.MarksOf(s));
        h.Advance(0.3);
        Assert.Single(h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void SettingTheTimerToZeroStopsItAndDropsATickThatIsQueued()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "default { state_entry() { llSetTimerEvent(1.0); } touch_start(integer n) { llSetTimerEvent(0.0); } timer() { llOwnerSay(\"t\"); } }",
            lsl: true);
        h.Pump();
        h.Advance(1.5);
        Assert.Single(h.MarksOf(s));

        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();
        Assert.Equal(0.0, s.TimerInterval);

        h.Advance(10.0);
        Assert.Single(h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ASluaTimerIsReadBackAfterTheMainFunctionAndWakesTheTimerEvent()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("LLTimers:every(2.0, function() ossl.Count() end)");
        h.Pump();
        Assert.Equal(2.0, s.TimerInterval, 3);

        long before = s.Dispatches;
        h.Advance(1.9);
        Assert.Equal(before, s.Dispatches);
        h.Advance(0.2);
        Assert.Equal(before + 1, s.Dispatches); // the host woke the timer event at the interval
    }

    [RequiresNativeFact]
    public void APausingCallParksTheScriptUntilItsResultIsDelivered()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("local v = ossl.Pause() ossl.Mark(\"got\", v)");
        h.Pump();
        Assert.Equal(ScriptRunState.Waiting, s.State);
        long steps = s.Steps;
        h.Passes(5);
        Assert.Equal(steps, s.Steps); // not stepped while it waits
        Assert.Empty(h.MarksOf(s));

        h.Scheduler.CompleteWait(s, Value.Int(42));
        h.Pump();

        Assert.Equal(new[] { "got:42" }, h.MarksOf(s));
        Assert.Equal(ScriptRunState.Idle, s.State);
    }

    [RequiresNativeFact]
    public void EventsAreQueuedAndDispatchedInTheOrderTheyArrived()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)");
        h.Pump();

        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("3"));
        h.Pump();

        Assert.Equal(new[] { "data:1", "data:2", "data:3" }, h.MarksOf(s));
        Assert.Equal(ScriptRunState.Idle, s.State);
    }

    [RequiresNativeFact]
    public void EventsPostedBeforeStartRunAfterTheMainFunction()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.Add("ossl.Mark(\"main\", 0) LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)");
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("7"));
        h.Scheduler.Start(s);
        h.Pump();

        Assert.Equal(new[] { "main:0", "data:7" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AFullEventQueueDropsTheExtraEventsAndCountsThem()
    {
        using var h = new SchedulerHarness(maxQueue: 4);
        SchedulerScript s = h.AddAndStart("LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end) ossl.Sleep(5.0)");
        h.Pump();
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        for (int i = 1; i <= 10; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();
        h.Advance(6.0);

        Assert.Equal(6, s.DroppedEvents);
        Assert.Equal(new[] { "data:1", "data:2", "data:3", "data:4" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ScriptsGoToTheLeastLoadedWorkerAndEachWorkerHasItsOwnVm()
    {
        using var h = new SchedulerHarness(workers: 2);
        var scripts = new List<SchedulerScript>();
        for (int i = 0; i < 6; i++) scripts.Add(h.Add(Yielder));
        h.Pump();

        Assert.Equal(2, h.Scheduler.WorkerCount);
        Assert.Equal(3, scripts.Count(s => s.WorkerIndex == 0));
        Assert.Equal(3, scripts.Count(s => s.WorkerIndex == 1));
        Assert.Equal(2, h.VmsMade);
        Assert.All(h.Scheduler.GetStats().Workers, w => Assert.Equal(3, w.Scripts));
    }

    [RequiresNativeFact]
    public void RemovingAScriptFreesItsNativeObjects()
    {
        AllocStats before = Host.GetAllocStats();
        using (var h = new SchedulerHarness())
        {
            SchedulerScript s = h.AddAndStart(Yielder);
            h.Passes(3);
            Assert.Equal(before.LiveScripts + 1, Host.GetAllocStats().LiveScripts);

            h.Scheduler.Remove(s);
            h.Pump();

            Assert.Equal(ScriptRunState.Removed, s.State);
            Assert.Equal(before.LiveScripts, Host.GetAllocStats().LiveScripts);
            Assert.Equal(before.LiveImages, Host.GetAllocStats().LiveImages);
        }
    }

    [RequiresNativeFact]
    public void ThreadedWorkersRunScriptsWithoutAnyPumping()
    {
        using var h = new SchedulerHarness(workers: 2, threads: true, clock: new MonotonicClock());
        var scripts = new List<SchedulerScript>();
        for (int i = 0; i < 4; i++) scripts.Add(h.Add(Yielder));
        foreach (SchedulerScript s in scripts) h.Scheduler.Start(s);

        Assert.True(SchedulerHarness.WaitFor(() => scripts.All(s => h.CountOf(s) >= 20)), "scripts did not run on the worker threads");
    }

    [RequiresNativeFact]
    public void AThreadedWorkerWakesForATimerWithoutBeingPumped()
    {
        using var h = new SchedulerHarness(threads: true, clock: new MonotonicClock(), minTimer: 0.05);
        SchedulerScript s = h.Add("default { state_entry() { llSetTimerEvent(0.1); } timer() { llOwnerSay(\"t\"); } }", lsl: true);
        h.Scheduler.Start(s);

        Assert.True(SchedulerHarness.WaitFor(() => h.MarksOf(s).Length >= 3), "the timer did not wake the worker");
    }
}
