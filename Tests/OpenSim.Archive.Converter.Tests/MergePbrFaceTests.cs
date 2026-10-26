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
using Vector3 = System.Numerics.Vector3;
using static OpenSim.Archive.Converter.Tests.ProofKit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Faces with PBR (glTF) materials (PbrFixture). A part names the material of each such face in its extra parameters and holds a face's
/// overrides in its material overrides, both by face number; a merged part carries them on the faces it makes. The viewer does not apply a
/// PBR face's texture transform to its coordinates (llface.cpp, getGeometryVolume: the transforms of a PBR face come from the material), so
/// the merged coordinates are the base ones. Each test builds its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergePbrFaceTests
{
    [Fact]
    public void TheFixtureStoresTheMaterialsAndOverridesItDescribes()
    {
        ArchiveMergePlanner.Plan(PbrFixture.Write(), new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed);
        Assert.Equal(PbrFixture.ObjectCount * PbrFixture.PartsPerObject, parsed.Count);
        foreach (ParsedPart pp in parsed.Values)
        {
            var (materials, overrides) = PbrOf(pp);
            Assert.Equal(new Dictionary<int, Guid> { [0] = PbrFixture.P1, [1] = PbrFixture.P1, [2] = PbrFixture.P2 }, materials);
            Assert.Equal(new Dictionary<int, string> { [1] = PbrFixture.O1, [2] = PbrFixture.O1 }, overrides);
        }
    }

    [Fact]
    public void TheFixtureCanBeWrittenToAFileForALoadIntoARegion()
    {
        string outPath = Environment.GetEnvironmentVariable("OARX_PBR_OUT");
        if (string.IsNullOrEmpty(outPath))
            return;
        File.Copy(PbrFixture.Write(), outPath, false);
    }

    [Fact]
    public void PbrFacesMergeAndTheirPartsLoseNothingToIt()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(PbrFixture.Write(), new MergeOptions());
        Assert.True(plan.KeptParts == 0, "parts kept: " + string.Join("; ", plan.Linksets.SelectMany(l => l.Kept).Select(k => k.Name + " " + string.Join(",", k.Rules.Select(r => r.Code)))));
        Assert.Equal(PbrFixture.ObjectCount, plan.MergedParts);
        foreach (LinksetPlan l in plan.Linksets)
        {
            MergedPartPlan merged = Assert.Single(l.Merged);
            Assert.Equal(PbrFixture.PartsPerObject, merged.Members.Count);
            // face 0, face 1 (the same material with an override), face 2 (another material, the same override) and the plain faces
            Assert.Equal(4, merged.Slots.Count);
            Assert.All(merged.Slots, s => Assert.True(s.Baked));
        }
    }

    [Fact]
    public void TheMergedPartNamesEachFacesMaterialAndKeepsItsOverrideExactly()
    {
        Written w = WriteAndRead(PbrFixture.Write());
        int pbrSlots = 0, overrideSlots = 0;
        for (int li = 0; li < w.Plan.Linksets.Count; li++)
            foreach (MergedPartPlan mp in w.Plan.Linksets[li].Merged)
            {
                List<MergeMember> members = Members(w, li, mp);
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                var (outMaterials, outOverrides) = PbrOf(w.OutParsed[mp.Id]);
                for (int slot = 0; slot < g.Faces.Count; slot++)
                {
                    (int mi, int fi) = g.Template[slot];
                    var (srcMaterials, srcOverrides) = PbrOf(w.Parsed[members[mi].PartId]);
                    Assert.Equal(srcMaterials.ContainsKey(fi), outMaterials.ContainsKey(slot));
                    Assert.Equal(srcOverrides.ContainsKey(fi), outOverrides.ContainsKey(slot));
                    if (srcMaterials.ContainsKey(fi))
                    {
                        Assert.Equal(srcMaterials[fi], outMaterials[slot]);
                        pbrSlots++;
                    }
                    if (srcOverrides.ContainsKey(fi))
                    {
                        Assert.Equal(srcOverrides[fi], outOverrides[slot]);
                        overrideSlots++;
                    }
                }
                Assert.Equal(3, outMaterials.Count);
                Assert.Equal(2, outOverrides.Count);
            }
        Assert.Equal(3 * PbrFixture.ObjectCount, pbrSlots);
        Assert.Equal(2 * PbrFixture.ObjectCount, overrideSlots);
    }

    [Fact]
    public void EveryCornerOfAPbrFaceIsWhereTheViewerDrawsItWithoutTheFacesTransform()
    {
        Written w = WriteAndRead(PbrFixture.Write());
        double worst = 0;
        long corners = 0, pbrCorners = 0, plainCorners = 0;
        for (int li = 0; li < w.Plan.Linksets.Count; li++)
            foreach (MergedPartPlan mp in w.Plan.Linksets[li].Merged)
            {
                Primitive.TextureEntry outTe = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
                List<MergeMember> members = Members(w, li, mp);
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                foreach (FaceRun run in g.Runs)
                {
                    MergeMember m = members[run.Member];
                    Primitive.TextureEntry te = new Primitive.TextureEntry(w.Parsed[m.PartId].TextureEntry, 0, w.Parsed[m.PartId].TextureEntry.Length);
                    Primitive.TextureEntryFace src = FaceOf(te, run.Face);
                    bool pbr = PbrOf(w.Parsed[m.PartId]).Materials.ContainsKey(run.Face);
                    Primitive.TextureEntryFace drawn = FaceOf(outTe, run.Slot);
                    Assert.Equal(MappingType.Default, drawn.TexMapType);
                    EncoderFace sf = m.Faces[run.Face], mf = g.Faces[run.Slot];
                    for (int k = 0; k < run.IndexCount; k++)
                    {
                        int sv = (int)sf.Indices[run.SourceFirstIndex + k], mv = (int)mf.Indices[run.FirstIndex + k];
                        Vector3 n = new Vector3(sf.Normals[sv * 3], sf.Normals[sv * 3 + 1], sf.Normals[sv * 3 + 2]);
                        Vector3 p = new Vector3(sf.Positions[sv * 3], sf.Positions[sv * 3 + 1], sf.Positions[sv * 3 + 2]);
                        // the viewer gives a PBR face the base coordinates (projected when it is planar) and an ordinary face its texture transform as well
                        Viewer(n, p, m.Scale, pbr ? FaceTransform.Identity : Of(src), src.TexMapType == MappingType.Planar, sf.Uvs[sv * 2], sf.Uvs[sv * 2 + 1], out float ws, out float wt);
                        Viewer(Vector3.UnitZ, Vector3.Zero, Vector3.One, Of(drawn), false, mf.Uvs[mv * 2], mf.Uvs[mv * 2 + 1], out float gs, out float gt);
                        worst = Math.Max(worst, Math.Max(Math.Abs(ws - gs), Math.Abs(wt - gt)));
                        corners++;
                        if (pbr) pbrCorners++; else plainCorners++;
                    }
                }
            }
        string summary = "corners " + corners + " (PBR faces " + pbrCorners + ", ordinary faces " + plainCorners + "), worst difference " + worst.ToString("0.0e+0", CultureInfo.InvariantCulture) + " (limit 1e-5)";
        string report = Environment.GetEnvironmentVariable("OARX_PBR_REPORT");
        if (!string.IsNullOrEmpty(report))
            File.WriteAllText(report, summary + Environment.NewLine);
        Assert.True(pbrCorners > 0 && plainCorners > 0, summary);
        Assert.True(worst < 1e-5, summary);
    }

    // ---------------------------------------------------------------- alpha

    private static string ArchiveOfOneFace(params (string Name, string Extra, string Overrides)[] parts)
    {
        List<KitPart> list = new List<KitPart>();
        for (int i = 0; i < parts.Length; i++)
            list.Add(PbrFixture.Part(parts[i].Name, i + 1, (i * 1.2).ToString("0.###", CultureInfo.InvariantCulture) + ",0,0", "1,1,1", "0,0,0,1", parts[i].Extra, parts[i].Overrides));
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { list }, PbrFixture.Assets());
    }

    private static (string, string) One(Guid material, string overrideData = null) =>
        PbrFixture.Blocks(new[] { (0, material) }, overrideData == null ? Array.Empty<(int, string)>() : new[] { (0, overrideData) });

    [Fact]
    public void APbrFaceBlendsWhenItsEffectiveAlphaModeIsBlend()
    {
        (string e1, string o1) = One(PbrFixture.P3Blend);                            // a blend material
        (string e2, string o2) = One(PbrFixture.P1, "{\"am\":1}");                   // an opaque material overridden to blend
        (string e3, string o3) = One(PbrFixture.P1, "{\"am\":0}");                   // opaque stays opaque
        (string e4, string o4) = One(PbrFixture.P3Blend, "{\"am\":0}");              // a blend material overridden to opaque
        (string e5, string o5) = One(Guid.Parse("4f111111-2222-4333-8444-555555555555")); // a material the archive does not hold: unknown, taken as blending
        string path = ArchiveOfOneFace(("blend", e1, o1), ("to blend", e2, o2), ("opaque", e3, o3), ("to opaque", e4, o4), ("unknown", e5, o5));
        ArchiveMergePlanner.Plan(path, new MergeOptions(), null, out _, out List<MergeLinkset> linksets);
        Dictionary<string, bool> alpha = linksets[0].Parts.ToDictionary(p => p.Name, p => p.Faces[0].Alpha);
        Assert.True(alpha["blend"]);
        Assert.True(alpha["to blend"]);
        Assert.False(alpha["opaque"]);
        Assert.False(alpha["to opaque"]);
        Assert.True(alpha["unknown"]);
    }

    // ---------------------------------------------------------------- what cannot be carried

    [Fact]
    public void MaterialDataThatCannotBeReadKeepsThePartAsItIs()
    {
        // an assignment block that claims three entries and holds one
        byte[] damaged = new byte[] { 1, 0x80, 0x00, 18, 0, 0, 0, 3, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        (string extra, string overrides) = PbrFixture.Blocks(new[] { (0, PbrFixture.P1) }, Array.Empty<(int, string)>());
        List<KitPart> parts = new List<KitPart>
        {
            PbrFixture.Part("damaged", 1, "0,0,0", "1,1,1", "0,0,0,1", Convert.ToBase64String(damaged), null),
            PbrFixture.Part("good 1", 2, "1.2,0,0", "1,1,1", "0,0,0,1", extra, overrides),
            PbrFixture.Part("good 2", 3, "2.4,0,0", "1,1,1", "0,0,0,1", extra, overrides)
        };
        MergePlan plan = ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, PbrFixture.Assets()), new MergeOptions());
        Assert.Contains(plan.Linksets[0].Kept, k => k.Name == "damaged" && k.Rules.Any(r => r.Code == "PBR_DATA"));
        Assert.Equal(2, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    [Fact]
    public void AnOverrideForAFaceThePartDoesNotHaveKeepsThePart()
    {
        (string extra, string overrides) = PbrFixture.Blocks(new[] { (0, PbrFixture.P1) }, new[] { (20, PbrFixture.O1) });
        (string goodExtra, string goodOverrides) = PbrFixture.Blocks(new[] { (0, PbrFixture.P1) }, new[] { (0, PbrFixture.O1) });
        List<KitPart> parts = new List<KitPart>
        {
            PbrFixture.Part("orphan", 1, "0,0,0", "1,1,1", "0,0,0,1", extra, overrides),
            PbrFixture.Part("good 1", 2, "1.2,0,0", "1,1,1", "0,0,0,1", goodExtra, goodOverrides),
            PbrFixture.Part("good 2", 3, "2.4,0,0", "1,1,1", "0,0,0,1", goodExtra, goodOverrides)
        };
        MergePlan plan = ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, PbrFixture.Assets()), new MergeOptions());
        Assert.Contains(plan.Linksets[0].Kept, k => k.Name == "orphan" && k.Rules.Any(r => r.Code == "PBR_DATA"));
    }

    // ---------------------------------------------------------------- the precision guard: the material scales its own textures

    private static MergePlan PlanWithScale(string scale)
    {
        string data = "{\"ti\":[{\"s\":[" + scale + "," + scale + "]}]}";
        (string extra, string overrides) = PbrFixture.Blocks(new[] { (0, PbrFixture.P1) }, new[] { (0, data) });
        List<KitPart> parts = Enumerable.Range(0, 4).Select(i => PbrFixture.Part("Scaled " + (i + 1), i + 1, (i * 1.2).ToString("0.###", CultureInfo.InvariantCulture) + ",0,0", "1,1,1", "0,0,0,1", extra, overrides)).ToList();
        return ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, PbrFixture.Assets()), new MergeOptions());
    }

    [Fact]
    public void AMaterialThatRepeatsItsTexturesManyTimesMakesTheCoordinatesTooWideForTheFace()
    {
        // the textures of a PBR material are not known (2048 pixels are assumed), and the material's own scale of 64 multiplies the mesh's storage error
        MergePlan plan = PlanWithScale("64");
        Assert.Equal(0, plan.MergedParts);
        Assert.Contains(plan.Linksets[0].Kept.SelectMany(k => k.Rules), r => r.Code == "UV_PRECISION");
    }

    [Fact]
    public void AMaterialThatDoesNotScaleItsTexturesMerges()
    {
        MergePlan plan = PlanWithScale("1");
        Assert.Equal(1, plan.MergedParts);
    }
}
