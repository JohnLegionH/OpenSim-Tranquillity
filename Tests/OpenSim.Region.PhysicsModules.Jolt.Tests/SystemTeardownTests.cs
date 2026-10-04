/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using JoltPhysicsSharp;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Backend teardown destroys the region's native physics system (JoltPhysicsSharp 2.19.1's PhysicsSystem.Dispose
/// does not), and creating and destroying systems from several threads is safe against joltc's unlocked global map
/// of systems. Serial collection: these tests measure the process's private memory and read the process-wide
/// Foundation reference count, and other tests' backends would disturb both.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SystemTeardownTests
{
    private readonly ITestOutputHelper _out;
    public SystemTeardownTests(ITestOutputHelper output) { _out = output; }

    private const long MB = 1024 * 1024;

    // Every leaked system holds at least its 8 MB temp allocator, so 30 leaked cycles hold 240 MB or more.
    // A destroyed system leaves nothing behind; the bound leaves room for heap growth and fragmentation.
    private const int Cycles = 30;
    private const long GrowthBound = 32 * MB;

    private static long PrivateBytes()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    private static int FoundationRefCount()
        => (int)typeof(JoltPhysicsBackend).GetField("s_foundationRefCount", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static PhysicsSystem? NativeSystem(JoltPhysicsBackend b)
        => (PhysicsSystem?)typeof(JoltPhysicsBackend).GetField("_system", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b);

    private static void Cycle()
    {
        var t = new JoltTestBackend();
        t.Ground();
        t.Step();
        t.Dispose();
    }

    [Fact]
    public void The_binding_has_the_fields_teardown_reads()
    {
        // A binding upgrade that renames them would leave the listeners and the GCHandle behind; fail here instead.
        Assert.True(JoltPhysicsBackend.PhysicsSystemFieldsFound);
    }

    [Fact]
    public void Repeated_create_and_teardown_does_not_grow_private_memory()
    {
        // A region that stays up keeps Foundation and the job pools alive, as on a simulator with other regions,
        // so the cycles measure the per-region system alone.
        using var anchor = new JoltTestBackend();
        anchor.Ground();
        anchor.Step();
        for (var i = 0; i < 3; i++)
            Cycle();   // warm the heaps

        long before = PrivateBytes();
        for (var i = 0; i < Cycles; i++)
            Cycle();
        long after = PrivateBytes();

        long growth = after - before;
        _out.WriteLine($"private bytes: before={before / MB} MB after={after / MB} MB growth={growth / MB} MB "
            + $"over {Cycles} cycles ({growth / Cycles / 1024} KB per cycle); bound {GrowthBound / MB} MB");
        Assert.True(growth < GrowthBound,
            $"private memory grew {growth / MB} MB over {Cycles} create/teardown cycles (bound {GrowthBound / MB} MB)");
    }

    [Fact]
    public void Backends_created_and_torn_down_on_several_threads_while_another_steps()
    {
        const int Workers = 4;
        const int CyclesPerWorker = 8;

        using var stepper = new JoltTestBackend();
        stepper.Ground();
        var box = stepper.B.CreateBoxShape(new System.Numerics.Vector3(0.5f));
        for (var i = 0; i < 20; i++)
            stepper.Dynamic(box, new System.Numerics.Vector3(128f, 128f, 2f + i * 1.1f));

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = 0;
        long steps = 0;
        var stepThread = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    stepper.Step();
                    Interlocked.Increment(ref steps);
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
        }) { IsBackground = true, Name = "stepper" };

        using var go = new ManualResetEventSlim(false);
        var workers = new List<Thread>();
        for (var w = 0; w < Workers; w++)
        {
            workers.Add(new Thread(() =>
            {
                try
                {
                    go.Wait();
                    for (var i = 0; i < CyclesPerWorker; i++)
                        Cycle();
                }
                catch (Exception e) { errors.Enqueue(e); }
            }) { IsBackground = true, Name = $"worker {w}" });
        }

        stepThread.Start();
        foreach (var th in workers)
            th.Start();
        go.Set();

        var limit = TimeSpan.FromSeconds(120);
        var clock = Stopwatch.StartNew();
        foreach (var th in workers)
            Assert.True(th.Join(limit - clock.Elapsed > TimeSpan.Zero ? limit - clock.Elapsed : TimeSpan.Zero),
                $"{th.Name} did not finish within {limit.TotalSeconds} s");
        Volatile.Write(ref stop, 1);
        Assert.True(stepThread.Join(TimeSpan.FromSeconds(30)), "the stepping thread did not stop");

        _out.WriteLine($"{Workers} workers x {CyclesPerWorker} cycles in {clock.ElapsedMilliseconds} ms; stepper stepped {steps} times");
        Assert.Empty(errors);
        Assert.True(steps > 0);
        stepper.Step();   // the region that kept stepping is still usable
    }

    [Fact]
    public void Teardown_twice_destroys_once_and_gives_back_one_reference()
    {
        using var anchor = new JoltTestBackend();
        int baseline = FoundationRefCount();

        var t = new JoltTestBackend();
        t.Ground();
        t.Step();
        PhysicsSystem system = NativeSystem(t.B)!;
        Assert.Equal(baseline + 1, FoundationRefCount());

        t.Dispose();
        Assert.True(system.IsDisposed);
        Assert.Equal(IntPtr.Zero, system.Handle);
        Assert.Null(NativeSystem(t.B));
        Assert.Equal(baseline, FoundationRefCount());

        t.Dispose();
        Assert.Equal(baseline, FoundationRefCount());

        anchor.Step();   // the region that stays up still steps
    }

    [Fact]
    public void Teardown_after_Initialize_failed_part_way_destroys_the_system()
    {
        using var anchor = new JoltTestBackend();
        int baseline = FoundationRefCount();

        var b = new JoltPhysicsBackend();
        PhysicsSystem? system = null;
        b.AfterSystemCreatedForTest = () =>
        {
            system = NativeSystem(b);
            throw new InvalidOperationException("injected start failure");
        };
        var e = Assert.Throws<InvalidOperationException>(() => b.Initialize(JoltTestBackend.Settings()));
        Assert.Equal("injected start failure", e.Message);
        Assert.NotNull(system);
        Assert.Equal(baseline + 1, FoundationRefCount());

        b.Dispose();
        Assert.True(system!.IsDisposed);
        Assert.Equal(IntPtr.Zero, system.Handle);
        Assert.Equal(baseline, FoundationRefCount());

        b.Dispose();
        Assert.Equal(baseline, FoundationRefCount());
        anchor.Step();
    }

    [Fact]
    public void Teardown_of_a_backend_never_initialized_leaves_other_regions_alone()
    {
        using var anchor = new JoltTestBackend();
        anchor.Ground();
        int baseline = FoundationRefCount();

        var b = new JoltPhysicsBackend();
        b.Dispose();
        b.Dispose();

        Assert.Equal(baseline, FoundationRefCount());
        anchor.Step();
    }
}
