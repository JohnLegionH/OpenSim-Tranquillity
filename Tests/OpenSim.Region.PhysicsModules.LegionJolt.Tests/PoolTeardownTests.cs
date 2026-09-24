using System.Diagnostics;
using System.Reflection;
using Legion.Physics.Jolt;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// JOLT-7 part C. The last backend out disposes the job pools (Dispose -> s_pools[i].System.Dispose()), and a later
/// first region recreates them - so creating and disposing backends one after another must not accumulate Jolt
/// worker threads (a test host was seen with 857).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PoolTeardownTests
{
    private readonly ITestOutputHelper _out;
    public PoolTeardownTests(ITestOutputHelper output) { _out = output; }

    private const int Margin = 4;

    private static int Threads()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.Threads.Count;
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Thread.Sleep(200);
    }

    /// <summary>What the binding says about the live pool's handle, read by reflection (NativeObject.OwnsHandle).</summary>
    private static string PoolHandleState()
    {
        var pools = (Array?)typeof(JoltPhysicsBackend).GetField("s_pools", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        if (pools == null || pools.Length == 0)
            return "no pools";
        var sys = pools.GetValue(0)!.GetType().GetField("System")!.GetValue(pools.GetValue(0))!;
        var owns = sys.GetType().GetProperty("OwnsHandle", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(sys);
        return $"{sys.GetType().Name}.OwnsHandle={owns}";
    }

    // JoltPhysicsSharp 2.19.1: JobSystem never sets NativeObject.OwnsHandle, so JobSystemThreadPool.Dispose skips
    // JPH_JobSystem_Destroy and the workers are never joined. Measured 2026-09-23 (JOLT-7): OwnsHandle=False on every
    // pool; threads 20 -> 39 -> 58 -> 77 -> 97 -> 116 across five cycles (19 workers each), still 116 after GC.
    // JOLT-7e: the backend destroys the native job system itself at teardown.
    [Fact]
    public void Disposing_the_last_backend_joins_the_pool_threads()
    {
        Collect();
        var start = Threads();
        var perPool = 0;
        for (var i = 0; i < 5; i++)
        {
            var t = new JoltTestBackend();
            t.Ground();
            t.Step();
            var s = t.B.GetCapacityStats();
            perPool = s.JobThreadsPerPool * s.JobPools;
            _out.WriteLine($"backend {i}: {PoolHandleState()} workers={perPool} threads now={Threads()}");
            t.Dispose();
        }
        Collect();
        var end = Threads();
        _out.WriteLine($"threads: start={start} end={end} (5 backends x {perPool} workers; margin {Margin})");
        Assert.InRange(end, start - Margin, start + Margin);
    }
}
