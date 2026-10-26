/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Sculpts merge with prims and with each other. A sphere (its poles are whole rows of the map that are one point), a torus, a plane and a
/// cylinder, with two boxes, are written to an archive, planned and merged. The expected triangles are worked out from the sculpt map by
/// the sculpt mesher alone: every triangle that has an area, each corner with its place in the root part's frame and its texture
/// coordinate. Every triangle of the merged part must be one of them, corner for corner, and the other way round. Each test writes its own
/// archive and shares nothing, so the class runs in parallel with others.
/// </summary>
public class MergeSculptTests
{
    private static readonly SculptKit.Shape[] Shapes = { SculptKit.Shape.Sphere, SculptKit.Shape.Torus, SculptKit.Shape.Plane, SculptKit.Shape.Cylinder };

    /// <summary>A linkset of four sculpts and two boxes, all with the same texture entry (repeats 1, no offset, no rotation).</summary>
    private static string Archive(out List<KitPart> parts)
    {
        string te = Convert.ToBase64String(new Primitive.TextureEntry(new UUID(Guid.NewGuid())).GetBytes());
        parts = new List<KitPart>();
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        for (int i = 0; i < Shapes.Length; i++)
        {
            Guid map = Guid.NewGuid();
            assets["assets/" + map + "_texture.jp2"] = SculptKit.Map(Shapes[i], 32);
            parts.Add(SculptKit.Part(Shapes[i] + " sculpt", i + 1, Shapes[i], i == 0 ? "0,0,0" : (i * 2) + ",0,0", map, te));
        }
        for (int i = 0; i < 2; i++)
            parts.Add(new KitPart("Box " + (i + 1), Shapes.Length + i + 1) { Offset = ((Shapes.Length + i) * 2) + ",0,0", Scale = "1,1,1", TextureEntry = te });
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets);
    }

    [Fact]
    public void FourSculptsAndTwoBoxesMergeIntoOnePart()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(Archive(out _), new MergeOptions());
        LinksetPlan l = Assert.Single(plan.Linksets);
        Assert.Empty(l.Kept);
        MergedPartPlan merged = Assert.Single(l.Merged);
        Assert.Equal(6, merged.Members.Count);
    }

    [Fact]
    public void ASculptWhoseMapIsNotInTheArchiveStaysAsItIs()
    {
        string te = Convert.ToBase64String(new Primitive.TextureEntry(new UUID(Guid.NewGuid())).GetBytes());
        List<KitPart> parts = new List<KitPart> { SculptKit.Part("Lost", 1, SculptKit.Shape.Sphere, "0,0,0", Guid.NewGuid(), te) };
        for (int i = 0; i < 3; i++)
            parts.Add(new KitPart("Box " + i, i + 2) { Offset = ((i + 1) * 2) + ",0,0", Scale = "1,1,1", TextureEntry = te });
        MergePlan plan = ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }), new MergeOptions());
        Assert.Contains(plan.Linksets[0].Kept, k => k.Name == "Lost");
        Assert.Equal(3, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    private readonly record struct Corner(Vector3 Position, float U, float V);

    private static float Area(Corner a, Corner b, Corner c) => 0.5f * Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length();

    private static bool Near(Corner a, Corner b) => Vector3.Distance(a.Position, b.Position) < 2e-4f && Math.Abs(a.U - b.U) < 2e-4f && Math.Abs(a.V - b.V) < 2e-4f;

    [Fact]
    public void EveryTriangleOfTheMergedPartIsATriangleWithAreaOfASourceSculptCornerForCorner()
    {
        string path = Archive(out List<KitPart> kit);
        MergePlan plan = ArchiveMergePlanner.Plan(path, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed, out List<MergeLinkset> linksets);
        MergedPartPlan mp = Assert.Single(plan.Linksets[0].Merged);
        Assert.Equal(6, mp.Members.Count);
        Dictionary<Guid, MergePart> read = linksets[0].Parts.ToDictionary(p => p.PartId);
        ArchiveSculptMapSource maps = new ArchiveSculptMapSource(path, kit.Select(p => p.SculptTexture));

        List<MergeMember> members = new List<MergeMember>();
        foreach (MemberPlan x in mp.Members)
        {
            ParsedPart pp = parsed[x.PartId];
            MergePart part = read[x.PartId];
            MemberShape shape = MemberGeometry.Build(part.Kind, pp, maps, clean: true, out string problem);
            Assert.True(shape != null, part.Name + ": " + problem);
            members.Add(new MergeMember
            {
                PartId = x.PartId, IsRoot = part.IsRoot, Scale = new Vector3(pp.Scale.X, pp.Scale.Y, pp.Scale.Z),
                Offset = part.IsRoot ? Vector3.Zero : new Vector3(pp.OffsetPosition.X, pp.OffsetPosition.Y, pp.OffsetPosition.Z),
                Rotation = part.IsRoot ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(pp.RotationOffset.X, pp.RotationOffset.Y, pp.RotationOffset.Z, pp.RotationOffset.W)),
                Faces = shape.Faces, FaceInfo = part.Faces, Physics = shape.Physics
            });
        }
        MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);

        for (int mi = 0; mi < members.Count; mi++)
        {
            MergePart part = read[members[mi].PartId];
            ParsedPart pp = parsed[members[mi].PartId];

            // what the source part should show: every triangle of the sculpt mesher's own output that has an area
            List<Corner[]> expected = new List<Corner[]>();
            int rawTriangles = 0;
            if (part.Kind == "sculpt")
            {
                SculptMap map = SculptMapReader.Decode(maps.GetBytes(pp.Shape.SculptTexture.Guid), out string problem);
                Assert.True(map != null, problem);
                PrimFace face = Assert.Single(SculptGeometry.BuildVolume(map, pp.Shape.SculptType, PrimParams.FromShape(pp.Shape)).Faces);
                for (int t = 0; t < face.TriangleCount; t++)
                {
                    Corner[] tri = new Corner[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int c = t * 3 + k;
                        tri[k] = new Corner(MergeGeometry.ToRoot(members[mi], new Vector3(face.Positions[c * 3], face.Positions[c * 3 + 1], face.Positions[c * 3 + 2])), face.Uvs[c * 2], 1f - face.Uvs[c * 2 + 1]);
                    }
                    rawTriangles++;
                    if (Area(tri[0], tri[1], tri[2]) > 1e-9f)
                        expected.Add(tri);
                }
                // the sphere's poles: many corners of the mesher's output are one point (a fan of triangles around it)
                if (part.Name.StartsWith("Sphere"))
                    Assert.True(Enumerable.Range(0, face.Positions.Length / 3).GroupBy(c => (Math.Round(face.Positions[c * 3], 3), Math.Round(face.Positions[c * 3 + 1], 3), Math.Round(face.Positions[c * 3 + 2], 3))).Max(gr => gr.Count()) >= 16,
                        "the sphere should have a pole where a whole row of the map is one point");
            }

            // what the merged part holds for it
            List<Corner[]> got = new List<Corner[]>();
            foreach (FaceRun run in g.Runs.Where(r => r.Member == mi))
            {
                EncoderFace merged = g.Faces[run.Slot];
                for (int k = 0; k < run.IndexCount; k += 3)
                {
                    Corner[] tri = new Corner[3];
                    for (int j = 0; j < 3; j++)
                    {
                        int v = (int)merged.Indices[run.FirstIndex + k + j];
                        tri[j] = new Corner(g.Position + new Vector3(merged.Positions[v * 3], merged.Positions[v * 3 + 1], merged.Positions[v * 3 + 2]) * g.Scale, merged.Uvs[v * 2], merged.Uvs[v * 2 + 1]);
                    }
                    got.Add(tri);
                }
            }
            if (part.Kind != "sculpt")
                continue;

            Assert.Equal(expected.Count, got.Count);
            bool[] used = new bool[expected.Count];
            foreach (Corner[] tri in got)
            {
                Assert.True(Area(tri[0], tri[1], tri[2]) > 1e-9f, part.Name + ": a merged triangle has no area");
                int hit = -1;
                for (int e = 0; e < expected.Count && hit < 0; e++)
                {
                    if (used[e])
                        continue;
                    for (int r = 0; r < 3; r++)
                        if (Near(tri[0], expected[e][r]) && Near(tri[1], expected[e][(r + 1) % 3]) && Near(tri[2], expected[e][(r + 2) % 3]))
                        {
                            hit = e;
                            break;
                        }
                }
                Assert.True(hit >= 0, part.Name + ": a merged triangle is not a source triangle");
                used[hit] = true;
            }
            Assert.All(used, u => Assert.True(u));

            // a texture seam is two corners at one place with different texture coordinates; the merged part keeps the ones the sculpt has
            // (the plane has none, the others wrap round)
            int Seams(List<Corner[]> tris)
            {
                List<Corner> corners = tris.SelectMany(t => t).ToList();
                int count = 0;
                foreach (IGrouping<(long, long, long), Corner> cell in corners.GroupBy(c => ((long)Math.Round(c.Position.X * 1000), (long)Math.Round(c.Position.Y * 1000), (long)Math.Round(c.Position.Z * 1000))))
                    if (cell.Any(a => cell.Any(b => Vector3.Distance(a.Position, b.Position) < 1e-5f && Math.Abs(a.U - b.U) > 0.1f)))
                        count++;
                return count;
            }
            if (!part.Name.StartsWith("Plane"))
                Assert.True(Seams(got) > 0, part.Name + ": the texture seam was welded away");
        }
    }

    [Fact]
    public void TheMergedArchiveHoldsOnePartWhoseMeshReadsBackWithinTheStorageStepOfEverySourceCorner()
    {
        string output = Path.Combine(Path.GetTempPath(), "merge-sculpt-tests", Guid.NewGuid().ToString("N"), "out.oar");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeWriteResult r = MergeArchiveWriter.Write(Archive(out _), output);
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.Equal(6, part.Members);
        Assert.True(part.Corners > 1000, part.Corners + " corners compared");
        Assert.True(part.WorstPositionMeters < 1e-5, "before encoding " + part.WorstPositionMeters);
        Assert.True(part.WorstUv < 1e-5, "before encoding " + part.WorstUv);
        Assert.True(part.WorstPositionMetersDecoded < 1e-3, "after reading the asset back " + part.WorstPositionMetersDecoded);
        Assert.True(part.MeasuredUvTexels <= UvPrecision.MaxTexels, part.MeasuredUvTexels + " texels");
    }
}
