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
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Faces that name a legacy material with normal and specular maps (MapsFixture). The viewer draws the diffuse texture with the face's own
/// transform and each map with the material's transform for that map, all from the same base coordinates (llface.cpp, getGeometryVolume);
/// a merged mesh has one set of coordinates, so a face bakes only when its maps are drawn where its diffuse is, and the merged face then
/// names a new material whose map transforms are the identity. The expected figures below come from the viewer's planarProjection and xform,
/// written out again here, not from FaceUv. Each test builds its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergeLegacyMapTests
{
    private static Primitive.TextureEntryFace FaceOf(Primitive.TextureEntry te, int i) =>
        ((te.FaceTextures != null && i < te.FaceTextures.Length) ? te.FaceTextures[i] : null) ?? te.DefaultTexture;

    private static string Temp(string stem) => Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N") + ".oar");

    [Fact]
    public void TheFixtureStoresTheFourFacesItDescribes()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed);
        Assert.Equal(MapsFixture.ObjectCount * MapsFixture.PartsPerObject, parsed.Count);
        foreach (ParsedPart pp in parsed.Values)
        {
            Primitive.TextureEntry te = ArchiveMergePlanner.ReadTextureEntry(pp);
            Guid[] materials = { MapsFixture.MaterialA, MapsFixture.MaterialB, MapsFixture.MaterialA2, MapsFixture.MaterialM };
            MappingType[] mappings = { MappingType.Default, MappingType.Planar, MappingType.Planar, MappingType.Default };
            for (int f = 0; f < 4; f++)
            {
                Assert.Equal(materials[f], FaceOf(te, f).MaterialID.Guid);
                Assert.Equal(mappings[f], FaceOf(te, f).TexMapType);
            }
            Assert.Equal(MapsFixture.TransformB.RepeatU, FaceOf(te, 1).RepeatU);
            Assert.Equal(MapsFixture.TransformA2.OffsetV, FaceOf(te, 2).OffsetV);
        }
    }

    [Fact]
    public void TheFixtureCanBeWrittenToAFileForALoadIntoARegion()
    {
        string outPath = Environment.GetEnvironmentVariable("OARX_MAPS_OUT");
        if (string.IsNullOrEmpty(outPath))
            return;
        File.Copy(MapsFixture.Write(), outPath, false);
    }

    [Fact]
    public void FacesWhoseMapsAreDrawnLikeTheirDiffuseBakeAndTheirPartsMerge()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions());
        Assert.Equal(MapsFixture.ObjectCount, plan.MergedParts);
        Assert.True(plan.KeptParts == 0, "parts kept: " + string.Join("; ", plan.Linksets.SelectMany(l => l.Kept).Select(k => k.Name + " " + string.Join(",", k.Rules.Select(r => r.Code + " " + r.Detail)))));
        foreach (LinksetPlan l in plan.Linksets)
        {
            MergedPartPlan merged = Assert.Single(l.Merged);
            Assert.Equal(MapsFixture.PartsPerObject, merged.Members.Count);
            // material A (faces 0 and 2 once the transforms are taken out), material B, the face that cannot be baked, and the plain faces
            Assert.Equal(4, merged.Slots.Count);
            Assert.Equal(3, merged.Slots.Count(s => s.Baked));
        }
    }

    internal sealed class Written
    {
        public string Source, Output;
        public MergePlan Plan;
        public Dictionary<Guid, ParsedPart> Parsed;      // the source
        public Dictionary<Guid, ParsedPart> OutParsed;   // the merged archive
        public Dictionary<Guid, LegacyMaterialInfo> OutMaterials;
        public List<MergeLinkset> Linksets;
    }

    internal static Written WriteFixture(string source = null)
    {
        Written w = new Written { Source = source ?? MapsFixture.Write(), Output = Temp("maps-merged") };
        MergeArchiveWriter.Write(w.Source, w.Output);
        w.Plan = ArchiveMergePlanner.Plan(w.Source, new MergeOptions(), null, out w.Parsed, out w.Linksets);
        ArchiveMergePlanner.Plan(w.Output, new MergeOptions(), null, out w.OutParsed);
        w.OutMaterials = ArchiveMaterials.Read(w.Output);
        return w;
    }

    [Fact]
    public void TheMergedFacesNameNewMaterialsWhoseMapTransformsAreTheIdentity()
    {
        Written w = WriteFixture();
        HashSet<Guid> bakedMaterials = new HashSet<Guid>();
        foreach (LinksetPlan l in w.Plan.Linksets)
        {
            MergedPartPlan mp = l.Merged.Single();
            Primitive.TextureEntry te = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
            for (int slot = 0; slot < mp.Slots.Count; slot++)
            {
                Primitive.TextureEntryFace face = FaceOf(te, slot);
                Guid material = face.MaterialID.Guid;
                if (mp.Slots[slot].Baked && material != Guid.Empty)
                {
                    // drawn with the identity and default mapping: the mapping and transform are in the mesh now
                    Assert.True(new FaceTransform(face.RepeatU, face.RepeatV, face.OffsetU, face.OffsetV, face.Rotation).SameAs(FaceTransform.Identity));
                    Assert.Equal(MappingType.Default, face.TexMapType);
                    Assert.NotEqual(MapsFixture.MaterialA, material);
                    Assert.NotEqual(MapsFixture.MaterialB, material);
                    Assert.NotEqual(MapsFixture.MaterialA2, material);
                    LegacyMaterialInfo info = w.OutMaterials[material];
                    Assert.True(info.HasNormalMap);
                    Assert.True(info.NormalTransform.SameAs(FaceTransform.Identity));
                    Assert.True(info.SpecularTransform.SameAs(FaceTransform.Identity));
                    bakedMaterials.Add(material);
                }
                else if (!mp.Slots[slot].Baked)
                {
                    // the face that cannot be baked keeps its material and its own transform
                    Assert.Equal(MapsFixture.MaterialM, material);
                    Assert.True(new FaceTransform(face.RepeatU, face.RepeatV, face.OffsetU, face.OffsetV, face.Rotation).SameAs(MapsFixture.FaceM));
                }
            }
        }
        // material A and material A2 differ only in their transforms, so they become one new material; material B is the other
        Assert.Equal(2, bakedMaterials.Count);
        Assert.True(w.OutMaterials[bakedMaterials.First()].HasNormalMap);
        Assert.Contains(bakedMaterials, m => w.OutMaterials[m].HasSpecularMap);
        Assert.Contains(bakedMaterials, m => !w.OutMaterials[m].HasSpecularMap);
    }

    /// <summary>The viewer's coordinates for one corner of a face: planar projection when the face is planar, then the transform.</summary>
    private static void Viewer(Vector3 normal, Vector3 pos, Vector3 size, FaceTransform t, bool planar, float u0, float v0, out float s, out float tv)
    {
        float su = u0, tt = v0;
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
            tt = -(Vector3.Dot(tangent, vec) * 2 - 0.5f);
            su = 1.0f + (Vector3.Dot(binormal, vec) * 2 - 0.5f);
        }
        float cosAng = (float)Math.Cos(t.Rotation), sinAng = (float)Math.Sin(t.Rotation);
        su -= 0.5f; tt -= 0.5f;
        float temp = su;
        su = su * cosAng + tt * sinAng;
        tt = -temp * sinAng + tt * cosAng;
        su *= t.RepeatU; tt *= t.RepeatV;
        su += t.OffsetU + 0.5f; tt += t.OffsetV + 0.5f;
        s = su; tv = tt;
    }

    private static FaceTransform Of(Primitive.TextureEntryFace f) => new FaceTransform(f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation);

    [Fact]
    public void EveryCornerOfTheDiffuseAndOfEachMapIsWhereTheViewerDrawsTheSourceCorner()
    {
        CornerProof p = ProveCorners(WriteFixture());
        string report = Environment.GetEnvironmentVariable("OARX_MAPS_REPORT");
        if (!string.IsNullOrEmpty(report))
            File.WriteAllText(report, p.Report);
        Assert.True(p.Corners > 0, p.Report);
        Assert.True(p.BakedCorners > 0 && p.UnbakedCorners > 0, p.Report);
        Assert.True(p.NormalCorners > 0 && p.SpecularCorners > 0, p.Report);
        Assert.True(p.WorstDiffuse < 1e-5, p.Report);
        Assert.True(p.WorstNormalTexels <= 0.25, p.Report);
        Assert.True(p.WorstSpecularTexels <= 0.25, p.Report);
    }

    /// <summary>What the corner-by-corner comparison of a written merge found: counts, and the worst difference of the diffuse and of each map.</summary>
    internal sealed class CornerProof
    {
        public long Corners, BakedCorners, UnbakedCorners, NormalCorners, SpecularCorners, MapCornersDrawnWithOtherTransform;
        public double WorstDiffuse, WorstNormalTexels, WorstSpecularTexels;
        public string Report;
    }

    /// <summary>
    /// Every corner of every merged part against the source corner it came from: the diffuse is where the viewer draws the source face, and each map
    /// is where the viewer draws the source material's map (the base coordinates with the material's transform) against the merged material's
    /// transform applied to the merged coordinates. The viewer's two steps are written out in <see cref="Viewer"/>, not taken from FaceUv.
    /// Maps are 512 pixels (normal) and 256 pixels (specular) in the fixtures.
    /// </summary>
    internal static CornerProof ProveCorners(Written w)
    {
        Dictionary<Guid, LegacyMaterialInfo> sourceMaterials = ArchiveMaterials.Read(w.Source);
        StringBuilder sb = new StringBuilder();
        double worstDiffuse = 0, worstNormalTexels = 0, worstSpecularTexels = 0;
        long corners = 0, bakedCorners = 0, unbakedCorners = 0, normalCorners = 0, specularCorners = 0, otherTransform = 0;
        for (int li = 0; li < w.Plan.Linksets.Count; li++)
        {
            Dictionary<Guid, MergePart> read = w.Linksets[li].Parts.ToDictionary(p => p.PartId);
            foreach (MergedPartPlan mp in w.Plan.Linksets[li].Merged)
            {
                Primitive.TextureEntry outTe = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
                List<MergeMember> members = new List<MergeMember>();
                foreach (MemberPlan x in mp.Members)
                {
                    ParsedPart pp = w.Parsed[x.PartId];
                    MergePart part = read[x.PartId];
                    members.Add(new MergeMember
                    {
                        PartId = x.PartId, IsRoot = part.IsRoot, Scale = new Vector3(pp.Scale.X, pp.Scale.Y, pp.Scale.Z),
                        Offset = part.IsRoot ? Vector3.Zero : new Vector3(pp.OffsetPosition.X, pp.OffsetPosition.Y, pp.OffsetPosition.Z),
                        Rotation = part.IsRoot ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(pp.RotationOffset.X, pp.RotationOffset.Y, pp.RotationOffset.Z, pp.RotationOffset.W)),
                        Faces = FaceBuilder.FromPrim(pp.Shape, out _, true), FaceInfo = part.Faces, Physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out _)
                    });
                }
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                foreach (FaceRun run in g.Runs)
                {
                    MergeMember m = members[run.Member];
                    Primitive.TextureEntry te = new Primitive.TextureEntry(w.Parsed[m.PartId].TextureEntry, 0, w.Parsed[m.PartId].TextureEntry.Length);
                    Primitive.TextureEntryFace src = FaceOf(te, run.Face);
                    bool planar = src.TexMapType == MappingType.Planar;
                    Primitive.TextureEntryFace drawn = FaceOf(outTe, run.Slot);
                    Assert.Equal(MappingType.Default, drawn.TexMapType);
                    EncoderFace sf = m.Faces[run.Face], mf = g.Faces[run.Slot];
                    sourceMaterials.TryGetValue(src.MaterialID.Guid, out LegacyMaterialInfo srcMaterial);
                    w.OutMaterials.TryGetValue(drawn.MaterialID.Guid, out LegacyMaterialInfo drawnMaterial);
                    bool baked = m.FaceInfo[run.Face].Bakeable;
                    for (int k = 0; k < run.IndexCount; k++)
                    {
                        int sv = (int)sf.Indices[run.SourceFirstIndex + k], mv = (int)mf.Indices[run.FirstIndex + k];
                        Vector3 n = new Vector3(sf.Normals[sv * 3], sf.Normals[sv * 3 + 1], sf.Normals[sv * 3 + 2]);
                        Vector3 p = new Vector3(sf.Positions[sv * 3], sf.Positions[sv * 3 + 1], sf.Positions[sv * 3 + 2]);
                        // diffuse: what the viewer draws for the source face, against the merged coordinates drawn with the merged face's own transform
                        Viewer(n, p, m.Scale, Of(src), planar, sf.Uvs[sv * 2], sf.Uvs[sv * 2 + 1], out float wantS, out float wantT);
                        Viewer(Vector3.UnitZ, Vector3.Zero, Vector3.One, Of(drawn), false, mf.Uvs[mv * 2], mf.Uvs[mv * 2 + 1], out float gotS, out float gotT);
                        worstDiffuse = Math.Max(worstDiffuse, Math.Max(Math.Abs(wantS - gotS), Math.Abs(wantT - gotT)));
                        corners++;
                        if (baked) bakedCorners++; else unbakedCorners++;
                        if (srcMaterial != null && srcMaterial.HasNormalMap)
                        {
                            // the normal map: the source material's transform on the same base coordinates, against the merged material's transform
                            Viewer(n, p, m.Scale, srcMaterial.NormalTransform, planar, sf.Uvs[sv * 2], sf.Uvs[sv * 2 + 1], out float ws, out float wt);
                            Viewer(Vector3.UnitZ, Vector3.Zero, Vector3.One, drawnMaterial.NormalTransform, false, mf.Uvs[mv * 2], mf.Uvs[mv * 2 + 1], out float gs, out float gt);
                            worstNormalTexels = Math.Max(worstNormalTexels, Math.Max(Math.Abs(ws - gs), Math.Abs(wt - gt)) * 512);
                            normalCorners++;
                            if (baked && !srcMaterial.NormalTransform.SameAs(Of(src)))
                                otherTransform++;
                        }
                        if (srcMaterial != null && srcMaterial.HasSpecularMap)
                        {
                            Viewer(n, p, m.Scale, srcMaterial.SpecularTransform, planar, sf.Uvs[sv * 2], sf.Uvs[sv * 2 + 1], out float ws, out float wt);
                            Viewer(Vector3.UnitZ, Vector3.Zero, Vector3.One, drawnMaterial.SpecularTransform, false, mf.Uvs[mv * 2], mf.Uvs[mv * 2 + 1], out float gs, out float gt);
                            worstSpecularTexels = Math.Max(worstSpecularTexels, Math.Max(Math.Abs(ws - gs), Math.Abs(wt - gt)) * 256);
                            specularCorners++;
                            if (baked && !srcMaterial.SpecularTransform.SameAs(Of(src)))
                                otherTransform++;
                        }
                    }
                }
            }
        }
        sb.AppendLine("corners " + corners + " (baked faces " + bakedCorners + ", faces drawn with their own transform " + unbakedCorners + "), normal-map corners " + normalCorners + ", specular-map corners " + specularCorners + ", map corners whose source map is drawn with a transform other than the face's " + otherTransform);
        sb.AppendLine("worst diffuse difference " + worstDiffuse.ToString("0.0e+0", CultureInfo.InvariantCulture) + " (limit 1e-5)");
        sb.AppendLine("worst normal-map difference " + worstNormalTexels.ToString("0.000", CultureInfo.InvariantCulture) + " texels of its 512 pixel map (limit 0.25)");
        sb.AppendLine("worst specular-map difference " + worstSpecularTexels.ToString("0.000", CultureInfo.InvariantCulture) + " texels of its 256 pixel map (limit 0.25)");
        return new CornerProof { Corners = corners, BakedCorners = bakedCorners, UnbakedCorners = unbakedCorners, NormalCorners = normalCorners, SpecularCorners = specularCorners,
            MapCornersDrawnWithOtherTransform = otherTransform, WorstDiffuse = worstDiffuse, WorstNormalTexels = worstNormalTexels, WorstSpecularTexels = worstSpecularTexels, Report = sb.ToString() };
    }

    // ---------------------------------------------------------------- the precision guard at each map's own size

    private static List<KitPart> OneObjectOfBigRepeats()
    {
        // a planar face drawn with 20 repeats runs over about 40 texture units on a 1 m box: far too wide for a 2048 pixel map to be stored to half a texel
        FaceTransform big = new FaceTransform(20f, 20f, 0f, 0f, 0f);
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(MapsFixture.Diffuse));
        Primitive.TextureEntryFace f = te.CreateFace(0);
        f.TextureID = new UUID(MapsFixture.Diffuse);
        f.MaterialID = new UUID(MapsFixture.MaterialA);
        f.TexMapType = MappingType.Planar;
        f.RepeatU = big.RepeatU; f.RepeatV = big.RepeatV;
        List<KitPart> parts = new List<KitPart>();
        for (int i = 0; i < 4; i++)
            parts.Add(new KitPart("Big " + (i + 1), i + 1) { Scale = "1,1,1", Offset = i == 0 ? "0,0,0" : (i * 1.2).ToString("0.###", CultureInfo.InvariantCulture) + ",0,0", TextureEntry = Convert.ToBase64String(te.GetBytes()) });
        return parts;
    }

    private static MergePlan PlanBigRepeats(int normalMapSize)
    {
        Dictionary<string, byte[]> assets = MapsFixture.Assets(normalMapSize: normalMapSize, specularMapSize: 64, diffuseSize: 64);
        assets["assets/" + MapsFixture.MaterialA + "_material.xml"] = MapsFixture.MaterialXml(MapsFixture.NormalMap, new FaceTransform(20f, 20f, 0f, 0f, 0f), MapsFixture.SpecularMap, new FaceTransform(20f, 20f, 0f, 0f, 0f));
        return ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { OneObjectOfBigRepeats() }, assets), new MergeOptions());
    }

    [Fact]
    public void ABigMapMakesTheCoordinatesTooWideForTheFaceAtThatMapsSize()
    {
        // the diffuse texture is 64 pixels, where the span stores to a hundredth of a texel; the normal map is 2048 pixels, where it does not
        MergePlan big = PlanBigRepeats(2048);
        Assert.Equal(0, big.MergedParts);
        Assert.Contains(big.Linksets[0].Kept.SelectMany(k => k.Rules), r => r.Code == "UV_PRECISION");
    }

    [Fact]
    public void ASmallMapKeepsTheSameFacesMerged()
    {
        MergePlan small = PlanBigRepeats(64);
        Assert.Equal(1, small.MergedParts);
        Assert.True(small.Linksets[0].Merged.Single().Slots.All(s => s.Baked));
    }
}
