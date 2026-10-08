/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// With JOLT_TEST_ACCEPT_UNRECORDED_NATIVE=1 the suite loads whatever joltc sits under the test output's
/// runtimes/&lt;rid&gt;/native/, recorded or not, so it can be run against another build of the native (put there by
/// hand). The tests that check the file in the output against the module's record still fail then, as they should.
///
/// <para>With JOLT_TEST_NATIVE_BASE set to a folder, the native is read from that folder (its runtimes/ tree, else the
/// file in the folder itself) instead of the test output. <see cref="PatchedNativeChild"/> sets it to the folder of
/// the patched build supplied through JOLT_TEST_PATCHED_NATIVE_DIR, to run the tests that need more than one job pool
/// in a child test host.</para>
/// </summary>
internal static class TestNativeOverride
{
    public const string Variable = "JOLT_TEST_ACCEPT_UNRECORDED_NATIVE";
    public const string BaseVariable = "JOLT_TEST_NATIVE_BASE";

    [ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable(Variable) == "1")
            JoltNative.AcceptUnrecordedForTest = true;
        string nativeBase = Environment.GetEnvironmentVariable(BaseVariable);
        if (!string.IsNullOrEmpty(nativeBase))
            JoltNative.BaseDirectoryForTest = nativeBase;
    }
}
