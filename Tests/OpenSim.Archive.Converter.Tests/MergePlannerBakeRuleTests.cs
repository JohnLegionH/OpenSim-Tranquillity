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
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 1, "", null, false, false, out _));
        Assert.True(FaceBaking.Bakeable(new FaceTransform(2, 2, 0.5f, 0, 1), 1, "", null, false, false, out _));
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "", null, false, false, out _));
    }

    [Fact]
    public void AMappingOtherThanDefaultOrPlanarDoesNotBake()
    {
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 2, "", null, false, false, out string why));
        Assert.Contains("texture mapping", why);
        Assert.False(FaceBaking.Bakeable(FaceTransform.Identity, 3, "", null, false, false, out _));
    }

    [Fact]
    public void ABakedPlanarFaceAndADefaultFaceWithTheSameMaterialShareOneSlot()
    {
        MergePart a = Part(1), b = Part(2, 1);
        b.Faces[0].TexGen = 1;
        Assert.Single(Assert.Single(Plan(a, b).Merged).Slots);
    }

    // ---------------------------------------------------------------- a legacy material's maps are drawn with the material's own transform

    private static readonly LegacyMaterialInfo MapsAtIdentity = new LegacyMaterialInfo { HasNormalMap = true, HasSpecularMap = true };

    [Fact]
    public void AFaceWithMapsAndNoTransformBakes()
    {
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 0, "m1", MapsAtIdentity, false, false, out _));
        Assert.True(FaceBaking.Bakeable(FaceTransform.Identity, 1, "m1", MapsAtIdentity, false, false, out _));
    }

    [Fact]
    public void AFaceWithMapsDrawnWithItsOwnNonIdentityTransformDoesNotBake()
    {
        // baking would move the diffuse transform into the coordinates, and the viewer would then apply the maps' transform on top of it
        FaceTransform xf = new FaceTransform(2, 2, 0, 0, 0);
        LegacyMaterialInfo same = new LegacyMaterialInfo { HasNormalMap = true, NormalTransform = xf };
        Assert.False(FaceBaking.Bakeable(xf, 0, "m1", same, false, false, out string why));
        Assert.Contains("map", why);
    }

    // ---------------------------------------------------------------- what a merge cannot carry

    [Fact]
    public void APartWithAPbrFaceOrAMediaFaceOrATextureAnimationStays()
    {
        MergePart pbr = Part(2, 1), media = Part(3, 2), anim = Part(4, 3);
        pbr.Faces[0].Pbr = true;
        media.Faces[0].Media = true;
        anim.TextureAnimation = true;
        LinksetPlan plan = Plan(Part(1), pbr, media, anim, Part(5, 4));
        Assert.Contains(plan.Kept.Single(k => k.PartId == pbr.PartId).Rules, r => r.Code == "PBR_FACE");
        Assert.Contains(plan.Kept.Single(k => k.PartId == media.PartId).Rules, r => r.Code == "MEDIA_FACE");
        Assert.Contains(plan.Kept.Single(k => k.PartId == anim.PartId).Rules, r => r.Code == "TEXTURE_ANIMATION");
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
        planar.Faces[0].TexGen = 1;
        planar.Faces[0].Bakeable = false;
        LinksetPlan plan = Plan(Part(1), planar, Part(3, 2));
        Assert.Contains(plan.Kept.Single(k => k.PartId == planar.PartId).Rules, r => r.Code == "UNBAKED_MAPPING");
        Assert.Single(plan.Merged);
    }
}
