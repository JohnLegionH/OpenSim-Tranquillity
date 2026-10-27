/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Restore.Import;
using Xunit;
using static OpenSim.Archive.Converter.Tests.ProofKit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// An archive saved by a grid that has PBR content holds each material asset as the viewer uploads it: a binary LLSD map around the glTF text. The merge planner
/// and the glTF export read the material through that map. Archives here are made by oar-import with its PBR option, which writes the viewer's form. Each test
/// writes its own files, so the class runs in parallel.
/// </summary>
public class PbrArchiveToolsTests
{
    private static readonly byte[] Base = ImportKit.Png(200, 40, 10, 16), Normal = ImportKit.Png(128, 128, 255, 16), Orm = ImportKit.Png(250, 120, 30, 16), Glow = ImportKit.Png(90, 80, 70, 16);

    /// <summary>A material asset as the viewer writes it: the header line and a binary LLSD map with the keys version, type and data.</summary>
    private static byte[] Wrap(string gltf, string version = "1.1", string type = "GLTF 2.0")
    {
        byte[] header = Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n");
        byte[] body = OSDParser.SerializeLLSDBinary(new OSDMap { ["data"] = gltf, ["type"] = type, ["version"] = version }, false);
        return header.Concat(body).ToArray();
    }

    private const string Blend = "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"11111111-2222-4333-8444-555555555555\"}],\"textures\":[{\"source\":0}],\"materials\":[{\"pbrMetallicRoughness\":" +
                                 "{\"baseColorTexture\":{\"index\":0,\"extensions\":{\"KHR_texture_transform\":{\"scale\":[3,2]}}}},\"alphaMode\":\"BLEND\"}]}";
    private const string Opaque = "{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"pbrMetallicRoughness\":{}}]}";

    [Fact]
    public void TheMergePlannerReadsTheAlphaModeAndTheTextureScaleOfAWrappedMaterial()
    {
        PbrMaterialInfo blend = PbrFaceData.ReadMaterial(Wrap(Blend));
        Assert.NotNull(blend);
        Assert.True(blend.Blend);
        Assert.Equal(3f, blend.TextureScale);
        PbrMaterialInfo opaque = PbrFaceData.ReadMaterial(Wrap(Opaque, version: "1.0"));
        Assert.NotNull(opaque);
        Assert.False(opaque.Blend);
        Assert.Equal(1f, opaque.TextureScale);
    }

    [Fact]
    public void AWrappedMaterialTheViewerWouldRefuseReadsAsNothing()
    {
        Assert.Null(PbrFaceData.ReadMaterial(Wrap(Blend, version: "2.0")));
        Assert.Null(PbrFaceData.ReadMaterial(Wrap(Blend, type: "GLTF 1.0")));
    }

    private static string ImportedArchive(Action<ImportMaterial, ImportMaterial> configure, string childKeys = null)
    {
        MaterialKit.Built built = MaterialKit.Write(childKeys: childKeys, configure: configure);
        string output = ImportKit.OutputPath();
        ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), output, MaterialKit.Options(pbr: true));
        Assert.True(File.Exists(output), "the archive was not written");
        return output;
    }

    private static void Pictures(ImportMaterial top, ImportMaterial side)
    {
        top.Picture = Base;
        side.Picture = Base;
        side.Normal = Normal;
        side.MetalRough = Orm;
        side.EmissiveImage = Glow;
        side.Emissive = new[] { 1f, 0.5f, 0.25f };
    }

    [Fact]
    public void AFaceIsDrawnThroughTheAlphaPoolOnlyWhenItsWrappedMaterialBlends()
    {
        // the fixture's materials, each stored as the viewer stores one
        Dictionary<string, byte[]> assets = PbrFixture.Assets();
        foreach (string name in assets.Keys.Where(k => k.EndsWith("_prmat.dat", StringComparison.Ordinal)).ToList())
            assets[name] = Wrap(Encoding.UTF8.GetString(assets[name]));
        List<KitPart> parts = new List<KitPart>();
        int n = 0;
        foreach ((string name, Guid material) in new[] { ("blend", PbrFixture.P3Blend), ("opaque", PbrFixture.P1) })
        {
            (string extra, string overrides) = PbrFixture.Blocks(new[] { (0, material) }, Array.Empty<(int, string)>());
            parts.Add(PbrFixture.Part(name, ++n, ((n - 1) * 1.2).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",0,0", "1,1,1", "0,0,0,1", extra, overrides));
        }
        string path = EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets);
        ArchiveMergePlanner.Plan(path, new MergeOptions(), null, out _, out List<MergeLinkset> linksets);
        Dictionary<string, bool> alpha = linksets[0].Parts.ToDictionary(p => p.Name, p => p.Faces[0].Alpha);
        Assert.True(alpha["blend"]);
        Assert.False(alpha["opaque"]);
    }

    [Fact]
    public void AnImportedArchiveGoesThroughMergeWithEveryMaterialAssetAndFaceReferenceIntact()
    {
        string path = ImportedArchive(Pictures, childKeys: "");
        Written w = WriteAndRead(path);
        Dictionary<string, byte[]> source = ImportKit.ReadOar(w.Source), merged = ImportKit.ReadOar(w.Output);
        string[] materials = source.Keys.Where(k => k.EndsWith("_prmat.dat", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(materials);
        foreach (string name in materials)
        {
            Assert.True(merged.TryGetValue(name, out byte[] bytes), "the merged archive lost " + name);
            Assert.Equal(source[name], bytes);
        }
        HashSet<Guid> ids = materials.Select(k => Guid.Parse(k.Substring("assets/".Length, 36))).ToHashSet();
        int referenced = 0;
        foreach (ParsedPart pp in w.OutParsed.Values)
            foreach (Guid id in PbrOf(pp).Materials.Values)
            {
                Assert.Contains(id, ids);
                referenced++;
            }
        Assert.True(referenced > 0, "no face of the merged archive names a PBR material");
    }

    /// <summary>The JSON chunk of a binary glTF file.</summary>
    private static JsonElement GlbJson(byte[] glb)
    {
        int length = BitConverter.ToInt32(glb, 12);
        Assert.Equal("JSON", Encoding.ASCII.GetString(glb, 16, 4));
        return JsonDocument.Parse(glb.AsMemory(20, length)).RootElement.Clone();
    }

    [Fact]
    public void AnImportedArchiveExportsItsMaterialsWithTheirPicturesAndTheirGltfText()
    {
        string archive = ImportedArchive(Pictures);
        string dir = Path.Combine(Path.GetTempPath(), "pbr-export-tests", Guid.NewGuid().ToString("N"));
        ExportSummary s = ArchiveExporter.Export(archive, new ExportOptions { SelfContained = true, OutputDirectory = Path.Combine(dir, "out") });
        SummaryBuild b = Assert.Single(s.BuildList);
        string folder = Path.Combine(dir, "out", b.Folder);
        JsonElement manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement;
        JsonElement[] roles = manifest.GetProperty("textures").EnumerateArray().Where(t => t.GetProperty("role").GetString().StartsWith("pbr-")).ToArray();
        Assert.Equal(new[] { "pbr-base-color", "pbr-emissive", "pbr-metallic-roughness", "pbr-normal" }, roles.Select(t => t.GetProperty("role").GetString()).Distinct().OrderBy(x => x));
        Assert.All(roles, t => Assert.Equal("embedded", t.GetProperty("status").GetString()));

        JsonElement root = GlbJson(File.ReadAllBytes(Path.Combine(folder, b.File)));
        JsonElement m = root.GetProperty("materials").EnumerateArray().First(x => x.GetProperty("extras").TryGetProperty("pbr", out JsonElement p) && p.TryGetProperty("textures", out _));
        Assert.True(m.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
        Assert.True(m.GetProperty("pbrMetallicRoughness").TryGetProperty("metallicRoughnessTexture", out _));
        Assert.True(m.TryGetProperty("normalTexture", out _));
        Assert.True(m.TryGetProperty("emissiveTexture", out _));
        JsonElement pbr = m.GetProperty("extras").GetProperty("pbr");
        Assert.Equal("read", pbr.GetProperty("materialStatus").GetString());
        // the stored material is carried as its glTF text, not as the binary map around it
        JsonElement carried = JsonDocument.Parse(pbr.GetProperty("material").GetString()).RootElement;
        Assert.Equal("2.0", carried.GetProperty("asset").GetProperty("version").GetString());
        Assert.True(carried.GetProperty("materials").GetArrayLength() > 0);
    }
}
