/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Archive.ScriptScan.Tests;

// No process-wide state: every test analyses its own script text, so the class runs in parallel with others.
public class AnalyzerTests
{
    private static ScriptAnalysis Run(string body) => LslScriptAnalyzer.Analyze("default { touch_start(integer n) { " + body + " } }");

    private static ScriptHit Single(string body, string code)
    {
        ScriptAnalysis a = Run(body);
        return Assert.Single(a.Hits, h => h.Code == code);
    }

    private static List<string> Codes(string body) => Run(body).Hits.Where(h => h.Level >= HitLevel.Warn).Select(h => h.Code).ToList();

    // ---------------------------------------------------------------- lexer

    [Fact]
    public void Lexer_SkipsCommentsAndKeepsStrings()
    {
        List<LslToken> t = LslLexer.Tokenize("// c1\n/* c2\n x */ a = \"q\\\"w // not a comment\"; b");
        Assert.Equal(new[] { "a", "=", "q\"w // not a comment", ";", "b" }, t.Select(x => x.Text));
        Assert.Equal(3, t[0].Line);
        Assert.Equal(3, t[^1].Line);
    }

    [Fact]
    public void Lexer_Numbers()
    {
        List<LslToken> t = LslLexer.Tokenize("12 0x10 1.5 .5 2e3 1.0f 0xFFFFFFFF");
        Assert.Equal(7, t.Count);
        Assert.Equal(12, t[0].IntValue);
        Assert.Equal(16, t[1].IntValue);
        Assert.Equal(LslTokenKind.Float, t[2].Kind);
        Assert.Equal(0.5, t[3].FloatValue);
        Assert.Equal(2000.0, t[4].FloatValue);
        Assert.Equal(-1, t[6].IntValue);
    }

    [Fact]
    public void Constants_ComeFromTheServerTable()
    {
        Assert.True(LslConstants.TryGet("PRIM_TYPE", out LslValue v));
        Assert.Equal(9, v.Int);
        Assert.True(LslConstants.TryGet("LINK_ALL_OTHERS", out v));
        Assert.Equal(-2, v.Int);
        Assert.True(LslConstants.TryGet("TRUE", out v));
        Assert.Equal(1, v.Int);
        Assert.False(LslConstants.TryGet("not_a_constant", out _));
    }

    // ---------------------------------------------------------------- expressions

    private static LslValue Eval(string expr)
    {
        List<LslToken> t = LslLexer.Tokenize(expr + ")");
        List<LslValue> args = new LslExpressionParser(t, 0).ParseArguments();
        Assert.NotNull(args);
        return Assert.Single(args);
    }

    [Theory]
    [InlineData("LINK_ROOT + 1", 2)]
    [InlineData("-(3 * 4)", -12)]
    [InlineData("(integer)\"7\"", 7)]
    [InlineData("(integer)2.9", 2)]
    [InlineData("1 << 3 | 1", 9)]
    [InlineData("PRIM_TYPE_SCULPT", 7)]
    public void Expression_Folds(string expr, long expected)
    {
        LslValue v = Eval(expr);
        Assert.True(v.IsInt, expr);
        Assert.Equal(expected, v.Int);
    }

    [Theory]
    [InlineData("someVariable")]
    [InlineData("llGetLinkNumber()")]
    [InlineData("n + 1")]
    [InlineData("llList2Integer(l, 0)")]
    public void Expression_VariablesAndCallsAreUnknown(string expr) => Assert.True(Eval(expr).IsUnknown);

    [Fact]
    public void Expression_ListLiteralsAndLiteralConcatenation()
    {
        LslValue l = Eval("[1, <1,2,3>, \"a,b\", PRIM_COLOR] + [<0,0,0,1>, 5.0]");
        Assert.Equal(LslValueKind.List, l.Kind);
        Assert.Equal(6, l.Items.Count);
        Assert.Equal(LslValueKind.Vector, l.Items[1].Kind);
        Assert.Equal(LslValueKind.Rotation, l.Items[4].Kind);
        Assert.True(Eval("[1] + someList").IsUnknown);
    }

    // ---------------------------------------------------------------- rule: shape and sculpt (unsafe)

    [Fact]
    public void PrimType_IsUnsafe()
    {
        ScriptHit h = Single("llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]);", LslScriptAnalyzer.ShapeSet);
        Assert.Equal(HitLevel.Unsafe, h.Level);
        Assert.Equal(LinkKind.Self, h.Target.Kind);
        Assert.Equal("llSetPrimitiveParams", h.Function);
        Assert.Equal(1, h.Line);
    }

    [Fact]
    public void PrimType_Sculpt_IsASwap()
    {
        ScriptHit h = Single("llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_SCULPT, \"tex\", PRIM_SCULPT_TYPE_SPHERE]);", LslScriptAnalyzer.SculptSwap);
        Assert.Equal(HitLevel.Unsafe, h.Level);
    }

    [Fact]
    public void PrimSlice_IsUnsafe() =>
        Assert.Equal(HitLevel.Unsafe, Single("llSetPrimitiveParams([PRIM_SLICE, <0,1,0>]);", LslScriptAnalyzer.SliceSet).Level);

    [Fact]
    public void Flexible_IsUnsafe() =>
        Assert.Equal(HitLevel.Unsafe, Single("llSetPrimitiveParams([PRIM_FLEXIBLE, TRUE, 2, 0.3, 1.0, 0.0, 1.0, <0,0,0>]);", LslScriptAnalyzer.FlexiSet).Level);

    [Fact]
    public void Rules_AfterAnotherRule_AreStillFound()
    {
        // The vector literals contain commas; the arity table keeps the walk in step.
        List<string> codes = Codes("llSetPrimitiveParams([PRIM_COLOR, 0, <1,0,0>, 1.0, PRIM_TEXTURE, 1, \"t\", <1,1,0>, <0,0,0>, 0.0, PRIM_SLICE, <0,1,0>]);");
        Assert.Equal(new[] { LslScriptAnalyzer.SliceSet }, codes);
    }

    [Fact]
    public void ShapeMentionedInStringOrComment_IsNotAHit()
    {
        ScriptAnalysis a = LslScriptAnalyzer.Analyze(
            "// llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX]);\n/* llSetPrimitiveParams([PRIM_SLICE, <0,1,0>]); */\ndefault { state_entry() { llSay(0, \"llSetPrimitiveParams([PRIM_TYPE])\"); } }");
        Assert.Empty(a.Hits);
        Assert.Equal(0, a.CallsSeen);
    }

    [Fact]
    public void ColorAndTextureRules_AreNotShapeChanges()
    {
        Assert.Empty(Codes("llSetPrimitiveParams([PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0, PRIM_FULLBRIGHT, 2, TRUE, PRIM_GLOW, 3, 0.1]);"));
    }

    // ---------------------------------------------------------------- rule: faces

    [Theory]
    [InlineData("llSetTexture(\"t\", 8);", "face 8")]
    [InlineData("llSetColor(<1,0,0>, 9);", "face 9")]
    [InlineData("llSetAlpha(0.5, 12);", "face 12")]
    [InlineData("llScaleTexture(1.0, 1.0, 8);", "face 8")]
    [InlineData("llSetLinkTexture(LINK_THIS, \"t\", 8);", "face 8")]
    [InlineData("llSetPrimitiveParams([PRIM_TEXTURE, 8, \"t\", <1,1,0>, <0,0,0>, 0.0]);", "face 8")]
    [InlineData("llSetPrimitiveParams([PRIM_COLOR, 7 + 1, <1,0,0>, 1.0]);", "face 8")]
    public void LiteralFaceOfEightOrMore_IsUnsafe(string body, string detail)
    {
        ScriptHit h = Single(body, LslScriptAnalyzer.FaceGt7);
        Assert.Equal(HitLevel.Unsafe, h.Level);
        Assert.Equal(detail, h.Detail);
    }

    [Theory]
    [InlineData("llSetTexture(\"t\", 7);")]
    [InlineData("llSetColor(<1,0,0>, ALL_SIDES);")]
    [InlineData("llSetAlpha(0.5, 0);")]
    [InlineData("llSetPrimitiveParams([PRIM_TEXTURE, 3, \"t\", <1,1,0>, <0,0,0>, 0.0]);")]
    [InlineData("llGetTexture(2);")]
    public void LiteralFaceBelowEight_IsSafeAndNotedForTheFaceCountCheck(string body)
    {
        ScriptAnalysis a = Run(body);
        Assert.DoesNotContain(a.Hits, h => h.Level >= HitLevel.Warn);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.FaceIndexed);
    }

    [Fact]
    public void VariableFace_IsOnlyANote()
    {
        ScriptAnalysis a = Run("integer i; for (i = 0; i < llGetNumberOfSides(); i++) llSetColor(<1,0,0>, i);");
        Assert.DoesNotContain(a.Hits, h => h.Level >= HitLevel.Warn);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.FaceVariable);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.SidesQuery);
    }

    [Fact]
    public void NumberOfSides_IsANoteOnItsTarget()
    {
        ScriptHit h = Single("integer n = llGetLinkNumberOfSides(3);", LslScriptAnalyzer.SidesQuery);
        Assert.Equal(HitLevel.Note, h.Level);
        Assert.Equal(new LinkTarget(LinkKind.Literal, 3), h.Target);
    }

    // ---------------------------------------------------------------- rule: link targets

    [Theory]
    [InlineData("LINK_SET", LinkKind.Set, 0)]
    [InlineData("LINK_ALL_OTHERS", LinkKind.AllOthers, 0)]
    [InlineData("LINK_ALL_CHILDREN", LinkKind.AllChildren, 0)]
    [InlineData("LINK_THIS", LinkKind.Self, 0)]
    [InlineData("LINK_ROOT", LinkKind.Root, 1)]
    [InlineData("0", LinkKind.Root, 0)]
    [InlineData("4", LinkKind.Literal, 4)]
    [InlineData("LINK_ROOT + 2", LinkKind.Literal, 3)]
    [InlineData("someLink", LinkKind.Unknown, 0)]
    [InlineData("llGetLinkNumber() + 1", LinkKind.Unknown, 0)]
    public void LinkArgument_IsResolved(string link, LinkKind kind, int number)
    {
        ScriptHit h = Single("llSetLinkPrimitiveParams(" + link + ", [PRIM_SLICE, <0,1,0>]);", LslScriptAnalyzer.SliceSet);
        Assert.Equal(kind, h.Target.Kind);
        Assert.Equal(number, h.Target.Number);
    }

    [Fact]
    public void PrimLinkTarget_ChangesTheTargetOfTheRulesThatFollow()
    {
        ScriptAnalysis a = Run("llSetPrimitiveParams([PRIM_COLOR, 0, <1,0,0>, 1.0, PRIM_LINK_TARGET, 5, PRIM_SLICE, <0,1,0>, PRIM_LINK_TARGET, LINK_SET, PRIM_FLEXIBLE, TRUE, 2, 0.3, 1.0, 0.0, 1.0, <0,0,0>]);");
        Assert.Equal(new LinkTarget(LinkKind.Literal, 5), a.Hits.Single(h => h.Code == LslScriptAnalyzer.SliceSet).Target);
        Assert.Equal(LinkKind.Set, a.Hits.Single(h => h.Code == LslScriptAnalyzer.FlexiSet).Target.Kind);
    }

    [Fact]
    public void LinkTextureCalls_CarryTheirLinkArgument()
    {
        ScriptHit h = Single("llSetLinkColor(6, <1,0,0>, 9);", LslScriptAnalyzer.FaceGt7);
        Assert.Equal(new LinkTarget(LinkKind.Literal, 6), h.Target);
    }

    // ---------------------------------------------------------------- uncertain

    [Theory]
    [InlineData("llSetPrimitiveParams(rules);")]
    [InlineData("llSetPrimitiveParams(llParseString2List(msg, [\",\"], []));")]
    [InlineData("llSetLinkPrimitiveParamsFast(LINK_SET, base + [PRIM_COLOR, 0, <1,0,0>, 1.0]);")]
    [InlineData("llSetPrimitiveParams([PRIM_COLOR, 0, <1,0,0>, 1.0] + extra);")]
    [InlineData("llSetPrimitiveParams([code, 0]);")]
    [InlineData("llSetPrimitiveParams([PRIM_TYPE, kind]);")]
    public void UnknownParameterList_IsUncertain(string body)
    {
        ScriptAnalysis a = Run(body);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown && h.Level == HitLevel.Uncertain);
    }

    [Fact]
    public void UnknownParameterList_InLinkMessage_SaysSo()
    {
        ScriptAnalysis a = LslScriptAnalyzer.Analyze("default { link_message(integer s, integer n, string m, key id) { llSetLinkPrimitiveParams(n, llParseString2List(m, [\"|\"], [])); } }");
        ScriptHit h = a.Hits.Single(x => x.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Equal("in link_message", h.Detail);
        Assert.Equal(LinkKind.Unknown, h.Target.Kind);
    }

    [Fact]
    public void UnparseableArguments_AreUncertain()
    {
        ScriptAnalysis a = Run("llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, 0 @@ 5]);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
    }

    [Fact]
    public void UserFunctionContext_IsTracked()
    {
        ScriptAnalysis a = LslScriptAnalyzer.Analyze("setShape(list p) { llSetPrimitiveParams(p); }\ndefault { state_entry() { setShape([PRIM_SLICE, <0,1,0>]); } }");
        Assert.Single(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
    }

    [Fact]
    public void OsSetPrimitiveParams_NamesAnotherObject()
    {
        ScriptHit h = Single("osSetPrimitiveParams(key_of_other, [PRIM_SLICE, <0,1,0>]);", LslScriptAnalyzer.SliceSet);
        Assert.Equal(LinkKind.OtherObject, h.Target.Kind);
    }

    // ---------------------------------------------------------------- warnings

    [Fact]
    public void ReadingPrimTypeBack_IsAWarning()
    {
        ScriptHit h = Single("list l = llGetPrimitiveParams([PRIM_TYPE]);", LslScriptAnalyzer.Readback);
        Assert.Equal(HitLevel.Warn, h.Level);
        Assert.Equal("PRIM_TYPE", h.Detail);
        Assert.Equal(new LinkTarget(LinkKind.Literal, 3), Single("list l = llGetLinkPrimitiveParams(3, [PRIM_TYPE]);", LslScriptAnalyzer.Readback).Target);
    }

    [Theory]
    [InlineData("llSetVehicleType(VEHICLE_TYPE_CAR);")]
    [InlineData("llSetVehicleFloatParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, 2.0);")]
    [InlineData("llSetStatus(STATUS_PHYSICS, TRUE);")]
    [InlineData("llSetPrimitiveParams([PRIM_PHYSICS, TRUE]);")]
    [InlineData("llSetPrimitiveParams([PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_NONE]);")]
    public void PhysicsAndVehicleCalls_AreWarnings(string body)
    {
        ScriptAnalysis a = Run(body);
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.PhysicsCall);
        Assert.Equal(HitLevel.Warn, h.Level);
    }

    [Fact]
    public void SetStatus_WithoutPhysics_IsNothing() =>
        Assert.Empty(Run("llSetStatus(STATUS_PHANTOM, TRUE);").Hits);

    [Fact]
    public void RepeatedCalls_AreCountedOnce()
    {
        ScriptAnalysis a = Run("llSetColor(<1,0,0>, 9); llSetColor(<0,1,0>, 9); llSetColor(<0,0,1>, 9);");
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.FaceGt7);
        Assert.Equal(3, h.Count);
    }

    // ---------------------------------------------------------------- list variables

    private static ScriptAnalysis Script(string text) => LslScriptAnalyzer.Analyze(text);

    private static List<string> Serious(ScriptAnalysis a) => a.Hits.Where(h => h.Level >= HitLevel.Warn).Select(h => h.Code).ToList();

    private const string LightBoard = @"
vector ON_COLOUR = <1.0, 0.9, 0.4>;
vector OFF_COLOUR = <0.25, 0.2, 0.1>;
float GLOW = 0.3;
integer LAMPS = 4;
show(integer mask)
{
    list params = [];
    integer lamp;
    for (lamp = 0; lamp < LAMPS; ++lamp)
    {
        integer lit = (mask >> lamp) & 1;
        vector colour = OFF_COLOUR;
        float glow = 0.0;
        if (lit) { colour = ON_COLOUR; glow = GLOW; }
        params += [PRIM_LINK_TARGET, lamp + 2,
                   PRIM_COLOR, ALL_SIDES, colour, 1.0,
                   PRIM_FULLBRIGHT, ALL_SIDES, lit,
                   PRIM_GLOW, ALL_SIDES, glow];
    }
    llSetLinkPrimitiveParamsFast(LINK_THIS, params);
}
default { state_entry() { show(0); } }";

    [Fact]
    public void ListVariable_LightBoardPattern_IsReadLikeALiteral()
    {
        ScriptAnalysis a = Script(LightBoard);
        Assert.DoesNotContain(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Empty(Serious(a));
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.FaceIndexed);
    }

    [Fact]
    public void ListVariable_PrimTypeThroughVariable_IsUnsafe()
    {
        ScriptAnalysis a = Run("list p = [PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]; llSetPrimitiveParams(p);");
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.ShapeSet);
        Assert.Equal(HitLevel.Unsafe, h.Level);
        Assert.Equal(LinkKind.Self, h.Target.Kind);
        Assert.DoesNotContain(a.Hits, x => x.Code == LslScriptAnalyzer.ParamsUnknown);
    }

    [Fact]
    public void ListVariable_FromListReplaceList_StaysUncertainAndNamesTheSource()
    {
        ScriptAnalysis a = Run("list p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; p = llListReplaceList(p, [<0,1,0>], 2, 2); llSetPrimitiveParams(p);");
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Equal(HitLevel.Uncertain, h.Level);
        Assert.Contains("llListReplaceList", h.Detail);
    }

    [Fact]
    public void ListVariable_FromParameter_StaysUncertainAndSaysSo()
    {
        ScriptAnalysis a = Script("go(list p) { llSetPrimitiveParams(p); }\ndefault { state_entry() { go([PRIM_SLICE, <0,1,0>]); } }");
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Contains("parameter", h.Detail);
    }

    [Fact]
    public void ListVariable_BuiltInBranches_IsUncertainUnlessEveryBranchIsFollowed()
    {
        ScriptAnalysis unknown = Run("list p; if (n > 1) p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; else p = llList2List(extra, 0, 3); llSetPrimitiveParams(p);");
        Assert.Contains(unknown.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown && h.Level == HitLevel.Uncertain);

        ScriptAnalysis followed = Run("list p; if (n > 1) p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; else p = [PRIM_SLICE, <0,1,0>]; llSetPrimitiveParams(p);");
        Assert.DoesNotContain(followed.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Contains(followed.Hits, h => h.Code == LslScriptAnalyzer.SliceSet && h.Level == HitLevel.Unsafe);
    }

    [Fact]
    public void ListVariable_ConcatenatedAcrossStatements_FindsTheRuleInTheLastPiece()
    {
        ScriptAnalysis a = Run(
            "list p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; p += [PRIM_FULLBRIGHT, 0, TRUE]; list q = p + [PRIM_FLEXIBLE, TRUE, 1, 0.5, 0.5, 0.5, 0.5, <0,0,0>]; q = q + p; llSetPrimitiveParams(q);");
        Assert.DoesNotContain(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.FlexiSet && h.Level == HitLevel.Unsafe);
    }

    [Fact]
    public void ListVariable_GlobalAssignedInStateEntry_IsFollowed()
    {
        ScriptAnalysis a = Script("list rules;\ndefault { state_entry() { rules = [PRIM_SLICE, <0,0.5,0>]; } touch_start(integer n) { llSetPrimitiveParams(rules); } }");
        Assert.DoesNotContain(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.SliceSet && h.Level == HitLevel.Unsafe);
    }

    [Fact]
    public void ListVariable_GlobalAlsoSetFromAnUnknownSource_IsUncertain()
    {
        ScriptAnalysis a = Script("list rules = [PRIM_COLOR, 0, <1,0,0>, 1.0];\ndefault { link_message(integer s, integer n, string m, key id) { rules = llParseString2List(m, [X], []); } touch_start(integer n) { llSetPrimitiveParams(rules); } }");
        ScriptHit h = Assert.Single(a.Hits, x => x.Code == LslScriptAnalyzer.ParamsUnknown);
        Assert.Contains("llParseString2List", h.Detail);
    }

    [Fact]
    public void ListVariable_LocalHidesAGlobalOfTheSameName()
    {
        ScriptAnalysis a = Script("list p = [PRIM_SLICE, <0,1,0>];\nsafe() { list p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; llSetPrimitiveParams(p); }\ndefault { state_entry() { safe(); } }");
        Assert.Empty(Serious(a));
    }

    [Fact]
    public void ListVariable_SetInsideAnExpression_IsUncertain()
    {
        ScriptAnalysis a = Run("list p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; integer k = llGetListLength(p = [PRIM_SLICE, <0,1,0>]); llSetPrimitiveParams(p);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown && h.Level == HitLevel.Uncertain);
    }

    [Fact]
    public void ListVariable_PieceThatEndsInsideARule_IsUncertain()
    {
        ScriptAnalysis a = Run("list p = [PRIM_COLOR]; p += [0, <1,0,0>, 1.0]; llSetPrimitiveParams(p);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown && h.Detail.Contains("inside a rule"));
    }

    [Fact]
    public void ListVariable_RuleCodeThatIsAVariable_IsUncertain()
    {
        ScriptAnalysis a = Run("integer code = PRIM_TYPE; list p = [code, PRIM_TYPE_BOX]; llSetPrimitiveParams(p);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.ParamsUnknown && h.Level == HitLevel.Uncertain);
    }

    [Fact]
    public void ListVariable_LinkTargetSetInAnEarlierPiece_ReachesTheLaterRule()
    {
        ScriptAnalysis a = Run("list p = [PRIM_LINK_TARGET, 2]; p += [PRIM_SLICE, <0,1,0>]; llSetLinkPrimitiveParamsFast(LINK_THIS, p);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.SliceSet && h.Target.Equals(new LinkTarget(LinkKind.Literal, 2)));
    }

    [Fact]
    public void ListVariable_SelfReferencingConcatenation_Terminates()
    {
        ScriptAnalysis a = Run("list p = [PRIM_COLOR, 0, <1,0,0>, 1.0]; list q = p; p = p + q; q = p + q; llSetPrimitiveParams(q);");
        Assert.Empty(Serious(a));
    }

    [Fact]
    public void ListVariable_ReadBackList_IsReadLikeALiteral()
    {
        ScriptAnalysis a = Run("list want = [PRIM_TYPE]; list got = llGetPrimitiveParams(want);");
        Assert.Contains(a.Hits, h => h.Code == LslScriptAnalyzer.Readback && h.Level == HitLevel.Warn);
    }
}
