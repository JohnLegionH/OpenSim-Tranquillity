/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Luau.ScriptEngine.Bindings;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// Measures throughput and fairness for worker counts and run windows on a mix of busy and idle scripts, to choose
/// the defaults. It runs only when SCHEDULER_MEASURE_OUT names a file to write tab-separated results to, because it
/// takes minutes and its numbers depend on the machine; SCHEDULER_MEASURE_SECONDS sets the length of each run
/// (default 8) and SCHEDULER_MEASURE_REPEATS how many times each setting is run (default 2).
/// </summary>
[Collection("native-library")]
public class SchedulerMeasurementTests
{
    // One unit of work every thousand loop turns: the unit counts are the throughput.
    private const string Busy = "local n = 0 while true do n = n + 1 if n % 1000 == 0 then ossl.Count() end end";
    private const string Receiver = "LLEvents:on(\"dataserver\", function(q, d) ossl.Recv(d) end)";
    private const string Ticker = "default { state_entry() { llSetTimerEvent(0.2); } timer() { llOwnerSay(\"t\"); } }";

    public sealed class MeasureFactAttribute : FactAttribute
    {
        public MeasureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCHEDULER_MEASURE_OUT")))
                Skip = "SCHEDULER_MEASURE_OUT is not set; the measurement runs only on request";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_SHIM_PATH")))
                Skip = "SLUA_SHIM_PATH is not set";
        }
    }

    private sealed record Row(int Workers, double QuantaMs, int BusyScripts, int IdleScripts, int Tickers, double Seconds,
        double UnitsPerSecond, double Jain, double MinOverMax, double LatencyP50, double LatencyP95, double LatencyP99, double LatencyMax,
        double TickLateP50, double TickLateP95, double TickLateMax, double CpuCores, long Passes, long Cutoffs, long BusyPunishments);

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

    private static Row Run(int workers, double quantaMs, int busy, int idle, int tickers, double seconds)
    {
        RuntimeLoadResult load = NativeRuntime.Load(new NativeRuntimeOptions());
        if (!load.Ok) throw new InvalidOperationException(load.Message);

        var counts = new ConcurrentDictionary<long, long>();
        var latenciesMs = new ConcurrentQueue<double>();
        var ticks = new ConcurrentDictionary<long, List<long>>();
        Vm Make()
        {
            var vm = new Vm(Watchdog.Default);
            vm.Register("ossl", "Count", (tag, _) => { counts.AddOrUpdate(tag, 1, (_, n) => n + 1); return null; });
            vm.Register("ossl", "Recv", (tag, a) =>
            {
                long sent = long.Parse(a[0].S, CultureInfo.InvariantCulture);
                latenciesMs.Enqueue((Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency);
                return null;
            });
            vm.Register("ll", "SetTimerEvent", (tag, a) => { SchedulerScript.Current.SetTimer(a[0].N); return null; });
            vm.Register("ll", "OwnerSay", (tag, _) =>
            {
                List<long> list = ticks.GetOrAdd(tag, _ => new List<long>());
                lock (list) list.Add(Stopwatch.GetTimestamp());
                return null;
            });
            return vm;
        }

        var log = new RecordingLog();
        using var scheduler = new LuauScheduler(new SchedulerOptions
        {
            WorkerThreads = workers,
            QuantaMs = quantaMs,
            VmFactory = Make,
            Log = log,
            Clock = new MonotonicClock(),
        });

        var busyScripts = new List<SchedulerScript>();
        var idleScripts = new List<SchedulerScript>();
        var tickScripts = new List<SchedulerScript>();
        byte[] busyAsset = Host.Compile(Busy, false), idleAsset = Host.Compile(Receiver, false), tickAsset = Host.Compile(Ticker, true);
        // Interleave kinds so every worker gets a mix, as least-loaded placement does.
        for (int i = 0; i < Math.Max(busy, Math.Max(idle, tickers)); i++)
        {
            if (i < busy) busyScripts.Add(scheduler.Add(busyAsset, ScriptLanguage.Slua));
            if (i < idle) idleScripts.Add(scheduler.Add(idleAsset, ScriptLanguage.Slua));
            if (i < tickers) tickScripts.Add(scheduler.Add(tickAsset, ScriptLanguage.Lsl));
        }
        var all = busyScripts.Concat(idleScripts).Concat(tickScripts).ToList();
        Assert.True(SchedulerHarness.WaitFor(() => all.All(s => s.State != ScriptRunState.Loading)), "scripts did not load");
        foreach (SchedulerScript s in idleScripts) scheduler.Start(s);
        foreach (SchedulerScript s in tickScripts) scheduler.Start(s);
        foreach (SchedulerScript s in busyScripts) scheduler.Start(s);

        // Events arrive at idle scripts at about 100 a second while the others are busy.
        using var stop = new CancellationTokenSource();
        var rng = new Random(12345);
        Thread poster = null;
        if (idleScripts.Count > 0)
        {
            poster = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    SchedulerScript target = idleScripts[rng.Next(idleScripts.Count)];
                    scheduler.Post(target, "dataserver", Value.Key("k"), Value.Str(Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture)));
                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            poster.Start();
        }

        Thread.Sleep(1500); // warm-up
        long[] before = busyScripts.Select(s => counts.TryGetValue(s.Id, out long n) ? n : 0).ToArray();
        long[] punishBefore = busyScripts.Select(s => s.Punishments).ToArray();
        WorkerStats[] statsBefore = scheduler.GetStats().Workers;
        while (latenciesMs.TryDequeue(out _)) { }
        foreach (var kv in ticks) lock (kv.Value) kv.Value.Clear();
        Process self = Process.GetCurrentProcess();
        TimeSpan cpu0 = self.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        double elapsed = wall.Elapsed.TotalSeconds;
        self.Refresh();
        double cpuCores = (self.TotalProcessorTime - cpu0).TotalSeconds / elapsed;
        long[] after = busyScripts.Select(s => counts.TryGetValue(s.Id, out long n) ? n : 0).ToArray();
        WorkerStats[] statsAfter = scheduler.GetStats().Workers;
        long punishments = busyScripts.Select((s, i) => s.Punishments - punishBefore[i]).Sum();
        stop.Cancel();
        poster?.Join();

        double[] units = after.Zip(before, (a, b) => (double)(a - b)).ToArray();
        double jain = units.Length == 0 || units.Sum() == 0 ? double.NaN : units.Sum() * units.Sum() / (units.Length * units.Sum(u => u * u));
        double minOverMax = units.Length == 0 || units.Max() == 0 ? double.NaN : units.Min() / units.Max();

        var lat = latenciesMs.ToList();
        lat.Sort();
        var late = new List<double>();
        foreach (var kv in ticks)
        {
            List<long> stamps;
            lock (kv.Value) stamps = new List<long>(kv.Value);
            for (int i = 1; i < stamps.Count; i++)
                late.Add((stamps[i] - stamps[i - 1]) * 1000.0 / Stopwatch.Frequency - 200.0);
        }
        late.Sort();

        return new Row(workers, quantaMs, busy, idle, tickers, elapsed,
            units.Sum() / elapsed, jain, minOverMax,
            Percentile(lat, 0.5), Percentile(lat, 0.95), Percentile(lat, 0.99), lat.Count == 0 ? double.NaN : lat[^1],
            Percentile(late, 0.5), Percentile(late, 0.95), late.Count == 0 ? double.NaN : late[^1],
            cpuCores,
            statsAfter.Sum(w => w.Passes) - statsBefore.Sum(w => w.Passes),
            statsAfter.Sum(w => w.BudgetCutoffs) - statsBefore.Sum(w => w.BudgetCutoffs),
            punishments);
    }

    private static string Line(string scenario, int repeat, Row r)
    {
        string F(double d) => double.IsNaN(d) ? "" : d.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Join("\t", scenario, repeat, r.Workers, F(r.QuantaMs), r.BusyScripts, r.IdleScripts, r.Tickers, F(r.Seconds),
            F(r.UnitsPerSecond), F(r.Jain), F(r.MinOverMax), F(r.LatencyP50), F(r.LatencyP95), F(r.LatencyP99), F(r.LatencyMax),
            F(r.TickLateP50), F(r.TickLateP95), F(r.TickLateMax), F(r.CpuCores), r.Passes, r.Cutoffs, r.BusyPunishments);
    }

    [MeasureFact]
    public void MeasureWorkersAndRunWindows()
    {
        string path = Environment.GetEnvironmentVariable("SCHEDULER_MEASURE_OUT");
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("SCHEDULER_MEASURE_SECONDS"), CultureInfo.InvariantCulture, out double s) ? s : 8;
        int repeats = int.TryParse(Environment.GetEnvironmentVariable("SCHEDULER_MEASURE_REPEATS"), out int n) ? n : 2;
        var lines = new List<string>
        {
            "# cores=" + Environment.ProcessorCount + " os=" + Environment.OSVersion + " runtime=" + Environment.Version,
            string.Join("\t", "scenario", "repeat", "workers", "quantaMs", "busy", "idle", "tickers", "seconds", "units_per_s", "jain", "min_over_max",
                "event_p50_ms", "event_p95_ms", "event_p99_ms", "event_max_ms", "tick_late_p50_ms", "tick_late_p95_ms", "tick_late_max_ms",
                "cpu_cores", "passes", "budget_cutoffs", "busy_punishments"),
        };
        // Mixed load: 16 busy scripts, 200 waiting for events, 50 on a 0.2 s timer.
        for (int repeat = 1; repeat <= repeats; repeat++)
            foreach (int workers in new[] { 1, 2, 4 })
                foreach (double quanta in new[] { 1.0, 2.0, 5.0 })
                {
                    lines.Add(Line("mixed", repeat, Run(workers, quanta, 16, 200, 50, seconds)));
                    File.WriteAllLines(path, lines);
                }
        // The cost of idle scripts alone, and of one worker with only busy ones.
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            lines.Add(Line("idle-only", repeat, Run(1, 2.0, 0, 200, 50, seconds)));
            lines.Add(Line("busy-only", repeat, Run(1, 2.0, 16, 0, 0, seconds)));
            File.WriteAllLines(path, lines);
        }
    }
}
