/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Finding the native library (a configured path or SLUA_SHIM_PATH first, then the package's runtimes folder)
/// and checking that its API version is the one this engine is written for. These tests replace the file system,
/// the loader and the version call, so they do not depend on a library being present.
/// </summary>
public class NativeRuntimeTests
{
    private static readonly string File_ = NativeRuntime.LibraryFileName;
    private static string Join(params string[] parts) => Path.Combine(parts);

    private static NativeRuntimeOptions Options(string configured = null, string env = null, string baseDir = "base",
        Func<string, bool> exists = null, Func<string, IntPtr> load = null, Func<int> version = null)
    {
        return new NativeRuntimeOptions
        {
            ConfiguredPath = configured,
            GetEnvironmentVariable = name => name == "SLUA_SHIM_PATH" ? env : null,
            BaseDirectory = baseDir,
            FileExists = exists ?? (_ => true),
            TryLoad = load ?? (_ => (IntPtr)1),
            ApiVersion = version ?? (() => NativeRuntime.RequiredApiVersion),
        };
    }

    [Fact]
    public void TheLibraryFileNameAndRuntimeIdentifierMatchThePackageLayout()
    {
        if (OperatingSystem.IsWindows()) Assert.Equal("slua_shim.dll", NativeRuntime.LibraryFileName);
        else Assert.Equal("libslua_shim.so", NativeRuntime.LibraryFileName);
        Assert.Matches(new Regex("^(win|linux|osx)-(x64|x86|arm64|arm)$"), NativeRuntime.Rid);
    }

    [Fact]
    public void CandidatesAreTriedInTheOrderConfiguredEnvironmentRuntimesFolderBaseFolder()
    {
        var c = NativeRuntime.Candidates(Options(configured: "cfg", env: "envdir", baseDir: "app"));
        Assert.Equal(new[]
        {
            Join("cfg", File_),
            Join("envdir", File_),
            Join("app", "runtimes", NativeRuntime.Rid, "native", File_),
            Join("app", File_),
        }, c);
    }

    [Fact]
    public void AnUnsetOrBlankConfiguredPathAndEnvironmentAreSkipped()
    {
        var c = NativeRuntime.Candidates(Options(configured: " ", env: "", baseDir: "app"));
        Assert.Equal(new[] { Join("app", "runtimes", NativeRuntime.Rid, "native", File_), Join("app", File_) }, c);
    }

    [Fact]
    public void APathNamingTheLibraryFileIsUsedAsItIs()
    {
        string file = Join("opt", "slua", File_);
        var c = NativeRuntime.Candidates(Options(configured: file));
        Assert.Equal(file, c[0]);
        Assert.Equal(1, c.Count(x => x == file));
    }

    [Fact]
    public void LoadingStopsAtTheFirstCandidateThatLoadsAndSkipsFilesThatAreNotThere()
    {
        var attempts = new List<string>();
        string cfg = Join("cfg", File_), env = Join("envdir", File_);
        var r = NativeRuntime.Load(Options(configured: "cfg", env: "envdir",
            exists: p => p != cfg,                        // nothing at the configured path
            load: p => { attempts.Add(p); return (IntPtr)5; }));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(env, r.LibraryPath);
        Assert.Equal(new[] { env }, attempts);
    }

    [Fact]
    public void AFileThatExistsButWillNotLoadFallsThroughToTheNextCandidate()
    {
        var attempts = new List<string>();
        string first = Join("cfg", File_), second = Join("envdir", File_);
        var r = NativeRuntime.Load(Options(configured: "cfg", env: "envdir",
            load: p => { attempts.Add(p); return p == first ? IntPtr.Zero : (IntPtr)5; }));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(second, r.LibraryPath);
        Assert.Equal(new[] { first, second }, attempts);
    }

    [Fact]
    public void NoLibraryAnywhereIsNotOkAndTheMessageSaysWhereItLooked()
    {
        var r = NativeRuntime.Load(Options(configured: "cfg", env: "envdir", exists: _ => false));
        Assert.False(r.Ok);
        Assert.Contains("SLUA_SHIM_PATH", r.Message);
        Assert.Contains("NativeLibraryPath", r.Message);
        Assert.Contains(Join("cfg", File_), r.Message);
    }

    [Fact]
    public void AnApiVersionOtherThanTheRequiredOneIsNotOkAndTheMessageNamesBoth()
    {
        var r = NativeRuntime.Load(Options(version: () => 3));
        Assert.False(r.Ok);
        Assert.Equal(3, r.ApiVersion);
        Assert.Contains("API version 3", r.Message);
        Assert.Contains("requires 5", r.Message);
    }

    [Fact]
    public void TheRequiredApiVersionIsOk()
    {
        var r = NativeRuntime.Load(Options(version: () => 5));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(5, r.ApiVersion);
    }

    [Fact]
    public void AVersionAboveTheRequiredOneIsAlsoRefused()
    {
        Assert.False(NativeRuntime.Load(Options(version: () => 5)).Ok);
    }

    [Fact]
    public void ALibraryWithoutTheVersionExportIsNotOk()
    {
        var r = NativeRuntime.Load(Options(version: () => throw new EntryPointNotFoundException("slua_api_version")));
        Assert.False(r.Ok);
        Assert.Contains("slua_api_version", r.Message);
    }

    [Fact]
    public void TheGateLogsOneErrorLineWhenTheLibraryIsMissing()
    {
        var log = new RecordingLog();
        Assert.False(NativeRuntime.Gate(Options(exists: _ => false), log));
        Assert.Single(log.Lines);
        Assert.Single(log.Errors);
    }

    [Fact]
    public void TheGateLogsOneErrorLineOnAVersionMismatch()
    {
        var log = new RecordingLog();
        Assert.False(NativeRuntime.Gate(Options(version: () => 7), log));
        Assert.Single(log.Lines);
        Assert.Contains("API version 7", Assert.Single(log.Errors));
    }

    [Fact]
    public void TheGateLogsOneInfoLineWhenTheLibraryIsUsable()
    {
        var log = new RecordingLog();
        Assert.True(NativeRuntime.Gate(Options(), log));
        Assert.Single(log.Lines);
        Assert.Empty(log.Errors);
    }

    private static Scene RegionWithPhloxAndOurEngine(NativeRuntimeOptions options, RecordingLog log, out SLuaEngine engine)
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.StackModuleInterface<IScriptModule>(new OtherEngine("InWorldz.Phlox"));
        engine = new SLuaEngine(log, (settings, l) => NativeRuntime.Gate(options, l));
        var source = new IniConfigSource();
        source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
        engine.Initialise(source);
        engine.AddRegion(scene);
        return scene;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    public void WithoutAUsableLibraryTheEngineStaysOutAndPhloxCarriesOn(string failure)
    {
        var log = new RecordingLog();
        var options = failure == "missing" ? Options(exists: _ => false) : Options(version: () => 2);
        Scene scene = RegionWithPhloxAndOurEngine(options, log, out SLuaEngine engine);

        IScriptModule only = Assert.Single(scene.RequestModuleInterfaces<IScriptModule>());
        Assert.Equal("InWorldz.Phlox", only.ScriptEngineName);
        Assert.Equal("InWorldz.Phlox", scene.ResolveScriptEngine("InWorldz.Phlox", "//SLua:\ndefault {}")?.ScriptEngineName);
        Assert.Single(log.Lines);
        Assert.Single(log.Errors);
    }

    [Fact]
    public void WithAUsableLibraryTheEngineRegisters()
    {
        var log = new RecordingLog();
        Scene scene = RegionWithPhloxAndOurEngine(Options(), log, out _);
        Assert.Equal(2, scene.RequestModuleInterfaces<IScriptModule>().Length);
        Assert.Empty(log.Errors);
    }

    private sealed class OtherEngine : MockScriptEngine, IScriptModule
    {
        public OtherEngine(string name) { ScriptEngineName = name; }
        public new string ScriptEngineName { get; }
    }
}
