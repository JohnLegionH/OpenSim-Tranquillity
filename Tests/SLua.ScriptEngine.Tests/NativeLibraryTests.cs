/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A fact about the native library the UtopiaSkye.SLua package puts in the build output. The package carries it for
/// win-x64 and linux-x64; on any other platform the fact is reported as skipped with the reason, not passed.
/// </summary>
public sealed class PackagedRuntimeFactAttribute : FactAttribute
{
    public PackagedRuntimeFactAttribute()
    {
        if (NativeRuntime.Rid != "win-x64" && NativeRuntime.Rid != "linux-x64")
            Skip = "the UtopiaSkye.SLua package carries no native library for " + NativeRuntime.Rid;
    }
}

/// <summary>
/// The real library, as the package delivers it, through the engine's loader and bindings. Loading it is process-wide
/// state (the handle the bindings resolve to), so these run one at a time.
/// </summary>
[Collection("native-library")]
public class NativeLibraryTests
{
    /// <summary>The loader as a region uses it, but without SLUA_SHIM_PATH, so the package's copy is the one found.</summary>
    private static NativeRuntimeOptions PackageOnly() => new NativeRuntimeOptions { GetEnvironmentVariable = _ => null };

    [PackagedRuntimeFact]
    public void ThePackagePutsTheLibraryInTheBuildOutput()
    {
        string expected = Path.Combine(AppContext.BaseDirectory, "runtimes", NativeRuntime.Rid, "native", NativeRuntime.LibraryFileName);
        Assert.True(File.Exists(expected), expected);
    }

    [PackagedRuntimeFact]
    public void TheLibraryFromThePackageLoadsAndReportsApiVersionFive()
    {
        RuntimeLoadResult r = NativeRuntime.Load(PackageOnly());
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, r.ApiVersion);
        Assert.Equal(5, Host.ApiVersion());
        Assert.Contains("api=5", Host.Version());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "runtimes", NativeRuntime.Rid, "native", NativeRuntime.LibraryFileName), r.LibraryPath);
    }

    [PackagedRuntimeFact]
    public void LoadingNamesTheEngineToTheLibrary()
    {
        RuntimeLoadResult r = NativeRuntime.Load(PackageOnly());
        Assert.True(r.Ok, r.Message);
        Assert.Equal("SLua", Host.EngineName);
    }

    [PackagedRuntimeFact]
    public void SwitchedOnInARegionTheEngineLoadsTheLibraryAndRegisters()
    {
        var log = new RecordingLog();
        var engine = new SLuaEngine(log, (settings, l) => NativeRuntime.Gate(PackageOnly(), l));
        var source = new IniConfigSource();
        source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
        Scene scene = new SceneHelpers().SetupScene();
        engine.Initialise(source);
        engine.AddRegion(scene);
        engine.RegionLoaded(scene);

        Assert.Same(engine, Assert.Single(scene.RequestModuleInterfaces<IScriptModule>()));
        Assert.Empty(log.Errors);
        Assert.Contains(log.Lines, l => l.StartsWith("INFO Native runtime ") && l.Contains("API version 5"));
    }
}
