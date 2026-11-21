/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Delays that come back with values, and the question of whether an overrun is the script's own. The runtime counts a
/// run window in wall time, so a worker the system kept off the processor looks like a script that ran over; the CPU
/// time of the windows, which Windows counts in ticks of about 15 ms, answers it only when averaged over many windows.
/// </summary>
[Collection("native-library")]
public class DelayAndOverrunTests
{
    // ---- a delayed call that returns values does not end the turn, and its delay still counts ----

    [RequiresNativeFact]
    public void TwoDelayedCallsThatReturnValuesInARowAddUpTheirDelays()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "local a = ossl.SleepRet(1.0)\n" +
            "local b = ossl.SleepRet(1.0)\n" +
            "for _ = 1, 2 do end\n" + // an interrupt point, where the delay takes effect
            "ossl.Count()");
        h.Pump();
        Assert.Equal(0, h.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        // in small steps, so that a second delay starts when the first ends: the two add up to two seconds
        double elapsed = 0;
        while (elapsed < 5 && h.CountOf(s) == 0)
        {
            h.Advance(0.05);
            elapsed += 0.05;
        }
        Assert.InRange(elapsed, 1.99, 2.2);
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void ADelayedCallThatReturnsValuesGivesThemBackToTheScript()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("local ok, n = ossl.SleepRet(0.5)\nossl.Mark('got', tostring(ok) .. ':' .. tostring(n))");
        h.Pump();
        h.Advance(0.6);
        Assert.Equal(new[] { "got:true:7" }, h.MarksOf(s));
    }

    // ---- the overrun question ----

    /// <summary>
    /// A run window that takes about <see cref="WindowMs"/> of wall time. The CPU time a test gives is for a window of one
    /// millisecond and is scaled to the window's length, so the share of CPU in the wall time is the one the test names. The
    /// windows are long so that a few milliseconds of jitter on the worker (a collection pause, another process on the processor)
    /// cannot move that share across the runtime's threshold of one half.
    /// </summary>
    private sealed class Windows
    {
        private const int WindowMs = 10;

        public readonly ConcurrentQueue<(double Cpu, float Sleep)> Queue = new();

        /// <summary>The window (counting from 0) in which the worker is stalled, and for how long: a collection pause, or another process taking the processor. The CPU time the window reports does not include it.</summary>
        public int StallAtWindow = -1;
        public int StallMs;
        private int m_windows;

        public StepResult? Next(SchedulerScript s)
        {
            if (!Queue.TryDequeue(out var w)) return null;
            int stallMs = m_windows++ == StallAtWindow ? StallMs : 0;
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000 * (WindowMs + stallMs);
            while (Stopwatch.GetTimestamp() < end) { }
            return new StepResult(StepStatus.Ok, 0, w.Sleep, FaultKind.None, w.Cpu * WindowMs);
        }

        public void Add(int count, double cpu) { for (int i = 0; i < count; i++) Queue.Enqueue((cpu, 0f)); }
    }

    private static SchedulerScript RunWindows(Windows w, int passes, out SchedulerHarness harness)
    {
        harness = new SchedulerHarness(configure: o => o.StepOverride = w.Next);
        SchedulerScript s = harness.AddAndStart("ossl.Count()");
        harness.Passes(passes);
        return s;
    }

    [RequiresNativeFact]
    public void AnOverrunAfterManyWindowsThatUsedNoCpuIsTheSystemsAndIsNotPunished()
    {
        var w = new Windows();
        w.Add(32, 0.0);
        w.Queue.Enqueue((0.0, 0.5f)); // the runtime reports a punishment
        SchedulerScript s = RunWindows(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(0, s.Punishments);
            Assert.Equal(1, s.WaivedOverruns);
            Assert.NotEqual(ScriptRunState.Sleeping, s.State);
        }
    }

    [RequiresNativeFact]
    public void AnOverrunAfterManyWindowsThatUsedTheirCpuIsPunished()
    {
        var w = new Windows();
        w.Add(32, 0.001);
        w.Queue.Enqueue((0.001, 0.5f));
        SchedulerScript s = RunWindows(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.Punishments);
            Assert.Equal(0, s.WaivedOverruns);
            Assert.Equal(ScriptRunState.Sleeping, s.State);
        }
    }

    [RequiresNativeFact]
    public void TheWindowsAreAveragedSoATickThatCameInOneWindowInTwoCountsForAll()
    {
        // Windows reports CPU time in coarse ticks: half the windows show a tick, half show nothing, and the
        // average is the whole of the time. The window that reports the overrun shows none.
        var w = new Windows();
        for (int i = 0; i < 16; i++) { w.Queue.Enqueue((0.002, 0f)); w.Queue.Enqueue((0.0, 0f)); }
        w.Queue.Enqueue((0.0, 0.5f));
        SchedulerScript s = RunWindows(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.Punishments);
            Assert.Equal(ScriptRunState.Sleeping, s.State);
        }
    }

    [RequiresNativeFact]
    public void AStallInOneWindowDoesNotMakeABusyScriptsOverrunLookLikeTheSystemsDoing()
    {
        // Every window shows the script's own CPU time; the worker is stalled for 40 ms once, which the CPU time cannot show.
        var w = new Windows { StallAtWindow = 5, StallMs = 40 };
        w.Add(32, 0.001);
        w.Queue.Enqueue((0.001, 0.5f));
        SchedulerScript s = RunWindows(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.Punishments);
            Assert.Equal(0, s.WaivedOverruns);
            Assert.Equal(ScriptRunState.Sleeping, s.State);
        }
    }

    [RequiresNativeFact]
    public void AMostlyIdleAverageIsNotEnoughEvenWithOneBusyWindow()
    {
        var w = new Windows();
        for (int i = 0; i < 4; i++) { w.Queue.Enqueue((0.002, 0f)); w.Add(7, 0.0); } // 8 ms of CPU in 32 windows of 1 ms
        w.Queue.Enqueue((0.0, 0.5f));
        SchedulerScript s = RunWindows(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(0, s.Punishments);
            Assert.Equal(1, s.WaivedOverruns);
        }
    }

    [RequiresNativeFact]
    public void WithTooFewWindowsToAverageAnOverrunIsPunishedAsBefore()
    {
        var w = new Windows();
        w.Add(5, 0.0);
        w.Queue.Enqueue((0.0, 0.5f));
        SchedulerScript s = RunWindows(w, 6, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.Punishments);
            Assert.Equal(0, s.WaivedOverruns);
        }
    }

    [RequiresNativeFact]
    public void ADelayTheScriptAskedForIsNeverWaived()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Sleep(1.0) ossl.Count()");
        h.Pump();
        Assert.Equal(ScriptRunState.Sleeping, s.State);
        Assert.Equal(0, s.WaivedOverruns);
        h.Advance(1.05);
        Assert.Equal(1, h.CountOf(s));
    }
}
