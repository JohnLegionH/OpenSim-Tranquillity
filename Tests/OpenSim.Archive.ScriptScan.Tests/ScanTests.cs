/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static OpenSim.Archive.ScriptScan.Tests.ScanKit;

namespace OpenSim.Archive.ScriptScan.Tests;

// No process-wide state: every test writes its own archive file, so the class runs in parallel with others.
public class ScanTests
{
    private static string Wrap(string body) => "default { touch_start(integer n) { " + body + " } }";

    private static ScanResult Scan(string path, ScanOptions options = null) => new ArchiveScriptScanner(options).Scan(path);

    [Fact]
    public void BuildWithoutScripts_IsConvertible()
    {
        List<KitPart> b = Build("Plain", 2);
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(3, r.Parts.Count);
        Assert.All(r.Parts, p => Assert.Equal(PartDecision.Convertible, p.Decision));
        Assert.False(r.AnyKept);
        Assert.Equal("oar", r.ArchiveKind);
    }

    [Fact]
    public void ShapeChangeInOwnPart_KeepsThatPartOnly()
    {
        List<KitPart> b = Build("Shifter", 2);
        b[1].Script(Wrap("llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]);"), "morph");
        ScanResult r = Scan(WriteOar(b));

        PartRow hit = Row(r, b[1]);
        Assert.Equal(PartDecision.KeepPrimUnsafe, hit.Decision);
        PartReason reason = Assert.Single(hit.Reasons);
        Assert.Equal("SHAPE_SET", reason.Code);
        Assert.Equal("morph", reason.Script);
        Assert.Equal(1, reason.Line);
        Assert.Equal("llSetPrimitiveParams", reason.Function);
        Assert.Equal(PartDecision.Convertible, Row(r, b[0]).Decision);
        Assert.Equal(PartDecision.Convertible, Row(r, b[2]).Decision);
        Assert.Equal(1, r.ReasonCounts["SHAPE_SET"]);
    }

    [Fact]
    public void LiteralLinkNumber_ReachesOnlyThatPart()
    {
        List<KitPart> b = Build("Remote", 3);
        b[0].Script(Wrap("llSetLinkPrimitiveParams(3, [PRIM_SLICE, <0,1,0>]);"));
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(PartDecision.KeepPrimUnsafe, Row(r, b[2]).Decision);
        Assert.Equal("link 3", Row(r, b[2]).Reasons.Single().Reach);
        Assert.Equal(PartDecision.Convertible, Row(r, b[0]).Decision);
        Assert.Equal(PartDecision.Convertible, Row(r, b[1]).Decision);
        Assert.Equal(PartDecision.Convertible, Row(r, b[3]).Decision);
    }

    [Theory]
    [InlineData("LINK_SET", new[] { true, true, true, true })]
    [InlineData("LINK_ALL_OTHERS", new[] { true, true, true, false })]
    [InlineData("LINK_ALL_CHILDREN", new[] { false, true, true, true })]
    [InlineData("LINK_ROOT", new[] { true, false, false, false })]
    [InlineData("LINK_THIS", new[] { false, false, false, true })]
    public void LinkConstants_ReachTheRightParts(string link, bool[] expected)
    {
        // The script is in the last part (a child), so LINK_THIS is that part and LINK_ALL_OTHERS leaves it out.
        List<KitPart> b = Build("Group", 3);
        b[3].Script(Wrap("llSetLinkPrimitiveParams(" + link + ", [PRIM_SLICE, <0,1,0>]);"));
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(expected, new[] { PartDecisions(r, b, 0), PartDecisions(r, b, 1), PartDecisions(r, b, 2), PartDecisions(r, b, 3) });
    }

    private static bool PartDecisions(ScanResult r, List<KitPart> b, int i) => PartDecisionNames.IsKept(Row(r, b[i]).Decision);

    [Fact]
    public void UnknownLinkWithShapeRule_MakesEveryPartOfTheBuildUncertain()
    {
        List<KitPart> b = Build("Loop", 2);
        b[1].Script(Wrap("integer i; for (i = 1; i <= llGetNumberOfPrims(); i++) llSetLinkPrimitiveParams(i, [PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]);"));
        ScanResult r = Scan(WriteOar(b));
        Assert.All(r.Parts, p => Assert.Equal(PartDecision.KeepPrimUncertain, p.Decision));
        Assert.All(r.Parts, p => Assert.Contains(p.Reasons, x => x.Code == "SHAPE_SET" && x.Reach == "unknown link" && !x.Literal));
    }

    [Fact]
    public void UnknownParameterList_InLinkMessage_MakesTheBuildUncertain_ButOtherBuildsAreNot()
    {
        List<KitPart> a = Build("Driven", 1);
        a[1].Script("default { link_message(integer s, integer n, string m, key id) { llSetLinkPrimitiveParams(LINK_SET, llParseString2List(m, [\"|\"], [])); } }");
        List<KitPart> other = Build("Other", 1);
        ScanResult r = Scan(WriteOar(a, other));
        Assert.All(a, p => Assert.Equal(PartDecision.KeepPrimUncertain, Row(r, p).Decision));
        Assert.All(other, p => Assert.Equal(PartDecision.Convertible, Row(r, p).Decision));
        Assert.Contains(Row(r, a[0]).Reasons, x => x.Code == "PARAMS_UNKNOWN" && x.Detail == "in link_message");
    }

    [Fact]
    public void UnknownParameterList_ForOwnPartOnly_IsUncertainForThatPartOnly()
    {
        List<KitPart> b = Build("Own", 2);
        b[2].Script(Wrap("llSetPrimitiveParams(rules);"));
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(PartDecision.KeepPrimUncertain, Row(r, b[2]).Decision);
        Assert.Equal(PartDecision.Convertible, Row(r, b[0]).Decision);
    }

    [Fact]
    public void MissingScriptSource_KeepsTheWholeBuild_UnlessAssumedSafe()
    {
        List<KitPart> b = Build("Gap", 2);
        KitScript s = b[1].Script(null, "lost");
        List<KitPart> other = Build("Fine", 1);
        string path = WriteOar(b, other);

        ScanResult r = Scan(path);
        Assert.All(b, p => Assert.Equal(PartDecision.KeepPrimUncertain, Row(r, p).Decision));
        Assert.All(b, p => Assert.Equal("SCRIPT_UNSCANNED", Row(r, p).Reasons.Single().Code));
        Assert.All(other, p => Assert.Equal(PartDecision.Convertible, Row(r, p).Decision));
        UnscannedScript u = Assert.Single(r.Unscanned);
        Assert.Equal("lost", u.Script);
        Assert.Equal("asset missing from the archive", u.Reason);
        Assert.Equal(s.Asset, u.AssetId);

        ScanResult assumed = Scan(path, new ScanOptions { AssumeSafeUnscanned = true });
        Assert.All(assumed.Parts, p => Assert.Equal(PartDecision.Convertible, p.Decision));
        Assert.Single(assumed.Unscanned);
    }

    [Fact]
    public void BytecodeOnly_IsUnscanned()
    {
        List<KitPart> b = Build("Compiled", 0);
        b[0].Scripts.Add(new KitScript { Name = "old", AssetType = 11, Source = "binary" });
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal("compiled bytecode only, no source", Assert.Single(r.Unscanned).Reason);
        Assert.Equal(PartDecision.KeepPrimUncertain, Row(r, b[0]).Decision);
    }

    [Fact]
    public void WarningsOnly_ConvertWithWarnings()
    {
        List<KitPart> b = Build("Cart", 1);
        b[0].Script(Wrap("llSetVehicleType(VEHICLE_TYPE_CAR); list t = llGetPrimitiveParams([PRIM_TYPE]);"));
        ScanResult r = Scan(WriteOar(b));
        PartRow root = Row(r, b[0]);
        Assert.Equal(PartDecision.ConvertibleWithWarnings, root.Decision);
        Assert.Equal(new[] { "PHYSICS_CALL", "READBACK" }, root.Reasons.Select(x => x.Code).OrderBy(x => x));
        Assert.False(r.AnyKept);
    }

    [Fact]
    public void FaceCalls_SafeBelowEight_AndFlaggedForTheFaceCountCheck()
    {
        List<KitPart> b = Build("Faces", 1);
        b[0].Script(Wrap("llSetColor(<1,0,0>, 3); llSetLinkAlpha(2, 0.5, 7); integer n = llGetNumberOfSides();"));
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(PartDecision.Convertible, Row(r, b[0]).Decision);
        Assert.True(Row(r, b[0]).FaceCountDependent);
        Assert.Equal(PartDecision.Convertible, Row(r, b[1]).Decision);
        Assert.True(Row(r, b[1]).FaceCountDependent);
    }

    [Fact]
    public void FaceEightOrMore_ThroughALink_KeepsTheTarget()
    {
        List<KitPart> b = Build("Wide", 1);
        b[0].Script(Wrap("llSetLinkTexture(2, \"t\", 8);"));
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(PartDecision.KeepPrimUnsafe, Row(r, b[1]).Decision);
        Assert.Equal("FACE_GT7", Row(r, b[1]).Reasons.Single().Code);
        Assert.Equal(PartDecision.Convertible, Row(r, b[0]).Decision);
    }

    [Fact]
    public void SameScriptAssetInManyParts_IsReadOnceAndReportedPerHost()
    {
        List<KitPart> b = Build("Copies", 2);
        KitScript first = b[1].Script(Wrap("llSetPrimitiveParams([PRIM_SLICE, <0,1,0>]);"), "a");
        b[2].Scripts.Add(new KitScript { Name = "b", Asset = first.Asset, Source = null });
        ScanResult r = Scan(WriteOar(b));
        Assert.Equal(1, r.ScriptAssetsRead);
        Assert.Equal(2, r.ScriptItems);
        Assert.Equal(PartDecision.KeepPrimUnsafe, Row(r, b[1]).Decision);
        Assert.Equal(PartDecision.KeepPrimUnsafe, Row(r, b[2]).Decision);
        Assert.Empty(r.Unscanned);
    }

    [Fact]
    public void TextAndJsonReports_CarryTheDecisions()
    {
        List<KitPart> b = Build("Report", 1);
        b[1].Script(Wrap("llSetPrimitiveParams([PRIM_SLICE, <0,1,0>]);"), "slicer");
        ScanResult r = Scan(WriteOar(b));

        string text = ScanReport.ToText(r);
        Assert.StartsWith("oar-scan:", text);
        Assert.True(text.IndexOf("SUMMARY", StringComparison.Ordinal) < text.IndexOf("KEPT AS PRIMS", StringComparison.Ordinal));
        Assert.Contains("KEEP-PRIM (UNSAFE) | Report", text);
        Assert.Contains("SLICE_SET in script 'slicer' line 1", text);
        Assert.Contains("WHAT THIS SCAN CANNOT SEE", text);

        using JsonDocument doc = JsonDocument.Parse(ScanReport.ToJson(r));
        JsonElement root = doc.RootElement;
        Assert.Equal("oar-scan", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("parts").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("keepPrimUnsafe").GetInt32());
        JsonElement kept = root.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("keepPrim").GetBoolean());
        Assert.Equal(b[1].Id.ToString(), kept.GetProperty("partId").GetString());
        Assert.Equal("keep-prim-unsafe", kept.GetProperty("decisionCode").GetString());
        JsonElement reason = kept.GetProperty("reasons")[0];
        Assert.Equal("SLICE_SET", reason.GetProperty("code").GetString());
        Assert.Equal("slicer", reason.GetProperty("script").GetString());
        Assert.Equal(1, reason.GetProperty("line").GetInt32());
        Assert.True(reason.GetProperty("literal").GetBoolean());
    }

    [Fact]
    public void ScanNeverChangesTheArchive()
    {
        List<KitPart> b = Build("Readonly", 1);
        b[0].Script(Wrap("llSetPrimitiveParams([PRIM_SLICE, <0,1,0>]);"));
        string path = WriteOar(b);
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Scan(path);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void NotAnArchive_Throws()
    {
        string path = TempFile(".oar");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Throws<ScanException>(() => Scan(path));
    }
}
