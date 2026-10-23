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
}
