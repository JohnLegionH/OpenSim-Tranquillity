/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How the module finds its native: the file for the running platform under runtimes/&lt;rid&gt;/native/, its
/// hash checked against the record, an unrecorded build refused unless [Jolt] AllowUnrecordedNative is set, and a
/// platform with no native refused with one clear message. Runs in parallel with the other classes: the only
/// process-wide state it touches is the one-time load, which every backend test shares and which is idempotent.
/// </summary>
public class JoltNativeTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string ModuleRuntimes => Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt", "runtimes");

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "jolt-native-test-" + Guid.NewGuid().ToString("N"));

    // ------------------------------------------------------------------ choosing the file

    [Theory]
    [InlineData("windows", Architecture.X64, false, "win-x64")]
    [InlineData("windows", Architecture.Arm64, false, "win-arm64")]
    [InlineData("linux", Architecture.X64, false, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, false, "linux-arm64")]
    [InlineData("linux", Architecture.X64, true, "linux-musl-x64")]
    [InlineData("osx", Architecture.Arm64, false, "osx-arm64")]
    [InlineData("osx", Architecture.X64, false, "osx-x64")]
    [InlineData("freebsd", Architecture.X64, false, "freebsd-x64")]
    public void The_platform_maps_to_its_runtime_identifier(string os, Architecture arch, bool musl, string rid)
    {
        OSPlatform platform = os switch
        {
            "windows" => OSPlatform.Windows,
            "linux" => OSPlatform.Linux,
            "osx" => OSPlatform.OSX,
            _ => OSPlatform.FreeBSD,
        };
        Assert.Equal(rid, JoltNative.RidFor(platform, arch, musl));
    }

    [Fact]
    public void An_unknown_os_has_no_runtime_identifier()
    {
        Assert.Null(JoltNative.RidFor(null, Architecture.X64));
        Assert.Null(JoltNative.RidFor(OSPlatform.Create("PLAN9"), Architecture.X64));
    }

    [Theory]
    [InlineData("win-x64", "joltc.dll")]
    [InlineData("linux-x64", "libjoltc.so")]
    public void Each_platform_gets_its_own_file_under_runtimes(string rid, string file)
    {
        string baseDir = Path.Combine("any", "app");
        Assert.Equal(Path.Combine(baseDir, "runtimes", rid, "native", file), JoltNative.PathFor(baseDir, rid));
    }

    [Fact]
    public void The_running_process_loads_its_platforms_file_from_runtimes()
    {
        JoltNativeInfo info = JoltNative.EnsureLoaded(allowUnrecorded: false);

        string expectedRid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null;
        Assert.NotNull(expectedRid);   // the suite runs on win-x64 and linux-x64
        Assert.Equal(expectedRid, info.Rid);
        Assert.Equal(JoltNative.PathFor(JoltNative.DefaultBaseDirectory(), expectedRid), info.Path);
        Assert.Equal(OperatingSystem.IsWindows() ? "joltc.dll" : "libjoltc.so", Path.GetFileName(info.Path));
        Assert.True(info.Recorded);
        Assert.Equal(JoltNative.Shipped[expectedRid].Sha256, info.Sha256);
        Assert.Contains(info.Path, info.Describe());
        Assert.Contains(info.Sha256, info.Describe());

        // Nothing at the output root: the file under runtimes/ is the only one.
        Assert.False(File.Exists(Path.Combine(JoltNative.DefaultBaseDirectory(), "joltc.dll")));
        Assert.False(File.Exists(Path.Combine(JoltNative.DefaultBaseDirectory(), "libjoltc.so")));
    }

    [Fact]
    public void The_binding_has_the_resolver_hook_the_loader_uses()
    {
        var hook = JoltNative.BindingHook();
        Assert.NotNull(hook);
        Assert.Equal(typeof(DllImportResolver), hook.EventHandlerType);
    }

    // ------------------------------------------------------------------ the hash record

    [Fact]
    public void The_record_matches_the_shipped_files_and_the_guard_script()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt", "assert-patched-joltc.ps1"));
        var guardEntries = Regex.Matches(guard, "\"([a-z0-9-]+)\"\\s*=\\s*@\\{\\s*File\\s*=\\s*\"([^\"]+)\";\\s*Sha256\\s*=\\s*\"([0-9A-F]{64})\"")
            .ToDictionary(m => m.Groups[1].Value, m => (m.Groups[2].Value, m.Groups[3].Value));
        Assert.Equal(JoltNative.Shipped.Keys.OrderBy(k => k), guardEntries.Keys.OrderBy(k => k));

        foreach (var (rid, entry) in JoltNative.Shipped)
        {
            Assert.Equal(entry, guardEntries[rid]);
            string inRepo = Path.Combine(ModuleRuntimes, rid, "native", entry.File);
            Assert.Equal(entry.Sha256, JoltNative.Sha256Of(inRepo));
            string inOutput = JoltNative.PathFor(JoltNative.DefaultBaseDirectory(), rid);
            Assert.Equal(entry.Sha256, JoltNative.Sha256Of(inOutput));
        }

        // Every native in the module's runtimes folder has a record.
        var files = Directory.GetDirectories(ModuleRuntimes)
            .SelectMany(d => Directory.GetFiles(Path.Combine(d, "native")).Select(f => (Rid: Path.GetFileName(d), File: Path.GetFileName(f))))
            .OrderBy(x => x.Rid).ToArray();
        Assert.Equal(JoltNative.Shipped.Select(kv => (kv.Key, kv.Value.File)).OrderBy(x => x.Key).ToArray(), files);
    }

    [Fact]
    public void The_readme_hash_table_matches_the_record()
    {
        // The table under "The files in the repository" in native/joltc/README.md: one row per shipped file.
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "native", "joltc", "README.md"));
        var rows = Regex.Matches(readme, @"^\| `runtimes/([a-z0-9-]+)/native/([^`]+)` \| `([0-9a-f]{64})` \|", RegexOptions.Multiline)
            .Select(m => (Rid: m.Groups[1].Value, File: m.Groups[2].Value, Sha256: m.Groups[3].Value.ToUpperInvariant()))
            .OrderBy(x => x.Rid).ToArray();
        Assert.Equal(JoltNative.Shipped.Select(kv => (kv.Key, kv.Value.File, kv.Value.Sha256)).OrderBy(x => x.Key).ToArray(), rows);
    }

    [Fact]
    public void An_unrecorded_native_is_refused_and_the_key_allows_it()
    {
        string dir = NewTempDir();
        try
        {
            string native = Path.Combine(dir, "runtimes", "win-x64", "native", "joltc.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.WriteAllText(native, "not the patched build");
            string hash = JoltNative.Sha256Of(native);

            var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(dir, "win-x64", allowUnrecorded: false));
            Assert.Contains(native, e.Message);
            Assert.Contains(hash, e.Message);
            Assert.Contains("AllowUnrecordedNative", e.Message);

            JoltNativeInfo allowed = JoltNative.Check(dir, "win-x64", allowUnrecorded: true);
            Assert.False(allowed.Recorded);
            Assert.Equal(native, allowed.Path);
            Assert.Equal(hash, allowed.Sha256);
            Assert.Contains("AllowUnrecordedNative", allowed.Describe());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_shipped_file_passes_the_check_without_the_key()
    {
        string baseDir = JoltNative.DefaultBaseDirectory();
        foreach (string rid in JoltNative.Shipped.Keys)
            Assert.True(JoltNative.Check(baseDir, rid, allowUnrecorded: false).Recorded);
    }

    // ------------------------------------------------------------------ no native

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("linux-arm64")]
    [InlineData("win-arm64")]
    [InlineData("linux-musl-x64")]
    public void A_platform_without_a_native_gets_one_clear_error(string rid)
    {
        var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(JoltNative.DefaultBaseDirectory(), rid, allowUnrecorded: true));
        Assert.Equal(
            $"Jolt physics has no native library for this platform ({rid}). Supported platforms: win-x64, linux-x64. " +
            "Choose another physics engine in [Startup] physics.",
            e.Message);
    }

    [Fact]
    public void A_missing_file_names_the_path_and_the_runtimes_folder()
    {
        string dir = NewTempDir();
        var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(dir, "linux-x64", allowUnrecorded: false));
        Assert.Contains(Path.Combine(dir, "runtimes", "linux-x64", "native", "libjoltc.so"), e.Message);
        Assert.Contains("missing", e.Message);
        Assert.False(Directory.Exists(dir));
    }

    // ------------------------------------------------------------------ the module

    [Fact]
    public void AllowUnrecordedNative_defaults_off_and_reaches_the_backend()
    {
        Assert.False(JoltConfig.FromConfig(new IniConfigSource(), new List<string>()).AllowUnrecordedNative);
        Assert.False(PhysicsBackendSettings.Default.AllowUnrecordedNative);

        var src = new IniConfigSource();
        src.AddConfig("Jolt").Set("AllowUnrecordedNative", "true");
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(src, warnings);
        Assert.Empty(warnings);
        Assert.True(c.AllowUnrecordedNative);
        Assert.True(c.ToBackendSettings(256, 256, false).AllowUnrecordedNative);
    }

    [Fact]
    public void The_module_loads_the_native_before_it_enables()
    {
        string scene = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));
        int at = scene.IndexOf("public void Initialise(IConfigSource source)", StringComparison.Ordinal);
        string body = scene[at..scene.IndexOf("public void Close()", at, StringComparison.Ordinal)];
        int load = body.IndexOf("NativeLoader(", StringComparison.Ordinal);
        int enable = body.IndexOf("m_Enabled = true;", StringComparison.Ordinal);
        Assert.True(load > 0 && enable > load, "Initialise must load and check the native before it sets m_Enabled");
        Assert.Contains("NativeLoader = JoltNative.EnsureLoaded;", scene);
        Assert.Contains("catch (JoltNativeException e)", body);
        Assert.Contains("m_log.LogError($\"{LogHeader} {e.Message}\");", body);
    }

    [Fact]
    public void The_module_with_physics_Jolt_initialises_on_this_platform()
    {
        var src = new IniConfigSource();
        var startup = src.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        var module = new JoltScene();
        module.Initialise(src);
        Assert.True(JoltNative.EnsureLoaded(false).Recorded);
    }
}
