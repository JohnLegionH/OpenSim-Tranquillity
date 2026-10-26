/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Luau.ScriptEngine.Bindings;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// Stop (the Running flag), suspend, resume, reset and remove, for the scheduler's scripts. The pump and the clock are
/// the test's. The tests load the native library, whose handle and counters are process-wide, so they run one at a
/// time with the other native tests.
/// </summary>
[Collection("native-library")]
public class SchedulerLifecycleTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";
    private const string Counter = "local n = 0 while true do n = n + 1 ossl.Mark(\"n\", n) coroutine.yield() end";
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)";
    private const string Safepoint = "for _ = 1, 2 do end";
    private const string LslTimer = "default { state_entry() { llSetTimerEvent(1.0); } timer() { llOwnerSay(\"t\"); } }";
    private const string LslCounting = "integer n; default { state_entry() { n += 1; llOwnerSay((string)n); llSetTimerEvent(1.0); } timer() { llOwnerSay(\"t\"); } }";

    [RequiresNativeFact]
    public void AStoppedScriptIsNotRunAndCostsNothing()
    {
        using var h = new SchedulerHarness();
        SchedulerScript a = h.AddAndStart(Yielder);
        SchedulerScript b = h.AddAndStart(Yielder);
        h.Passes(3);

        h.Scheduler.SetRunning(a, false);
        h.Passes(1);
        Assert.Equal(ScriptRunState.Stopped, a.State);
        Assert.False(a.Running);

        long steps = a.Steps, count = h.CountOf(a), other = h.CountOf(b);
        h.Passes(10);

        Assert.Equal(steps, a.Steps);
        Assert.Equal(count, h.CountOf(a));
        Assert.Equal(other + 10, h.CountOf(b));
    }

    [RequiresNativeFact]
    public void AStoppedScriptContinuesWhereItStoppedWhenItIsSetRunningAgain()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Counter);
        h.Passes(3);
        h.Scheduler.SetRunning(s, false);
        h.Passes(5);
        int before = h.MarksOf(s).Length;
        Assert.True(before >= 2);

        h.Scheduler.SetRunning(s, true);
        h.Passes(2);

        string[] marks = h.MarksOf(s);
        Assert.Equal(before + 2, marks.Length);
        Assert.Equal($"n:{before + 1}", marks[before]); // its local variable survived the stop
        Assert.Equal($"n:{before + 2}", marks[before + 1]);
        Assert.True(s.Running);
    }

    [RequiresNativeFact]
    public void EventsPostedToAStoppedScriptAreDropped()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();
        h.Scheduler.SetRunning(s, false);
        h.Pump();

        for (int i = 1; i <= 3; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();
        h.Scheduler.SetRunning(s, true);
        h.Pump();
        Assert.Empty(h.MarksOf(s));

        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("4"));
        h.Pump();
        Assert.Equal(new[] { "data:4" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AStoppedScriptsTimerIsHeldAndStartsAgainWhenItRuns()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer, lsl: true);
        h.Pump();
        h.Advance(0.5);
        h.Scheduler.SetRunning(s, false);
        h.Pump();

        h.Advance(5.0);
        Assert.Empty(h.MarksOf(s));
        Assert.Equal(1.0, s.TimerInterval); // kept while the script is stopped

        h.Scheduler.SetRunning(s, true);
        h.Pump();
        h.Advance(0.5);
        Assert.Empty(h.MarksOf(s)); // the interval counts from when it ran again
        h.Advance(0.7);
        Assert.Equal(new[] { "say:t" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void ASuspendedScriptKeepsItsEventsAndRunsThemWhenResumed()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();

        h.Scheduler.Suspend(s);
        h.Pump();
        Assert.Equal(ScriptRunState.Suspended, s.State);
        Assert.True(s.Suspended);
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Pump();
        Assert.Empty(h.MarksOf(s));

        h.Scheduler.Resume(s);
        h.Pump();

        Assert.Equal(new[] { "data:1", "data:2" }, h.MarksOf(s));
        Assert.False(s.Suspended);
        Assert.Equal(ScriptRunState.Idle, s.State);
    }

    [RequiresNativeFact]
    public void ResetStartsTheScriptOverWithItsVariablesTimerAndEventsGone()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslCounting, lsl: true);
        h.Pump();
        h.Advance(1.1);
        Assert.Equal(new[] { "say:1", "say:t" }, h.MarksOf(s));

        h.Scheduler.Reset(s);
        h.Pump();
        Assert.Equal(new[] { "say:1", "say:t", "say:1" }, h.MarksOf(s)); // n is 1 again: a fresh script object

        h.Advance(0.5);
        Assert.Equal(3, h.MarksOf(s).Length); // the old timer is gone
        h.Advance(0.6);
        Assert.Equal(new[] { "say:1", "say:t", "say:1", "say:t" }, h.MarksOf(s)); // the new one runs
    }

    [RequiresNativeFact]
    public void ResetDropsASleepAndTheScriptRunsFromTheTop()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Count() ossl.Sleep(60.0) " + Safepoint + " ossl.Count()");
        h.Pump();
        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Scheduler.Reset(s);
        h.Pump();
        Assert.Equal(2, h.CountOf(s)); // started over at once, not after the old sleep
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(61.0);
        Assert.Equal(3, h.CountOf(s)); // one wake-up, not two
    }

    [RequiresNativeFact]
    public void ResetDropsEventsStillQueued()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Mark(\"main\", 0) " + Listener + " ossl.Sleep(5.0)");
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Pump();
        Assert.Equal(new[] { "main:0" }, h.MarksOf(s)); // the event waits behind the sleep

        h.Scheduler.Reset(s);
        h.Advance(6.0);

        Assert.Equal(new[] { "main:0", "main:0" }, h.MarksOf(s)); // the old event did not survive the reset
    }

    [RequiresNativeFact]
    public void ResetOfAStoppedScriptLeavesItStoppedUntilItIsSetRunning()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Mark(\"start\", 0)");
        h.Pump();
        h.Scheduler.SetRunning(s, false);
        h.Scheduler.Reset(s);
        h.Pump();
        Assert.Equal(1, h.MarksOf(s).Length);
        Assert.False(s.Running);

        h.Scheduler.SetRunning(s, true);
        h.Pump();
        Assert.Equal(new[] { "start:0", "start:0" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void RemovingAScriptFreesItAndTheOthersCarryOn()
    {
        AllocStats baseline = Host.GetAllocStats();
        using var h = new SchedulerHarness();
        SchedulerScript a = h.AddAndStart(Yielder), b = h.AddAndStart(Yielder), c = h.AddAndStart(Yielder);
        h.Passes(3);
        Assert.Equal(baseline.LiveScripts + 3, Host.GetAllocStats().LiveScripts);

        h.Scheduler.Remove(b);
        h.Passes(1);
        Assert.Equal(ScriptRunState.Removed, b.State);
        Assert.Equal(baseline.LiveScripts + 2, Host.GetAllocStats().LiveScripts);
        Assert.Equal(2, h.Scheduler.GetStats().Scripts);

        long steps = b.Steps, ca = h.CountOf(a), cc = h.CountOf(c);
        h.Passes(5);
        Assert.Equal(steps, b.Steps);
        Assert.Equal(ca + 5, h.CountOf(a));
        Assert.Equal(cc + 5, h.CountOf(c));
    }

    [RequiresNativeFact]
    public void ARemovedScriptIgnoresEveryLaterCall()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();
        h.Scheduler.Remove(s);
        h.Pump();

        h.Scheduler.SetRunning(s, true);
        h.Scheduler.Suspend(s);
        h.Scheduler.Resume(s);
        h.Scheduler.Reset(s);
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Pump();

        Assert.Equal(ScriptRunState.Removed, s.State);
        Assert.Empty(h.MarksOf(s));
        Assert.Empty(h.Log.Errors);
    }
}

/// <summary>
/// The kill rules: a script the runtime punishes turn after turn is killed; a memory fault or a runtime error stops
/// it; a stopped script stays stopped until it is reset or its Running flag is set again.
/// </summary>
[Collection("native-library")]
public class SchedulerKillRuleTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";
    private const string Burner = "while true do ossl.Burn(20) end";
    private const string BurnEveryOtherTurn = "local i = 0 while true do i = i + 1 if i % 2 == 0 then ossl.Burn(20) end coroutine.yield() end";

    private static void Run(SchedulerHarness h, int rounds, double step, Func<bool> until = null)
    {
        for (int i = 0; i < rounds && !(until?.Invoke() ?? false); i++)
        {
            h.Clock.Now += step;
            h.Passes(2);
        }
    }

    [RequiresNativeFact]
    public void AScriptPunishedTurnAfterTurnIsKilledAndTheOthersCarryOn()
    {
        using var h = new SchedulerHarness(quantaMs: 1.0, configure: o => o.KillAfterPunishments = 3);
        SchedulerScript burner = h.AddAndStart(Burner);
        SchedulerScript other = h.AddAndStart(Yielder);

        Run(h, 40, 0.5, () => burner.State == ScriptRunState.Faulted);

        Assert.Equal(ScriptRunState.Faulted, burner.State);
        Assert.True(burner.Killed);
        Assert.False(burner.Running);
        Assert.Equal(FaultKind.Timeout, burner.Fault);
        Assert.Contains("3 turns in a row", burner.FaultMessage);
        Assert.Equal(3, burner.Punishments);
        Assert.Equal(new[] { burner }, h.FaultedScripts.ToArray());
        long before = h.CountOf(other);
        h.Passes(5);
        Assert.Equal(before + 5, h.CountOf(other));
    }

    [RequiresNativeFact]
    public void PunishmentsThatAreNotInARowDoNotKill()
    {
        using var h = new SchedulerHarness(quantaMs: 1.0, configure: o => o.KillAfterPunishments = 2);
        SchedulerScript s = h.AddAndStart(BurnEveryOtherTurn);

        Run(h, 60, 0.5, () => s.Punishments >= 4);

        Assert.True(s.Punishments >= 4, $"punishments: {s.Punishments}");
        Assert.NotEqual(ScriptRunState.Faulted, s.State);
        Assert.False(s.Killed);
        Assert.Empty(h.FaultedScripts);
    }

    [RequiresNativeFact]
    public void AMemoryFaultStopsTheScriptForGood()
    {
        using var h = new SchedulerHarness(memoryLimit: 16 * 1024);
        SchedulerScript hog = h.AddAndStart("local t = {} for i = 1, 1000000 do t[i] = i end");
        h.Passes(5);
        Assert.Equal(FaultKind.OutOfMemory, hog.Fault);
        Assert.True(hog.Killed);
        Assert.False(hog.Running);

        long steps = hog.Steps;
        h.Clock.Now += 3600;
        h.Passes(20);

        Assert.Equal(steps, hog.Steps); // never silently started again
        Assert.Equal(ScriptRunState.Faulted, hog.State);
        Assert.Single(h.FaultedScripts);
    }

    [RequiresNativeFact]
    public void ARuntimeErrorStopsTheScriptForGood()
    {
        using var h = new SchedulerHarness();
        SchedulerScript bad = h.AddAndStart("ossl.Mark(\"start\", 0) error('boom')");
        h.Passes(5);
        Assert.Equal(FaultKind.Runtime, bad.Fault);
        Assert.Contains("boom", bad.FaultMessage);
        Assert.True(bad.Killed);
        Assert.False(bad.Running);

        h.Clock.Now += 3600;
        h.Passes(20);

        Assert.Equal(new[] { "start:0" }, h.MarksOf(bad));
        Assert.Single(h.FaultedScripts);
    }

    [RequiresNativeFact]
    public void SettingTheRunningFlagAgainStartsAStoppedScriptOverFresh()
    {
        using var h = new SchedulerHarness();
        SchedulerScript bad = h.AddAndStart("ossl.Mark(\"start\", 0) error('boom')");
        h.Passes(5);
        Assert.True(bad.Killed);

        h.Scheduler.SetRunning(bad, true);
        h.Passes(5);

        Assert.Equal(new[] { "start:0", "start:0" }, h.MarksOf(bad));
        Assert.Equal(2, h.FaultedScripts.Count); // it faulted again, on its own account
    }

    [RequiresNativeFact]
    public void AResetStartsAStoppedScriptOverFresh()
    {
        using var h = new SchedulerHarness();
        SchedulerScript bad = h.AddAndStart("ossl.Mark(\"start\", 0) error('boom')");
        h.Passes(5);
        Assert.True(bad.Killed);

        h.Scheduler.Reset(bad);
        h.Passes(5);

        Assert.Equal(new[] { "start:0", "start:0" }, h.MarksOf(bad));
        Assert.True(bad.Killed); // and it died of the same error again
        Assert.Equal(2, h.FaultedScripts.Count);
    }

    [RequiresNativeFact]
    public void TheKillLimitAndTheErrorPauseDefaultToTwentyAndFifteenMilliseconds()
    {
        var options = new SchedulerOptions();
        Assert.Equal(20, options.KillAfterPunishments);
        Assert.Equal(0.015, options.ErrorPauseSeconds);
        Assert.Equal(1000, options.WorkerRestartDelayMs);
        Assert.Equal(10, options.MaxWorkerCrashes);
    }
}

/// <summary>Worker crash restart (Phlox's rule) and the cap on a plain step the runtime keeps refusing.</summary>
[Collection("native-library")]
public class SchedulerCrashAndRefusalTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";

    [RequiresNativeFact]
    public void AWorkerWhosePassFailedCarriesOnAfterTheRestartDelay()
    {
        int calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var h = new SchedulerHarness(threads: true, clock: new MonotonicClock(), configure: o =>
        {
            o.WorkerRestartDelayMs = 30;
            o.BeforePass = (worker, pass) => { if (Interlocked.Increment(ref calls) <= 3) throw new InvalidOperationException("injected"); };
        });
        SchedulerScript s = h.Add(Yielder);
        h.Scheduler.Start(s);

        Assert.True(SchedulerHarness.WaitFor(() => h.CountOf(s) > 0));
        Assert.True(watch.ElapsedMilliseconds >= 85, $"three restarts took {watch.ElapsedMilliseconds} ms");
        SchedulerStats stats = h.Scheduler.GetStats();
        Assert.Equal(3, stats.Crashes);
        Assert.Equal(0, stats.FailedWorkers);
        Assert.Empty(h.StoppedWorkers);
        Assert.Equal(3, h.Log.Errors.Count(e => e.Contains("injected")));
    }

    [RequiresNativeFact]
    public void AWorkerThatFailsTenTimesStopsAndIsReportedOnce()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var h = new SchedulerHarness(threads: true, clock: new MonotonicClock(), configure: o =>
        {
            o.WorkerRestartDelayMs = 10;
            o.BeforePass = (worker, pass) => throw new InvalidOperationException("injected");
        });

        Assert.True(SchedulerHarness.WaitFor(() => !h.StoppedWorkers.IsEmpty));
        Thread.Sleep(100); // a window in which a second report would have come
        var stopped = Assert.Single(h.StoppedWorkers);
        Assert.Equal(0, stopped.Index);
        Assert.Equal(10, stopped.Crashes);
        Assert.Contains("injected", stopped.Message);
        Assert.True(watch.ElapsedMilliseconds >= 90, $"nine restart delays took {watch.ElapsedMilliseconds} ms");
        WorkerStats w = h.Scheduler.GetStats().Workers[0];
        Assert.True(w.Failed);
        Assert.Equal(10, w.Crashes);
        Assert.Equal(1, h.Scheduler.GetStats().FailedWorkers);
        Assert.Throws<InvalidOperationException>(() => h.Add(Yielder));
    }

    [RequiresNativeFact]
    public void AFailedWorkerDoesNotStopTheOthers()
    {
        using var h = new SchedulerHarness(workers: 2, threads: true, clock: new MonotonicClock(), configure: o =>
        {
            o.WorkerRestartDelayMs = 5;
            o.BeforePass = (worker, pass) => { if (worker == 1) throw new InvalidOperationException("injected"); };
        });
        SchedulerScript s = h.Add(Yielder); // the first script goes to worker 0
        h.Scheduler.Start(s);

        Assert.True(SchedulerHarness.WaitFor(() => !h.StoppedWorkers.IsEmpty));
        Assert.Equal(1, h.StoppedWorkers.First().Index);
        long count = h.CountOf(s);
        Assert.True(SchedulerHarness.WaitFor(() => h.CountOf(s) > count + 10));
        SchedulerScript later = h.Add(Yielder); // goes to the worker that is still running
        Assert.Equal(0, later.WorkerIndex);
    }

    [RequiresNativeFact]
    public void StoppingTheSchedulerDuringARestartDelayDoesNotWaitForTheDelay()
    {
        int calls = 0;
        var h = new SchedulerHarness(threads: true, clock: new MonotonicClock(), configure: o =>
        {
            o.WorkerRestartDelayMs = 60000;
            o.BeforePass = (worker, pass) => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("injected"); };
        });
        Assert.True(SchedulerHarness.WaitFor(() => h.Scheduler.GetStats().Crashes == 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        h.Dispose();

        Assert.True(watch.ElapsedMilliseconds < 4000, $"stopping took {watch.ElapsedMilliseconds} ms");
        Assert.Empty(h.Log.Errors.Where(e => e.Contains("did not stop")));
    }

    [RequiresNativeFact]
    public void APlainStepThatKeepsBeingRefusedIsNotSteppedEveryPass()
    {
        bool refuse = true;
        long target = -1;
        using var h = new SchedulerHarness(configure: o =>
            o.StepOverride = s => refuse && s.Id == Interlocked.Read(ref target) ? new StepResult(StepStatus.Refused, 0, 0, FaultKind.None) : null);
        SchedulerScript refused = h.AddAndStart(Yielder);
        SchedulerScript other = h.AddAndStart(Yielder);
        Interlocked.Exchange(ref target, refused.Id);

        h.Passes(50);

        Assert.True(refused.Steps <= 6, $"a refused script was stepped {refused.Steps} times in 50 passes");
        Assert.True(h.CountOf(other) >= 48);
        Assert.True(h.Scheduler.GetStats().RefusedCaps >= 1);

        refuse = false;
        h.Clock.Now += 2.0; // longer than the longest back-off
        h.Passes(3);
        Assert.True(h.CountOf(refused) >= 1); // it runs again once the runtime stops refusing
    }
}

/// <summary>A script's own error report: it goes to the host, and the script pays the error pause.</summary>
[Collection("native-library")]
public class SchedulerErrorReportTests
{
    private const string Safepoint = "for _ = 1, 2 do end";

    [RequiresNativeFact]
    public void AnErrorAScriptReportsReachesTheHostAndPausesTheScriptFifteenMilliseconds()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Count() ossl.Err('bad thing') " + Safepoint + " ossl.Count()");
        h.Pump();

        Assert.Equal(new[] { $"{s.Id}:bad thing" }, h.ReportedErrors.ToArray());
        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(0.010);
        Assert.Equal(1, h.CountOf(s));
        h.Advance(0.006);
        Assert.Equal(2, h.CountOf(s));
    }
}

/// <summary>The status counters the console command will show (E11): every state and every rule has one.</summary>
[Collection("native-library")]
public class SchedulerStatusCounterTests
{
    private const string Yielder = "while true do ossl.Count() coroutine.yield() end";
    private const string Burner = "while true do ossl.Burn(20) end";

    [RequiresNativeFact]
    public void TheCountersReadStoppedSuspendedAndFaultedScripts()
    {
        using var h = new SchedulerHarness();
        SchedulerScript a = h.AddAndStart(Yielder), b = h.AddAndStart(Yielder), c = h.AddAndStart(Yielder), d = h.AddAndStart("error('boom')");
        h.Passes(3);

        h.Scheduler.SetRunning(a, false);
        h.Scheduler.Suspend(b);
        h.Passes(1);

        SchedulerStats stats = h.Scheduler.GetStats();
        Assert.Equal(4, stats.Scripts);
        Assert.Equal(1, stats.Stopped);
        Assert.Equal(1, stats.Suspended);
        Assert.Equal(1, stats.Faulted);
        Assert.Equal(1, stats.Workers[0].Runnable); // c
        Assert.Equal(0, stats.Kills); // a runtime error is a fault, not a kill for time
    }

    [RequiresNativeFact]
    public void TheCountersReadKillsAndPunishments()
    {
        using var h = new SchedulerHarness(quantaMs: 1.0, configure: o => o.KillAfterPunishments = 3);
        SchedulerScript burner = h.AddAndStart(Burner);
        for (int i = 0; i < 40 && burner.State != ScriptRunState.Faulted; i++)
        {
            h.Clock.Now += 0.5;
            h.Passes(2);
        }

        SchedulerStats stats = h.Scheduler.GetStats();
        Assert.Equal(1, stats.Kills);
        Assert.Equal(3, stats.Punishments);
        Assert.Equal(1, stats.Faulted);
    }
}
