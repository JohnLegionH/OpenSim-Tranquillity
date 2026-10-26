/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using Luau.ScriptEngine.Bindings;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// What the scheduler promises every script: a busy one cannot starve the rest of its worker, an idle or sleeping one
/// costs nothing, a runaway or a failing one is cut off without touching its neighbours, and shutdown frees everything.
/// The clock is the test's unless the test needs a thread to wake by itself.
/// </summary>
[Collection("native-library")]
public class SchedulerFairnessTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";
    private const string Spinner = "while true do end";

    [RequiresNativeFact]
    public void ABusyScriptCannotStarveTheOthersOnItsWorker()
    {
        using var h = new SchedulerHarness(quantaMs: 2.0);
        SchedulerScript spin = h.AddAndStart(Spinner);
        SchedulerScript a = h.AddAndStart(Yielder);
        SchedulerScript b = h.AddAndStart(Yielder);
        h.Passes(1);
        long a0 = h.CountOf(a), b0 = h.CountOf(b), spin0 = spin.Steps;

        h.Passes(20);

        Assert.Equal(a0 + 20, h.CountOf(a));
        Assert.Equal(b0 + 20, h.CountOf(b));
        Assert.Equal(spin0 + 20, spin.Steps); // the spinner gets exactly one window per pass, no more
    }

    [RequiresNativeFact]
    public void AnOverrunIsPunishedWithASleepAndTheOthersKeepRunning()
    {
        using var h = new SchedulerHarness(quantaMs: 1.0);
        SchedulerScript burner = h.AddAndStart("while true do ossl.Burn(20) end");
        SchedulerScript other = h.AddAndStart(Yielder);

        // The runtime reports a punishment at the start of the step after the overrun.
        h.Passes(2);
        Assert.Equal(ScriptRunState.Sleeping, burner.State);
        Assert.Equal(1, burner.Punishments);

        long steps = burner.Steps, before = h.CountOf(other);
        h.Passes(10);
        Assert.Equal(steps, burner.Steps); // not stepped while it is punished
        Assert.Equal(before + 10, h.CountOf(other));

        h.Advance(1.0); // 5 x the 20 ms overrun is about 0.1 s
        Assert.True(burner.Steps > steps, "the punished script did not run again after its sleep");
    }

    [RequiresNativeFact]
    public void ADelayTheScriptAskedForIsServedOnceAndIsNotCountedAsPunishment()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Sleep(1.0) for _ = 1, 2 do end ossl.Count() ossl.Sleep(1.0) for _ = 1, 2 do end ossl.Count()");
        h.Pump();
        Assert.Equal(0, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(1.05); // one second, not two: the runtime's later report of the same delay adds nothing
        Assert.Equal(1, h.CountOf(s));
        h.Advance(1.05);
        Assert.Equal(2, h.CountOf(s));
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void APassStopsAtItsTimeBudgetAndTheRestKeepTheirPlaceInTheRing()
    {
        using var h = new SchedulerHarness(quantaMs: 2.0, passBudgetMs: 5.0);
        var spinners = new List<SchedulerScript>();
        for (int i = 0; i < 12; i++) spinners.Add(h.AddAndStart(Spinner));
        h.Passes(1);
        long[] before = spinners.Select(s => s.Steps).ToArray();

        h.Passes(40);

        WorkerStats w = Assert.Single(h.Scheduler.GetStats().Workers);
        Assert.True(w.BudgetCutoffs > 0, "no pass was cut short");
        long[] turns = spinners.Select((s, i) => s.Steps - before[i]).ToArray();
        Assert.True(turns.Max() - turns.Min() <= 1, "turns were not shared evenly: " + string.Join(",", turns));
    }

    [RequiresNativeFact]
    public void AnIdleWorkerSleepsUntilSomethingIsDue()
    {
        using var h = new SchedulerHarness(threads: true, clock: new MonotonicClock());
        var scripts = new List<SchedulerScript>();
        for (int i = 0; i < 50; i++)
        {
            SchedulerScript s = h.Add("default { state_entry() { llSetTimerEvent(3600.0); } }", lsl: true);
            h.Scheduler.Start(s);
            scripts.Add(s);
        }
        Assert.True(SchedulerHarness.WaitFor(() => scripts.All(s => s.State == ScriptRunState.Idle)), "scripts did not settle");
        Thread.Sleep(200); // let the last pass finish
        long wakeups = h.Scheduler.GetStats().Workers[0].Wakeups;

        Thread.Sleep(500); // a window in which nothing is due

        Assert.True(h.Scheduler.GetStats().Workers[0].Wakeups - wakeups <= 1, "an idle worker kept waking");
    }

    [RequiresNativeFact]
    public void AScriptThatRaisesAnErrorEndsAloneAndTheOthersCarryOn()
    {
        using var h = new SchedulerHarness();
        SchedulerScript bad = h.AddAndStart("ossl.Count() error('boom')");
        SchedulerScript good = h.AddAndStart(Yielder);
        h.Passes(5);

        Assert.Equal(ScriptRunState.Faulted, bad.State);
        Assert.Equal(FaultKind.Runtime, bad.Fault);
        Assert.Contains("boom", bad.FaultMessage);
        Assert.Equal(1, h.FaultsSeen);
        Assert.True(h.CountOf(good) >= 5);
        Assert.Equal(ScriptRunState.Runnable, good.State);
    }

    [RequiresNativeFact]
    public void AScriptOverItsMemoryLimitIsFaultedAndTheOthersCarryOn()
    {
        using var h = new SchedulerHarness(memoryLimit: 16 * 1024);
        SchedulerScript hog = h.AddAndStart("local t = {} for i = 1, 1000000 do t[i] = i end");
        SchedulerScript good = h.AddAndStart(Yielder);
        h.Passes(5);

        Assert.Equal(ScriptRunState.Faulted, hog.State);
        Assert.Equal(FaultKind.OutOfMemory, hog.Fault);
        Assert.True(h.CountOf(good) >= 5);
    }

    [RequiresNativeFact]
    public void ATimerKeepsItsBeatBesideASpinningScript()
    {
        using var h = new SchedulerHarness(threads: true, clock: new MonotonicClock(), minTimer: 0.05);
        SchedulerScript spin = h.Add(Spinner);
        SchedulerScript ticker = h.Add("default { state_entry() { llSetTimerEvent(0.1); } timer() { llOwnerSay(\"t\"); } }", lsl: true);
        h.Scheduler.Start(spin);
        h.Scheduler.Start(ticker);

        var clock = Stopwatch.StartNew();
        Assert.True(SchedulerHarness.WaitFor(() => h.MarksOf(ticker).Length >= 10), "the timer did not keep its beat");
        Assert.True(clock.Elapsed.TotalSeconds < 10, "ten ticks of 0.1 s took " + clock.Elapsed.TotalSeconds + " s");
    }

    [RequiresNativeFact]
    public void ShutdownStopsEveryWorkerAndFreesTheNativeObjects()
    {
        AllocStats before = Host.GetAllocStats();
        var clock = Stopwatch.StartNew();
        using (var h = new SchedulerHarness(workers: 3, threads: true, clock: new MonotonicClock()))
        {
            var scripts = new List<SchedulerScript>();
            for (int i = 0; i < 9; i++) scripts.Add(h.Add(i % 3 == 0 ? Spinner : Yielder));
            foreach (SchedulerScript s in scripts) h.Scheduler.Start(s);
            Assert.True(SchedulerHarness.WaitFor(() => scripts.All(s => s.Steps > 2)), "scripts did not run");
            Assert.Equal(before.LiveVms + 3, Host.GetAllocStats().LiveVms);

            h.Scheduler.Dispose();

            Assert.Empty(h.Log.Errors);
            long steps = scripts.Sum(s => s.Steps);
            Thread.Sleep(100); // a window in which nothing may run
            Assert.Equal(steps, scripts.Sum(s => s.Steps));
        }
        Assert.True(clock.Elapsed.TotalSeconds < 5, "shutdown took " + clock.Elapsed.TotalSeconds + " s");
        AllocStats after = Host.GetAllocStats();
        Assert.Equal(before.LiveVms, after.LiveVms);
        Assert.Equal(before.LiveImages, after.LiveImages);
        Assert.Equal(before.LiveScripts, after.LiveScripts);
    }
}
