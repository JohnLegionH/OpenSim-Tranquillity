/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Facts about the os, iw and bot functions that a guide for script writers states, held here so the guide cannot drift from the code.
/// </summary>
public class ScripterFactsTests
{
    [Fact]
    public void NoOsIwOrBotFunctionAnswersThatItIsNotAvailable()
    {
        // Every os, iw and bot function reaches the real API; the refusal list is empty until a function needs a host member the engine lacks.
        Assert.Empty(OsBindings.Unavailable);
    }

    [Fact]
    public void TheOnlyDetectedFunctionsInTheOsIwAndBotTablesAreDetectedCountryAndDetectedBot()
    {
        string[] detected = OsBindings.All.Where(f => f.Name.StartsWith("Detected", StringComparison.Ordinal)).Select(f => f.Table + "." + f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // A script reads the objects of an event from the table its handler is given, through ll.Detected* (see EventArgumentShapeTests);
        // ossl.DetectedCount, ossl.DetectedName and ossl.DetectedFace are functions of the test rig only.
        Assert.Equal(new[] { "iw.DetectedBot", "ossl.DetectedCountry" }, detected);
    }
}
