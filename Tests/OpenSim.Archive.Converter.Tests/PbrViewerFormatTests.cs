/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The PBR material asset and the face reference oar-import writes, checked against what the Firestorm viewer writes and reads. The viewer writes the asset in
/// LLMaterialEditor::getEncodedAsset (llmaterialeditor.cpp:1241-1254) as a binary LLSD map; LLSDBinaryFormatter::format_impl (llsdserialize.cpp:1538-1643)
/// lays a map out as '{', a big-endian count, then 'k', length, key and the value for each key in the order of a std::map (sorted), then '}'. The glTF text
/// comes from LLGLTFMaterial::writeToModel (llgltfmaterial.cpp:309-370) and writeToTexture (llgltfmaterial_templates.h:88-142): one image and one texture for
/// each picture the material uses, in the order base colour, normal, metallic-roughness, emissive, occlusion, with the uuid as the image uri. It reads the asset
/// back in LLGLTFMaterialList::onAssetLoadComplete (llgltfmateriallist.cpp:559-613) and gltf_get_texture_image (llgltfmaterial_templates.h:35-54). The face
/// reference is LLRenderMaterialParams (llprimitive.cpp:2344-2370), a parameter of type 0x80 in the object's extra parameters
/// (llviewerobject.cpp:1588-1597). Each test writes its own folder and archive, so the class runs in parallel.
/// </summary>
public class PbrViewerFormatTests
{
    private static readonly byte[] Base = ImportKit.Png(200, 40, 10, 16), Normal = ImportKit.Png(128, 128, 255, 16), Orm = ImportKit.Png(250, 120, 30, 16), Glow = ImportKit.Png(90, 80, 70, 16);

    private static void Rich(ImportMaterial top, ImportMaterial side)
    {
        side.Picture = Base;
        side.Normal = Normal;
        side.MetalRough = Orm;
        side.EmissiveImage = Glow;
        side.Emissive = new[] { 1f, 0.5f, 0.25f };
        side.AlphaMode = "MASK";
        side.AlphaCutoff = 0.375f;
    }

    private static (Dictionary<string, byte[]> Oar, XElement Part) Run(Action<ImportMaterial, ImportMaterial> configure = null)
    {
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(MaterialKit.Write(configure: configure ?? Rich), pbr: true);
        return (oar, MaterialKit.Parts(oar)[0]);
    }

    // ------------------------------------------------------------------ the asset as the viewer lays it out

    private static uint U32(byte[] b, ref int at)
    {
        uint v = ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];
        at += 4;
        return v;
    }

    private static string Text(byte[] b, ref int at)
    {
        int length = (int)U32(b, ref at);
        string s = Encoding.UTF8.GetString(b, at, length);
        at += length;
        return s;
    }

    /// <summary>The keys and string values of a binary LLSD map as the viewer's parser takes them (llsdserialize.cpp:123-217, 953-1111), failing on any other byte.</summary>
    internal static List<(string Key, string Value)> ViewerMap(byte[] asset)
    {
        byte[] header = Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n");
        Assert.True(asset.Length > header.Length && asset.AsSpan(0, header.Length).SequenceEqual(header), "the asset does not start with the binary LLSD header line");
        int at = header.Length;
        Assert.Equal((byte)'{', asset[at++]);
        uint count = U32(asset, ref at);
        List<(string, string)> entries = new List<(string, string)>();
        for (uint i = 0; i < count; i++)
        {
            Assert.Equal((byte)'k', asset[at++]);
            string key = Text(asset, ref at);
            Assert.Equal((byte)'s', asset[at++]);
            entries.Add((key, Text(asset, ref at)));
        }
        Assert.Equal((byte)'}', asset[at++]);
        Assert.Equal(asset.Length, at);
        return entries;
    }

    /// <summary>The texture the viewer takes from a texture info of the glTF text: gltf_get_texture_image, an empty id for anything that does not resolve.</summary>
    internal static Guid ViewerTexture(JsonNode root, JsonNode info)
    {
        int index = (int?)info?["index"] ?? -1;
        JsonArray textures = root["textures"] as JsonArray, images = root["images"] as JsonArray;
        if (index < 0 || textures == null || index >= textures.Count)
            return Guid.Empty;
        int source = (int?)textures[index]?["source"] ?? -1;
        if (source < 0 || images == null || source >= images.Count)
            return Guid.Empty;
        return Guid.TryParse((string)images[source]?["uri"], out Guid id) ? id : Guid.Empty;
    }

    [Fact]
    public void TheAssetBytesAreTheViewersBinaryLlsdMapKeyForKey()
    {
        byte[] asset = PbrMaterialAsset.Build(new PbrMaterialData { BaseColorTexture = Guid.Parse("11111111-2222-4333-8444-555555555555") });
        List<(string Key, string Value)> map = ViewerMap(asset);
        // a std::map holds its keys sorted, and the formatter writes them in that order
        Assert.Equal(new[] { "data", "type", "version" }, map.Select(e => e.Key).ToArray());
        Assert.Equal("GLTF 2.0", map[1].Value);
        Assert.Equal("1.1", map[2].Value);
        Assert.Equal("11111111-2222-4333-8444-555555555555", (string)JsonNode.Parse(map[0].Value)["images"][0]["uri"]);
    }

    [Fact]
    public void EachPictureHasItsOwnImageAndTextureInTheOrderTheViewerWritesThem()
    {
        Guid b = Guid.NewGuid(), n = Guid.NewGuid(), o = Guid.NewGuid(), e = Guid.NewGuid();
        byte[] asset = PbrMaterialAsset.Build(new PbrMaterialData { BaseColorTexture = b, NormalTexture = n, MetalRoughTexture = o, EmissiveTexture = e });
        JsonNode root = JsonNode.Parse(ViewerMap(asset).Single(x => x.Key == "data").Value);
        // base colour, normal, metallic-roughness, emissive, then occlusion (the same picture as metallic-roughness, written again as the viewer does)
        Assert.Equal(new[] { b, n, o, e, o }.Select(g => g.ToString("D")).ToArray(), root["images"].AsArray().Select(i => (string)i["uri"]).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, root["textures"].AsArray().Select(t => (int)t["source"]).ToArray());
        JsonNode m = root["materials"][0], mr = m["pbrMetallicRoughness"];
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, new[] { mr["baseColorTexture"], m["normalTexture"], mr["metallicRoughnessTexture"], m["emissiveTexture"], m["occlusionTexture"] }.Select(t => (int)t["index"]).ToArray());
    }

    [Fact]
    public void AMaterialWithNoPicturesHasNoImagesOrTexturesAsTheViewerWritesNone()
    {
        JsonNode root = JsonNode.Parse(ViewerMap(PbrMaterialAsset.Build(new PbrMaterialData())).Single(x => x.Key == "data").Value);
        Assert.Null(root["images"]);
        Assert.Null(root["textures"]);
        Assert.Equal("2.0", (string)root["asset"]["version"]);
    }

    [Fact]
    public void TheEmissiveFactorIsWrittenOnlyWhenItIsNotTheDefault()
    {
        // llgltfmaterial.cpp:338: the viewer leaves the emissive factor out while it is the default (0, 0, 0)
        JsonNode plain = JsonNode.Parse(ViewerMap(PbrMaterialAsset.Build(new PbrMaterialData())).Single(x => x.Key == "data").Value);
        Assert.Null(plain["materials"][0]["emissiveFactor"]);
        JsonNode lit = JsonNode.Parse(ViewerMap(PbrMaterialAsset.Build(new PbrMaterialData { Emissive = new[] { 1f, 0.5f, 0.25f } })).Single(x => x.Key == "data").Value);
        Assert.Equal(new[] { 1f, 0.5f, 0.25f }, lit["materials"][0]["emissiveFactor"].AsArray().Select(v => (float)v).ToArray());
    }

    // ------------------------------------------------------------------ what the viewer takes from an import

    [Fact]
    public void EveryMaterialAssetOfAnImportLoadsAsTheViewerLoadsIt()
    {
        (Dictionary<string, byte[]> oar, XElement part) = Run();
        Dictionary<int, Guid> faces = MaterialKit.PbrFaces(part);
        Assert.NotEmpty(faces);
        int resolved = 0;
        foreach (Guid id in faces.Values.Distinct())
        {
            List<(string Key, string Value)> map = ViewerMap(MaterialKit.Asset(oar, id, "_prmat.dat"));
            Assert.Contains(map, e => e.Key == "version" && (e.Value == "1.0" || e.Value == "1.1"));
            Assert.Contains(map, e => e.Key == "type" && e.Value == "GLTF 2.0");
            JsonNode root = JsonNode.Parse(map.Single(e => e.Key == "data").Value);
            Assert.Equal("2.0", (string)root["asset"]["version"]);
            JsonNode m = root["materials"][0], mr = m["pbrMetallicRoughness"];
            foreach (JsonNode info in new[] { mr["baseColorTexture"], m["normalTexture"], mr["metallicRoughnessTexture"], m["emissiveTexture"] }.Where(i => i != null))
            {
                resolved++;
                Guid texture = ViewerTexture(root, info);
                Assert.NotEqual(Guid.Empty, texture);
                Assert.True(oar.ContainsKey("assets/" + texture.ToString("D") + "_texture.jp2"), "the material names a picture " + texture + " that the archive does not hold");
                Assert.Contains(root["images"].AsArray(), i => (string)i["uri"] == texture.ToString("D"));
            }
        }
        // the side face of the import has all four pictures
        Assert.True(resolved >= 4, "only " + resolved + " pictures were named by the materials");
    }

    [Fact]
    public void TheFaceReferenceIsLaidOutAsTheViewerUnpacksIt()
    {
        (Dictionary<string, byte[]> oar, XElement part) = Run();
        byte[] extra = Convert.FromBase64String(ImportKit.Text(part, "Shape/ExtraParams"));
        Dictionary<int, Guid> faces = MaterialKit.PbrFaces(part);
        Assert.NotEmpty(faces);
        // the object's parameter list: a count, then for each parameter a little-endian type, a little-endian size and the data
        int at = 0, count = extra[at++];
        Dictionary<int, byte[]> blocks = new Dictionary<int, byte[]>();
        for (int i = 0; i < count; i++)
        {
            int type = extra[at] | (extra[at + 1] << 8);
            int size = extra[at + 2] | (extra[at + 3] << 8) | (extra[at + 4] << 16) | (extra[at + 5] << 24);
            at += 6;
            blocks.Add(type, extra.AsSpan(at, size).ToArray());
            at += size;
        }
        Assert.Equal(extra.Length, at);
        byte[] block = blocks[0x80];
        // LLRenderMaterialParams::unpack: a count, then a face number and the 16 bytes of the uuid for each
        Assert.Equal(faces.Count, block[0]);
        Assert.Equal(1 + 17 * faces.Count, block.Length);
        // the slab has two faces, and both have a material
        Assert.Equal(new[] { 0, 1 }, Enumerable.Range(0, faces.Count).Select(i => (int)block[1 + 17 * i]).OrderBy(f => f).ToArray());
        for (int i = 0; i < faces.Count; i++)
        {
            string id = Convert.ToHexString(block, 2 + 17 * i, 16).ToLowerInvariant();
            Assert.Equal(faces[block[1 + 17 * i]].ToString("N"), id);
            Assert.True(oar.ContainsKey("assets/" + Guid.Parse(id).ToString("D") + "_prmat.dat"), "the face names a material that the archive does not hold");
        }
    }
}
