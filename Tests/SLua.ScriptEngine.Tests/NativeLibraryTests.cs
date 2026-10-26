/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A fact that runs only where the native library can be found: SLUA_SHIM_PATH names a folder (or file) holding it.
/// Without it the fact is reported as skipped with the reason, not passed.
/// </summary>
public sealed class RequiresNativeFactAttribute : FactAttribute
{
    public RequiresNativeFactAttribute()
    {
        string dir = Environment.GetEnvironmentVariable("SLUA_SHIM_PATH");
        if (string.IsNullOrWhiteSpace(dir))
            Skip = "SLUA_SHIM_PATH is not set; point it at the folder holding the native library";
        else if (!File.Exists(dir) && !File.Exists(Path.Combine(dir, NativeRuntime.LibraryFileName)))
            Skip = "SLUA_SHIM_PATH does not hold " + NativeRuntime.LibraryFileName;
    }
}

/// <summary>
/// The real library through the bindings. Loading it is process-wide state (the handle the bindings resolve to), so
/// these run one at a time, apart from the tests that replace the loader.
/// </summary>
[Collection("native-library")]
public class NativeLibraryTests
{
    private static void LoadRealLibrary()
    {
        RuntimeLoadResult r = NativeRuntime.Load(new NativeRuntimeOptions());
        Assert.True(r.Ok, r.Message);
    }

    [RequiresNativeFact]
    public void TheRealLibraryLoadsFromSluaShimPathAndReportsApiVersionThree()
    {
        RuntimeLoadResult r = NativeRuntime.Load(new NativeRuntimeOptions());
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.ApiVersion);
        Assert.Equal(3, Host.ApiVersion());
        Assert.Contains("api=3", Host.Version());
        Assert.True(File.Exists(r.LibraryPath));
    }

    [RequiresNativeFact]
    public void ASluaScriptRunsThroughTheBindingsAndItsResultIsRead()
    {
        LoadRealLibrary();
        AllocStats before = Host.GetAllocStats();

        var reported = new List<Value>();
        using (var vm = new Vm(Watchdog.Resident))
        {
            vm.Register("ossl", "Report", (tag, args) => { reported.AddRange(args.ToArray()); return null; });
            byte[] asset = Host.Compile("ossl.Report(6 * 7)", lsl: false);
            using Script script = vm.NewScript(asset, tag: 1);
            script.Start();
            StepResult last = script.RunToIdle(0.05);
            Assert.NotEqual(StepStatus.Fault, last.Status);
        }

        Value result = Assert.Single(reported);
        Assert.Equal(42, result.Type == Bindings.ValueType.Number ? (long)result.N : result.I);

        AllocStats after = Host.GetAllocStats();
        Assert.Equal(before.LiveVms, after.LiveVms);
        Assert.Equal(before.LiveScripts, after.LiveScripts);
        Assert.Equal(before.LiveImages, after.LiveImages);
    }
}
