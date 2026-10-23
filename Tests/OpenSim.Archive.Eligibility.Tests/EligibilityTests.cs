/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenSim.Archive.Compare;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>The eligibility rules, one part at a time.</summary>
public class EligibilityTests
{
    private static EligibilityResult Evaluate(KitPart[] parts, Dictionary<string, byte[]> assets = null, EligibilityOptions options = null)
    {
        string archive = EligibilityKit.WriteArchive(parts.Select(p => new List<KitPart> { p }).ToList(), assets);
        return new ArchiveEligibility(options).Evaluate(archive);
    }

    private static EligibilityRow Row(EligibilityResult r, KitPart p) => r.Parts.Single(x => x.PartId == p.Id);

    private static EligibilityRow One(KitPart p, Dictionary<string, byte[]> assets = null, EligibilityOptions options = null) => Row(Evaluate(new[] { p }, assets, options), p);

    /// <summary>Gives the part a script with this source and returns the assets that hold it.</summary>
    private static Dictionary<string, byte[]> WithScript(KitPart part, string source)
    {
        var s = EligibilityKit.Script("script", source);
        part.Items.Add(s.item);
        return new Dictionary<string, byte[]> { [s.entry] = s.data };
    }

    private static string ExtraWith(Action<OpenSim.Framework.PrimitiveBaseShape> set)
    {
        OpenSim.Framework.PrimitiveBaseShape s = new OpenSim.Framework.PrimitiveBaseShape();
        set(s);
        return Convert.ToBase64String(s.ExtraParamsToBytes());
    }

    // ------------------------------------------------------------ the rules

    [Fact]
    public void APlainBoxIsConvertedWithItsViewerFaceCount()
    {
        EligibilityRow row = One(new KitPart("Box"));
        Assert.Equal(PartAction.Convert, row.Action);
        Assert.Null(row.Code);
        Assert.Equal(6, row.TargetFaces);
        Assert.Equal(6, row.ServerFaces);
        Assert.Equal(6, row.ViewerFaces);
        Assert.Equal("prim", row.Kind);
        Assert.True(ArchiveEligibility.IsPlaceholder(row.PlaceholderAsset));
        Assert.Empty(row.Warnings);
    }

    [Fact]
    public void AMeshIsSkipped()
    {
        KitPart p = new KitPart("Mesh");
        p.Shape.SculptEntry = true;
        p.Shape.SculptType = 5;
        EligibilityRow row = One(p);
        Assert.Equal(PartAction.Skip, row.Action);
        Assert.Equal(ReasonCodes.AlreadyMesh, row.Code);
        Assert.Equal("mesh", row.Kind);
    }

    [Theory]
    [InlineData(95, ReasonCodes.Plant)]   // grass
    [InlineData(255, ReasonCodes.Plant)]  // tree
    [InlineData(111, ReasonCodes.Plant)]  // new tree
    [InlineData(143, ReasonCodes.OtherPCode)] // particle system
    public void PlantsAndOtherPCodesAreSkipped(int pcode, string code)
    {
        KitPart p = new KitPart("X") { PCode = pcode };
        p.Shape.PCode = pcode;
        EligibilityRow row = One(p);
        Assert.Equal(PartAction.Skip, row.Action);
        Assert.Equal(code, row.Code);
    }

    [Fact]
    public void APartWhoseCreatorRefusedExportIsKept()
    {
        uint export = (uint)PermissionMask.Export;
        KitPart p = new KitPart("Held")
        {
            Owner = "b0b00000-0000-4000-8000-000000000002",
            BaseMask = 2147483647 | export,
            OwnerMask = 2147483647 & ~export,
            EveryoneMask = 0
        };
        EligibilityRow row = One(p);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.ExportDenied, row.Code);
    }

    [Fact]
    public void ExportIsNotRefusedWhenTheOwnerMaskAllowsItOrTheOwnerIsTheCreator()
    {
        uint export = (uint)PermissionMask.Export;
        KitPart allowed = new KitPart("Allowed") { Owner = "b0b00000-0000-4000-8000-000000000002", BaseMask = 2147483647 | export, OwnerMask = 2147483647 | export };
        KitPart noInfo = new KitPart("NoInfo") { Owner = "b0b00000-0000-4000-8000-000000000002", BaseMask = 2147483647 & ~export, OwnerMask = 2147483647 & ~export };
        KitPart own = new KitPart("Own") { BaseMask = 2147483647 | export, OwnerMask = 2147483647 & ~export };
        EligibilityResult r = Evaluate(new[] { allowed, noInfo, own });
        Assert.All(new[] { allowed, noInfo, own }, p => Assert.Equal(PartAction.Convert, Row(r, p).Action));
    }

    [Fact]
    public void APhysicalPartOrAVehicleIsKeptWhateverTheScanSays()
    {
        KitPart physical = new KitPart("Phys") { Flags = "Physics Phantom" };
        KitPart vehicle = new KitPart("Car") { Vehicle = true };
        KitPart phantom = new KitPart("Ghost") { Flags = "Phantom" };
        EligibilityResult r = Evaluate(new[] { physical, vehicle, phantom });
        Assert.Equal(ReasonCodes.Physical, Row(r, physical).Code);
        Assert.Equal(ReasonCodes.Physical, Row(r, vehicle).Code);
        Assert.Contains("vehicle", Row(r, vehicle).Reasons[0].Detail);
        Assert.Equal(PartAction.Convert, Row(r, phantom).Action);
    }

    [Fact]
    public void AFlexiblePartIsKept()
    {
        KitPart p = new KitPart("Rope") { ExtraParams = ExtraWith(s => { s.FlexiEntry = true; s.FlexiTension = 1f; }) };
        EligibilityRow row = One(p);
        Assert.Equal(ReasonCodes.Flexi, row.Code);
    }

    [Fact]
    public void AShapeWithMoreThanEightViewerFacesIsKept()
    {
        KitPart p = new KitPart("Cut") { Shape = FaceMatrix.Shape("box", "hollow+profile-cut-off-grid") };
        EligibilityRow row = One(p);
        Assert.Equal(9, row.ViewerFaces);
        Assert.Equal(ReasonCodes.FacesGt8, row.Code);
        // eight faces is the limit and is converted
        Assert.Equal(PartAction.Convert, One(new KitPart("Eight") { Shape = FaceMatrix.Shape("box", "profile-cut-off-grid") }).Action);
    }

    [Fact]
    public void AScriptThatChangesTheShapeKeepsThePartAndNamesTheScanCode()
    {
        KitPart p = new KitPart("Morph");
        var assets = WithScript(p, "default { state_entry() { llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]); } }");
        EligibilityRow row = One(p, assets);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.ScriptUnsafe, row.Code);
        Assert.NotEmpty(row.ScanCodes);
        Assert.Contains("script", row.Reasons[0].Detail);
    }

    [Fact]
    public void AScriptThatCannotBeReadKeepsItsBuildUnlessToldOtherwise()
    {
        KitPart p = new KitPart("Mystery");
        p.Items.Add(new KitItem { Name = "gone", Asset = Guid.NewGuid(), Type = 10 });
        EligibilityRow row = One(p);
        Assert.Equal(ReasonCodes.ScriptUncertain, row.Code);

        EligibilityResult safe = Evaluate(new[] { p }, null, new EligibilityOptions { AssumeSafeUnscanned = true });
        Assert.Equal(PartAction.Convert, Row(safe, p).Action);
        Assert.True(safe.AssumeSafeUnscanned);
        Assert.Contains("WARNING: --assume-safe-unscanned is ON", EligibilityReport.ToText(safe));
        Assert.DoesNotContain("assume-safe-unscanned is ON", EligibilityReport.ToText(new ArchiveEligibility().Evaluate(EligibilityKit.WriteArchive(p))));
    }

    // a sphere with taper: the server counts 1 side and the viewer 3 faces
    private static KitPart TaperedSphere(string name = "Ball")
    {
        KitPart p = new KitPart(name) { Shape = FaceMatrix.Shape("sphere", "taper") };
        return p;
    }

    [Fact]
    public void ADisagreementOnAPartWhoseScriptsCountFacesKeepsIt()
    {
        KitPart p = TaperedSphere();
        var assets = WithScript(p, "default { state_entry() { integer n = llGetNumberOfSides(); llSay(0, (string)n); } }");
        EligibilityRow row = One(p, assets);
        Assert.Equal(1, row.ServerFaces);
        Assert.Equal(3, row.ViewerFaces);
        Assert.True(row.FaceCountDependent);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.FaceCountMismatch, row.Code);
    }

    [Fact]
    public void ADisagreementWithoutAScriptThatCountsFacesConvertsWithAWarning()
    {
        EligibilityRow row = One(TaperedSphere());
        Assert.Equal(PartAction.Convert, row.Action);
        Assert.Equal(3, row.TargetFaces);
        EligibilityReason w = Assert.Single(row.Warnings);
        Assert.Equal(ReasonCodes.SideCountChanges, w.Code);
        Assert.Contains("3", w.Detail);
    }

    [Fact]
    public void AnAgreementOnAPartWhoseScriptsCountFacesConverts()
    {
        KitPart p = new KitPart("Box");
        var assets = WithScript(p, "default { state_entry() { llSay(0, (string)llGetNumberOfSides()); } }");
        EligibilityRow row = One(p, assets);
        Assert.Equal(PartAction.Convert, row.Action);
        Assert.True(row.FaceCountDependent);
        Assert.Equal(6, row.TargetFaces);
    }

    [Fact]
    public void ASculptWithItsMapInTheArchiveConvertsToOneFace()
    {
        Guid map = Guid.NewGuid();
        KitPart p = new KitPart("Sculpt") { SculptTexture = map };
        p.Shape.SculptEntry = true;
        p.Shape.SculptType = 1;
        p.Shape.SculptTexture = new OpenMetaverse.UUID(map);
        EligibilityRow row = One(p, new Dictionary<string, byte[]> { ["assets/" + map + "_texture.jp2"] = new byte[] { 1, 2, 3 } });
        Assert.Equal(PartAction.Convert, row.Action);
        Assert.Equal("sculpt", row.Kind);
        Assert.Equal(1, row.TargetFaces);
    }

    [Fact]
    public void ASculptWhoseMapIsMissingIsKept()
    {
        Guid map = Guid.NewGuid();
        KitPart p = new KitPart("Sculpt") { SculptTexture = map };
        p.Shape.SculptEntry = true;
        p.Shape.SculptType = 1;
        p.Shape.SculptTexture = new OpenMetaverse.UUID(map);
        EligibilityRow row = One(p);
        Assert.Equal(ReasonCodes.SculptMapMissing, row.Code);
    }

    [Fact]
    public void TheFirstRuleThatAppliesDecidesAndTheOthersAreListed()
    {
        KitPart p = new KitPart("Both") { Flags = "Physics", ExtraParams = ExtraWith(s => s.FlexiEntry = true) };
        EligibilityRow row = One(p);
        Assert.Equal(ReasonCodes.Physical, row.Code);
        Assert.Equal(new[] { ReasonCodes.Physical, ReasonCodes.Flexi }, row.Reasons.Select(x => x.Code));
    }

    [Fact]
    public void AScriptInTheRootThatSetsEveryLinkKeepsTheChildrenToo()
    {
        KitPart root = new KitPart("Root");
        KitPart child = new KitPart("Child", 2);
        var assets = WithScript(root, "default { state_entry() { llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]); } }");
        string archive = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { root, child } }, assets);
        EligibilityResult r = new ArchiveEligibility().Evaluate(archive);
        Assert.All(r.Parts, p => Assert.Equal(PartAction.KeepPrim, p.Action));
    }

    // ------------------------------------------------------------ reports and the plan

    private static EligibilityResult Mixed(out KitPart box, out KitPart kept, out KitPart sphere, out KitPart mesh)
    {
        box = new KitPart("Box");
        kept = new KitPart("Phys") { Flags = "Physics" };
        sphere = TaperedSphere();
        mesh = new KitPart("Mesh");
        mesh.Shape.SculptEntry = true;
        mesh.Shape.SculptType = 5;
        return Evaluate(new[] { box, kept, sphere, mesh });
    }

    [Fact]
    public void TheTextReportStartsWithTheSummaryAndListsKeptAndWarnedParts()
    {
        EligibilityResult r = Mixed(out _, out _, out _, out _);
        string text = EligibilityReport.ToText(r);
        Assert.True(text.IndexOf("SUMMARY", StringComparison.Ordinal) < text.IndexOf("KEPT AS PRIMS", StringComparison.Ordinal));
        Assert.Contains("CONVERT:              2 (1 with a warning)", text);
        Assert.Contains("KEEP-PRIM:            1", text);
        Assert.Contains("SKIP:                 1", text);
        Assert.Contains("KEEP-PRIM (PHYSICAL)", text);
        Assert.Contains("WARN SIDE_COUNT_CHANGES", text);
    }

    [Fact]
    public void TheJsonReportHasOneEntryForEveryPart()
    {
        EligibilityResult r = Mixed(out KitPart box, out _, out KitPart sphere, out _);
        using JsonDocument doc = JsonDocument.Parse(EligibilityReport.ToJson(r));
        Assert.Equal("oar-eligibility", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("parts").GetArrayLength());
        JsonElement s = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, s.GetProperty("convert").GetInt32());
        Assert.Equal(1, s.GetProperty("convertWithWarning").GetInt32());
        JsonElement sphereRow = doc.RootElement.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("part").GetString() == sphere.Id.ToString());
        Assert.Equal("CONVERT", sphereRow.GetProperty("decision").GetString());
        Assert.Equal(3, sphereRow.GetProperty("targetFaces").GetInt32());
        Assert.Equal(1, sphereRow.GetProperty("serverFaces").GetInt32());
        Assert.Equal("SIDE_COUNT_CHANGES", sphereRow.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void TheDraftPlanListsEveryShapeChangeAndEveryMeshAssetAndTheComparerTakesIt()
    {
        EligibilityResult r = Mixed(out KitPart box, out KitPart kept, out KitPart sphere, out KitPart mesh);
        string json = EligibilityReport.ToDraftPlan(r);
        ComparePlan plan = ComparePlan.Parse(json);

        Assert.Equal(2, plan.PartChanges.Count);
        PartChange b = plan.PartChanges.Single(c => c.Part == box.Id.ToString());
        Assert.Equal("shape", b.Field);
        Assert.Equal("mesh", b.To);
        Assert.Equal(6, b.Faces);
        Assert.Equal(3, plan.PartChanges.Single(c => c.Part == sphere.Id.ToString()).Faces);
        Assert.DoesNotContain(plan.PartChanges, c => c.Part == kept.Id.ToString() || c.Part == mesh.Id.ToString());

        Assert.Equal(2, plan.AddedEntries.Count);
        Assert.All(plan.AddedEntries, e => Assert.StartsWith("assets/" + ArchiveEligibility.PlaceholderPrefix, e));
        Assert.All(plan.AddedEntries, e => Assert.EndsWith("_mesh.llmesh", e));
        Assert.Equal(plan.AddedEntries.Count, plan.AddedEntries.Distinct().Count());
    }
}
