/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The planar fixture (PlanarFixture) is written, merged, and every corner of the merged meshes is compared with the texture coordinates
/// the viewer computes for the source part. The expected figures come from the viewer's planarProjection and xform in llface.cpp, written
/// out again below, not from FaceUv. Each test builds its own archive and shares no state, so the class runs in parallel.
/// Set OARX_PLANAR_IN to the path of another archive (for example one a region saved from the fixture) to run the proof on that,
/// OARX_PLANAR_OUT to also write the fixture archive to that path, OARX_PLANAR_REPORT for the proof's report.
/// </summary>
public class PlanarFixtureTests
{
    [Fact]
    public void TheFixtureWritesThreePlanarFacesAndOneDefaultMappedFaceInEveryPart()
    {
        string census = PlanarFixture.Census(PlanarFixture.Write());
        string[] lines = census.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("  te ")).ToArray();
        Assert.Equal(PlanarFixture.ObjectCount * PlanarFixture.PartsPerObject, lines.Length);
        foreach (string line in lines)
            Assert.Contains("f0=Planar f1=Planar f2=Planar f3=Default", line);
    }

    [Fact]
    public void TheFixtureCanBeWrittenToAFileForALoadIntoARegion()
    {
        string outPath = Environment.GetEnvironmentVariable("OARX_PLANAR_OUT");
        if (string.IsNullOrEmpty(outPath))
            return;
        File.Copy(PlanarFixture.Write(), outPath, false);
    }

    [Fact]
    public void TheFixtureMergesIntoOnePartForEachObjectWithPlanarAndDefaultFacesBoth()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(PlanarFixture.Write(), new MergeOptions());
        Assert.Equal(PlanarFixture.ObjectCount, plan.MergedParts);
        Assert.All(plan.Linksets, l => Assert.Equal(PlanarFixture.PartsPerObject, Assert.Single(l.Merged).Members.Count));
    }

    private static void Viewer(Vector3 normal, Vector3 pos, Vector3 size, Primitive.TextureEntryFace tef, bool planar, float u0, float v0, out float s, out float t)
    {
        float su = u0, tv = v0;
        if (planar)
        {
            Vector3 vec = pos * size;
            Vector3 binormal;
            float d = normal.X;
            if (d >= 0.5f || d <= -0.5f)
                binormal = d < 0 ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
            else
                binormal = normal.Y > 0 ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
            Vector3 tangent = Vector3.Cross(binormal, normal);
            tv = -(Vector3.Dot(tangent, vec) * 2 - 0.5f);
            su = 1.0f + (Vector3.Dot(binormal, vec) * 2 - 0.5f);
        }
        float cosAng = (float)Math.Cos(tef.Rotation), sinAng = (float)Math.Sin(tef.Rotation);
        su -= 0.5f; tv -= 0.5f;
        float temp = su;
        su = su * cosAng + tv * sinAng;
        tv = -temp * sinAng + tv * cosAng;
        su *= tef.RepeatU; tv *= tef.RepeatV;
        su += tef.OffsetU + 0.5f; tv += tef.OffsetV + 0.5f;
        s = su; t = tv;
    }

    [Fact]
    public void EveryCornerOfTheMergedFixtureIsWhereTheViewerDrawsTheSourceCorner()
    {
        string path = Environment.GetEnvironmentVariable("OARX_PLANAR_IN");
        bool fresh = string.IsNullOrEmpty(path);
        if (fresh)
            path = PlanarFixture.Write();
        MergePlan plan = ArchiveMergePlanner.Plan(path, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed, out List<MergeLinkset> linksets);
        StringBuilder sb = new StringBuilder();
        double worst = 0, worstPos = 0;
        long corners = 0, planarCorners = 0, defaultCorners = 0;
        for (int li = 0; li < plan.Linksets.Count; li++)
        {
            Dictionary<Guid, MergePart> read = linksets[li].Parts.ToDictionary(p => p.PartId);
            foreach (MergedPartPlan mp in plan.Linksets[li].Merged)
            {
                List<MergeMember> members = new List<MergeMember>();
                foreach (MemberPlan x in mp.Members)
                {
                    ParsedPart pp = parsed[x.PartId];
                    MergePart part = read[x.PartId];
                    List<EncoderFace> faces = FaceBuilder.FromPrim(pp.Shape, out _, true);
                    PhysicsGeometry physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out _);
                    members.Add(new MergeMember
                    {
                        PartId = x.PartId, IsRoot = part.IsRoot, Scale = new Vector3(pp.Scale.X, pp.Scale.Y, pp.Scale.Z),
                        Offset = part.IsRoot ? Vector3.Zero : new Vector3(pp.OffsetPosition.X, pp.OffsetPosition.Y, pp.OffsetPosition.Z),
                        Rotation = part.IsRoot ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(pp.RotationOffset.X, pp.RotationOffset.Y, pp.RotationOffset.Z, pp.RotationOffset.W)),
                        Faces = faces, FaceInfo = part.Faces, Physics = physics
                    });
                }
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                foreach (FaceRun run in g.Runs)
                {
                    MergeMember m = members[run.Member];
                    ParsedPart pp = parsed[m.PartId];
                    Primitive.TextureEntry te = new Primitive.TextureEntry(pp.TextureEntry, 0, pp.TextureEntry.Length);
                    Primitive.TextureEntryFace tef = (te.FaceTextures[run.Face] ?? te.DefaultTexture);
                    bool planar = tef.TexMapType == MappingType.Planar;
                    EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
                    for (int k = 0; k < run.IndexCount; k++)
                    {
                        int sv = (int)src.Indices[run.SourceFirstIndex + k], mv = (int)merged.Indices[run.FirstIndex + k];
                        Viewer(new Vector3(src.Normals[sv * 3], src.Normals[sv * 3 + 1], src.Normals[sv * 3 + 2]),
                            new Vector3(src.Positions[sv * 3], src.Positions[sv * 3 + 1], src.Positions[sv * 3 + 2]), m.Scale, tef, planar,
                            src.Uvs[sv * 2], src.Uvs[sv * 2 + 1], out float s, out float t);
                        double d = Math.Max(Math.Abs(s - merged.Uvs[mv * 2]), Math.Abs(t - merged.Uvs[mv * 2 + 1]));
                        worst = Math.Max(worst, d);
                        corners++;
                        if (planar) planarCorners++; else defaultCorners++;
                        Vector3 want = MergeGeometry.ToRoot(m, new Vector3(src.Positions[sv * 3], src.Positions[sv * 3 + 1], src.Positions[sv * 3 + 2]));
                        Vector3 got = g.Position + new Vector3(merged.Positions[mv * 3], merged.Positions[mv * 3 + 1], merged.Positions[mv * 3 + 2]) * g.Scale;
                        worstPos = Math.Max(worstPos, Vector3.Distance(want, got));
                    }
                }
                sb.AppendLine(mp.Name + ": members " + mp.Members.Count + ", faces " + g.Faces.Count);
            }
        }
        sb.AppendLine("corners " + corners + " (planar-mapped " + planarCorners + ", default-mapped " + defaultCorners + ")");
        sb.AppendLine("worst uv difference before encoding " + worst.ToString("0.0e+0", CultureInfo.InvariantCulture) + " (limit 1e-5)");
        sb.AppendLine("worst position difference before encoding " + worstPos.ToString("0.0e+0", CultureInfo.InvariantCulture) + " m");
        string report = Environment.GetEnvironmentVariable("OARX_PLANAR_REPORT");
        if (!string.IsNullOrEmpty(report))
            File.WriteAllText(report, sb.ToString());
        Assert.True(corners > 0);
        Assert.True(worst < 1e-5, sb.ToString());
        Assert.True(worstPos < 1e-4, sb.ToString());
        Assert.True(planarCorners > 0);
        if (fresh)
            Assert.True(defaultCorners > 0, "the fixture mixes default-mapped faces with planar ones");
    }
}
