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
/// An overrun that is waived (the script's CPU time over its recent run windows says the system kept its worker off the
/// processor) is counted against the host function the step called last, because a script blocked inside a host call
/// without using CPU is waived too and the function is where to look. A step that called none counts as "(none)". The
/// counts are per script and, summed, per scheduler, for the status command.
/// </summary>
[Collection("native-library")]
public class WaiverCountTests
{
    /// <summary>One run window of about a millisecond that uses no CPU, calling the named host functions and reporting this much delay.</summary>
    private sealed class Windows
    {
        public readonly ConcurrentQueue<(string[] Calls, float Sleep)> Queue = new();

        public StepResult? Next(SchedulerScript s)
        {
            if (!Queue.TryDequeue(out var w)) return null;
            foreach (string call in w.Calls) SchedulerScript.Current.NoteHostCall(call);
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
            while (Stopwatch.GetTimestamp() < end) { }
            return new StepResult(StepStatus.Ok, 0, w.Sleep, FaultKind.None, 0.0);
        }

        public void Idle(int count) { for (int i = 0; i < count; i++) Queue.Enqueue((System.Array.Empty<string>(), 0f)); }
        public void Overrun(params string[] calls) => Queue.Enqueue((calls, 0.5f));
    }

    private static SchedulerScript Run(Windows w, int passes, out SchedulerHarness harness)
    {
        harness = new SchedulerHarness(configure: o => o.StepOverride = w.Next);
        SchedulerScript s = harness.AddAndStart("ossl.Count()");
        harness.Passes(passes);
        return s;
    }

    [RequiresNativeFact]
    public void AWaivedOverrunIsCountedAgainstTheHostFunctionTheStepCalled()
    {
        var w = new Windows();
        w.Idle(32);
        w.Overrun("ll.Sleep");
        SchedulerScript s = Run(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.WaivedOverruns);
            Assert.Equal(1, s.WaivedByFunction["ll.Sleep"]);
            Assert.Single(s.WaivedByFunction);
            Assert.Equal(1, h.Scheduler.WaivedOverrunsByFunction()["ll.Sleep"]);
        }
    }

    [RequiresNativeFact]
    public void TheLastFunctionTheStepCalledIsTheOneCounted()
    {
        var w = new Windows();
        w.Idle(32);
        w.Overrun("ll.Say", "ll.Sleep", "ll.Say");
        SchedulerScript s = Run(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.WaivedByFunction["ll.Say"]);
            Assert.Single(s.WaivedByFunction);
        }
    }

    [RequiresNativeFact]
    public void AWaivedOverrunInAStepThatCalledNoFunctionIsCountedAsNone()
    {
        var w = new Windows();
        w.Idle(32);
        w.Overrun();
        SchedulerScript s = Run(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.WaivedOverruns);
            Assert.Equal(1, s.WaivedByFunction["(none)"]);
        }
    }

    [RequiresNativeFact]
    public void AFunctionCalledInAnEarlierStepIsNotCountedForALaterOne()
    {
        var w = new Windows();
        w.Queue.Enqueue((new[] { "ll.Sleep" }, 0f)); // an early window calls it
        w.Idle(31);
        w.Overrun();
        SchedulerScript s = Run(w, 33, out SchedulerHarness h);
        using (h)
        {
            Assert.Equal(1, s.WaivedByFunction["(none)"]);
            Assert.False(s.WaivedByFunction.ContainsKey("ll.Sleep"));
        }
    }

    [RequiresNativeFact]
    public void APunishedOverrunIsNotCountedAsWaived()
    {
        var busy = new ConcurrentQueue<(double Cpu, float Sleep)>();
        for (int i = 0; i < 32; i++) busy.Enqueue((0.001, 0f));
        busy.Enqueue((0.001, 0.5f));
        using var h = new SchedulerHarness(configure: o => o.StepOverride = s =>
        {
            if (!busy.TryDequeue(out var win)) return null;
            SchedulerScript.Current.NoteHostCall("ll.Sleep");
            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
            while (Stopwatch.GetTimestamp() < end) { }
            return new StepResult(StepStatus.Ok, 0, win.Sleep, FaultKind.None, win.Cpu);
        });
        SchedulerScript script = h.AddAndStart("ossl.Count()");
        h.Passes(33);

        Assert.Equal(1, script.Punishments);
        Assert.Equal(0, script.WaivedOverruns);
        Assert.Empty(script.WaivedByFunction);
        Assert.Empty(h.Scheduler.WaivedOverrunsByFunction());
    }

    [RequiresNativeFact]
    public void TheSchedulerSumsTheCountsOfItsScriptsByFunction()
    {
        var windows = new ConcurrentDictionary<long, Windows>();
        using var h = new SchedulerHarness(configure: o => o.StepOverride = s => windows.TryGetValue(s.Id, out Windows w) ? w.Next(s) : null);
        SchedulerScript s1 = h.AddAndStart("ossl.Count()");
        SchedulerScript s2 = h.AddAndStart("ossl.Count()");
        SchedulerScript s3 = h.AddAndStart("ossl.Count()");
        foreach (var (script, call) in new[] { (s1, "ll.Sleep"), (s2, "ll.Sleep"), (s3, "ll.Say") })
        {
            var w = new Windows();
            w.Idle(32);
            w.Overrun(call);
            windows[script.Id] = w;
        }
        h.Passes(33);

        Assert.Equal(1, s1.WaivedByFunction["ll.Sleep"]);
        Assert.Equal(1, s2.WaivedByFunction["ll.Sleep"]);
        Assert.Equal(1, s3.WaivedByFunction["ll.Say"]);
        var total = h.Scheduler.WaivedOverrunsByFunction();
        Assert.Equal(2, total["ll.Sleep"]);
        Assert.Equal(1, total["ll.Say"]);
        Assert.Equal(2, total.Count);
    }

    [RequiresNativeFact]
    public void ARealHostCallIsRecordedUnderItsTableAndName()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Count()");
        h.Pump();
        Assert.Equal("ossl.Count", s.LastHostFunction);

        SchedulerScript p = h.AddAndStart("ossl.Pause()");
        h.Pump();
        Assert.Equal("ossl.Pause", p.LastHostFunction);
    }
}
