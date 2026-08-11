// SLICE-4 INSTRUMENTATION — wired in as the module was ported, for the post-slice-4 scaling /
// thread-pool MEASUREMENT GATE (see tranq-migration-plan.md, Jolt track). Kept in its own file so it
// is additive and does not touch the ported physics logic beyond three one-line hooks
// (AddRegion init, StepOnce, and a `jolt metrics` console subcommand).
//
// Provides the gate metrics that are obtainable from managed code:
//   - per-region RSS delta at backend init (8 MB TempAllocator + MaxBodies preallocation + job pool)
//   - per-region step time (EMA + last) and active-body count
//   - whole-process step-time sum and total process THREAD COUNT (design item #1: per-region
//     JobSystemThreadPool of ProcessorCount-1 -> N*(cores-1) threads; this is how we watch it)
//   - a throttled process-wide summary emitted to the LOG (~30 s) so the gate is captured without
//     needing console interaction.
//
// NOT YET obtainable here: TempAllocator high-water / malloc-fallback rate. The native
// TempAllocatorImplWithMallocFallback tracks that internally but the joltc C API does not export it.
// Add a counter to the native the next time it is patched (same bucket as the s_PhysicsSystems lock
// TODO in native/joltc/README.md), then surface it here.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal static class JoltMetrics
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[LEGION JOLT METRICS]";

        private sealed class RegionStat
        {
            public long Steps;
            public double LastMs;
            public double EmaMs;           // exponential moving average of per-step physics ms
            public int ActiveBodies;
            public double InitRssDeltaMB;  // process RSS growth across this region's backend Initialize
        }

        private static readonly ConcurrentDictionary<string, RegionStat> s_regions =
            new ConcurrentDictionary<string, RegionStat>();
        private static long s_lastLogTick;   // throttle the periodic process summary (~30 s)

        public static void RecordRegionInit(string region, long rssDeltaBytes)
        {
            RegionStat st = s_regions.GetOrAdd(region, _ => new RegionStat());
            st.InitRssDeltaMB = rssDeltaBytes / (1024.0 * 1024.0);
            m_log.Info($"{LogHeader} region '{region}': backend-init RSS delta = {st.InitRssDeltaMB:0.0} MB; process threads now = {ThreadCount()}.");
        }

        public static void RecordStep(string region, float physicsMs, int activeBodies)
        {
            RegionStat st = s_regions.GetOrAdd(region, _ => new RegionStat());
            st.Steps++;
            st.LastMs = physicsMs;
            st.EmaMs = st.EmaMs <= 0 ? physicsMs : st.EmaMs * 0.98 + physicsMs * 0.02;
            st.ActiveBodies = activeBodies;

            // Throttled process-wide summary to the log so the gate metrics are captured without
            // console interaction. Single-writer via CompareExchange so only one region logs per window.
            // TickCount64 (monotonic, non-wrapping) — plain Environment.TickCount goes negative past
            // ~24.9 days uptime, which silently disables the throttle.
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref s_lastLogTick);
            if (now - last > 30000 &&
                Interlocked.CompareExchange(ref s_lastLogTick, now, last) == last)
            {
                m_log.Info(Report());
            }
        }

        public static int ThreadCount()
        {
            using Process p = Process.GetCurrentProcess();
            return p.Threads.Count;
        }

        public static string Report()
        {
            using Process p = Process.GetCurrentProcess();
            double rssMB = p.WorkingSet64 / (1024.0 * 1024.0);
            int threads = p.Threads.Count;
            double sumMs = 0;

            StringBuilder sb = new StringBuilder();
            sb.Append($"{LogHeader} process RSS={rssMB:0} MB, threads={threads}, regions={s_regions.Count}");
            foreach (var kv in s_regions)
            {
                sumMs += kv.Value.EmaMs;
                sb.Append($" | {kv.Key}: step~{kv.Value.EmaMs:0.00}ms (last {kv.Value.LastMs:0.00}), active={kv.Value.ActiveBodies}, initRSSd={kv.Value.InitRssDeltaMB:0.0}MB, steps={kv.Value.Steps}");
            }
            sb.Append($" | whole-process step-sum~{sumMs:0.00}ms");
            sb.Append(" | TempAllocator high-water/malloc-fallback: N/A (needs native counter)");
            return sb.ToString();
        }
    }
}
