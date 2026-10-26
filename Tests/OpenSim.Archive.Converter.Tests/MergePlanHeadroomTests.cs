/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The planner counts the faces a material needs with room to spare: the packer closes a face as soon as the next triangle might not
/// fit, so a face holds a little less than the limit and a count made from the plain limit can come out one face short. Each test
/// builds its own parts and shares nothing, so the class runs in parallel with others.
/// </summary>
public class MergePlanHeadroomTests
{
    private static MergePart Part(int link, int vertices, float x)
    {
        MergePart p = new MergePart
        {
            PartId = Guid.NewGuid(), LinkNum = link, IsRoot = link == 1, Name = "Part " + link, Creator = "c", Masks = "m", PhysicsShapeType = 0, PhysicsVertices = 8,
            BoxMin = new[] { x, 0f, 0f }, BoxMax = new[] { x + 1, 1f, 1f }
        };
        // faces of 1,000 vertices: no face of a prim is anywhere near the limit of one mesh face
        for (int left = vertices; left > 0; left -= 1000)
            p.Faces.Add(new MergeFace { TextureId = "tex", Vertices = Math.Min(1000, left) });
        return p;
    }

    private static MergeLinkset Set(params MergePart[] parts)
    {
        MergeLinkset l = new MergeLinkset { Id = Guid.NewGuid(), Name = "Set", Entry = "objects/set.xml" };
        l.Parts.AddRange(parts);
        return l;
    }

    [Fact]
    public void AMaterialThatFillsFourFacesToTheLimitIsPlannedWithAFifthBecauseAFaceClosesBeforeItIsFull()
    {
        LinksetPlan plan = MergePlanner.PlanLinkset(Set(Part(1, 131070, 0), Part(2, 131070, 1)), new MergeOptions());
        MergedPartPlan merged = Assert.Single(plan.Merged);
        Assert.Equal(5, merged.Slots.Count);
        Assert.All(merged.Slots, s => Assert.True(s.Vertices <= 65533, s.Vertices + " vertices in a slot"));
    }

    [Fact]
    public void AMergeThatWouldPackIntoNineFacesUnderTheOldCountIsNotPlannedAsOnePart()
    {
        // 2 x 262,140 vertices of one material: eight faces of 65,535 by the plain count, nine by what the packer can fit
        LinksetPlan plan = MergePlanner.PlanLinkset(Set(Part(1, 262140, 0), Part(2, 262140, 1)), new MergeOptions());
        Assert.All(plan.Merged, m => Assert.True(m.Slots.Count <= 8, m.Slots.Count + " slots"));
        Assert.Empty(plan.Merged);
        Assert.Equal(2, plan.Kept.Count);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(30)]
    public void TheFacesTheGeometryStageBuildsNeverExceedTheSlotsThePlannerCounted(int faceLimit)
    {
        for (int boxes = 2; boxes <= 9; boxes++)
        {
            MergeOptions o = new MergeOptions { MaxFaceVertices = faceLimit, MaxFacesPerPart = 1000, SoftCapMeters = 1000, HardCapMeters = 2000 };
            MergeLinkset ls = Set();
            List<MergeMember> members = new List<MergeMember>();
            for (int i = 0; i < boxes; i++)
            {
                List<EncoderFace> faces = FaceBuilder.FromPrim(FaceMatrix.Shape("box", "plain"), out _);
                MergeMember m = new MergeMember
                {
                    PartId = Guid.NewGuid(), IsRoot = i == 0, Scale = Vector3.One, Offset = i == 0 ? Vector3.Zero : new Vector3(i * 2, 0, 0), Faces = faces,
                    Physics = new PhysicsGeometry { Positions = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 }, Indices = new[] { 0, 1, 2 } }
                };
                MergePart p = new MergePart
                {
                    PartId = m.PartId, LinkNum = i + 1, IsRoot = i == 0, Name = "P", Creator = "c", Masks = "m", PhysicsShapeType = 0, PhysicsVertices = 3,
                    BoxMin = new[] { i * 2f, 0f, 0f }, BoxMax = new[] { i * 2f + 1, 1f, 1f }
                };
                foreach (EncoderFace f in faces)
                {
                    MergeFace info = new MergeFace { TextureId = "tex", Vertices = f.VertexCount };
                    p.Faces.Add(info);
                    m.FaceInfo.Add(info);
                }
                ls.Parts.Add(p);
                members.Add(m);
            }
            MergedPartPlan plan = Assert.Single(MergePlanner.PlanLinkset(ls, o).Merged);
            List<MergeMember> ordered = plan.Members.Select(x => members.Single(m => m.PartId == x.PartId)).ToList();
            MergedGeometry g = MergeGeometry.Build(plan, ordered, ordered[0].IsRoot, o);
            Assert.True(g.Faces.Count <= plan.Slots.Count, "limit " + faceLimit + ", " + boxes + " boxes: built " + g.Faces.Count + " faces, planned " + plan.Slots.Count);
        }
    }
}
