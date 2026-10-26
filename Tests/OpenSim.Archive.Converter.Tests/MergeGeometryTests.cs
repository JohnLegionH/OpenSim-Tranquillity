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
/// The mesh of a merged part against the single-part meshes it is made from. The members are real prim faces from the converter's
/// own face builder. Nothing is shared between tests, so the class runs in parallel with others.
/// </summary>
public class MergeGeometryTests
{
    private const float PosTol = 1e-5f, UvTol = 1e-5f, NormalTol = 1e-4f;

    private sealed class Spec
    {
        public string Shape = "box";
        public Vector3 Scale = Vector3.One;
        public Vector3 Offset;
        public Quaternion Rotation = Quaternion.Identity;
        public string Texture = "tex-a";
        public FaceTransform Transform = FaceTransform.Identity;
        public int TexGen;
        public bool Bakeable = true;
    }

    private static (MergedPartPlan plan, List<MergeMember> members, List<Guid> ids) Make(MergeOptions options, params Spec[] specs)
    {
        MergeLinkset ls = new MergeLinkset { Id = Guid.NewGuid(), Name = "Set", Entry = "objects/set.xml" };
        List<MergeMember> members = new List<MergeMember>();
        for (int i = 0; i < specs.Length; i++)
        {
            Spec s = specs[i];
            List<EncoderFace> faces = FaceBuilder.FromPrim(FaceMatrix.Shape(s.Shape, "plain"), out _);
            MergeMember m = new MergeMember
            {
                PartId = Guid.NewGuid(), IsRoot = i == 0, Scale = s.Scale, Offset = i == 0 ? Vector3.Zero : s.Offset,
                Rotation = i == 0 ? Quaternion.Identity : s.Rotation, Faces = faces
            };
            m.Physics = new PhysicsGeometry { Positions = new float[] { -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f }, Indices = new[] { 0, 1, 2 } };
            MergePart p = new MergePart
            {
                PartId = m.PartId, LinkNum = i + 1, IsRoot = i == 0, Name = "P" + (i + 1), Creator = "c", Masks = "m", PhysicsShapeType = 0, PhysicsVertices = 3
            };
            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3((c & 1) == 0 ? -0.5f : 0.5f, (c & 2) == 0 ? -0.5f : 0.5f, (c & 4) == 0 ? -0.5f : 0.5f);
                Vector3 w = MergeGeometry.ToRoot(m, corner);
                lo = Vector3.Min(lo, w);
                hi = Vector3.Max(hi, w);
            }
            p.BoxMin = new[] { lo.X, lo.Y, lo.Z };
            p.BoxMax = new[] { hi.X, hi.Y, hi.Z };
            foreach (EncoderFace f in faces)
            {
                MergeFace info = new MergeFace { TextureId = s.Texture, Transform = s.Transform, TexGen = s.TexGen, Bakeable = s.Bakeable, Vertices = f.VertexCount };
                p.Faces.Add(info);
                m.FaceInfo.Add(info);
            }
            ls.Parts.Add(p);
            members.Add(m);
        }
        LinksetPlan plan = MergePlanner.PlanLinkset(ls, options ?? new MergeOptions());
        MergedPartPlan merged = Assert.Single(plan.Merged);
        List<MergeMember> ordered = merged.Members.Select(x => members.Single(m => m.PartId == x.PartId)).ToList();
        return (merged, ordered, ordered.Select(m => m.PartId).ToList());
    }

    private static Vector3 V(float[] a, int i) => new Vector3(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    private static Spec[] Three() => new[]
    {
        new Spec { Scale = new Vector3(2, 1, 0.5f) },
        new Spec { Scale = new Vector3(1, 3, 1), Offset = new Vector3(4, 1, 0.5f), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f) },
        new Spec { Shape = "cylinder", Scale = new Vector3(0.5f, 0.5f, 2), Offset = new Vector3(-3, 2, 1), Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 0)), 1.1f) }
    };

    [Fact]
    public void EveryCornerOfEveryMemberFaceComesBackToItsOriginalPlaceInTheRootFrame()
    {
        var (plan, members, _) = Make(null, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        Assert.NotEmpty(g.Runs);
        int corners = 0;
        foreach (FaceRun run in g.Runs)
        {
            MergeMember m = members[run.Member];
            EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
            for (int k = 0; k < run.IndexCount; k++)
            {
                Vector3 want = MergeGeometry.ToRoot(m, V(src.Positions, (int)src.Indices[run.SourceFirstIndex + k]));
                Vector3 got = g.Position + V(merged.Positions, (int)merged.Indices[run.FirstIndex + k]) * g.Scale;
                Assert.True(Vector3.Distance(want, got) < PosTol, "corner " + k + " of member " + run.Member + " face " + run.Face + " is " + Vector3.Distance(want, got) + " m off");
                corners++;
            }
        }
        Assert.Equal(members.Sum(m => m.Faces.Sum(f => f.Indices.Length)), corners);
    }

    [Fact]
    public void ANormalIsCarriedThroughTheMembersAndTheMergedPartsSizes()
    {
        var (plan, members, _) = Make(null, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        foreach (FaceRun run in g.Runs)
        {
            MergeMember m = members[run.Member];
            EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
            for (int k = 0; k < run.IndexCount; k += 7)
            {
                Vector3 want = Vector3.Normalize(Vector3.Transform(V(src.Normals, (int)src.Indices[run.SourceFirstIndex + k]) / m.Scale, m.Rotation));
                Vector3 got = Vector3.Normalize(V(merged.Normals, (int)merged.Indices[run.FirstIndex + k]) / g.Scale);
                Assert.True(Vector3.Distance(want, got) < NormalTol);
                Assert.InRange(V(merged.Normals, (int)merged.Indices[run.FirstIndex + k]).Length(), 0.999f, 1.001f);
            }
        }
    }

    [Fact]
    public void ABakedFaceCarriesItsRepeatsOffsetRotationAndFlipInItsCoordinates()
    {
        Spec[] specs = Three();
        specs[1].Transform = new FaceTransform(-3, 2, 0.25f, -0.4f, 0.7f);
        var (plan, members, _) = Make(null, specs);
        Assert.Single(plan.Slots); // baked, so all three share one material slot
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        Assert.Single(g.Faces);
        foreach (FaceRun run in g.Runs)
        {
            MergeMember m = members[run.Member];
            EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
            FaceTransform xf = m.FaceInfo[run.Face].Transform;
            for (int k = 0; k < run.IndexCount; k++)
            {
                int sv = (int)src.Indices[run.SourceFirstIndex + k], mv = (int)merged.Indices[run.FirstIndex + k];
                float s = src.Uvs[sv * 2], t = src.Uvs[sv * 2 + 1];
                FaceUv.Transform(ref s, ref t, xf);
                Assert.Equal(s, merged.Uvs[mv * 2], UvTol);
                Assert.Equal(t, merged.Uvs[mv * 2 + 1], UvTol);
            }
        }
        // member 1 carries the stated transform, so its faces differ from the originals; member 0 has none, so its faces are unchanged
        FaceRun first = g.Runs.First(r => r.Member == 0);
        EncoderFace f0 = members[0].Faces[first.Face], m0 = g.Faces[first.Slot];
        Assert.Equal(f0.Uvs[(int)f0.Indices[first.SourceFirstIndex] * 2], m0.Uvs[(int)m0.Indices[first.FirstIndex] * 2], UvTol);
    }

    [Fact]
    public void APlanarFaceIsProjectedOnItsOwnPartsSizeNotTheMergedPartsAndThenTransformed()
    {
        Spec[] specs = Three();
        specs[1].TexGen = 1;
        specs[1].Transform = new FaceTransform(2, 2, 0.1f, 0.2f, 0.4f);
        var (plan, members, _) = Make(null, specs);
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        MergeMember planarMember = members.Single(m => m.FaceInfo[0].TexGen == 1);
        int mi = members.IndexOf(planarMember);
        int checkedCorners = 0;
        foreach (FaceRun run in g.Runs.Where(r => r.Member == mi))
        {
            EncoderFace src = planarMember.Faces[run.Face], merged = g.Faces[run.Slot];
            for (int k = 0; k < run.IndexCount; k++)
            {
                int sv = (int)src.Indices[run.SourceFirstIndex + k], mv = (int)merged.Indices[run.FirstIndex + k];
                Vector3 n = V(src.Normals, sv), p = V(src.Positions, sv) * planarMember.Scale;
                // the viewer's planarProjection written out for an axis-aligned or curved normal
                Vector3 b = MathF.Abs(n.X) >= 0.5f ? new Vector3(0, n.X < 0 ? -1 : 1, 0) : new Vector3(n.Y > 0 ? -1 : 1, 0, 0);
                Vector3 tg = Vector3.Cross(b, n);
                float s = 1f + (Vector3.Dot(b, p) * 2 - 0.5f), t = -(Vector3.Dot(tg, p) * 2 - 0.5f);
                FaceTransform xf = planarMember.FaceInfo[run.Face].Transform;
                float c = MathF.Cos(xf.Rotation), sn = MathF.Sin(xf.Rotation);
                s -= 0.5f; t -= 0.5f;
                float s2 = (s * c + t * sn) * xf.RepeatU + xf.OffsetU + 0.5f, t2 = (-s * sn + t * c) * xf.RepeatV + xf.OffsetV + 0.5f;
                Assert.Equal(s2, merged.Uvs[mv * 2], UvTol);
                Assert.Equal(t2, merged.Uvs[mv * 2 + 1], UvTol);
                checkedCorners++;
            }
        }
        Assert.True(checkedCorners > 0);
    }

    [Fact]
    public void FacesWithTheSameMaterialShareAMergedFaceAndDifferentTexturesGetTheirOwn()
    {
        Spec[] specs = Three();
        specs[2].Texture = "tex-b";
        var (plan, members, _) = Make(null, specs);
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        Assert.Equal(2, plan.Slots.Count);
        Assert.Equal(2, g.Faces.Count);
        Assert.Equal(2, g.Template.Count);
        Assert.Equal(0, g.Template[0].Member);
        Assert.Equal(2, g.Template[1].Member);
    }

    [Fact]
    public void AnUnbakeableFaceKeepsItsOwnCoordinatesAndItsTransformStaysOnTheFaceTemplate()
    {
        Spec[] specs = Three();
        specs[1].Bakeable = false;
        specs[1].Transform = new FaceTransform(4, 4, 0, 0, 0);
        var (plan, members, _) = Make(null, specs);
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        Assert.Equal(2, g.Faces.Count);
        (int member, int face) = g.Template[1];
        Assert.Equal(1, member);
        Assert.Equal(new FaceTransform(4, 4, 0, 0, 0), members[member].FaceInfo[face].Transform);
        FaceRun run = g.Runs.First(r => r.Member == 1);
        EncoderFace src = members[1].Faces[run.Face], merged = g.Faces[run.Slot];
        Assert.Equal(src.Uvs[(int)src.Indices[run.SourceFirstIndex] * 2], merged.Uvs[(int)merged.Indices[run.FirstIndex] * 2], UvTol);
    }

    [Fact]
    public void AMergedPartThatIsNotTheRootIsCentredOnItsGeometryAndFillsItsBox()
    {
        var (plan, members, _) = Make(null, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        float[] all = g.Faces.SelectMany(f => f.Positions).ToArray();
        for (int axis = 0; axis < 3; axis++)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = axis; i < all.Length; i += 3)
            {
                min = Math.Min(min, all[i]);
                max = Math.Max(max, all[i]);
            }
            Assert.InRange(min, -0.5f - PosTol, -0.5f + PosTol);
            Assert.InRange(max, 0.5f - PosTol, 0.5f + PosTol);
        }
    }

    [Fact]
    public void AMergedPartThatTakesTheRootsPlaceStaysAtTheRootsOriginAndHoldsAllItsGeometry()
    {
        var (plan, members, _) = Make(null, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: true);
        Assert.Equal(Vector3.Zero, g.Position);
        float[] all = g.Faces.SelectMany(f => f.Positions).ToArray();
        Assert.True(all.All(v => v >= -0.5f - PosTol && v <= 0.5f + PosTol));
        // the geometry still comes back to its original place, relative to the root's origin
        FaceRun run = g.Runs.First(r => r.Member == 1);
        MergeMember m = members[1];
        EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
        Vector3 want = MergeGeometry.ToRoot(m, V(src.Positions, (int)src.Indices[run.SourceFirstIndex]));
        Vector3 got = V(merged.Positions, (int)merged.Indices[run.FirstIndex]) * g.Scale;
        Assert.True(Vector3.Distance(want, got) < PosTol);
    }

    [Fact]
    public void TheCollisionTrianglesOfAllMembersAreCarriedIntoTheMergedBox()
    {
        var (plan, members, _) = Make(null, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false);
        Assert.Equal(9, g.Physics.VertexCount);
        Assert.Equal(3, g.Physics.TriangleCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, g.Physics.Indices);
        // member 1's first physics vertex, back in the root frame
        Vector3 want = MergeGeometry.ToRoot(members[1], new Vector3(-0.5f, -0.5f, -0.5f));
        Vector3 got = g.Position + V(g.Physics.Positions, 3) * g.Scale;
        Assert.True(Vector3.Distance(want, got) < PosTol);
    }

    [Fact]
    public void AMaterialPastTheVertexLimitIsSplitIntoTheFacesThePlanHas()
    {
        MergeOptions o = new MergeOptions { MaxFaceVertices = 40 };
        var (plan, members, _) = Make(o, Three());
        MergedGeometry g = MergeGeometry.Build(plan, members, carrierIsRoot: false, o);
        Assert.True(plan.Slots.Count > 1);
        Assert.Equal(plan.Slots.Count, g.Faces.Count);
        Assert.All(g.Faces, f => Assert.True(f.VertexCount <= 40, f.VertexCount + " vertices"));
        Assert.Equal(members.Sum(m => m.Faces.Sum(f => f.Indices.Length)), g.Faces.Sum(f => f.Indices.Length));
    }

    [Fact]
    public void TheMembersMustMatchThePlan()
    {
        var (plan, members, _) = Make(null, Three());
        Assert.Throws<InvalidOperationException>(() => MergeGeometry.Build(plan, members.Take(2).ToList(), false));
    }
}
