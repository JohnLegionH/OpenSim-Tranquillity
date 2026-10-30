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
/// The calls that run on the service lane (the Async and Service classes) answer through the lane's own return path in
/// LlBindings.Register, which shifts positions and converts a single truth value but does not convert the truth values inside a
/// list result. Today none of the eight functions that have a list rule (the generated LlBooleans.ListRules) is on the lane. This
/// guard fails if one is added to the lane, or given a list rule while it runs there, until the lane's path converts its list
/// result as the worker path does (ConvertListBooleans).
/// </summary>
public class ServiceLaneListBooleanGuardTests
{
    private static BoundFunction Function(string apiName, FunctionClass functionClass) =>
        new BoundFunction(apiName.Substring(2), apiName, functionClass, "list", new string[0], null);

    private static readonly IReadOnlyDictionary<string, string> Rules = new Dictionary<string, string>
    {
        ["llGetObjectDetails"] = "ObjectDetailsParam",
        ["llGetPrimitiveParams"] = "PrimParamGet",
    };

    [Fact]
    public void AFunctionWithAListRuleThatRunsOnTheLaneIsReportedByName()
    {
        string[] gaps = ServiceLaneGuard.LaneFunctionsWithAListRule(new[]
        {
            Function("llGetObjectDetails", FunctionClass.Service),
            Function("llGetPrimitiveParams", FunctionClass.Async),
        }, Rules);
        Assert.Equal(new[] { "llGetObjectDetails", "llGetPrimitiveParams" }, gaps.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AListRuleFunctionOffTheLaneAndALaneFunctionWithoutARuleAreNotReported()
    {
        string[] gaps = ServiceLaneGuard.LaneFunctionsWithAListRule(new[]
        {
            Function("llGetObjectDetails", FunctionClass.NonBlocking),
            Function("llGetPrimitiveParams", FunctionClass.Delayed),
            Function("llName2Key", FunctionClass.Service),
            Function("llEmail", FunctionClass.Async),
        }, Rules);
        Assert.Empty(gaps);
    }

    [Fact]
    public void NoGeneratedFunctionThatRunsOnTheLaneHasAListRule()
    {
        // Not vacuous: there are functions on the lane and functions with a list rule, and the two sets do not meet.
        Assert.Contains(LlBindings.All, f => f.Class is FunctionClass.Async or FunctionClass.Service);
        Assert.Equal(8, LlBooleans.ListRules.Count);
        Assert.All(LlBooleans.ListRules.Keys, name => Assert.Contains(LlBindings.All, f => f.ApiName == name));

        string[] gaps = ServiceLaneGuard.LaneFunctionsWithAListRule(LlBindings.All, LlBooleans.ListRules);
        Assert.True(gaps.Length == 0,
            "these functions run on the service lane, whose return path does not convert the truth values in a list result: "
            + string.Join(", ", gaps) + ". Convert the list on the lane's path (see ConvertListBooleans) before adding them.");
    }
}

internal static class ServiceLaneGuard
{
    /// <summary>The names of the functions that run on the service lane and have a list rule.</summary>
    public static string[] LaneFunctionsWithAListRule(IEnumerable<BoundFunction> functions, IReadOnlyDictionary<string, string> listRules)
    {
        return new string[0];
    }
}
