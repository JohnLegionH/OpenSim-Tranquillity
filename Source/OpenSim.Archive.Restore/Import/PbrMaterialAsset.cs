/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json.Nodes;
using OpenMetaverse.StructuredData;

namespace OpenSim.Archive.Restore.Import;

/// <summary>The fields of one glTF PBR material asset: the factors, and the texture assets for the four pictures a face can use.</summary>
public sealed class PbrMaterialData
{
    /// <summary>baseColorFactor (r, g, b, a).</summary>
    public float[] BaseColor { get; set; } = { 1f, 1f, 1f, 1f };
    public float Metallic { get; set; } = 1f;
    public float Roughness { get; set; } = 1f;
    /// <summary>emissiveFactor (r, g, b).</summary>
    public float[] Emissive { get; set; } = { 0f, 0f, 0f };
    /// <summary>OPAQUE, MASK or BLEND.</summary>
    public string AlphaMode { get; set; } = "OPAQUE";
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }
    /// <summary>Texture assets; <see cref="Guid.Empty"/> when the material has none.</summary>
    public Guid BaseColorTexture { get; set; }
    public Guid NormalTexture { get; set; }
    /// <summary>Occlusion in red, roughness in green, metal in blue.</summary>
    public Guid MetalRoughTexture { get; set; }
    public Guid EmissiveTexture { get; set; }
}

/// <summary>
/// A PBR material asset (asset type Material, file extension prmat.dat): a one-material glTF text whose "images" hold the texture assets' ids as URIs, in the
/// map the viewer uploads for a material, a binary LLSD map with the keys version ("1.1"), type ("GLTF 2.0") and data (the glTF text). The region does not read
/// the text; it scans the asset for texture ids when an archive is saved (UuidGatherer.RecordEmbeddedAssetDataUuids). The reader also takes plain glTF text.
/// </summary>
public static class PbrMaterialAsset
{
    private const string Version = "1.1", Type = "GLTF 2.0";
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n");

    public static byte[] Build(PbrMaterialData d)
    {
        JsonArray images = new JsonArray(), textures = new JsonArray();
        int Texture(Guid id)
        {
            string uri = id.ToString("D");
            for (int i = 0; i < images.Count; i++)
                if ((string)images[i]["uri"] == uri)
                    return i;
            images.Add(new JsonObject { ["uri"] = uri });
            textures.Add(new JsonObject { ["source"] = images.Count - 1 });
            return images.Count - 1;
        }
        JsonObject pbr = new JsonObject
        {
            ["baseColorFactor"] = Array(d.BaseColor), ["metallicFactor"] = d.Metallic, ["roughnessFactor"] = d.Roughness
        };
        if (d.BaseColorTexture != Guid.Empty)
            pbr["baseColorTexture"] = new JsonObject { ["index"] = Texture(d.BaseColorTexture) };
        if (d.MetalRoughTexture != Guid.Empty)
            pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = Texture(d.MetalRoughTexture) };
        JsonObject material = new JsonObject { ["pbrMetallicRoughness"] = pbr };
        if (d.NormalTexture != Guid.Empty)
            material["normalTexture"] = new JsonObject { ["index"] = Texture(d.NormalTexture) };
        if (d.EmissiveTexture != Guid.Empty)
            material["emissiveTexture"] = new JsonObject { ["index"] = Texture(d.EmissiveTexture) };
        material["emissiveFactor"] = Array(d.Emissive);
        material["alphaMode"] = d.AlphaMode;
        material["alphaCutoff"] = d.AlphaCutoff;
        material["doubleSided"] = d.DoubleSided;
        JsonObject root = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } };
        if (images.Count > 0)
        {
            root["images"] = images;
            root["textures"] = textures;
        }
        root["materials"] = new JsonArray(material);
        OSDMap map = new OSDMap { ["version"] = Version, ["type"] = Type, ["data"] = root.ToJsonString() };
        byte[] body = OSDParser.SerializeLLSDBinary(map, false);
        byte[] result = new byte[Header.Length + body.Length];
        Header.CopyTo(result, 0);
        body.CopyTo(result, Header.Length);
        return result;
    }

    /// <summary>The material in an asset, or null when the bytes are neither the viewer's map nor glTF text, or hold no material.</summary>
    public static PbrMaterialData Parse(byte[] asset)
    {
        if (asset == null || asset.Length == 0 || asset.Length > 4 * 1024 * 1024)
            return null;
        try
        {
            string text;
            int first = 0;
            while (first < asset.Length && (asset[first] == ' ' || asset[first] == '\n' || asset[first] == '\r' || asset[first] == '\t'))
                first++;
            if (first < asset.Length && asset[first] == '{')
                text = Encoding.UTF8.GetString(asset);
            else
            {
                if (OSDParser.Deserialize(asset) is not OSDMap map || map["data"].Type != OSDType.String)
                    return null;
                text = map["data"].AsString();
            }
            JsonNode root = JsonNode.Parse(text);
            JsonArray images = root?["images"] as JsonArray, textures = root?["textures"] as JsonArray;
            if (root?["materials"] is not JsonArray materials || materials.Count == 0 || materials[0] is not JsonObject m)
                return null;
            Guid Resolve(JsonNode reference)
            {
                if (reference?["index"] == null)
                    return Guid.Empty;
                int index = (int)reference["index"];
                int source = textures != null ? (int?)textures[index]?["source"] ?? -1 : index;
                string uri = images != null && source >= 0 && source < images.Count ? (string)images[source]?["uri"] : null;
                return Guid.TryParse(uri, out Guid id) ? id : Guid.Empty;
            }
            JsonNode mr = m["pbrMetallicRoughness"];
            PbrMaterialData d = new PbrMaterialData
            {
                BaseColorTexture = Resolve(mr?["baseColorTexture"]), MetalRoughTexture = Resolve(mr?["metallicRoughnessTexture"]),
                NormalTexture = Resolve(m["normalTexture"]), EmissiveTexture = Resolve(m["emissiveTexture"])
            };
            if (Floats(mr?["baseColorFactor"], 4) is float[] color)
                d.BaseColor = color;
            if (mr?["metallicFactor"] != null)
                d.Metallic = (float)(double)mr["metallicFactor"];
            if (mr?["roughnessFactor"] != null)
                d.Roughness = (float)(double)mr["roughnessFactor"];
            if (Floats(m["emissiveFactor"], 3) is float[] emissive)
                d.Emissive = emissive;
            if (m["alphaMode"] != null)
                d.AlphaMode = (string)m["alphaMode"];
            if (m["alphaCutoff"] != null)
                d.AlphaCutoff = (float)(double)m["alphaCutoff"];
            if (m["doubleSided"] != null)
                d.DoubleSided = (bool)m["doubleSided"];
            return d;
        }
        catch (Exception)
        {
            // bytes that are not a material read as nothing (the LLSD reader throws a library exception of its own for text it cannot parse)
            return null;
        }
    }

    private static JsonArray Array(float[] values) => new JsonArray(values.Select(v => (JsonNode)v).ToArray());

    private static float[] Floats(JsonNode n, int count)
    {
        if (n is not JsonArray a || a.Count != count)
            return null;
        return a.Select(v => (float)(double)v).ToArray();
    }
}
