/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// glTF PBR materials in oar-import: with the option on, every face that has a material in the .glb gets a PBR material asset (base colour, normal, occlusion
/// roughness metallic and emissive pictures as bare JPEG 2000 codestreams, and the factors) and a face reference in the part's ExtraParams, beside the legacy
/// texture and material the face has always had. The slab folder has face 0 = "top" and face 1 = "side". Each test writes its own folder and archive, so the
/// class runs in parallel.
/// </summary>
public class PbrImportTests
{
    private static readonly byte[] Base = ImportKit.Png(200, 40, 10, 16), Normal = ImportKit.Png(128, 128, 255, 16), Orm = ImportKit.Png(250, 120, 30, 16), Glow = ImportKit.Png(90, 80, 70, 16);

    private static void Rich(ImportMaterial top, ImportMaterial side)
    {
        side.Picture = Base;
        side.Normal = Normal;
        side.MetalRough = Orm;
        side.EmissiveImage = Glow;
        side.Emissive = new[] { 1f, 0.5f, 0.25f };
        side.Metallic = 0.25f;
        side.Roughness = 0.5f;
        side.AlphaMode = "MASK";
        side.AlphaCutoff = 0.375f;
        side.DoubleSided = false;
        side.Color = new[] { 0.2f, 0.4f, 0.6f, 0.5f };
    }

    private static (ImportResult Result, Dictionary<string, byte[]> Oar, XElement Part) Run(Action<ImportMaterial, ImportMaterial> configure = null, bool option = true, string topKeys = null)
    {
        MaterialKit.Built b = MaterialKit.Write(topKeys: topKeys, configure: configure ?? Rich);
        (ImportResult result, Dictionary<string, byte[]> oar) = MaterialKit.Import(b, pbr: option);
        return (result, oar, MaterialKit.Parts(oar)[0]);
    }

    private static PbrMaterialData Material(Dictionary<string, byte[]> oar, XElement part, int face)
    {
        Dictionary<int, Guid> faces = MaterialKit.PbrFaces(part);
        Assert.True(faces.TryGetValue(face, out Guid id), "face " + face + " has no PBR material");
        PbrMaterialData data = PbrMaterialAsset.Parse(MaterialKit.Asset(oar, id, "_prmat.dat"));
        Assert.NotNull(data);
        return data;
    }

    // ------------------------------------------------------------------ the asset format

    [Fact]
    public void ABuiltMaterialAssetReadsBackWithEveryFieldEqual()
    {
        Guid b = Guid.NewGuid(), n = Guid.NewGuid(), o = Guid.NewGuid(), e = Guid.NewGuid();
        PbrMaterialData data = new PbrMaterialData
        {
            BaseColor = new[] { 0.25f, 0.5f, 0.75f, 0.5f }, Metallic = 0.125f, Roughness = 0.875f, Emissive = new[] { 1f, 0.5f, 0f },
            AlphaMode = "BLEND", AlphaCutoff = 0.25f, DoubleSided = true,
            BaseColorTexture = b, NormalTexture = n, MetalRoughTexture = o, EmissiveTexture = e
        };
        PbrMaterialData back = PbrMaterialAsset.Parse(PbrMaterialAsset.Build(data));
        Assert.NotNull(back);
        Assert.Equal(data.BaseColor, back.BaseColor);
        Assert.Equal(0.125f, back.Metallic);
        Assert.Equal(0.875f, back.Roughness);
        Assert.Equal(data.Emissive, back.Emissive);
        Assert.Equal("BLEND", back.AlphaMode);
        Assert.Equal(0.25f, back.AlphaCutoff);
        Assert.True(back.DoubleSided);
        Assert.Equal((b, n, o, e), (back.BaseColorTexture, back.NormalTexture, back.MetalRoughTexture, back.EmissiveTexture));
    }

    [Fact]
    public void TheAssetIsTheViewersBinaryLlsdMapWithTheGltfTextInIt()
    {
        byte[] asset = PbrMaterialAsset.Build(new PbrMaterialData { BaseColorTexture = Guid.Parse("11111111-2222-4333-8444-555555555555") });
        Assert.NotEmpty(asset);
        Assert.StartsWith("<? LLSD/Binary ?>", Encoding.ASCII.GetString(asset, 0, Math.Min(asset.Length, 17)));
        OpenMetaverse.StructuredData.OSDMap map = Assert.IsType<OpenMetaverse.StructuredData.OSDMap>(OpenMetaverse.StructuredData.OSDParser.Deserialize(asset));
        Assert.Equal("1.1", map["version"].AsString());
        Assert.Equal("GLTF 2.0", map["type"].AsString());
        string text = map["data"].AsString();
        Assert.Contains("\"images\"", text);
        Assert.Contains("11111111-2222-4333-8444-555555555555", text);
    }

    private static System.Text.Json.Nodes.JsonNode GltfText(byte[] asset) =>
        System.Text.Json.Nodes.JsonNode.Parse(((OpenMetaverse.StructuredData.OSDMap)OpenMetaverse.StructuredData.OSDParser.Deserialize(asset))["data"].AsString());

    [Fact]
    public void TheOcclusionPictureIsTheMetalRoughPictureBecauseTheViewerTakesOcclusionFromTheSameTexture()
    {
        Guid orm = Guid.NewGuid();
        System.Text.Json.Nodes.JsonNode withMr = GltfText(PbrMaterialAsset.Build(new PbrMaterialData { MetalRoughTexture = orm, BaseColorTexture = Guid.NewGuid() }));
        System.Text.Json.Nodes.JsonNode material = withMr["materials"][0];
        Assert.NotNull(material["occlusionTexture"]);
        Assert.Equal((int)material["pbrMetallicRoughness"]["metallicRoughnessTexture"]["index"], (int)material["occlusionTexture"]["index"]);
        System.Text.Json.Nodes.JsonNode without = GltfText(PbrMaterialAsset.Build(new PbrMaterialData { BaseColorTexture = Guid.NewGuid() }));
        Assert.Null(without["materials"][0]["occlusionTexture"]);
    }

    [Fact]
    public void AMaterialTextThatIsPlainGltfJsonReadsToo()
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"11111111-2222-4333-8444-555555555555\"}],\"textures\":[{\"source\":0}]," +
                      "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0.5,\"roughnessFactor\":0.25},\"doubleSided\":true}]}";
        PbrMaterialData data = PbrMaterialAsset.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(data);
        Assert.Equal(Guid.Parse("11111111-2222-4333-8444-555555555555"), data.BaseColorTexture);
        Assert.Equal(0.5f, data.Metallic);
        Assert.Equal(0.25f, data.Roughness);
        Assert.True(data.DoubleSided);
    }

    [Fact]
    public void ABrokenMaterialAssetReadsAsNothing()
    {
        Assert.Null(PbrMaterialAsset.Parse(Encoding.ASCII.GetBytes("not a material")));
        Assert.Null(PbrMaterialAsset.Parse(Array.Empty<byte>()));
    }

    // ------------------------------------------------------------------ what the import writes

    [Fact]
    public void WithTheOptionOffNoPbrMaterialIsWrittenAndNoFaceReferencesOne()
    {
        (_, Dictionary<string, byte[]> oar, XElement part) = Run(option: false);
        Assert.DoesNotContain(oar.Keys, k => k.EndsWith("_prmat.dat", StringComparison.Ordinal));
        Assert.Empty(MaterialKit.PbrFaces(part));
    }

    [Fact]
    public void WithTheOptionOnEveryFaceThatHasAMaterialGetsOneInTheExtraParams()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, XElement part) = Run();
        Dictionary<int, Guid> faces = MaterialKit.PbrFaces(part);
        Assert.Equal(new[] { 0, 1 }, faces.Keys.OrderBy(k => k));
        Assert.NotEqual(faces[0], faces[1]);
        foreach (Guid id in faces.Values)
        {
            Assert.True(oar.ContainsKey("assets/" + id.ToString("D") + "_prmat.dat"));
            Assert.Contains(id, result.PbrMaterials);
        }
        Assert.Equal(2, oar.Keys.Count(k => k.EndsWith("_prmat.dat", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheManifestKeyTurnsTheOptionOnWithoutTheCommandLine()
    {
        (_, _, XElement part) = Run(option: false, topKeys: "\"pbr\": true");
        Assert.Equal(2, MaterialKit.PbrFaces(part).Count);
    }

    [Fact]
    public void ThePbrMaterialCarriesEveryFieldOfTheGlbMaterialAndItsPicturesAreBareCodestreamsThatDecodeToThePictures()
    {
        (_, Dictionary<string, byte[]> oar, XElement part) = Run();
        PbrMaterialData m = Material(oar, part, 1);
        Assert.Equal(new[] { 0.2f, 0.4f, 0.6f, 0.5f }, m.BaseColor);
        Assert.Equal(0.25f, m.Metallic);
        Assert.Equal(0.5f, m.Roughness);
        Assert.Equal(new[] { 1f, 0.5f, 0.25f }, m.Emissive);
        Assert.Equal("MASK", m.AlphaMode);
        Assert.Equal(0.375f, m.AlphaCutoff);
        Assert.False(m.DoubleSided);
        foreach ((Guid id, byte[] png, string what) in new[] { (m.BaseColorTexture, Base, "base colour"), (m.NormalTexture, Normal, "normal"), (m.MetalRoughTexture, Orm, "occlusion roughness metallic"), (m.EmissiveTexture, Glow, "emissive") })
        {
            Assert.NotEqual(Guid.Empty, id);
            byte[] asset = MaterialKit.Asset(oar, id, "_texture.jp2");
            Assert.True(asset.AsSpan(0, 4).SequenceEqual(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }), what + " is not a bare codestream");
            Assert.Equal(MaterialKit.PngPixels(png), MaterialKit.Pixels(asset));
        }
    }

    [Fact]
    public void AFaceKeepsItsLegacyTextureAndMaterialBesideThePbrMaterial()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, XElement part) = Run();
        PbrMaterialData m = Material(oar, part, 1);
        Primitive.TextureEntryFace face = MaterialKit.Face(part, 1);
        Assert.Equal(m.BaseColorTexture, face.TextureID.Guid);
        Assert.NotEqual(UUID.Zero, face.MaterialID);
        OpenMetaverse.StructuredData.OSDMap legacy = Assert.IsType<OpenMetaverse.StructuredData.OSDMap>(OpenMetaverse.StructuredData.OSDParser.Deserialize(MaterialKit.Asset(oar, face.MaterialID.Guid, "_material.xml")));
        Assert.Equal(m.NormalTexture, legacy["NormMap"].AsUUID().Guid);
        Assert.Contains(result.Textures, t => t.Id == m.MetalRoughTexture && t.Role == "occlusion roughness metallic");
        Assert.Contains(result.Textures, t => t.Id == m.EmissiveTexture && t.Role == "emissive");
    }

    [Fact]
    public void TheExtraParamsKeepTheMeshBlockBesideThePbrBlock()
    {
        (_, _, XElement part) = Run();
        byte[] extra = Convert.FromBase64String(ImportKit.Text(part, "Shape/ExtraParams"));
        Dictionary<int, int> blocks = ExtraParamBlocks(extra);
        Assert.Equal(2, extra[0]);
        Assert.True(blocks.ContainsKey(0x30), "no sculpt (mesh) block");
        Assert.True(blocks.ContainsKey(0x80), "no PBR block");
        Assert.Equal(new Guid(ImportKit.Text(part, "Shape/SculptTexture/UUID")), new UUID(extra, blocks[0x30] + 6).Guid);
    }

    /// <summary>The block types of an ExtraParams value, each with the position of its header.</summary>
    private static Dictionary<int, int> ExtraParamBlocks(byte[] extra)
    {
        Dictionary<int, int> blocks = new Dictionary<int, int>();
        int at = 1;
        for (int i = 0; i < extra[0]; i++)
        {
            blocks[BitConverter.ToUInt16(extra, at)] = at;
            at += 6 + (int)BitConverter.ToUInt32(extra, at + 2);
        }
        return blocks;
    }

    [Fact]
    public void AGlbMaterialWithNoFactorsTakesTheGltfDefaultsOfFullMetalAndFullRoughness()
    {
        (_, Dictionary<string, byte[]> oar, XElement part) = Run(configure: (top, side) => { side.Metallic = null; side.Roughness = null; });
        PbrMaterialData m = Material(oar, part, 1);
        Assert.Equal(0f, m.Metallic);   // the test file writes metallicFactor 0 itself
        Assert.Equal(1f, m.Roughness);  // roughnessFactor absent: the glTF default
        Assert.Equal(new[] { 0f, 0f, 0f }, m.Emissive);
        Assert.Equal("OPAQUE", m.AlphaMode);
    }

    [Fact]
    public void TheSameMaterialInTwoPartsIsOneAssetAndTheIdsAreTheSameOnEveryRun()
    {
        MaterialKit.Built b = MaterialKit.Write(childKeys: "", configure: Rich);
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b, pbr: true);
        (_, Dictionary<string, byte[]> again) = MaterialKit.Import(b, pbr: true);
        List<XElement> parts = MaterialKit.Parts(oar);
        Assert.Equal(MaterialKit.PbrFaces(parts[0]), MaterialKit.PbrFaces(parts[1]));
        Assert.Equal(2, oar.Keys.Count(k => k.EndsWith("_prmat.dat", StringComparison.Ordinal)));
        Assert.Equal(MaterialKit.PbrFaces(parts[0]), MaterialKit.PbrFaces(MaterialKit.Parts(again)[0]));
        foreach (Guid id in MaterialKit.PbrFaces(parts[0]).Values)
            Assert.Equal(oar["assets/" + id.ToString("D") + "_prmat.dat"], again["assets/" + id.ToString("D") + "_prmat.dat"]);
    }

    [Fact]
    public void ATextureInThePbrMaterialThatTheFaceAlreadyUsesIsNotWrittenTwice()
    {
        (_, Dictionary<string, byte[]> oar, _) = Run();
        // base, normal, occlusion roughness metallic and emissive of the side; the base of the top; nothing is stored twice
        int textures = oar.Keys.Count(k => k.EndsWith("_texture.jp2", StringComparison.Ordinal));
        Assert.Equal(oar.Keys.Where(k => k.EndsWith("_texture.jp2", StringComparison.Ordinal)).Distinct().Count(), textures);
        Assert.Equal(5, textures);
    }
}
