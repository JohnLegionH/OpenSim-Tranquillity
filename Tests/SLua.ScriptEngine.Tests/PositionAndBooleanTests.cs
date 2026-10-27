/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Glue;
using Xunit;
using static SLua.ScriptEngine.Tests.AdaptedFunctionTests;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What Second Life's definitions mark (secondlife/lsl-definitions: index-semantics and bool-semantics) decides what an SLua
/// script sees from the ll table: marked positions count from 1, marked truth values are true and false. The llcompat table,
/// and every function of an LSL script, keep LSL's integers and positions from 0.
/// </summary>
[Collection("native-library")]
public class PositionAndBooleanTests
{
    private const string AgentKey = "6b3f1d52-8a47-4e0b-9c61-2f5d7e8a9b10";

    private static string[] IntegerBooleans() =>
        IndexShiftTests.Marks().Where(kv => kv.Value.Contains("return:bool") && IndexShiftTests.ReturnTypeOf(kv.Key) == "integer")
            .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] ListBooleans() =>
        IndexShiftTests.Marks().Where(kv => kv.Value.Contains("return:bool") && IndexShiftTests.ReturnTypeOf(kv.Key) == "list")
            .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- the lists ----

    [Fact]
    public void TheBooleanResultsAreTheIntegerFunctionsTheDefinitionsMarkAsBoolean()
    {
        string[] expected = IntegerBooleans();

        Assert.Equal(16, expected.Length);
        Assert.Equal(expected, LlBooleans.Returns.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains("llOverMyLand", LlBooleans.Returns);
        Assert.Contains("llGetStatus", LlBooleans.Returns);
        Assert.Contains("llManageEstateAccess", LlBooleans.Returns);
        Assert.DoesNotContain("llStringLength", LlBooleans.Returns);
    }

    [Fact]
    public void TheListResultsTheDefinitionsMarkAsBooleanAreListedAsNotConverted()
    {
        string[] expected = ListBooleans();

        Assert.Equal(8, expected.Length);
        Assert.Equal(expected, LlBooleans.ListsNotConverted.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains("llGetObjectDetails", LlBooleans.ListsNotConverted);
    }

    // ---- what a script sees: booleans ----

    [RequiresNativeFact]
    public void AnSLuaScriptGetsTrueAndFalseFromAMarkedFunction()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llOverMyLand", 1);
        SchedulerScript s1 = rig.H.AddAndStart("local r = ll.OverMyLand('" + AgentKey + "') ossl.Mark('t', type(r) .. ':' .. tostring(r))");
        rig.H.Pump();
        rig.Fake.Returns("llOverMyLand", 0);
        SchedulerScript s0 = rig.H.AddAndStart("local r = ll.OverMyLand('" + AgentKey + "') ossl.Mark('t', type(r) .. ':' .. tostring(r))");
        rig.H.Pump();

        Assert.Equal(new[] { "t:boolean:true" }, rig.H.MarksOf(s1));
        Assert.Equal(new[] { "t:boolean:false" }, rig.H.MarksOf(s0));
    }

    [RequiresNativeFact]
    public void AFunctionTheDefinitionsDoNotMarkKeepsItsInteger()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llStringLength", 1);
        rig.Fake.Returns("llGetListLength", 1);
        SchedulerScript s = rig.H.AddAndStart("local r = ll.StringLength('a') ossl.Mark('t', type(r) .. ':' .. tostring(r))");
        rig.H.Pump();

        Assert.Equal(new[] { "t:number:1" }, rig.H.MarksOf(s));
    }

    [RequiresNativeFact]
    public void TheLlcompatTableAndAnLslScriptKeepTheInteger()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llOverMyLand", 1);
        rig.Fake.Returns("llStringLength", 0);
        SchedulerScript compat = rig.H.AddAndStart("local r = llcompat.OverMyLand('" + AgentKey + "') ossl.Mark('t', type(r) .. ':' .. tostring(r))");
        rig.H.Pump();
        rig.H.AddAndStart("default { state_entry() { integer r = llOverMyLand(\"" + AgentKey + "\"); llStringLength((string)r); } }", lsl: true);
        rig.H.Pump();

        Assert.Equal(new[] { "t:number:1" }, rig.H.MarksOf(compat));
        Assert.Contains(rig.Fake.Calls, c => c.Name == "llStringLength" && (string)c.Args[0] == "1");
    }

    [RequiresNativeFact]
    public void ALongRunningFunctionMarkedBooleanGivesABooleanToo()
    {
        using var rig = new LaneRig();
        rig.Fake.Runs("llManageEstateAccess", a => { SyscallContext.Current.SetResult(1, 0); return null; });

        string[] marks = rig.Run("local r = ll.ManageEstateAccess(4, '" + AgentKey + "') ossl.Mark('t', type(r) .. ':' .. tostring(r))");

        Assert.Equal(new[] { "t:boolean:true" }, marks);
    }

    // ---- what a script sees: positions that the name rule missed ----

    [RequiresNativeFact]
    public void ADetectedNumberCountsFromOneForAnSLuaScript()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("ll.DetectedKey(1) ll.DetectedName(3) ll.DetectedType(2)");
        rig.H.Pump();

        Assert.Equal(new[] { "llDetectedKey(0)", "llDetectedName(2)", "llDetectedType(1)" }, rig.Fake.Calls.Select(c => c.Name + "(" + c.Args[0] + ")").ToArray());
        Assert.Empty(rig.H.FaultedScripts);
    }

    [RequiresNativeFact]
    public void ZeroIsAnErrorForADetectedNumberAndForTheOccurrenceOfAListSearch()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart(
            "local a = pcall(function() ll.DetectedKey(0) end) local b = pcall(function() ll.ListFindListNext({'a'}, {'a'}, 0) end) ossl.Mark('r', tostring(a) .. tostring(b))");
        rig.H.Pump();

        Assert.Equal(new[] { "r:falsefalse" }, rig.H.MarksOf(s));
        Assert.Empty(rig.Fake.Calls);
    }

    [RequiresNativeFact]
    public void ANumberTheDefinitionsDoNotMarkIsNotShifted()
    {
        using var rig = new BindingRig();
        rig.H.AddAndStart("ll.MessageLinked(1, 2, 'x', '') ll.SetTexture('t', 3)");
        rig.H.Pump();

        Assert.Equal(new[] { "llMessageLinked(1|2|x|)", "llSetTexture(t|3)" },
            rig.Fake.Calls.Select(c => c.Name + "(" + string.Join("|", c.Args) + ")").ToArray());
    }

    [RequiresNativeFact]
    public void AMarkedPositionOfAFunctionOnTheLaneIsShiftedToo()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llGetNotecardLineSync", "text");

        string[] marks = rig.Run("ossl.Mark('r', ll.GetNotecardLineSync('card', 1))");
        string[] zero = rig.Run("local ok = pcall(function() ll.GetNotecardLineSync('card', 0) end) ossl.Mark('z', tostring(ok))");

        Assert.Equal(new[] { "r:text" }, marks);
        Assert.Equal(new[] { "z:false" }, zero);
        Assert.Equal(new[] { "llGetNotecardLineSync(card|0)" }, rig.Calls().Where(c => !c.StartsWith("NeedsService")).ToArray());
    }
}
