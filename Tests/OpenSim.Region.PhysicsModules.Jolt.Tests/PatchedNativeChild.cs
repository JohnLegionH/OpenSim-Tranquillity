/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Runs one test in a child test host on the patched joltc the repository keeps under the Jolt module's runtimes/
/// folder. The suite's own process loads the stock native from the package, which runs one job pool; a test that needs
/// more than one pool checks the one-pool fallback in-process and runs its multi-pool half here, on the build that
/// allows it. The child is the same test build (dotnet test --no-build, same configuration) with JOLT_TEST_NATIVE_BASE
/// pointing at the module's folder (TestNativeOverride).
/// </summary>
internal static class PatchedNativeChild
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>The Jolt module's project folder: the base of the runtimes/ tree that holds the patched builds.</summary>
    public static string ModuleFolder => Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt");

    private static string TestProject => Path.Combine(RepoRoot(), "Tests", "OpenSim.Region.PhysicsModules.Jolt.Tests");

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>
    /// The record has a patched build for the running platform. Then the repository must hold it, unchanged: a missing
    /// or altered file fails here rather than quietly skipping the multi-pool runs.
    /// </summary>
    public static bool Available()
    {
        string rid = JoltNative.CurrentRid();
        if (!JoltNative.Platforms.TryGetValue(rid, out var platform) ||
            !JoltNative.Known.Any(b => b.Origin == JoltNativeOrigin.PatchedBuild && b.Folder == platform.Folder))
            return false;
        string path = JoltNative.PathFor(ModuleFolder, rid);
        Assert.True(File.Exists(path), $"the patched joltc for {rid} is recorded but missing from the repository: {path}");
        Assert.True(JoltNative.Find(rid, JoltNative.Sha256Of(path)) is { SafeForMultiplePools: true },
            $"{path} is not the recorded patched build");
        return true;
    }

    /// <summary>In a child test host started by <see cref="RunAndAssertPassed"/>: the native loaded is the patched build.</summary>
    public static void AssertPatchedWhenChild()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestNativeOverride.BaseVariable)))
            return;
        JoltNativeInfo native = JoltNative.EnsureLoaded(allowUnrecorded: false);
        Assert.True(native.Build is { Origin: JoltNativeOrigin.PatchedBuild, SafeForMultiplePools: true },
            $"the child test host loaded {native.Describe()}, not the patched build");
    }

    /// <summary>
    /// Runs the test <paramref name="method"/> of <paramref name="testClass"/> in a child test host on the patched native
    /// and asserts that it ran and passed. Returns the child's output.
    /// </summary>
    public static string RunAndAssertPassed(Type testClass, string method)
    {
        string name = $"{testClass.FullName}.{method}";
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Run where the repository's global.json applies, not wherever the parent test host was started.
            WorkingDirectory = TestProject,
        };
        foreach (string a in new[] { "test", TestProject, "--no-build", "-c", Configuration, "--nologo",
                                     "--filter", $"FullyQualifiedName={name}", "-l", "console;verbosity=normal" })
            psi.ArgumentList.Add(a);
        psi.Environment[TestNativeOverride.BaseVariable] = ModuleFolder;

        using var p = Process.Start(psi)!;
        // Both streams are read at once, so a full stderr pipe cannot stall the child.
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd() + err.Result;
        Assert.True(p.WaitForExit(15 * 60 * 1000), $"the child test host for {name} did not finish in 15 minutes");
        Assert.True(p.ExitCode == 0, $"the child test host for {name} on the patched native exited {p.ExitCode}:\n{output}");
        // The normal console logger: the test's own result line, then the run's totals.
        Assert.Contains($"  Passed {name} [", output);
        Assert.Matches(new Regex(@"Total tests: 1\r?\n\s+Passed: 1\r?\n"), output);
        return output;
    }
}
