/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Types;
using SLua.ScriptEngine.Bindings;
using Xunit;
using VT = SLua.ScriptEngine.Bindings.ValueType;
using static SLua.ScriptEngine.Tests.AdaptedFunctionTests;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The list results of the eight functions that secondlife/lsl-definitions marks bool-semantics hold truth values at places
/// the request decides. The definitions give each request tag the type of the value that follows it (value-type: boolean on
/// PRIM_PHYSICS, OBJECT_PHYSICS, PRIM_FULLBRIGHT's "enabled" ...), so an SLua script gets true and false at those places and
/// every other element as it was. The llcompat table and an LSL script keep LSL's integers.
/// </summary>
[Collection("native-library")]
public class ListBooleanTests
{
    // Tag values from lsl-definitions (lsl_definitions.yaml, constants).
    private const int ObjectName = 1, ObjectPhysics = 21, ObjectLocked = 55;
    private const int PrimPhysics = 3, PrimType = 9, PrimFlexible = 21, PrimFullbright = 20, PrimGlow = 25, PrimColor = 18, PrimSitTarget = 41;
    private const int SkySun = 14, PrimMediaAutoLoop = 4, PrimMediaCurrentUrl = 2, ParcelDetailsSeeAvatars = 6, ParcelDetailsName = 0;
    private const int AllSides = -1;

    private static Value I(long n) => Value.Int(n);

    private static Value[] L(params Value[] items) => items;

    /// <summary>The list as text, one element per entry: "b:true", "i:5", "s:text", "n:0.5", or the type name.</summary>
    private static string Show(Value[] result)
    {
        Assert.Single(result);
        return string.Join(",", result[0].Items.Select(v => v.Type switch
        {
            VT.Bool => "b:" + (v.I != 0 ? "true" : "false"),
            VT.Int => "i:" + v.I,
            VT.String => "s:" + v.S,
            VT.Number => "n:" + v.N.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => v.Type.ToString(),
        }));
    }

    private static string Converted(string function, Value[] request, params Value[] result) =>
        Show(LlBindings.ConvertListBooleans(function, request, new[] { Value.List(result) }));

    private static string RuleOf(string function) => LlBooleans.ListRules.TryGetValue(function, out string rule) ? rule : null;

    private static TagLayout LayoutOf(string rule, int tag) =>
        LlBooleans.Tags.TryGetValue(rule, out var tags) && tags.TryGetValue(tag, out TagLayout layout) ? layout : null;

    private static string Cells(string rule, int tag) => LayoutOf(rule, tag)?.Cells;

    private static bool PerFace(string rule, int tag) => LayoutOf(rule, tag)?.PerFace ?? false;

    // ---- the table: which functions, and the layout of a tag's values ----

    [Fact]
    public void TheEightFunctionsAreListedWithTheRequestSetTheirTagsComeFrom()
    {
        Assert.Equal(8, LlBooleans.ListRules.Count);
        Assert.Equal("ObjectDetailsParam", RuleOf("llGetObjectDetails"));
        Assert.Equal("PrimParamGet", RuleOf("llGetPrimitiveParams"));
        Assert.Equal("PrimParamGet", RuleOf("llGetLinkPrimitiveParams"));
        Assert.Equal("ParcelDetailsParam", RuleOf("llGetParcelDetails"));
        Assert.Equal("PrimMediaParam", RuleOf("llGetPrimMediaParams"));
        Assert.Equal("PrimMediaParam", RuleOf("llGetLinkMedia"));
        Assert.Equal("EnvironmentParamReadOnly", RuleOf("llGetEnvironment"));
        Assert.Equal("ParcelMediaQuery", RuleOf("llParcelMediaQuery"));
    }

    [Fact]
    public void ATagKnowsHowManyValuesItReturnsAndWhichOnesAreBooleans()
    {
        Assert.Equal("b", Cells("PrimParamGet", PrimPhysics));
        Assert.False(PerFace("PrimParamGet", PrimPhysics));

        Assert.Equal("b", Cells("PrimParamGet", PrimFullbright));   // the face is an argument and returns nothing
        Assert.True(PerFace("PrimParamGet", PrimFullbright));

        Assert.Equal("bxxxxxx", Cells("PrimParamGet", PrimFlexible));
        Assert.Equal("bxx", Cells("PrimParamGet", PrimSitTarget));
        Assert.Equal("xxxbxxx", Cells("EnvironmentParamReadOnly", SkySun));   // is_default_texture
        Assert.Equal("b", Cells("ObjectDetailsParam", ObjectPhysics));
        Assert.Equal("x", Cells("ObjectDetailsParam", ObjectName));
        Assert.Equal("b", Cells("ParcelDetailsParam", ParcelDetailsSeeAvatars));
        Assert.Equal("b", Cells("PrimMediaParam", PrimMediaAutoLoop));
    }

    [Fact]
    public void ATagWhoseNumberOfValuesTheDefinitionsDoNotGiveIsOpenEnded()
    {
        // PRIM_TYPE returns the shape and a number of parameters that depends on it; the definitions give it one integer.
        Assert.Equal("?", Cells("PrimParamGet", PrimType));
    }

    private static string ListMarksPath() =>
        Path.Combine(LlBindingGeneratorTests.RepoRoot(), "Tests", "SLua.ScriptEngine.Tests", "TestData", "lsl-definition-list-marks.txt");

    [Fact]
    public void TheTablesAreTheListMarksFileTagForTag()
    {
        // lsl-definition-list-marks.txt is written by tools/slua-bindings/extract-marks.py from the definitions
        string[][] lines = File.ReadAllLines(ListMarksPath()).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('	')).ToArray();
        string[][] functions = lines.Where(f => f[0] == "function").ToArray();
        string[][] tags = lines.Where(f => f[0] == "tag").ToArray();

        Assert.Equal(functions.Select(f => f[1] + "=" + f[2]).OrderBy(x => x, StringComparer.Ordinal),
            LlBooleans.ListRules.Select(kv => kv.Key + "=" + kv.Value).OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(tags.Length > 100, "the list marks file has " + tags.Length + " tags");
        Assert.Equal(tags.Length, LlBooleans.Tags.Values.Sum(t => t.Count));
        foreach (string[] tag in tags)
        {
            TagLayout layout = LayoutOf(tag[1], int.Parse(tag[2]));
            Assert.True(layout != null, tag[3] + " is not in the table");
            Assert.Equal(tag[4], layout.Cells);
            Assert.Equal(tag[5] == "face", layout.PerFace);
        }
    }

    // ---- the conversion: one request, one result ----

    [Fact]
    public void ObjectDetailsGivesTrueAndFalseForTheBooleanTagsAndLeavesTheRest()
    {
        Assert.Equal("b:true,s:box,b:false",
            Converted("llGetObjectDetails", L(I(ObjectPhysics), I(ObjectName), I(ObjectLocked)), I(1), Value.Str("box"), I(0)));
    }

    [Fact]
    public void TagsAndFacesThatAScriptWroteAsNumbersAreFoundToo()
    {
        // an SLua table of numbers reaches the binding as Number values
        Assert.Equal("b:true,b:false,n:0.5",
            Converted("llGetPrimitiveParams", L(Value.Num(PrimPhysics), Value.Num(PrimFullbright), Value.Num(2), Value.Num(PrimGlow), Value.Num(2)), I(1), I(0), Value.Num(0.5)));
        Assert.Equal("i:1", Converted("llGetPrimitiveParams", L(Value.Num(2.5)), I(1)));
    }

    [Fact]
    public void AnyNonZeroIntegerIsTrue()
    {
        Assert.Equal("b:true", Converted("llGetObjectDetails", L(I(ObjectPhysics)), I(5)));
    }

    [Fact]
    public void AValueThatIsNotAnIntegerIsLeftWhereABooleanWasExpected()
    {
        Assert.Equal("s:x", Converted("llGetObjectDetails", L(I(ObjectPhysics)), Value.Str("x")));
    }

    [Fact]
    public void PrimitiveParamsGivesABooleanForEachNamedFaceAndKeepsTheOthers()
    {
        Assert.Equal("b:true,b:false,n:0.5",
            Converted("llGetPrimitiveParams", L(I(PrimPhysics), I(PrimFullbright), I(2), I(PrimGlow), I(2)), I(1), I(0), Value.Num(0.5)));
        Assert.Equal("b:false",
            Converted("llGetLinkPrimitiveParams", L(I(PrimFullbright), I(0)), I(0)));
    }

    [Fact]
    public void AllSidesRepeatsATagsValuesOncePerFaceTheResultHolds()
    {
        // each rule gives its values for every face before the next rule starts; three faces here
        Assert.Equal("b:true,b:false,b:true,n:0.1,n:0.2,n:0.3",
            Converted("llGetPrimitiveParams", L(I(PrimFullbright), I(AllSides), I(PrimGlow), I(AllSides)),
                I(1), I(0), I(5), Value.Num(0.1), Value.Num(0.2), Value.Num(0.3)));
    }

    [Fact]
    public void AllSidesAfterAFixedTagStillFindsTheBooleansOfEachFace()
    {
        Assert.Equal("b:true,b:true,b:false",
            Converted("llGetPrimitiveParams", L(I(PrimPhysics), I(PrimFullbright), I(AllSides)), I(1), I(1), I(0)));
    }

    [Fact]
    public void ATagWithSeveralValuesConvertsOnlyItsBooleanOnes()
    {
        // PRIM_FLEXIBLE: enabled (boolean), softness (integer), gravity, friction, wind, tension, force
        Assert.Equal("b:true,i:2,n:1,n:2,n:3,n:4,Vector",
            Converted("llGetPrimitiveParams", L(I(PrimFlexible)), I(1), I(2), Value.Num(1), Value.Num(2), Value.Num(3), Value.Num(4), Value.Vec(0, 0, 1)));
        Assert.Equal("b:false,Vector,Rotation",
            Converted("llGetPrimitiveParams", L(I(PrimSitTarget)), I(0), Value.Vec(0, 0, 0), Value.Rot(0, 0, 0, 1)));
    }

    [Fact]
    public void TheEnvironmentsDefaultFlagsAreBooleans()
    {
        Assert.Equal("Rotation,n:1,Vector,b:true,Vector,Vector,Vector",
            Converted("llGetEnvironment", L(I(SkySun)), Value.Rot(0, 0, 0, 1), Value.Num(1), Value.Vec(1, 1, 1), I(1), Value.Vec(0, 0, 1), Value.Vec(0, 0, 0), Value.Vec(1, 1, 1)));
    }

    [Fact]
    public void MediaAndParcelDetailsFollowTheirOwnTags()
    {
        Assert.Equal("b:true,s:http://example.org/",
            Converted("llGetPrimMediaParams", L(I(PrimMediaAutoLoop), I(PrimMediaCurrentUrl)), I(1), Value.Str("http://example.org/")));
        Assert.Equal("b:false,s:http://example.org/",
            Converted("llGetLinkMedia", L(I(PrimMediaAutoLoop), I(PrimMediaCurrentUrl)), I(0), Value.Str("http://example.org/")));
        Assert.Equal("b:true,s:Park",
            Converted("llGetParcelDetails", L(I(ParcelDetailsSeeAvatars), I(ParcelDetailsName)), I(1), Value.Str("Park")));
    }

    [Fact]
    public void AFunctionWhoseTagsHaveNoBooleanKeepsItsIntegers()
    {
        // llParcelMediaQuery is marked bool-semantics but none of its tags has a boolean value: PARCEL_MEDIA_COMMAND_LOOP_SET (13), _SIZE (11)
        Assert.Equal("i:1,i:640,i:480", Converted("llParcelMediaQuery", L(I(13), I(11)), I(1), I(640), I(480)));
    }

    // ---- what the request cannot place ----

    [Fact]
    public void ABooleanBeforeAnOpenEndedTagIsConvertedAndNothingAfterIt()
    {
        Assert.Equal("b:true,i:0,i:1,i:1",
            Converted("llGetPrimitiveParams", L(I(PrimPhysics), I(PrimType), I(PrimPhysics)), I(1), I(0), I(1), I(1)));
    }

    [Fact]
    public void ATagTheTableDoesNotKnowStopsTheConversionThere()
    {
        Assert.Equal("b:true,i:5,i:1",
            Converted("llGetPrimitiveParams", L(I(PrimPhysics), I(9999), I(PrimPhysics)), I(1), I(5), I(1)));
    }

    [Fact]
    public void AResultOfTheWrongLengthIsLeftAsItIs()
    {
        Assert.Equal("i:1,i:2", Converted("llGetPrimitiveParams", L(I(PrimPhysics)), I(1), I(2)));
        // two all-sides tags of one value each cannot hold five values
        Assert.Equal("i:1,i:2,i:3,i:4,i:5", Converted("llGetPrimitiveParams", L(I(PrimFullbright), I(AllSides), I(PrimGlow), I(AllSides)), I(1), I(2), I(3), I(4), I(5)));
    }

    [Fact]
    public void AnEmptyOrNonListResultAndAFunctionOutsideTheTableAreLeftAlone()
    {
        Assert.Equal("", Converted("llGetObjectDetails", L(I(ObjectPhysics))));
        Value[] number = { I(7) };
        Assert.Same(number, LlBindings.ConvertListBooleans("llGetObjectDetails", L(I(ObjectPhysics)), number));
        Assert.Equal("i:1", Converted("llGetListLength", L(I(PrimPhysics)), I(1)));
    }

    // ---- what a script sees ----

    private const string Target = "6b3f1d52-8a47-4e0b-9c61-2f5d7e8a9b10";

    [RequiresNativeFact]
    public void AnSLuaScriptGetsBooleansInAListFromGetObjectDetails()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llGetObjectDetails", new LSLList(new object[] { 1, "box", 0 }));
        SchedulerScript s = rig.H.AddAndStart(
            "local r = ll.GetObjectDetails('" + Target + "', {21, 1, 55}) ossl.Mark('t', type(r[1]) .. ':' .. tostring(r[1]) .. ' ' .. type(r[2]) .. ' ' .. type(r[3]) .. ':' .. tostring(r[3]))");
        rig.H.Pump();

        Assert.Equal(new[] { "t:boolean:true string boolean:false" }, rig.H.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptGetsBooleansFromGetPrimitiveParamsToo()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llGetPrimitiveParams", new LSLList(new object[] { 0, 1 }));
        SchedulerScript s = rig.H.AddAndStart(
            "local r = ll.GetPrimitiveParams({3, 20, 1}) ossl.Mark('t', tostring(r[1]) .. ' ' .. tostring(r[2]) .. ' ' .. type(r[2]))");
        rig.H.Pump();

        Assert.Equal(new[] { "t:false true boolean" }, rig.H.MarksOf(s));
    }

    [RequiresNativeFact]
    public void TheLlcompatTableKeepsTheIntegersOfTheList()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llGetObjectDetails", new LSLList(new object[] { 1 }));
        SchedulerScript s = rig.H.AddAndStart(
            "local r = llcompat.GetObjectDetails('" + Target + "', {21}) ossl.Mark('t', type(r[1]) .. ':' .. tostring(r[1]))");
        rig.H.Pump();

        Assert.Equal(new[] { "t:number:1" }, rig.H.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AnLslScriptKeepsTheIntegersOfTheList()
    {
        using var rig = new BindingRig();
        // 5, not 1: a converted 5 would reach the script as 1
        rig.Fake.Returns("llGetObjectDetails", new LSLList(new object[] { 5 }));
        rig.Fake.Returns("llDumpList2String", "5");
        rig.H.AddAndStart("default { state_entry() { list l = llGetObjectDetails((key)\"" + Target + "\", [21]); llDumpList2String(l, \",\"); } }", lsl: true);
        rig.H.Pump();

        Assert.Contains(rig.Fake.Calls, c => c.Name == "llDumpList2String" && c.Args[0] is LSLList l && l.Data.Length == 1 && l.Data[0] is int i && i == 5);
    }
}
