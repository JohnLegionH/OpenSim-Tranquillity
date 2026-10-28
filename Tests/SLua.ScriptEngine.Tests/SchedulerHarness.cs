/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Globalization;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : ISchedulerClock
{
    private double m_now;
    public double Now { get => Volatile.Read(ref m_now); set => Volatile.Write(ref m_now, value); }
}

/// <summary>
/// A scheduler with no region: the real native library, test host functions that count, sleep, pause, set a timer
/// and record, and (unless threads are asked for) a manual clock and a pump the test drives.
/// Host functions are in two tables so both languages can reach them: <c>ossl.*</c> for SLua, <c>ll.*</c> for LSL.
/// </summary>
internal sealed class SchedulerHarness : IDisposable
{
    public readonly ManualClock Clock = new();
    public readonly RecordingLog Log = new();
    public readonly ConcurrentDictionary<long, long> Counts = new();
    public readonly SLuaScheduler Scheduler;
    public int VmsMade;
    public int FaultsSeen;

    /// <summary>Scripts reported as faulted or killed, in order.</summary>
    public readonly ConcurrentQueue<SchedulerScript> FaultedScripts = new();

    /// <summary>Scripts a fault had stopped that were put back stopped from their saved state (not a new fault).</summary>
    public readonly ConcurrentQueue<SchedulerScript> FaultRestoredScripts = new();

    /// <summary>Errors scripts reported through ReportError, as "scriptId:text".</summary>
    public readonly ConcurrentQueue<string> ReportedErrors = new();

    /// <summary>Workers that stopped for good: index, failures, last message.</summary>
    public readonly ConcurrentQueue<(int Index, int Crashes, string Message)> StoppedWorkers = new();

    private readonly List<string> m_marks = new();

    public SchedulerHarness(int workers = 1, double quantaMs = 2.0, bool threads = false, int maxQueue = 64,
        double passBudgetMs = 20.0, double minTimer = 0.1, int memoryLimit = 0, ISchedulerClock clock = null,
        Action<SchedulerOptions> configure = null)
    {
        RuntimeLoadResult r = NativeRuntime.Load(new NativeRuntimeOptions());
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        var options = new SchedulerOptions
        {
            WorkerThreads = workers,
            QuantaMs = quantaMs,
            PassBudgetMs = passBudgetMs,
            MinTimerInterval = minTimer,
            MaxEventQueue = maxQueue,
            MemoryLimitBytes = memoryLimit,
            RunThreads = threads,
            Clock = clock ?? Clock,
            Log = Log,
            VmFactory = MakeVm,
            ScriptFaulted = f => { FaultedScripts.Enqueue(f); Interlocked.Increment(ref FaultsSeen); },
            ScriptFaultRestored = f => FaultRestoredScripts.Enqueue(f),
            ScriptError = (script, text) => ReportedErrors.Enqueue(script.Id + ":" + text),
            WorkerStopped = (index, crashes, message) => StoppedWorkers.Enqueue((index, crashes, message)),
        };
        configure?.Invoke(options);
        Scheduler = new SLuaScheduler(options);
    }

    private Vm MakeVm()
    {
        Interlocked.Increment(ref VmsMade);
        var vm = new Vm(Watchdog.Default);
        foreach (string table in new[] { "ossl", "ll" })
        {
            // Counter: one tick per call, per script.
            vm.Register(table, "Count", (tag, _) => { Counts.AddOrUpdate(tag, 1, (_, n) => n + 1); return null; });
        }
        vm.Register("ossl", "Mark", (tag, a) => { Mark(tag, a[0].S, a.Length > 1 ? Format(a[1]) : ""); return null; });
        vm.Register("ll", "OwnerSay", (tag, a) => { Mark(tag, "say", a[0].S); return null; });
        vm.Register("ossl", "Err", (tag, a) => { SchedulerScript.Current.ReportError(a[0].S); return null; });
        vm.Register("ossl", "Sleep", (tag, a) => { SchedulerScript.Current.AddSleep(Num(a[0])); return null; });
        vm.Register("ll", "Sleep", (tag, a) => { SchedulerScript.Current.AddSleep(Num(a[0])); return null; });
        // A delayed call that returns values (a lookup with a delay): the delay is asked for and the script is not stopped by it.
        vm.Register("ossl", "SleepRet", (tag, a) => { SchedulerScript.Current.AddSleep(Num(a[0])); return new[] { Value.Bool(true), Value.Int(7) }; });
        vm.Register("ll", "SetTimerEvent", (tag, a) => { SchedulerScript.Current.SetTimer(Num(a[0])); return null; });
        // Holds the thread for the given milliseconds inside a host call: an overrun the runtime cannot interrupt.
        vm.Register("ossl", "Burn", (tag, a) =>
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(a[0].N / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
            return null;
        });
        vm.RegisterPausing("ossl", "Pause",
            (tag, _) => { SchedulerScript.Current.BeginWait(); return null; },
            (tag, _) => SchedulerScript.Current.TakeResult());
        return vm;
    }

    public static double Num(Value v) => v.Type == Bindings.ValueType.Number ? v.N : v.I;

    private static string Format(Value v) => v.Type is Bindings.ValueType.Number or Bindings.ValueType.Int
        ? Num(v).ToString("G", CultureInfo.InvariantCulture)
        : v.S;

    private void Mark(long tag, string kind, string value)
    {
        lock (m_marks) m_marks.Add($"{tag}:{kind}:{value}");
    }

    /// <summary>Everything recorded through ossl.Mark and ll.OwnerSay, as "scriptId:kind:value".</summary>
    public string[] Marks { get { lock (m_marks) return m_marks.ToArray(); } }

    /// <summary>Marks of one script, without the id.</summary>
    public string[] MarksOf(SchedulerScript s) =>
        Marks.Where(m => m.StartsWith(s.Id + ":")).Select(m => m.Substring(m.IndexOf(':') + 1)).ToArray();

    public long CountOf(SchedulerScript s) => Counts.TryGetValue(s.Id, out long n) ? n : 0;

    public SchedulerScript Add(string source, bool lsl = false) =>
        Scheduler.Add(Host.Compile(source, lsl), lsl ? ScriptLanguage.Lsl : ScriptLanguage.Slua);

    /// <summary>Adds a script, lets its worker make it and starts it.</summary>
    public SchedulerScript AddAndStart(string source, bool lsl = false)
    {
        SchedulerScript s = Add(source, lsl);
        // Only until the script is made: scripts that never go idle would keep a full pump going.
        for (int i = 0; i < 1000 && s.State == ScriptRunState.Loading; i++)
            Scheduler.DoWork(Clock.Now);
        Scheduler.Start(s);
        return s;
    }

    /// <summary>Pumps until nothing is runnable, at most <paramref name="maxPasses"/> passes.</summary>
    public int Pump(int maxPasses = 1000)
    {
        int n = 0;
        while (n < maxPasses)
        {
            n++;
            if (!Scheduler.DoWork(Clock.Now)) break;
        }
        return n;
    }

    /// <summary>Exactly this many passes, for scripts that never go idle.</summary>
    public void Passes(int n)
    {
        for (int i = 0; i < n; i++) Scheduler.DoWork(Clock.Now);
    }

    public void Advance(double seconds)
    {
        Clock.Now += seconds;
        Pump();
    }

    /// <summary>The longest a poll waits when the caller gives no cap; SLUA_TEST_WAIT_CAP_MS shortens it for a run where failures are expected.</summary>
    private static readonly int DefaultWaitMs = int.TryParse(Environment.GetEnvironmentVariable("SLUA_TEST_WAIT_CAP_MS"), out int cap) ? cap : 30000;

    /// <summary>Polls until the condition holds or the cap passes; for threaded runs.</summary>
    public static bool WaitFor(Func<bool> condition, int timeoutMs = -1)
    {
        if (timeoutMs < 0) timeoutMs = DefaultWaitMs;
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    public void Dispose() => Scheduler.Dispose();
}
