/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The merge planner on hand-made linksets: which parts merge and which stay as they are, and the rule that keeps each one.
/// No process-wide state: every test builds its own linkset, so the class runs in parallel with others.
/// </summary>
public class MergePlannerTests
{
    private static MergePart Part(int link, float x = 0, float size = 1f, string texture = "tex-a", int vertices = 24)
    {
        MergePart p = new MergePart
        {
            PartId = Guid.NewGuid(), LinkNum = link, IsRoot = link == 1, Name = "Part " + link, Creator = "creator-1", Masks = "m1",
            PhysicsShapeType = 0, PhysicsVertices = 8,
            BoxMin = new[] { x, 0f, 0f }, BoxMax = new[] { x + size, size, size }
        };
        p.Faces.Add(new MergeFace { TextureId = texture, Vertices = vertices });
        return p;
    }

    private static MergeLinkset Set(params MergePart[] parts)
    {
        MergeLinkset l = new MergeLinkset { Id = Guid.NewGuid(), Name = "Set", Entry = "objects/set.xml" };
        l.Parts.AddRange(parts);
        return l;
    }

    private static LinksetPlan Plan(MergeLinkset l, MergeOptions o = null) => MergePlanner.PlanLinkset(l, o ?? new MergeOptions());

    private static KeptPartPlan Kept(LinksetPlan plan, MergePart p) => Assert.Single(plan.Kept, k => k.PartId == p.PartId);

    private static bool HasRule(LinksetPlan plan, MergePart p, string rule, string code) =>
        Kept(plan, p).Rules.Any(r => r.Rule == rule && r.Code == code);

    // ---------------------------------------------------------------- the basic merge

    [Fact]
    public void PlainPartsOfOneLinksetMergeIntoOnePart()
    {
        MergeLinkset l = Set(Part(1), Part(2, 1), Part(3, 2));
        LinksetPlan plan = Plan(l);
        MergedPartPlan m = Assert.Single(plan.Merged);
        Assert.Equal(3, m.Members.Count);
        Assert.Empty(plan.Kept);
        Assert.Equal(3, plan.PartsBefore);
        Assert.Equal(1, plan.PartsAfter);
    }

    [Fact]
    public void PartsWithTheSameMaterialShareOneSlotAndDifferentTexturesGetTheirOwn()
    {
        LinksetPlan plan = Plan(Set(Part(1, 0, 1, "tex-a"), Part(2, 1, 1, "tex-a"), Part(3, 2, 1, "tex-b")));
        MergedPartPlan m = Assert.Single(plan.Merged);
        Assert.Equal(2, m.Slots.Count);
        Assert.Equal(new[] { 2, 1 }, m.Slots.Select(s => s.SourceFaces));
    }

    [Fact]
    public void ABakeableFaceKeepsOneSlotWhateverItsRepeatsAndAnUnbakeableFaceDoesNot()
    {
        MergePart a = Part(1), b = Part(2, 1);
        b.Faces[0].Transform = new FaceTransform(4, 4, 0.25f, 0, 0);
        Assert.Single(Assert.Single(Plan(Set(a, b)).Merged).Slots);

        MergePart c = Part(1), d = Part(2, 1);
        c.Faces[0].Bakeable = false;
        d.Faces[0].Bakeable = false;
        d.Faces[0].Transform = new FaceTransform(4, 4, 0.25f, 0, 0);
        Assert.Equal(2, Assert.Single(Plan(Set(c, d)).Merged).Slots.Count);
    }

    [Fact]
    public void ALinksetNeverNeedsMoreThanEightFacesPerMergedPart()
    {
        MergePart[] parts = Enumerable.Range(1, 20).Select(i => Part(i, i * 0.5f, 0.5f, "tex-" + i)).ToArray();
        LinksetPlan plan = Plan(Set(parts));
        Assert.True(plan.Merged.Count >= 3);
        Assert.All(plan.Merged, m => Assert.True(m.Slots.Count <= 8, m.Slots.Count + " slots"));
        Assert.Equal(20, plan.Merged.Sum(m => m.Members.Count) + plan.Kept.Count);
    }

    [Fact]
    public void AMaterialWithMoreThanTheFaceVertexLimitUsesMoreThanOneSlot()
    {
        MergeOptions o = new MergeOptions { MaxFaceVertices = 100 };
        // six faces of ten vertices each: a single face of 60 against a limit of 100 would not be a mesh face the planner can count on
        MergePart a = Part(1, 0, 1, "t", 10), b = Part(2, 1, 1, "t", 10);
        foreach (MergePart p in new[] { a, b })
            for (int i = 0; i < 5; i++)
                p.Faces.Add(new MergeFace { TextureId = "t", Vertices = 10 });
        LinksetPlan plan = Plan(Set(a, b), o);
        MergedPartPlan m = Assert.Single(plan.Merged);
        Assert.Equal(2, m.Slots.Count);
        Assert.All(m.Slots, s => Assert.True(s.Vertices <= 100));
    }

    [Fact]
    public void PhysicsVerticesOfAMergedPartStayWithinTheLimit()
    {
        MergeOptions o = new MergeOptions { MaxPhysicsVertices = 20 };
        MergePart[] parts = Enumerable.Range(1, 6).Select(i => Part(i, i * 0.5f, 0.5f)).ToArray();
        foreach (MergePart p in parts) p.PhysicsVertices = 8;
        LinksetPlan plan = Plan(Set(parts), o);
        Assert.True(plan.Merged.Count >= 2);
        Assert.All(plan.Merged, m => Assert.True(m.PhysicsVertices <= 20, m.PhysicsVertices + " physics vertices"));
    }

    [Fact]
    public void LinkNumbersAfterTheMergeAreContiguousFromOneAndTheRootIsOne()
    {
        MergePart script = Part(3, 2);
        script.Scripted = true;
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), script, Part(4, 3, 1, "tex-z")));
        List<int> numbers = plan.Merged.Select(m => m.NewLinkNum).Concat(plan.Kept.Select(k => k.NewLinkNum)).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
        Assert.Equal(1, plan.Merged.Single(m => m.Members.Any(x => x.LinkNum == 1)).NewLinkNum);
    }

    [Fact]
    public void ThePlanIsTheSameOnEveryRun()
    {
        MergePart[] a = Enumerable.Range(1, 9).Select(i => Part(i, i, 1, "tex-" + (i % 3))).ToArray();
        Guid[] ids = a.Select(p => p.PartId).ToArray();
        LinksetPlan p1 = Plan(Set(a));
        MergeLinkset l2 = Set(a.Reverse().ToArray());
        LinksetPlan p2 = Plan(l2);
        Assert.Equal(p1.Merged.Select(m => m.Id), p2.Merged.Select(m => m.Id));
        Assert.Equal(p1.Merged.Select(m => m.Name), p2.Merged.Select(m => m.Name));
    }

    // ---------------------------------------------------------------- rule 7: nothing merges across objects

    [Fact]
    public void Rule7_APartAloneInItsObjectStaysAsItIs()
    {
        MergePart p = Part(0);
        LinksetPlan plan = Plan(Set(p));
        Assert.Empty(plan.Merged);
        Assert.True(HasRule(plan, p, "7", "SINGLE_PART_OBJECT"));
        Assert.Equal(1, plan.PartsAfter);
    }

    // ---------------------------------------------------------------- rule 9: size caps

    [Fact]
    public void Rule9_PartsWhoseUnionWouldPassTheSoftCapDoNotMergeIntoOnePart()
    {
        MergeOptions o = new MergeOptions { SoftCapMeters = 10, HardCapMeters = 64 };
        LinksetPlan plan = Plan(Set(Part(1, 0, 6), Part(2, 7, 6)), o);
        Assert.Empty(plan.Merged);
        Assert.All(plan.Kept, k => Assert.Contains(k.Rules, r => r.Rule == "9"));
    }

    [Fact]
    public void Rule9_ASoftCapIsAimedForSoAGroupIsSplitIntoSeveralWithinTheCap()
    {
        MergeOptions o = new MergeOptions { SoftCapMeters = 10 };
        MergePart[] parts = Enumerable.Range(1, 12).Select(i => Part(i, (i - 1) * 3f, 2f)).ToArray();
        LinksetPlan plan = Plan(Set(parts), o);
        Assert.True(plan.Merged.Count >= 3);
        Assert.All(plan.Merged, m => Assert.True(m.ExtentMeters <= 10.0001, m.ExtentMeters + " m"));
    }

    [Fact]
    public void Rule9_APartLongerThanTheHardCapIsKeptWithTheHardCapCode()
    {
        MergePart big = Part(1, 0, 70);
        LinksetPlan plan = Plan(Set(big, Part(2, 80)));
        Assert.True(HasRule(plan, big, "9", "HARD_CAP"));
    }

    [Fact]
    public void Rule9_APartBetweenTheCapsIsKeptWithTheSoftCapCode()
    {
        MergePart slab = Part(1, 0, 25);
        LinksetPlan plan = Plan(Set(slab, Part(2, 30), Part(3, 31)));
        Assert.True(HasRule(plan, slab, "9", "SOFT_CAP"));
        Assert.Single(plan.Merged);
    }

    // ---------------------------------------------------------------- rule 10: creators and permissions

    [Fact]
    public void Rule10_PartsWithDifferentCreatorsNeverMerge()
    {
        MergePart a = Part(1), b = Part(2, 1), c = Part(3, 2);
        c.Creator = "creator-2";
        LinksetPlan plan = Plan(Set(a, b, c));
        MergedPartPlan m = Assert.Single(plan.Merged);
        Assert.DoesNotContain(m.Members, x => x.PartId == c.PartId);
        Assert.True(HasRule(plan, c, "10", "SOLE_CREATOR_OR_PERMISSIONS"));
    }

    [Fact]
    public void Rule10_PartsWithDifferentPermissionsNeverMerge()
    {
        MergePart a = Part(1), b = Part(2, 1), c = Part(3, 2);
        c.Masks = "m2";
        LinksetPlan plan = Plan(Set(a, b, c));
        Assert.DoesNotContain(Assert.Single(plan.Merged).Members, x => x.PartId == c.PartId);
        Assert.True(HasRule(plan, c, "10", "SOLE_CREATOR_OR_PERMISSIONS"));
    }

    // ---------------------------------------------------------------- rule 11: existing meshes

    [Fact]
    public void Rule11_AnExistingMeshIsNotPlannedForMerging()
    {
        MergePart mesh = Part(3, 2);
        mesh.Kind = "mesh";
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), mesh));
        Assert.True(HasRule(plan, mesh, "11", "ALREADY_MESH"));
        Assert.DoesNotContain(Assert.Single(plan.Merged).Members, x => x.PartId == mesh.PartId);
    }

    // ---------------------------------------------------------------- rule 12: what a face bakes

    [Fact]
    public void Rule12_AFaceWithNoMaterialIdBakes()
    {
        Assert.True(FaceBaking.Bakeable(new FaceTransform(4, 4, 0, 0, 0), 0, "", null, false, false, out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void Rule12_AFaceWithAMaterialIdAndNoMapsBakesLikeAnyOther()
    {
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "mat-1", new LegacyMaterialInfo(), false, false, out _));
    }

    [Fact]
    public void Rule12_AFaceWhoseMapsAreDrawnLikeTheDiffuseBakesAndTheMergedFaceTakesAMaterialWithIdentityMaps()
    {
        FaceTransform xf = new FaceTransform(4, 2, 0.5f, 0, 0);
        LegacyMaterialInfo m = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = xf, HasSpecularMap = true, SpecularTransform = xf };
        Assert.True(FaceBaking.Bakeable(xf, 0, "mat-1", m, false, false, out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void Rule12_AFaceWhoseMapRepeatsDifferentlyFromTheDiffuseBakesWhenTheDifferenceCanBeStated()
    {
        LegacyMaterialInfo m = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = new FaceTransform(1, 1, 0, 0, 0) };
        Assert.True(FaceBaking.Bakeable(new FaceTransform(4, 4, 0, 0, 0), 0, "mat-1", m, false, false, out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void Rule12_AFaceWhoseMapIsTurnedAgainstAnUnevenDiffuseStaysUnbaked()
    {
        LegacyMaterialInfo m = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = new FaceTransform(4, 2, 0, 0, 0.6f) };
        Assert.False(FaceBaking.Bakeable(new FaceTransform(4, 2, 0, 0, 0.1f), 0, "mat-1", m, false, false, out string why));
        Assert.Contains("material", why);
    }

    [Fact]
    public void Rule12_AMaterialIdWhoseMaterialIsNotInTheArchiveStaysUnbaked()
    {
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 0, "mat-1", null, false, false, out string why));
        Assert.Contains("material", why);
    }

    [Fact]
    public void Rule12_PbrAndMediaFacesBakeLikeOthers()
    {
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0, 0, 0), 0, "", null, true, false, out string pbr));
        Assert.Equal("", pbr);
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0, 0, 0), FaceBaking.PlanarMapping, "", null, true, false, out _));
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0, 0, 0), 0, "", null, false, true, out string media));
        Assert.Equal("", media);
    }

    [Fact]
    public void Rule12_APbrFaceIgnoresItsLegacyMaterial()
    {
        // the viewer draws a PBR face from the PBR material and ignores the face's legacy material (llvovolume.cpp, "ignore traditional material if GLTF material is present")
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "mat-1", null, true, false, out _));
    }

    [Fact]
    public void Rule12_APbrFaceWithAnotherMappingStaysUnbaked()
    {
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 4, "", null, true, false, out string why));
        Assert.Contains("mapping", why);
    }

    [Fact]
    public void Rule12_AMappingThatIsNeitherDefaultNorPlanarStaysUnbaked()
    {
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 4, "", null, false, false, out string why));
        Assert.Contains("mapping", why);
    }

    // ---------------------------------------------------------------- rule 14: the region minimum

    [Fact]
    public void Rule14_APlanWithMergedPartsWarnsAboutRegionsOlderThan642AndNamesTheFix()
    {
        MergePlan plan = MergePlanner.Plan(new[] { Set(Part(1), Part(2, 1)) }, new MergeOptions(), "a.oar");
        string w = Assert.Single(plan.Warnings);
        Assert.Contains("6.4.2", w);
        Assert.Contains("#378", w);
    }

    [Fact]
    public void Rule14_APlanWithNoMergedPartsHasNoRegionWarning()
    {
        MergePlan plan = MergePlanner.Plan(new[] { Set(Part(0)) }, new MergeOptions(), "a.oar");
        Assert.Empty(plan.Warnings);
    }

    // ---------------------------------------------------------------- rule 17: scripts and what they address

    [Fact]
    public void Rule17_AScriptedPartStaysSeparate()
    {
        MergePart s = Part(3, 2);
        s.Scripted = true;
        s.HasContents = true;
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), s, Part(4, 3)));
        Assert.True(HasRule(plan, s, "17", "SCRIPTED"));
    }

    [Fact]
    public void Rule17_APartAScriptAddressesByLinkNumberStaysSeparateAndSoDoLowerNumbers()
    {
        MergeLinkset l = Set(Part(1), Part(2, 1), Part(3, 2), Part(4, 3), Part(5, 4), Part(6, 5));
        l.Links.Literal.Add(4);
        LinksetPlan plan = Plan(l);
        Assert.True(HasRule(plan, l.Parts[3], "17", "ADDRESSED"));
        Assert.True(HasRule(plan, l.Parts[1], "17", "PROTECTED_LINK_NUMBER"));
        Assert.True(HasRule(plan, l.Parts[2], "17", "PROTECTED_LINK_NUMBER"));
        Assert.Equal(new[] { 5, 6 }, Assert.Single(plan.Merged).Members.Select(x => x.LinkNum));
    }

    [Fact]
    public void Rule17_ALockedLinksetKeepsEveryPart()
    {
        MergeLinkset l = Set(Part(1), Part(2, 1), Part(3, 2));
        l.Links.LockedBy = "a script addresses LINK_SET";
        LinksetPlan plan = Plan(l);
        Assert.Empty(plan.Merged);
        Assert.Equal(3, plan.Kept.Count);
        Assert.All(plan.Kept, k => Assert.Contains(k.Rules, r => r.Rule == "17" && r.Code == "LINKSET_LOCKED"));
    }

    [Fact]
    public void Rule17_TheRootStaysSeparateWhenAScriptAddressesIt()
    {
        MergeLinkset l = Set(Part(1), Part(2, 1), Part(3, 2));
        l.Links.RootAddressed = true;
        LinksetPlan plan = Plan(l);
        Assert.True(HasRule(plan, l.Parts[0], "17", "ADDRESSED"));
    }

    // ---------------------------------------------------------------- the design review's per-part reasons

    [Theory]
    [InlineData("Light")]
    [InlineData("Flexible")]
    [InlineData("SitTarget")]
    [InlineData("HoverText")]
    [InlineData("Particles")]
    [InlineData("Sound")]
    [InlineData("Physical")]
    [InlineData("VolumeDetect")]
    [InlineData("HasContents")]
    public void APartWithAPerPartFeatureStaysSeparate(string feature)
    {
        MergePart p = Part(3, 2);
        typeof(MergePart).GetProperty(feature).SetValue(p, true);
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), p, Part(4, 3)));
        Assert.Contains(Kept(plan, p).Rules, r => r.Rule == "part");
    }

    [Fact]
    public void AConvexPhysicsPartAndARootWithNoPhysicsStaySeparate()
    {
        MergePart convex = Part(2, 1);
        convex.PhysicsShapeType = 2;
        MergePart root = Part(1);
        root.PhysicsShapeType = 1;
        LinksetPlan plan = Plan(Set(root, convex, Part(3, 2), Part(4, 3)));
        Assert.True(HasRule(plan, convex, "part", "CONVEX_PHYSICS"));
        Assert.True(HasRule(plan, root, "part", "ROOT_PHYSICS_NONE"));
    }

    [Fact]
    public void PhantomAndSolidPartsDoNotMergeWithEachOther()
    {
        MergePart ph = Part(3, 2);
        ph.Phantom = true;
        MergePart ph2 = Part(4, 3);
        ph2.Phantom = true;
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), ph, ph2));
        Assert.Equal(2, plan.Merged.Count);
        Assert.All(plan.Merged, m => Assert.True(m.Members.All(x => x.LinkNum < 3) || m.Members.All(x => x.LinkNum >= 3)));
    }

    [Fact]
    public void ASculptIsPlannedLikeAPrim()
    {
        MergePart sculpt = Part(3, 2);
        sculpt.Kind = "sculpt";
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), sculpt));
        Assert.Empty(plan.Kept);
        Assert.Equal(3, Assert.Single(plan.Merged).Members.Count);
    }

    [Fact]
    public void APartTheConverterDoesNotConvertStaysSeparateWithItsEligibilityCode()
    {
        MergePart p = Part(3, 2);
        p.NotConvertible = "FACES_GT8";
        LinksetPlan plan = Plan(Set(Part(1), Part(2, 1), p));
        Assert.True(HasRule(plan, p, "part", "NOT_CONVERTED"));
    }

    // ---------------------------------------------------------------- rule 18: alpha faces

    [Fact]
    public void Rule18_ClosePanesMergeIntoAnAlphaPartWithinTheTightCapAndTheirFacesAreMarked()
    {
        MergePart pane1 = Part(2, 0, 1, "glass"), pane2 = Part(3, 1.5f, 1, "glass");
        pane1.Faces[0].Alpha = true;
        pane2.Faces[0].Alpha = true;
        LinksetPlan plan = Plan(Set(Part(1), pane1, pane2));
        MergedPartPlan alpha = Assert.Single(plan.Merged, m => m.Alpha);
        Assert.True(alpha.ExtentMeters <= 4.0001);
        Assert.Equal(2, alpha.Members.Count);
        Assert.Equal(2, plan.AlphaFaces);
        Assert.All(alpha.Slots, s => { Assert.True(s.Alpha); Assert.Contains("|a1", s.MaterialKey); });
    }

    [Fact]
    public void Rule18_PanesFartherApartThanTheAlphaCapStaySeparateAndStillCountAsAlphaFaces()
    {
        MergePart pane1 = Part(2, 0, 1, "glass"), pane2 = Part(3, 10, 1, "glass");
        pane1.Faces[0].Alpha = true;
        pane2.Faces[0].Alpha = true;
        LinksetPlan plan = Plan(Set(Part(1), pane1, pane2));
        Assert.DoesNotContain(plan.Merged, m => m.Members.Any(x => x.PartId == pane1.PartId) && m.Members.Any(x => x.PartId == pane2.PartId));
        Assert.True(HasRule(plan, pane1, "18", "ALPHA_CAP"));
        Assert.Equal(2, plan.AlphaFaces);
    }

    [Fact]
    public void Rule18_AnAlphaPartNeverSharesAMergedPartWithOpaqueOnes()
    {
        MergePart pane = Part(2, 0, 1, "glass");
        pane.Faces[0].Alpha = true;
        MergePart pane2 = Part(3, 1, 1, "glass");
        pane2.Faces[0].Alpha = true;
        LinksetPlan plan = Plan(Set(Part(1), pane, pane2, Part(4, 2)));
        foreach (MergedPartPlan m in plan.Merged)
            Assert.True(m.Slots.All(s => s.Alpha) || m.Slots.All(s => !s.Alpha));
    }

    // ---------------------------------------------------------------- the manifest (rule 8) and the counts

    [Fact]
    public void Rule8_TheManifestListsTheOriginalPartNamesAndCreatorsAndNamesThePlanFile()
    {
        MergePart a = Part(1), b = Part(2, 1);
        a.Name = "Wall left";
        b.Name = "Wall right";
        a.Creator = b.Creator = "Example Creator";
        MergePlan plan = MergePlanner.Plan(new[] { Set(a, b) }, new MergeOptions(), "shop.oar");
        string manifest = plan.Linksets[0].Merged[0].Manifest;
        Assert.Contains("Wall left", manifest);
        Assert.Contains("Wall right", manifest);
        Assert.Contains("Example Creator", manifest);
        Assert.Contains(a.PartId.ToString(), manifest);
        Assert.Contains("merge-plan", manifest);
    }

    [Fact]
    public void TheArchiveCountsAddUpTheLinksets()
    {
        MergePlan plan = MergePlanner.Plan(new[] { Set(Part(1), Part(2, 1), Part(3, 2)), Set(Part(0)) }, new MergeOptions(), "a.oar");
        Assert.Equal(4, plan.PartsBefore);
        Assert.Equal(2, plan.PartsAfter);
        Assert.Equal(1, plan.MergedParts);
        Assert.Equal(1, plan.KeptParts);
    }

    [Fact]
    public void EveryPartIsEitherMergedOrKeptExactlyOnceAndEveryKeptPartHasARule()
    {
        MergePart s = Part(3, 2);
        s.Scripted = true;
        MergeLinkset l = Set(Part(1), Part(2, 1), s, Part(4, 3), Part(5, 4, 1, "other"));
        LinksetPlan plan = Plan(l);
        List<Guid> ids = plan.Merged.SelectMany(m => m.Members.Select(x => x.PartId)).Concat(plan.Kept.Select(k => k.PartId)).ToList();
        Assert.Equal(l.Parts.Select(p => p.PartId).OrderBy(g => g), ids.OrderBy(g => g));
        Assert.All(plan.Kept, k => Assert.NotEmpty(k.Rules));
    }
}
