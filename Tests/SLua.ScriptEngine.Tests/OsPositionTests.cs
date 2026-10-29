/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using Xunit;
using OpenMetaverse;
using Phlox.ScriptEngine;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The os, iw and bot functions that take or give a position count from 1 for an SLua script, as Second Life's ll functions do
/// (secondlife/lsl-definitions, index-semantics: a list index, a character position in a string, the number of the detected
/// object of an event, a notecard line, an inventory item number; the SLua runtime's luaSL_checkindexlike turns 1 and up into
/// 0 and up, keeps a negative position, and refuses 0; luaSL_pushindexlike gives 1 and up and nil for -1). An LSL script keeps
/// counting from 0 and the API underneath always sees 0-based positions.
/// </summary>
[Collection("native-library")]
public class OsPositionTests
{
    /// <summary>The scheduler harness with the ll, os, iw and bot bindings, a fake API and a service lane.</summary>
    internal sealed class PositionRig : IDisposable
    {
        public readonly ILSLSystemAPI Api;
        public readonly FakeLslApi Fake;
        public readonly SchedulerHarness H;
        public readonly ServiceLane Lane;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, UUID> m_items = new();

        public PositionRig()
        {
            (Api, Fake) = FakeLslApi.Create();
            H = new SchedulerHarness(configure: o =>
            {
                Func<Bindings.Vm> inner = o.VmFactory;
                o.VmFactory = () =>
                {
                    Bindings.Vm vm = inner();
                    Func<SchedulerScript, ScriptContext> contextOf = s => new ScriptContext(s, Api, m_items.GetOrAdd(s.Id, _ => UUID.Random()), Lane);
                    LlBindings.Register(vm, contextOf, serviceLane: true);
                    OsBindings.Register(vm, contextOf, serviceLane: true);
                    return vm;
                };
            });
            Lane = new ServiceLane(H.Scheduler, "auto", 4, 35000, 30000);
        }

        public string[] Run(string source, bool lsl = false, int expectedMarks = 1)
        {
            SchedulerScript s = H.AddAndStart(source, lsl);
            SchedulerHarness.WaitFor(() => { H.Pump(); H.Advance(0.5); return H.MarksOf(s).Length >= expectedMarks; }, 10000); // a delayed call sleeps on the harness's clock
            Assert.True(H.FaultedScripts.IsEmpty, "the script faulted: " + string.Join("; ", H.FaultedScripts.Select(f => f.FaultMessage)));
            Assert.True(H.ReportedErrors.IsEmpty, "the script reported: " + string.Join("; ", H.ReportedErrors));
            return H.MarksOf(s);
        }

        public string[] Calls() =>
            Fake.Calls.Select(c => c.Name + "(" + string.Join("|", c.Args.Select(a => a is LSLList l ? "[" + string.Join(",", l.Data) + "]" : Convert.ToString(a))) + ")").Where(c => !c.StartsWith("NeedsService")).ToArray();

        public void Dispose()
        {
            Lane.Dispose();
            H.Dispose();
        }
    }

    private static string[] Calls(PositionRig rig) => rig.Calls();

    // ---- one test per kind of position ----

    [RequiresNativeFact]
    public void AListIndexCountsFromOne()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osListAsString", "b");
        rig.Fake.Returns("osOldList2ListStrided", new LSLList(new object[] { "a" }));

        string[] marks = rig.Run("ossl.Mark('r', ossl.ListAsString({'a', 'b', 'c'}, 2))\nossl.Mark('s', #ossl.OldList2ListStrided({'a', 'b', 'c'}, 1, -1, 1))", expectedMarks: 2);

        Assert.Equal(new[] { "r:b", "s:1" }, marks);
        Assert.Equal(new[] { "osListAsString([a,b,c]|1)", "osOldList2ListStrided([a,b,c]|0|-1|1)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ACharacterPositionInAStringCountsFromOneAndANegativeOneStaysFromTheEnd()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osStringSubString", "ell");
        rig.Fake.Returns("osStringRemove", "hlo");
        rig.Fake.Returns("osReplaceString", "heLLo");
        rig.Fake.Returns("iwChar2Int", 104);

        string[] marks = rig.Run(
            "ossl.Mark('a', ossl.StringSubString('hello', 2))\nossl.Mark('b', ossl.StringSubString('hello', 2, 3))\nossl.Mark('c', ossl.StringRemove('hello', 2, 2))\n"
            + "ossl.Mark('d', ossl.ReplaceString('hello', 'l', 'L', 0, 3))\nossl.Mark('e', iw.Char2Int('hello', 1))\nossl.Mark('f', ossl.StringSubString('hello', -2))", expectedMarks: 6);

        Assert.Equal(new[] { "a:ell", "b:ell", "c:hlo", "d:heLLo", "e:104", "f:ell" }, marks);
        Assert.Equal(new[]
        {
            "osStringSubString(hello|1)", "osStringSubString(hello|1|3)", "osStringRemove(hello|1|2)", "osReplaceString(hello|l|L|0|2)",
            "iwChar2Int(hello|0)", "osStringSubString(hello|-2)",
        }, Calls(rig));
    }

    [RequiresNativeFact]
    public void APositionAFunctionGivesCountsFromOneAndNotFoundIsNil()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osStringIndexOf", 2);
        rig.Fake.Returns("iwSubStringIndex", 4);
        string[] found = rig.Run("ossl.Mark('a', ossl.StringIndexOf('hello', 'l', 1, 5, 0))\nossl.Mark('b', iw.SubStringIndex('hello', 'l', 4, 1))", expectedMarks: 2);

        rig.Fake.Returns("osStringIndexOf", -1);
        string[] missing = rig.Run("ossl.Mark('a', tostring(ossl.StringIndexOf('hello', 'z', 1, 5, 0)))");

        Assert.Equal(new[] { "a:3", "b:5" }, found);
        Assert.Equal(new[] { "a:nil" }, missing);
        Assert.Equal(new[] { "osStringIndexOf(hello|l|0|5|0)", "iwSubStringIndex(hello|l|3|1)", "osStringIndexOf(hello|z|0|5|0)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ThePositionsOfASignatureAreThoseOfItsOwnParameterCount()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osStringIndexOf", 0);

        // osStringIndexOf(src, value, ignorecase): the third parameter is a flag, not an offset
        string[] marks = rig.Run("ossl.Mark('a', tostring(ossl.StringIndexOf('hello', 'h', 1)))");

        Assert.Equal(new[] { "a:1" }, marks);
        Assert.Equal(new[] { "osStringIndexOf(hello|h|1)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ANotecardLineAndAnOffsetInItCountFromOne()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osGetNotecardLine", "text");
        rig.Fake.Returns("iwGetNotecardSegment", "11111111-2222-3333-4444-555555555555");
        rig.Fake.Returns("iwGetLinkNotecardLine", "11111111-2222-3333-4444-555555555555");
        rig.Fake.Returns("iwGetLinkNotecardSegment", "11111111-2222-3333-4444-555555555555");

        string[] marks = rig.Run(
            "ossl.Mark('a', ossl.GetNotecardLine('card', 1))\nlocal q = iw.GetNotecardSegment('card', 2, 5, 10)\nq = iw.GetLinkNotecardLine(3, 'card', 1)\nq = iw.GetLinkNotecardSegment(3, 'card', 4, 6, 10)\nossl.Mark('z', 'done')", expectedMarks: 2);

        Assert.Equal(new[] { "a:text", "z:done" }, marks);
        Assert.Equal(new[]
        {
            "osGetNotecardLine(card|0)", "iwGetNotecardSegment(card|1|4|10)", "iwGetLinkNotecardLine(3|card|0)", "iwGetLinkNotecardSegment(3|card|3|5|10)",
        }, Calls(rig));
    }

    [RequiresNativeFact]
    public void AnInventoryItemNumberCountsFromOneAndTheLinkNumberDoesNot()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("iwGetLinkInventoryName", "Chair");

        string[] marks = rig.Run("ossl.Mark('a', iw.GetLinkInventoryName(2, 7, 1))");

        Assert.Equal(new[] { "a:Chair" }, marks);
        Assert.Equal(new[] { "iwGetLinkInventoryName(2|7|0)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void TheNumberOfADetectedObjectCountsFromOne()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osDetectedCountry", "NZ");

        string[] marks = rig.Run("ossl.Mark('a', ossl.DetectedCountry(1))");

        Assert.Equal(new[] { "a:NZ" }, marks);
        Assert.Equal(new[] { "osDetectedCountry(0)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ThePositionsInAStridedAnswerCountFromOneAndTheTextsDoNot()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osMatchString", new LSLList(new object[] { "ab", 3, "c", 7 }));

        string[] marks = rig.Run("ossl.Mark('a', table.concat(ossl.MatchString('xxxabxxcx', '(ab)|c', 1), ','))");

        Assert.Equal(new[] { "a:ab,4,c,8" }, marks);
        Assert.Equal(new[] { "osMatchString(xxxabxxcx|(ab)|c|0)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void AListSearchTakesAndGivesPositionsAndTheInstanceFromOne()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osListFindListNext", 3);
        string[] found = rig.Run("ossl.Mark('a', ossl.ListFindListNext({'a', 'b', 'c', 'b'}, {'b'}, 1, -1, 2))");

        rig.Fake.Returns("osListFindListNext", -1);
        string[] missing = rig.Run("ossl.Mark('a', tostring(ossl.ListFindListNext({'a'}, {'z'}, 1, -1, 1)))");

        Assert.Equal(new[] { "a:4" }, found);
        Assert.Equal(new[] { "a:nil" }, missing);
        Assert.Equal(new[] { "osListFindListNext([a,b,c,b]|[b]|0|-1|1)", "osListFindListNext([a]|[z]|0|-1|0)" }, Calls(rig));
    }

    [RequiresNativeFact]
    public void ABotFunctionCountsItsRangeFromOneToo()
    {
        using var rig = new PositionRig();
        rig.Fake.Runs("botSearchBotOutfits", _ => { SyscallContext.Current?.SetResult(new LSLList(new object[] { "Casual" }), 0); return null; });

        string[] marks = rig.Run("local r = bot.SearchBotOutfits('Cas', 0, 1, -1)\nossl.Mark('n', #r)");

        Assert.Equal(new[] { "n:1" }, marks);
        Assert.Equal(new[] { "botSearchBotOutfits(Cas|0|0|-1)" }, Calls(rig));
    }

    // ---- the convention's edges ----

    [RequiresNativeFact]
    public void PositionZeroIsAnErrorForAnSLuaScriptAndNothingReachesTheApi()
    {
        using var rig = new PositionRig();

        string[] marks = rig.Run("local ok = pcall(ossl.StringSubString, 'hello', 0)\nlocal ok2 = pcall(ossl.ListAsString, {'a'}, 0)\nossl.Mark('r', tostring(ok) .. tostring(ok2))");

        Assert.Equal(new[] { "r:falsefalse" }, marks);
        Assert.Empty(Calls(rig));
    }

    [RequiresNativeFact]
    public void AnLslScriptKeepsCountingFromZeroOnTheSameFunctions()
    {
        using var rig = new PositionRig();
        rig.Fake.Returns("osStringSubString", "hel");
        rig.Fake.Returns("osStringIndexOf", 2);
        rig.Fake.Returns("osListAsString", "a");
        OsBindings.ApplyHostList();

        rig.Run(
            "default { state_entry() { string s = osStringSubString(\"hello\", 0, 3); integer i = osStringIndexOf(\"hello\", \"l\", 0, 5, 0); string e = osListAsString([\"a\", \"b\"], 0); llOwnerSay(s + (string)i + e); } }",
            lsl: true, expectedMarks: 0);
        SchedulerHarness.WaitFor(() => { rig.H.Pump(); return Calls(rig).Length >= 4; }, 10000);

        Assert.Equal(new[] { "osStringSubString(hello|0|3)", "osStringIndexOf(hello|l|0|5|0)", "osListAsString([a,b]|0)" }, Calls(rig).Where(c => c.StartsWith("os")).ToArray());
        Assert.Contains(rig.Fake.Calls, c => c.Name == "llOwnerSay" && (string)c.Args[0] == "hel2a");
    }

    // ---- which functions ----

    [Fact]
    public void ThePositionFunctionsAreThoseTheRuleFindsAndOnlyThose()
    {
        // by kind: list index, string position (argument), string position (result), notecard line, inventory item number,
        // detected object number, positions inside a strided answer, list search with a position result
        string[] expected =
        {
            "botSearchBotOutfits", "iwChar2Int", "iwGetLinkInventoryName", "iwGetLinkNotecardLine", "iwGetLinkNotecardSegment", "iwGetNotecardSegment",
            "iwSubStringIndex", "osDetectedCountry", "osGetNotecardLine", "osListAsFloat", "osListAsInteger", "osListAsRotation", "osListAsString",
            "osListAsVector", "osListFindListNext", "osListSortInPlaceStrided", "osMatchString", "osOldList2ListStrided", "osReplaceString",
            "osStringIndexOf", "osStringLastIndexOf", "osStringRemove", "osStringSubString",
        };
        string[] listed = OsIndexes.Arguments.Keys.Concat(OsIndexes.Returns).Concat(OsIndexes.ListStrides.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, listed);
        Assert.Equal(new[] { "iwSubStringIndex", "osListFindListNext", "osStringIndexOf", "osStringLastIndexOf" }, OsIndexes.Returns.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, OsIndexes.ListStrides["osMatchString"].Stride);
        Assert.Equal(new[] { 1 }, OsIndexes.ListStrides["osMatchString"].Positions);
    }
}
