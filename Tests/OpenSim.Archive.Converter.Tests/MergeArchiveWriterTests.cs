/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The merged archive, read back: parts, link numbers, mesh, texture entry, manifest notecard, and the numbers that say how close the
/// merged mesh is to the parts it replaced. Each test writes its own archives; nothing is shared, so the class runs in parallel.
/// </summary>
public class MergeArchiveWriterTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "merge-write-tests", Guid.NewGuid().ToString("N"));

    /// <summary>A root and four children in a row; the part numbered <paramref name="scripted"/> holds a script and stays as it is.</summary>
    private static (string archive, List<KitPart> parts) Build(int scripted = 3, Action<List<KitPart>> change = null)
    {
        List<KitPart> parts = Enumerable.Range(1, 5).Select(i => new KitPart("Box " + i, i)
        {
            Offset = (i * 2) + "," + (i % 2) + ",0.5", Scale = (1 + i * 0.25f).ToString(CultureInfo.InvariantCulture) + ",1,0.5"
        }).ToList();
        parts[0].Offset = "0,0,0";
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        if (scripted > 0)
        {
            var script = EligibilityKit.Script("lamp", "default { state_entry() { llSay(0, \"hi\"); } }");
            parts[scripted - 1].Items.Add(script.item);
            assets[script.entry] = script.data;
        }
        change?.Invoke(parts);
        return (EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets), parts);
    }

    private static Dictionary<Guid, ParsedPart> Read(string path)
    {
        Dictionary<Guid, ParsedPart> d = new Dictionary<Guid, ParsedPart>();
        new ArchiveEligibility(new EligibilityOptions { PartObserver = pp => d.TryAdd(pp.Part.PartId.Guid, pp) }).Evaluate(path);
        return d;
    }

    private static byte[] Entry(string archive, string name)
    {
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            if (h.Name == name)
                return tar.ReadBody();
        return null;
    }

    private static MergeWriteResult Merge(string source, out string output)
    {
        output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        return MergeArchiveWriter.Write(source, output);
    }

    [Fact]
    public void TheMergedArchiveHoldsThePlannedPartsWithContiguousLinkNumbersAndTheKeptPartKeepsItsScript()
    {
        (string src, List<KitPart> parts) = Build();
        MergeWriteResult r = Merge(src, out string output);
        LinksetPlan plan = Assert.Single(r.Plan.Linksets);
        Dictionary<Guid, ParsedPart> after = Read(output);

        Assert.Equal(plan.PartsAfter, after.Count);
        Assert.Equal(Enumerable.Range(1, plan.PartsAfter), after.Values.Select(p => p.Part.LinkNum).OrderBy(n => n));
        Assert.All(plan.Merged, m => Assert.Equal(m.NewLinkNum, after[m.Id].Part.LinkNum));
        Assert.All(plan.Kept, k => Assert.Equal(k.NewLinkNum, after[k.PartId].Part.LinkNum));
        Assert.Contains(after[parts[2].Id].Items, i => i.Name == "lamp" && i.AssetType == 10);
        Assert.True(after[parts[0].Id].Part.IsRoot);
        Assert.False(after.ContainsKey(parts[3].Id), "a part that went into a merged part is gone");
    }

    [Fact]
    public void AMergedPartIsAMeshPartWhoseMeshDecodesWithOneFacePerMaterialSlot()
    {
        (string src, List<KitPart> parts) = Build();
        MergeWriteResult r = Merge(src, out string output);
        MergedPartPlan merged = r.Plan.Linksets[0].Merged.Single(m => m.Members.Any(x => x.PartId == parts[0].Id));
        ParsedPart p = Read(output)[merged.Id];
        Assert.True(p.Shape.SculptEntry);
        Assert.Equal((int)SculptType.Mesh, p.Shape.SculptType);
        byte[] mesh = Entry(output, "assets/" + p.Shape.SculptTexture.ToString() + "_mesh.llmesh");
        Assert.NotNull(mesh);
        MeshAssetModel model = MeshCodec.Decode(mesh);
        Assert.Equal(merged.Slots.Count, model.High.Faces.Count);
        Primitive.TextureEntry te = new Primitive.TextureEntry(p.TextureEntry, 0, p.TextureEntry.Length);
        for (int slot = 0; slot < merged.Slots.Count; slot++)
        {
            Primitive.TextureEntryFace face = te.FaceTextures[slot] ?? te.DefaultTexture;
            Assert.NotNull(face);
            Assert.Equal(1f, face.RepeatU, 1e-4f); // baked into the coordinates
        }
        Assert.Equal(r.Parts.Single(x => x.Id == merged.Id).MeshId.ToString(), p.Shape.SculptTexture.ToString());
    }

    [Fact]
    public void AMergedPartThatIsNotTheRootSitsAtTheCentreOfItsMembersWithTheirBoxAsItsSizeAndTheRootIsUntouched()
    {
        (string src, List<KitPart> parts) = Build(scripted: 1);
        MergeWriteResult r = Merge(src, out string output);
        MergedPartPlan m = Assert.Single(r.Plan.Linksets[0].Merged);
        Assert.Equal(parts[1].Id, m.Id);
        Assert.Equal(2, m.NewLinkNum);
        Dictionary<Guid, ParsedPart> after = Read(output);
        ParsedPart p = after[m.Id];
        // the members' boxes: x from 3.25 to 11.125, y from -0.5 to 1.5, z from 0.25 to 0.75
        Assert.Equal(7.1875f, p.OffsetPosition.X, 1e-3f);
        Assert.Equal(0.5f, p.OffsetPosition.Y, 1e-3f);
        Assert.Equal(0.5f, p.OffsetPosition.Z, 1e-3f);
        Assert.Equal(7.875f, p.Scale.X, 1e-3f);
        Assert.Equal(2f, p.Scale.Y, 1e-3f);
        Assert.Equal(0.5f, p.Scale.Z, 1e-3f);
        Assert.Equal(1f, p.RotationOffset.W, 1e-5f);
        Assert.Equal(new OpenMetaverse.Vector3(1.25f, 1, 0.5f), after[parts[0].Id].Scale);
    }

    [Fact]
    public void ARootThatTakesPartInTheMergeKeepsItsPlaceAndGrowsToHoldTheMergedGeometry()
    {
        (string src, List<KitPart> parts) = Build(scripted: 0);
        MergeWriteResult r = Merge(src, out string output);
        MergedPartPlan m = Assert.Single(r.Plan.Linksets[0].Merged);
        Assert.Equal(parts[0].Id, m.Id);
        Dictionary<Guid, ParsedPart> after = Read(output);
        ParsedPart root = Assert.Single(after).Value;
        Assert.True(root.Part.IsRoot);
        Assert.True(root.Scale.X >= 20f, "the box reaches both sides of the root's origin: " + root.Scale);
        Assert.True(r.Parts.Single().TookTheRoot);
    }

    [Fact]
    public void TheManifestNotecardIsInTheMergedPartAndNamesEveryMember()
    {
        (string src, _) = Build();
        MergeWriteResult r = Merge(src, out string output);
        MergedPartPlan merged = r.Plan.Linksets[0].Merged.Single();
        ParsedPart p = Read(output)[merged.Id];
        ItemRecord note = Assert.Single(p.Items, i => i.AssetType == 7);
        Assert.Equal("Merge manifest", note.Name);
        string text = Encoding.UTF8.GetString(Entry(output, "assets/" + note.AssetId + "_notecard.txt"));
        Assert.StartsWith("Linden text version 2", text);
        foreach (MemberPlan x in merged.Members)
        {
            Assert.Contains(x.Name, text);
            Assert.Contains(x.PartId.ToString(), text);
        }
    }

    [Fact]
    public void TheMergedMeshIsWithinAMicrometreAndAHundred_thousandthOfATextureOfTheOriginalsBeforeItIsEncoded()
    {
        (string src, _) = Build(scripted: 0, change: p =>
        {
            p[1].Rotation = "0,0,0.38,0.92";
            p[3].Rotation = "0.2,0,0,0.98";
            p[3].TextureEntry = Convert.ToBase64String(new Primitive.TextureEntry(UUID.Random()).GetBytes());
        });
        MergeWriteResult r = Merge(src, out _);
        MergedPartReport rep = Assert.Single(r.Parts);
        Assert.True(rep.Corners > 100);
        Assert.True(rep.WorstPositionMeters < 1e-5, "position " + rep.WorstPositionMeters);
        Assert.True(rep.WorstUv < 1e-5, "uv " + rep.WorstUv);
        Assert.True(rep.WorstNormal < 1e-4, "normal " + rep.WorstNormal);
        // the asset stores positions in 16 bits over the mesh's range (about 24 m here): a fraction of a millimetre
        Assert.True(rep.WorstPositionMetersDecoded < 1e-3, "decoded position " + rep.WorstPositionMetersDecoded);
        Assert.True(rep.WorstUvDecoded < 1e-3, "decoded uv " + rep.WorstUvDecoded);
    }

    [Fact]
    public void PlanarFacesOfAnArchiveAreBakedCountedAndDrawnWithDefaultMapping()
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(UUID.Random());
        te.DefaultTexture.TexMapType = MappingType.Planar;
        te.DefaultTexture.RepeatU = 2f;
        te.DefaultTexture.OffsetV = 0.25f;
        (string src, _) = Build(scripted: 0, change: p =>
        {
            foreach (KitPart k in p)
                k.TextureEntry = Convert.ToBase64String(te.GetBytes());
            p[1].Rotation = "0,0,0.38,0.92";
        });
        MergeWriteResult r = Merge(src, out string output);
        MergedPartReport rep = Assert.Single(r.Parts);
        Assert.Equal(30, rep.PlanarFaces);
        Assert.Equal(0, rep.UnbakedFaces);
        Assert.True(rep.WorstUv < 1e-5, "uv " + rep.WorstUv);
        ParsedPart p0 = Read(output).Values.Single();
        Primitive.TextureEntry after = new Primitive.TextureEntry(p0.TextureEntry, 0, p0.TextureEntry.Length);
        Assert.Equal(MappingType.Default, (after.FaceTextures[0] ?? after.DefaultTexture).TexMapType);
    }

    [Fact]
    public void TwoMergedPartsWithTheSameMeshShareOneAsset()
    {
        (string src, List<KitPart> first) = Build(scripted: 0);
        // the same row of boxes again, as a second object
        List<KitPart> second = first.Select(k => new KitPart(k.Name, k.LinkNum) { Offset = k.Offset, Scale = k.Scale }).ToList();
        string both = EligibilityKit.WriteArchive(new List<List<KitPart>> { first, second });
        MergeWriteResult r = Merge(both, out string output);
        Assert.Equal(2, r.Parts.Count);
        Assert.Equal(r.Parts[0].MeshId, r.Parts[1].MeshId);
        Assert.Equal(1, r.Rewrite.AddedAssets.Count(a => a.EndsWith("_mesh.llmesh", StringComparison.Ordinal)));
        Assert.Equal(2, Read(output).Count);
    }

    [Fact]
    public void TheSourceArchiveIsNotChangedAndAnOutputThatExistsIsRefused()
    {
        (string src, _) = Build();
        byte[] before = File.ReadAllBytes(src);
        Merge(src, out string output);
        Assert.Equal(before, File.ReadAllBytes(src));
        Assert.ThrowsAny<IOException>(() => MergeArchiveWriter.Write(src, output));
    }
}
