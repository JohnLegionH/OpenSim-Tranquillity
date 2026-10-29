/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Four ll functions are private and deprecated in Second Life's definitions (no description, no wiki page). They are
/// intentionally absent, so a script that calls one is told so, in the words the coverage table and the engine's start line
/// use ("private in SL"), and not that support is still to come ("not available on this engine yet").
/// </summary>
[Collection("native-library")]
public class PrivateFunctionTextTests
{
    private static readonly string[] Private =
    {
        "llClearExperience", "llClearExperiencePermissions", "llGetExperienceList", "llSetExperienceKey",
    };

    private static string TextOf(string function) => function + " is private in SL and not available to scripts";

    [Fact]
    public void TheErrorOfAPrivateFunctionSaysItIsPrivateInSlAndNotAvailableToScripts()
    {
        foreach (string function in Private)
        {
            string message = FunctionUnavailableException.Private(function).Message;

            Assert.Equal(TextOf(function), message);
            Assert.DoesNotContain("yet", message);
        }
    }

    [Fact]
    public void TheErrorOfAFunctionThatWillComeStillSaysSo()
    {
        Assert.Equal("llRequestExperiencePermissions is not available on this engine yet",
            new FunctionUnavailableException("llRequestExperiencePermissions").Message);
    }

    [Fact]
    public void TheFourFunctionsAreThoseTheCoverageTableListsAsPrivateInSl()
    {
        Assert.Equal(Private, LlCoverage.PrivateInSl.Select(r => r.Substring(0, r.IndexOf(':'))).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(LlCoverage.PrivateInSl, r => Assert.Contains("private in SL", r));
        Assert.All(Private, n => Assert.Contains(n, LlBindings.Unavailable));
    }

    [RequiresNativeFact]
    public void AScriptThatCallsAPrivateFunctionGetsTheTextOfThatFunction()
    {
        using var rig = new EngineRig(productionFunctions: true);
        string names = string.Join(",", Private.Select(n => "'" + n.Substring(2) + "'"));
        UUID item = rig.Rez(Slua(
            "for _, n in ipairs({" + names + "}) do local ok, err = pcall(ll[n]) ossl.Mark(n .. '|' .. tostring(ok) .. '|' .. tostring(err)) end"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= Private.Length));
        string[] marks = rig.MarksOf(item);
        foreach (string function in Private)
        {
            string mark = Assert.Single(marks, m => m.StartsWith(function.Substring(2) + "|false|"));
            Assert.Contains(TextOf(function), mark);
            Assert.DoesNotContain("on this engine yet", mark);
        }
    }

    [RequiresNativeFact]
    public void AFunctionThatIsNotPrivateKeepsTheTextThatItWillCome()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("local ok, err = pcall(ll.RequestExperiencePermissions, '6b3f1d52-8a47-4e0b-9c61-2f5d7e8a9b10', '') ossl.Mark(tostring(ok) .. '|' .. tostring(err))"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= 1));
        Assert.Contains("llRequestExperiencePermissions is not available on this engine yet", rig.MarksOf(item)[0]);
        Assert.DoesNotContain("private in SL", rig.MarksOf(item)[0]);
    }
}
