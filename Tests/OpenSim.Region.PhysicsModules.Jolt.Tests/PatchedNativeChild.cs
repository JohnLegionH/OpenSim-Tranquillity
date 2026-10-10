/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Runs one test in a child test host on a patched joltc supplied to the tests. The suite's own process loads the
/// stock native from the package, which runs one job pool; a test that needs more than one pool checks the one-pool
/// fallback in-process and runs its multi-pool half here, on the build that allows it. The child is a
/// <see cref="ChildTestHost"/> with JOLT_TEST_NATIVE_BASE pointing at the supplied file's folder (TestNativeOverride).
///
/// <para>The repository does not keep the patched build. To supply it, set JOLT_TEST_PATCHED_NATIVE_DIR to a folder
/// with one subfolder per runtime identifier, each holding that platform's file: win-x64/joltc.dll,
/// linux-x64/libjoltc.so. Those are the contents of the joltc-win-x64 and joltc-linux-x64 artifacts of the
/// joltc-native workflow (native/joltc/README.md). Without it, the tests marked <see cref="PatchedNativeFactAttribute"/>
/// are reported as skipped, with that instruction as the reason.</para>
/// </summary>
internal static class PatchedNativeChild
{
    public const string DirVariable = "JOLT_TEST_PATCHED_NATIVE_DIR";

    /// <summary>The folder JOLT_TEST_PATCHED_NATIVE_DIR names, or null when it is not set.</summary>
    public static string? SuppliedFolder
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable(DirVariable);
            return string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir);
        }
    }

    /// <summary>The patched builds the module has a record of.</summary>
    public static IEnumerable<JoltNativeBuild> Recorded => JoltNative.Known.Where(b => b.Origin == JoltNativeOrigin.PatchedBuild);

    /// <summary>
    /// Why a test that needs the patched build cannot run here, or null when it can: the record has a patched build
    /// for the running platform and JOLT_TEST_PATCHED_NATIVE_DIR is set.
    /// </summary>
    public static string? SkipReason()
    {
        string rid = JoltNative.CurrentRid();
        if (!JoltNative.Platforms.TryGetValue(rid, out var platform) || !Recorded.Any(b => b.Folder == platform.Folder))
            return $"there is no patched joltc build for {rid}";
        if (SuppliedFolder == null)
            return $"needs the patched joltc, which the repository does not keep: set {DirVariable} to a folder holding " +
                   $"{platform.Folder}/{platform.File}, the joltc-{platform.Folder} artifact of the joltc-native workflow " +
                   "(native/joltc/README.md)";
        return null;
    }

    /// <summary>Where the supplied folder holds the patched file of the platform folder <paramref name="folder"/>.</summary>
    public static string PathIn(string supplied, string folder)
        => Path.Combine(supplied, folder, Recorded.First(b => b.Folder == folder).File);

    /// <summary>
    /// The supplied patched file for the running platform. A setting that names a folder without it, or with a file
    /// that is not the recorded patched build, fails here rather than quietly skipping the multi-pool runs.
    /// </summary>
    public static string Require()
    {
        string? supplied = SuppliedFolder;
        Assert.True(supplied != null, $"{DirVariable} is not set");
        string rid = JoltNative.CurrentRid();
        string path = PathIn(supplied!, JoltNative.Platforms[rid].Folder);
        Assert.True(File.Exists(path), $"{DirVariable} is {supplied}, but the patched joltc for {rid} is not there: {path}");
        Assert.True(JoltNative.Find(rid, JoltNative.Sha256Of(path)) is { SafeForMultiplePools: true },
            $"{path} is not the recorded patched build");
        return path;
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
    /// Runs the test <paramref name="method"/> of <paramref name="testClass"/> in a child test host on the supplied
    /// patched native and asserts that it ran and passed. Returns the child's output.
    /// </summary>
    public static string RunAndAssertPassed(Type testClass, string method)
    {
        string native = Require();
        // The module takes the file from this folder itself, as it takes the package's file from beside the
        // assemblies of a build for one runtime identifier (JoltNative.Locate).
        return ChildTestHost.RunAndAssertPassed(testClass, method,
            new Dictionary<string, string> { [TestNativeOverride.BaseVariable] = Path.GetDirectoryName(native)! });
    }
}

/// <summary>
/// A test that needs the patched joltc. It is reported as skipped, with the reason from
/// <see cref="PatchedNativeChild.SkipReason"/>, when the build is not supplied or the platform has none.
/// </summary>
public sealed class PatchedNativeFactAttribute : FactAttribute
{
    public PatchedNativeFactAttribute()
    {
        string? reason = PatchedNativeChild.SkipReason();
        if (reason != null)
            Skip = reason;
    }
}
