/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Types;
using Xunit;
using static SLua.ScriptEngine.Tests.AdaptedFunctionTests;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// llFindNotecardTextSync as Second Life's definitions describe it (secondlife/lsl-definitions, func-id 508): the answer is a
/// strided list of [line, column, length, ...], the definitions mark the answer as holding list indices and the start
/// parameter as an index. They do not say which elements of a stride are indices. Line and column are positions in the
/// notecard and count from 1 for an SLua script, as every ll position does; the length is a count and is not shifted. The
/// start parameter counts from 1 for an SLua script. The llcompat table, and an LSL script, keep LSL's numbers. The API
/// underneath always sees LSL's.
/// </summary>
[Collection("native-library")]
public class NotecardSearchBindingTests
{
    // "b+" in "alpha beta bb" / "Beta bbb": line 0 column 6 length 1, line 0 column 11 length 2, line 1 column 5 length 3.
    private static readonly object[] ThreeMatches = { 0, 6, 1, 0, 11, 2, 1, 5, 3 };

    [RequiresNativeFact]
    public void AnSLuaScriptGetsTheLineAndColumnOfEachMatchFromOneAndTheLengthAsItIs()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(ThreeMatches));

        string[] marks = rig.Run("local t = ll.FindNotecardTextSync('card', 'b+', 1, 0, {})\nossl.Mark('t', table.concat(t, ','))");

        Assert.Equal(new[] { "t:1,7,1,1,12,2,2,6,3" }, marks);
    }

    [RequiresNativeFact]
    public void TheStartMatchCountsFromOneForAnSLuaScriptAndTheCountIsPassedAsGiven()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(new object[] { 1, 5, 3 }));

        string[] marks = rig.Run("local t = ll.FindNotecardTextSync('card', 'b+', 3, 0, {})\nossl.Mark('n', #t)");

        Assert.Equal(new[] { "n:3" }, marks);
        Assert.Equal(new[] { "llFindNotecardTextSync(card|b+|2|0|[])" }, rig.Calls().Where(c => !c.StartsWith("NeedsService")).ToArray());
    }

    [RequiresNativeFact]
    public void StartZeroIsAnErrorForAnSLuaScript()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(ThreeMatches));

        string[] marks = rig.Run("local ok, err = pcall(ll.FindNotecardTextSync, 'card', 'b+', 0, 0, {})\nossl.Mark('ok', tostring(ok))");

        Assert.Equal(new[] { "ok:false" }, marks);
        Assert.Empty(rig.Calls().Where(c => !c.StartsWith("NeedsService")).ToArray());
    }

    [RequiresNativeFact]
    public void TheLlcompatTableKeepsLslsNumbersInBothDirections()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(ThreeMatches));

        string[] marks = rig.Run("local t = llcompat.FindNotecardTextSync('card', 'b+', 0, 0, {})\nossl.Mark('t', table.concat(t, ','))");

        Assert.Equal(new[] { "t:0,6,1,0,11,2,1,5,3" }, marks);
        Assert.Equal(new[] { "llFindNotecardTextSync(card|b+|0|0|[])" }, rig.Calls().Where(c => !c.StartsWith("NeedsService")).ToArray());
    }

    [RequiresNativeFact]
    public void ANotCachedAnswerAndNoMatchesComeThroughUnchanged()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(new object[] { "NAK" }));
        string[] nak = rig.Run("local t = ll.FindNotecardTextSync('card', 'b+', 1, 0, {})\nossl.Mark('t', #t .. ':' .. tostring(t[1]))");

        rig.Fake.Returns("llFindNotecardTextSync", new LSLList(new object[0]));
        string[] none = rig.Run("local t = ll.FindNotecardTextSync('card', 'zz', 1, 0, {})\nossl.Mark('t', tostring(#t))");

        Assert.Equal(new[] { "t:1:NAK" }, nak);
        Assert.Equal(new[] { "t:0" }, none);
    }

    [Fact]
    public void ItIsBoundAndNoLongerAGapAndTheDefinitionsMarkItsAnswerAsPositions()
    {
        Assert.Contains(LlBindings.All, f => f.ApiName == "llFindNotecardTextSync");
        Assert.DoesNotContain(LlBindings.Unavailable, n => n == "llFindNotecardTextSync");
        Assert.Contains("return:index", IndexShiftTests.Marks()["llFindNotecardTextSync"]);
        Assert.Equal(new[] { 2 }, LlIndexes.Arguments["llFindNotecardTextSync"]);
        // the strided answer: line and column are positions, the length is not
        Assert.True(LlIndexes.ListStrides.TryGetValue("llFindNotecardTextSync", out ListPositions positions));
        Assert.Equal(3, positions.Stride);
        Assert.Equal(new[] { 0, 1 }, positions.Positions);
    }
}
