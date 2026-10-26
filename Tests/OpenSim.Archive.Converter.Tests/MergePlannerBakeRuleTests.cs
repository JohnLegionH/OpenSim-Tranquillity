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
/// The planner rules the geometry stage depends on: which faces bake, which parts a merge cannot carry, and which part a merged
/// part takes its identity from. Hand-made linksets only; no process-wide state, so the class runs in parallel with others.
/// </summary>
public class MergePlannerBakeRuleTests
{
    private static MergePart Part(int link, float x = 0, string texture = "tex-a")
    {
        MergePart p = new MergePart
        {
            PartId = Guid.NewGuid(), LinkNum = link, IsRoot = link == 1, Name = "Part " + link, Creator = "creator-1", Masks = "m1",
            PhysicsShapeType = 0, PhysicsVertices = 8, BoxMin = new[] { x, 0f, 0f }, BoxMax = new[] { x + 1, 1f, 1f }
        };
        p.Faces.Add(new MergeFace { TextureId = texture, Vertices = 24 });
        return p;
    }

    private static LinksetPlan Plan(params MergePart[] parts)
    {
        MergeLinkset l = new MergeLinkset { Id = Guid.NewGuid(), Name = "Set", Entry = "objects/set.xml" };
        l.Parts.AddRange(parts);
        return MergePlanner.PlanLinkset(l, new MergeOptions());
    }

    // ---------------------------------------------------------------- planar faces bake, other mappings do not

    [Fact]
    public void APlanarFaceBakesAndSoDoesADefaultMappedOne()
    {
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, FaceBaking.PlanarMapping, "", null, false, false, out _));
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0.5f, 0, 1), FaceBaking.PlanarMapping, "", null, false, false, out _));
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "", null, false, false, out _));
    }

    [Fact]
    public void AMappingOtherThanDefaultOrPlanarDoesNotBake()
    {
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 4, "", null, false, false, out string why));
        Assert.Contains("texture mapping", why);
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 6, "", null, false, false, out _));
    }

    [Fact]
    public void ABakedPlanarFaceAndADefaultFaceWithTheSameMaterialShareOneSlot()
    {
        MergePart a = Part(1), b = Part(2, 1);
        b.Faces[0].TexGen = FaceBaking.PlanarMapping;
        Assert.Single(Assert.Single(Plan(a, b).Merged).Slots);
    }

    // ---------------------------------------------------------------- a legacy material's maps are drawn with the material's own transform

    private static readonly LegacyMaterialInfo MapsAtIdentity = new LegacyMaterialInfo { HasNormalMap = true, HasSpecularMap = true };

    [Fact]
    public void AFaceWithMapsAndNoTransformBakes()
    {
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "m1", MapsAtIdentity, false, false, out _));
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, FaceBaking.PlanarMapping, "m1", MapsAtIdentity, false, false, out _));
    }

    [Fact]
    public void AFaceWithMapsDrawnWithTheFacesOwnTransformBakes()
    {
        // the maps are drawn from the same base coordinates with the material's own transform, so the baked coordinates serve them as well
        FaceTransform xf = new FaceTransform(2, 2, 0, 0, 0);
        LegacyMaterialInfo same = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = xf };
        Assert.True(FaceBaking.Bakeable(xf, 0, "m1", same, false, false, out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void AFaceWithMapsDrawnWithAnotherTransformBakesWhenTheDifferenceCanBeStated()
    {
        // the face is scaled alike in both directions, so the map's transform relative to it is a turn, repeats and an offset
        FaceTransform xf = new FaceTransform(2, 2, 0, 0, 0);
        LegacyMaterialInfo other = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = new FaceTransform(2, 3, 0, 0, 0), HasSpecularMap = true, SpecularTransform = xf };
        Assert.True(FaceBaking.Bakeable(xf, 0, "m1", other, false, false, out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void AFaceWithMapsDrawnWithATransformThatShearsRelativeToTheFaceDoesNotBake()
    {
        FaceTransform xf = new FaceTransform(2, 3, 0, 0, 0.2f);
        LegacyMaterialInfo sheared = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = new FaceTransform(2, 3, 0, 0, 0.9f), HasSpecularMap = true, SpecularTransform = xf };
        Assert.False(FaceBaking.Bakeable(xf, 0, "m1", sheared, false, false, out string why));
        Assert.Contains("map", why);
    }

    [Fact]
    public void AMapThatIsNotThereIsNotComparedWithTheFace()
    {
        // a transform left on a map the material does not have changes nothing the viewer draws
        LegacyMaterialInfo noMaps = new LegacyMaterialInfo { NormalTransform = new FaceTransform(9, 9, 0, 0, 0) };
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0, 0, 0), 0, "m1", noMaps, false, false, out _));
    }

    // ---------------------------------------------------------------- what a merge cannot carry

    [Fact]
    public void APartWithATextureAnimationOrMaterialOrMediaDataThatCannotBeReadStays()
    {
        MergePart badPbr = Part(2, 1), badMedia = Part(3, 2), anim = Part(4, 3);
        badPbr.PbrDataUnreadable = true;
        badMedia.MediaDataUnreadable = true;
        anim.TextureAnimation = true;
        LinksetPlan plan = Plan(Part(1), badPbr, badMedia, anim, Part(5, 4));
        Assert.Contains(plan.Kept.Single(k => k.PartId == badPbr.PartId).Rules, r => r.Code == "PBR_DATA");
        Assert.Contains(plan.Kept.Single(k => k.PartId == badMedia.PartId).Rules, r => r.Code == "MEDIA_DATA");
        Assert.Contains(plan.Kept.Single(k => k.PartId == anim.PartId).Rules, r => r.Code == "TEXTURE_ANIMATION");
    }

    [Fact]
    public void APartWithAPbrFaceOrAMediaFaceMerges()
    {
        MergePart pbr = Part(2, 1), media = Part(3, 2);
        pbr.Faces[0].Pbr = true;
        media.Faces[0].Media = true;
        LinksetPlan plan = Plan(Part(1), pbr, media, Part(4, 3));
        Assert.Empty(plan.Kept);
        Assert.Equal(4, Assert.Single(plan.Merged).Members.Count);
    }

    // ---------------------------------------------------------------- identity of a merged part

    [Fact]
    public void AMergedPartHasTheIdOfItsLowestNumberedMemberSoARootStaysTheRoot()
    {
        MergePart a = Part(1), b = Part(2, 1), c = Part(3, 2);
        MergedPartPlan m = Assert.Single(Plan(c, b, a).Merged);
        Assert.Equal(a.PartId, m.Id);
        Assert.Equal(1, m.NewLinkNum);
    }

    [Fact]
    public void ALampArmLinksetOfARootAndALightStaysUnmerged()
    {
        MergePart root = Part(1), lamp = Part(2, 1);
        lamp.Light = true;
        LinksetPlan plan = Plan(root, lamp);
        Assert.Empty(plan.Merged);
        Assert.Equal(2, plan.Kept.Count);
    }

    [Fact]
    public void APartWhoseUnbakeablePlanarFaceCannotBeBakedStaysBecauseTheViewerWouldProjectItOnTheMergedShape()
    {
        MergePart planar = Part(2, 1);
        planar.Faces[0].TexGen = FaceBaking.PlanarMapping;
        planar.Faces[0].Bakeable = false;
        LinksetPlan plan = Plan(Part(1), planar, Part(3, 2));
        Assert.Contains(plan.Kept.Single(k => k.PartId == planar.PartId).Rules, r => r.Code == "UNBAKED_MAPPING");
        Assert.Single(plan.Merged);
    }
}
