/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using SLua.ScriptEngine.Bindings;
using Xunit;
using Xunit.Abstractions;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Shared images through the real library. The library's counters are process-wide, so these run one at a time with
/// the other tests that load it.
/// </summary>
[Collection("native-library")]
public class SharedImageTests
{
    private readonly ITestOutputHelper m_output;

    public SharedImageTests(ITestOutputHelper output) => m_output = output;

    private static Vm NewVm()
    {
        RealLibrary.Load();
        return new Vm(Watchdog.Resident);
    }

    private static byte[] Asset(string slua)
    {
        RealLibrary.Load();
        CompileResult r = new ScriptCompiler().Compile("--!slua\n" + slua);
        Assert.True(r.Success, string.Join("; ", r.EditorErrors()));
        return r.Asset;
    }

    /// <summary>A script of some size: many small functions, so the image is not trivial.</summary>
    private static string BigScript()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 300; i++)
            sb.Append($"function f{i}(a, b) local t = {{a, b, {i}}} for k, v in ipairs(t) do a = a + v * {i + 1} end return a end\n");
        sb.Append("local total = 0\nfor i = 1, 10 do total = total + f1(i, i) end\n");
        return sb.ToString();
    }

    [RequiresNativeFact]
    public void ManyScriptsFromOneAssetShareOneImage()
    {
        byte[] asset = Asset(BigScript());
        using Vm vm = NewVm();
        var images = new SharedImages(vm);
        AllocStats before = Host.GetAllocStats();

        var leases = new List<ImageLease>();
        var scripts = new List<Script>();
        for (int i = 0; i < 50; i++)
        {
            ImageLease lease = images.Acquire(asset);
            leases.Add(lease);
            scripts.Add(lease.NewScript(tag: i));
        }
        AllocStats during = Host.GetAllocStats();

        Assert.Equal(1, images.ImageCount);
        Assert.Equal(1UL, during.LiveImages - before.LiveImages);
        Assert.Equal(50UL, during.LiveScripts - before.LiveScripts);

        scripts.ForEach(s => s.Dispose());
        leases.ForEach(l => l.Dispose());
        Assert.Equal(0, images.ImageCount);
        AllocStats after = Host.GetAllocStats();
        Assert.Equal(before.LiveImages, after.LiveImages);
        Assert.Equal(before.LiveScripts, after.LiveScripts);
    }

    [RequiresNativeFact]
    public void DifferentAssetsGetTheirOwnImages()
    {
        using Vm vm = NewVm();
        var images = new SharedImages(vm);
        using ImageLease a = images.Acquire(Asset("ll.Say(0, 'one')"));
        using ImageLease b = images.Acquire(Asset("ll.Say(0, 'two')"));
        using ImageLease again = images.Acquire(Asset("ll.Say(0, 'one')"));
        Assert.Equal(2, images.ImageCount);
    }

    [RequiresNativeFact]
    public void TheImageStaysWhileAnyScriptHoldsIt_AndIsMadeAgainAfterTheLastIsGone()
    {
        byte[] asset = Asset("local a = 1");
        using Vm vm = NewVm();
        var images = new SharedImages(vm);
        ImageLease first = images.Acquire(asset);
        ImageLease second = images.Acquire(asset);
        first.Dispose();
        Assert.Equal(1, images.ImageCount);
        using (Script s = second.NewScript()) { }
        second.Dispose();
        Assert.Equal(0, images.ImageCount);

        using ImageLease third = images.Acquire(asset);
        Assert.Equal(1, images.ImageCount);
        using Script fresh = third.NewScript();
    }

    [RequiresNativeFact]
    public void AScriptMadeFromASharedImageIsReadyAndRunsToTheEnd()
    {
        byte[] asset = Asset(BigScript());
        using Vm vm = NewVm();
        var images = new SharedImages(vm);
        using ImageLease lease = images.Acquire(asset);
        using Script script = lease.NewScript();
        script.Start();
        StepResult last = script.RunToIdle(0.05);
        Assert.NotEqual(StepStatus.Fault, last.Status);
    }

    [RequiresNativeFact]
    public void ADisposedLeaseCannotMakeScripts()
    {
        using Vm vm = NewVm();
        var images = new SharedImages(vm);
        ImageLease lease = images.Acquire(Asset("local a = 1"));
        lease.Dispose();
        lease.Dispose(); // twice is harmless
        Assert.Throws<ObjectDisposedException>(() => lease.NewScript());
        Assert.Equal(0, images.ImageCount);
    }

    /// <summary>
    /// Measures the memory a script costs when it shares an image and when it has its own copy of the code. The
    /// numbers go to the test output; the assertion is that sharing is cheaper per script.
    /// </summary>
    [RequiresNativeFact]
    public void ASharedScriptCostsLessMemoryThanOneWithItsOwnImage()
    {
        const int N = 400;
        byte[] asset = Asset(BigScript());

        AllocStats baseline = Host.GetAllocStats();
        using Vm vm = NewVm();
        var images = new SharedImages(vm);

        var shared = new List<Script>();
        var leases = new List<ImageLease>();
        for (int i = 0; i < N; i++)
        {
            ImageLease lease = images.Acquire(asset);
            leases.Add(lease);
            shared.Add(lease.NewScript(tag: i));
        }
        AllocStats afterShared = Host.GetAllocStats();
        shared.ForEach(s => s.Dispose());
        leases.ForEach(l => l.Dispose());

        var own = new List<Script>();
        AllocStats beforeOwn = Host.GetAllocStats();
        for (int i = 0; i < N; i++)
            own.Add(vm.NewScript(asset, tag: i));
        AllocStats afterOwn = Host.GetAllocStats();
        own.ForEach(s => s.Dispose());

        long sharedBytes = (long)afterShared.HeapInUseBytes - (long)baseline.HeapInUseBytes;
        long ownBytes = (long)afterOwn.HeapInUseBytes - (long)beforeOwn.HeapInUseBytes;
        m_output.WriteLine($"asset {asset.Length} bytes; {N} scripts; images {afterShared.LiveImages - baseline.LiveImages} shared vs {afterOwn.LiveImages - beforeOwn.LiveImages} own");
        m_output.WriteLine($"MEMORY shared: {sharedBytes} bytes total, {sharedBytes / N} per script");
        m_output.WriteLine($"MEMORY own image: {ownBytes} bytes total, {ownBytes / N} per script");

        Assert.Equal(1UL, afterShared.LiveImages - baseline.LiveImages);
        Assert.True(sharedBytes < ownBytes, $"shared {sharedBytes} bytes, own {ownBytes} bytes");
    }
}
