/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Every function of the runtime's list against ILSLSystemAPI, in four groups (bound; in the runtime list but missing from the
/// interface; in the interface but not in the runtime list; signature mismatch) and the os, iw and bot functions apart. The
/// groups are checked against the runtime's list read here and the interface read by reflection, not against the generator.
/// </summary>
public class LlCoverageTests
{
    private static string NameOf(string entry) => entry.Substring(0, entry.IndexOf(':'));

    [Fact]
    public void EveryRuntimeFunctionIsInExactlyOneOfBoundMissingAndMismatch()
    {
        string[] runtime = LlBindingGeneratorTests.ReadBuiltins().Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] mismatch = LlCoverage.SignatureMismatch.Select(NameOf).ToArray();
        string[] all = LlCoverage.Bound.Concat(LlCoverage.MissingFromSeam).Concat(mismatch).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(runtime.Length > 500, "the runtime list has only " + runtime.Length + " ll functions");
        Assert.Equal(runtime, all);
    }

    [Fact]
    public void TheMissingFromSeamGroupIsTheRuntimeNamesTheInterfaceHasNoMethodFor()
    {
        HashSet<string> seam = LlBindingGeneratorTests.SeamMembers("ll").Select(m => m.Name).ToHashSet();
        string[] expected = LlBindingGeneratorTests.ReadBuiltins().Where(b => !seam.Contains(b.Name)).Select(b => b.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, LlCoverage.MissingFromSeam.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheMismatchGroupIsTheRuntimeNamesTheInterfaceHasUnderADifferentSignature()
    {
        MethodInfo[] members = LlBindingGeneratorTests.SeamMembers("ll");
        string[] expected = LlBindingGeneratorTests.ReadBuiltins()
            .Where(b => members.Any(m => m.Name == b.Name) && LlBindingGeneratorTests.Matching(b, members) == null && !LlCoverage.Adapted.Any(r => r.StartsWith(b.Name + ":")))
            .Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, LlCoverage.SignatureMismatch.Select(NameOf).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // each entry shows both signatures
        Assert.All(LlCoverage.SignatureMismatch, e => { Assert.Contains("runtime: ", e); Assert.Contains("seam: ", e); });
    }

    [Fact]
    public void EveryLlMemberOfTheInterfaceIsBoundOrListedAsInTheInterfaceOnly()
    {
        MethodInfo[] members = LlBindingGeneratorTests.SeamMembers("ll");
        Assert.True(members.Length > 500);
        Assert.Equal(members.Length, LlCoverage.Bound.Length + LlCoverage.SeamOnly.Length);
        // a member whose name is not in the runtime list is listed with that reason
        HashSet<string> runtime = LlBindingGeneratorTests.ReadBuiltins().Select(b => b.Name).ToHashSet();
        foreach (MethodInfo m in members.Where(m => !runtime.Contains(m.Name)))
            Assert.Contains(LlCoverage.SeamOnly, e => e.Contains(" " + m.Name + "(") && e.EndsWith("not in the runtime list", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryOsIwAndBotMemberOfTheInterfaceIsBoundByTheOsTableAndNoneByTheLlTable()
    {
        MethodInfo[] other = typeof(ILSLSystemAPI).GetMethods()
            .Where(m => m.Name.StartsWith("os", StringComparison.Ordinal) || m.Name.StartsWith("iw", StringComparison.Ordinal) || m.Name.StartsWith("bot", StringComparison.Ordinal))
            .ToArray();
        Assert.True(other.Length > 400, "the interface has only " + other.Length + " os, iw and bot members");
        Assert.Equal(other.Length, OsBindings.All.Sum(f => f.Overloads.Length));
        Assert.All(LlBindings.All, f => Assert.StartsWith("ll", f.ApiName));
        foreach (string prefix in new[] { "os", "iw", "bot" })
            Assert.Equal(other.Count(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)),
                OsBindings.All.Where(f => f.ApiName.StartsWith(prefix, StringComparison.Ordinal)).Sum(f => f.Overloads.Length));
    }

    [Fact]
    public void TheBoundGroupIsTheTable()
    {
        Assert.NotEmpty(LlCoverage.Bound);
        Assert.Equal(LlBindings.All.Select(f => f.ApiName).OrderBy(n => n, StringComparer.Ordinal), LlCoverage.Bound.OrderBy(n => n, StringComparer.Ordinal));
    }
}
