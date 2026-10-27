/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An SLua script gets the convention of the Second Life runtime: positions count from 1, 0 is an error, negative positions
/// count from the end, and "not found" is nil (VM/src/llsl.cpp luaSL_checkindexlike and luaSL_pushindexlike, VM/src/lll.cpp
/// _checkobjectindex). The same functions in the llcompat table, and every function of an LSL script, keep the LSL convention
/// (0-based, -1 for not found). The API underneath always sees the LSL convention.
/// </summary>
public class IndexShiftTests
{
    // ---- which arguments and returns are indexes, from the runtime's own definitions ----

    private static readonly Regex Signature = new(@"^(\w+)\s+(ll\w+)\(\s*(.*?)\s*\)", RegexOptions.Compiled);

    private static IEnumerable<(string Name, string Return, string[][] Params)> Definitions() =>
        File.ReadAllLines(LlBindingGeneratorTests.BuiltinsPath())
            .Select(l => Signature.Match(l)).Where(m => m.Success)
            .Select(m => (m.Groups[2].Value, m.Groups[1].Value,
                m.Groups[3].Value.Length == 0 ? new string[0][] : m.Groups[3].Value.Split(',').Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray()));

    [Fact]
    public void TheIndexArgumentsAreTheIntegerParametersTheDefinitionsNameIndex()
    {
        var expected = new Dictionary<string, int[]>();
        foreach (var (name, _, ps) in Definitions())
        {
            int[] at = Enumerable.Range(0, ps.Length).Where(i => ps[i][0] == "integer" && ps[i].Length == 2 && (ps[i][1] == "index" || ps[i][1].EndsWith("_index"))).ToArray();
            if (at.Length > 0) expected[name] = at;
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Keys.OrderBy(k => k), LlIndexes.Arguments.Keys.OrderBy(k => k));
        foreach (var kv in expected) Assert.Equal(kv.Value, LlIndexes.Arguments[kv.Key]);
        Assert.Equal(new[] { 1 }, LlIndexes.Arguments["llList2String"]);
        Assert.Equal(new[] { 1, 2 }, LlIndexes.Arguments["llGetSubString"]);
    }

    [Fact]
    public void TheIndexReturnsAreTheIntegerFunctionsThatFindAPosition()
    {
        string[] expected = Definitions().Where(d => d.Return == "integer" && Regex.IsMatch(d.Name, "Find|Index")).Select(d => d.Name).OrderBy(n => n).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, LlIndexes.Returns.OrderBy(n => n));
        Assert.Contains("llListFindList", LlIndexes.Returns);
        Assert.Contains("llSubStringIndex", LlIndexes.Returns);
    }

    [Fact]
    public void PositionLikeParametersThatAreNotDerivedAreListed()
    {
        Assert.Contains("llDetectedKey(number)", LlIndexes.NotDerived);
        Assert.Contains("llGetNotecardLine(line)", LlIndexes.NotDerived);
        Assert.Contains("llDetectedName(item)", LlIndexes.NotDerived);
        Assert.DoesNotContain(LlIndexes.NotDerived, n => n.StartsWith("llList2String"));
    }

    [Fact]
    public void NoIndexFunctionRunsOnTheServiceLane()
    {
        var indexed = new HashSet<string>(LlIndexes.Arguments.Keys.Concat(LlIndexes.Returns));
        Assert.NotEmpty(indexed);
        Assert.DoesNotContain(LlBindings.All, f => indexed.Contains(f.ApiName) && (f.Class == FunctionClass.Async || f.Class == FunctionClass.Service));
    }

    // ---- what a script sees ----

    private static string[] Calls(BindingRig rig) =>
        rig.Fake.Calls.Select(c => c.Name + "(" + string.Join("|", c.Args.Select(a => a is InWorldz.Phlox.Types.LSLList l ? "[" + string.Join(",", l.Data) + "]" : Convert.ToString(a))) + ")").ToArray();

    private static string[] Run(BindingRig rig, string source, bool lsl = false)
    {
        SchedulerScript s = rig.H.AddAndStart(source, lsl);
        rig.H.Pump();
        return rig.H.MarksOf(s);
    }

    [RequiresNativeFact]
    public void AnSLuaScriptCountsFromOneAndTheApiSeesFromZero()
    {
        using var rig = new BindingRig();
        Run(rig, "ll.List2String({'a','b','c'}, 1) ll.List2String({'a','b','c'}, 3) ll.List2String({'a','b','c'}, -1)");

        Assert.Equal(new[] { "llList2String([a,b,c]|0)", "llList2String([a,b,c]|2)", "llList2String([a,b,c]|-1)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void BothEndsOfARangeAreShifted()
    {
        using var rig = new BindingRig();
        Run(rig, "ll.GetSubString('hello', 1, 3) ll.DeleteSubString('hello', 2, -1)");

        Assert.Equal(new[] { "llGetSubString(hello|0|2)", "llDeleteSubString(hello|1|-1)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ZeroIsAnErrorForAnSLuaScriptAndTheApiIsNotCalled()
    {
        using var rig = new BindingRig();
        string[] marks = Run(rig, "local ok, err = pcall(function() ll.List2String({'a'}, 0) end) ossl.Mark('r', tostring(ok) .. ' ' .. tostring(err))");

        Assert.Contains(marks, m => m.StartsWith("r:false") && m.Contains("passed 0 when a 1-based index was expected"));
        Assert.Empty(Calls(rig));
    }

    [RequiresNativeFact]
    public void AFoundPositionComesBackFromOneAndNotFoundIsNil()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llSubStringIndex", 0);
        rig.Fake.Returns("llListFindList", -1);
        string[] marks = Run(rig, "ossl.Mark('found', ll.SubStringIndex('abc', 'a')) ossl.Mark('none', tostring(ll.ListFindList({'a'}, {'z'})))");

        Assert.Contains("found:1", marks);
        Assert.Contains("none:nil", marks);
    }

    [RequiresNativeFact]
    public void ALaterPositionIsOneMoreThanTheApiGave()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llSubStringIndex", 4);
        string[] marks = Run(rig, "ossl.Mark('at', ll.SubStringIndex('abcde', 'e'))");

        Assert.Contains("at:5", marks);
    }

    [RequiresNativeFact]
    public void AnLslScriptKeepsTheLslConvention()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llSubStringIndex", -1);
        rig.Fake.Returns("llStringLength", 0);
        Run(rig, "default { state_entry() { llList2String([\"a\", \"b\"], 1); integer n = llSubStringIndex(\"abc\", \"z\"); llStringLength((string)n); } }", lsl: true);

        Assert.Equal(new[] { "llList2String([a,b]|1)", "llSubStringIndex(abc|z)", "llStringLength(-1)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void TheLlcompatTableKeepsTheLslConventionForAnSLuaScript()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llSubStringIndex", -1);
        string[] marks = Run(rig, "llcompat.List2String({'a','b'}, 1) ossl.Mark('r', llcompat.SubStringIndex('abc', 'z')) local ok = pcall(function() llcompat.List2String({'a'}, 0) end) ossl.Mark('zero', tostring(ok))");

        Assert.Equal(new[] { "llList2String([a,b]|1)", "llSubStringIndex(abc|z)", "llList2String([a]|0)" }, Calls(rig));
        Assert.Contains("r:-1", marks);
        Assert.Contains("zero:true", marks);
    }

    [RequiresNativeFact]
    public void EveryBoundFunctionIsInBothTables()
    {
        using var rig = new BindingRig();
        string names = string.Join(",", LlBindings.All.Select(f => "'" + f.Name + "'"));
        string[] marks = Run(rig, "local missing = 0 for _, n in ipairs({" + names + "}) do if ll[n] == nil or llcompat[n] == nil then missing = missing + 1 end end ossl.Mark('missing', missing)");

        Assert.Contains("missing:0", marks);
    }

    [RequiresNativeFact]
    public void AFunctionThatTakesNoIndexSeesItsArgumentsUnchanged()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("llStringLength", 3);
        string[] marks = Run(rig, "ossl.Mark('len', ll.StringLength('abc')) ll.GetLinkName(1)");

        Assert.Equal(new[] { "llStringLength(abc)", "llGetLinkName(1)" }, Calls(rig));
        Assert.Contains("len:3", marks);
    }

    [RequiresNativeFact]
    public void TheUnavailableFunctionsAreInTheLlcompatTableToo()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("local ok, err = pcall(function() llcompat.Say(0, 'x') end) ossl.Mark('r=' .. tostring(err))"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Any(m => m.StartsWith("r="))));
        Assert.Contains("is not available on this engine yet", rig.MarksOf(item).First(m => m.StartsWith("r=")));
    }
}
