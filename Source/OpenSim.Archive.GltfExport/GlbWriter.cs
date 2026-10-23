/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSim.Archive.GltfExport;

// A small glTF 2.0 binary writer: nodes, meshes made of non-indexed triangle primitives, materials, and PNG images.
// Texture transforms use the KHR_texture_transform extension (Khronos),
// https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_texture_transform
// Written to the glTF 2.0 specification (Khronos), https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html

/// <summary>A glTF texture used by a material, with the texture transform to apply to the texture coordinates.</summary>
internal sealed class GltfTextureRef
{
    public int Texture;
    /// <summary>KHR_texture_transform offset, rotation (radians) and scale; null offset means no transform.</summary>
    public float[] Offset;
    public float Rotation;
    public float[] Scale;
}

internal sealed class GltfImage
{
    public string Name;
    public byte[] Png;
}

internal sealed class GltfMaterial
{
    public string Name;
    /// <summary>Base colour factor, red green blue alpha, as given.</summary>
    public float[] Color = { 1, 1, 1, 1 };
    /// <summary>OPAQUE, MASK or BLEND.</summary>
    public string AlphaMode = "OPAQUE";
    public float AlphaCutoff = 0.5f;
    public GltfTextureRef BaseColorTexture;
    public GltfTextureRef NormalTexture;
    public GltfTextureRef MetallicRoughnessTexture;
    public GltfTextureRef EmissiveTexture;
    public JsonObject Extras;
}

internal sealed class GltfPrimitive
{
    public float[] Positions;
    public float[] Normals;
    public float[] Uvs;
    /// <summary>One value per vertex, written as the application attribute _PRIM_FACE; it keeps the prim face number when a tool merges primitives.</summary>
    public float[] FaceNumbers;
    /// <summary>One value per vertex, written as the application attribute _PART_LINK: the link number of the part the vertex belongs to (0 for a single-part build), so a tool that joins parts' meshes keeps which part each vertex came from.</summary>
    public float[] PartLinks;
    public int Material = -1;
    public JsonObject Extras;
}

internal sealed class GltfMesh
{
    public string Name;
    public List<GltfPrimitive> Primitives = new List<GltfPrimitive>();
    public JsonObject Extras;
}

internal sealed class GltfNode
{
    public string Name;
    public float[] Translation;
    public float[] Rotation;
    public int Mesh = -1;
    public List<int> Children = new List<int>();
    public JsonObject Extras;
}

internal sealed class GltfDocument
{
    public List<GltfNode> Nodes = new List<GltfNode>();
    public List<GltfMesh> Meshes = new List<GltfMesh>();
    public List<GltfMaterial> Materials = new List<GltfMaterial>();
    public List<GltfImage> Images = new List<GltfImage>();
    /// <summary>One entry per glTF texture: the index of its image. All textures share one repeating sampler.</summary>
    public List<int> Textures = new List<int>();
    /// <summary>Written to asset.generator.</summary>
    public string Generator = "OpenSim.Archive.GltfExport";

    public byte[] ToGlb()
    {
        using MemoryStream bin = new MemoryStream();
        List<(int offset, int length, int target)> views = new List<(int, int, int)>();
        List<(int view, int count, string type, float[] min, float[] max)> accessors = new List<(int, int, string, float[], float[])>();
        // per mesh, per primitive: accessor indices of position, normal, uv
        List<List<(int pos, int nor, int uv, int face, int link)>> meshAccessors = new List<List<(int, int, int, int, int)>>();

        foreach (GltfMesh mesh in Meshes)
        {
            List<(int, int, int, int, int)> list = new List<(int, int, int, int, int)>();
            foreach (GltfPrimitive p in mesh.Primitives)
            {
                int pos = AddFloats(bin, views, accessors, p.Positions, 3, "VEC3", true);
                int nor = p.Normals != null ? AddFloats(bin, views, accessors, p.Normals, 3, "VEC3", false) : -1;
                int uv = p.Uvs != null ? AddFloats(bin, views, accessors, p.Uvs, 2, "VEC2", false) : -1;
                int face = p.FaceNumbers != null ? AddFloats(bin, views, accessors, p.FaceNumbers, 1, "SCALAR", false) : -1;
                int link = p.PartLinks != null ? AddFloats(bin, views, accessors, p.PartLinks, 1, "SCALAR", false) : -1;
                list.Add((pos, nor, uv, face, link));
            }
            meshAccessors.Add(list);
        }

        List<int> imageViews = new List<int>();
        foreach (GltfImage image in Images)
        {
            while (bin.Length % 4 != 0)
                bin.WriteByte(0);
            views.Add(((int)bin.Length, image.Png.Length, 0));
            bin.Write(image.Png, 0, image.Png.Length);
            imageViews.Add(views.Count - 1);
        }

        byte[] json = WriteJson(views, accessors, meshAccessors, imageViews, (int)bin.Length);
        int jsonPad = (4 - json.Length % 4) % 4;
        int binLen = (int)bin.Length;
        int binPad = (4 - binLen % 4) % 4;
        bool hasBin = binLen > 0;

        int total = 12 + 8 + json.Length + jsonPad + (hasBin ? 8 + binLen + binPad : 0);
        using MemoryStream glb = new MemoryStream(total);
        using BinaryWriter w = new BinaryWriter(glb);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)(json.Length + jsonPad));
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(json);
        for (int i = 0; i < jsonPad; i++)
            w.Write((byte)0x20);
        if (hasBin)
        {
            w.Write((uint)(binLen + binPad));
            w.Write(0x004E4942u); // "BIN\0"
            w.Write(bin.GetBuffer(), 0, binLen);
            for (int i = 0; i < binPad; i++)
                w.Write((byte)0);
        }
        w.Flush();
        return glb.ToArray();
    }

    private static int AddFloats(MemoryStream bin, List<(int offset, int length, int target)> views,
        List<(int view, int count, string type, float[] min, float[] max)> accessors, float[] data, int components, string type, bool withBounds)
    {
        int offset = (int)bin.Length;
        byte[] bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bin.Write(bytes, 0, bytes.Length);
        views.Add((offset, bytes.Length, 34962));

        float[] min = null, max = null;
        if (withBounds)
        {
            min = new float[components];
            max = new float[components];
            for (int c = 0; c < components; c++)
            {
                min[c] = float.PositiveInfinity;
                max[c] = float.NegativeInfinity;
            }
            for (int i = 0; i < data.Length; i += components)
                for (int c = 0; c < components; c++)
                {
                    min[c] = Math.Min(min[c], data[i + c]);
                    max[c] = Math.Max(max[c], data[i + c]);
                }
        }
        accessors.Add((views.Count - 1, data.Length / components, type, min, max));
        return accessors.Count - 1;
    }

    private byte[] WriteJson(List<(int offset, int length, int target)> views, List<(int view, int count, string type, float[] min, float[] max)> accessors,
        List<List<(int pos, int nor, int uv, int face, int link)>> meshAccessors, List<int> imageViews, int binLength)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", Generator);
            w.WriteEndObject();

            bool usesTransform = Materials.Any(m => m.BaseColorTexture?.Offset != null || m.NormalTexture?.Offset != null);
            if (usesTransform)
            {
                w.WriteStartArray("extensionsUsed");
                w.WriteStringValue("KHR_texture_transform");
                w.WriteEndArray();
            }

            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteStartArray("nodes");
            w.WriteNumberValue(0);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            foreach (GltfNode n in Nodes)
            {
                w.WriteStartObject();
                if (n.Name != null)
                    w.WriteString("name", n.Name);
                if (n.Children.Count > 0)
                {
                    w.WriteStartArray("children");
                    foreach (int c in n.Children)
                        w.WriteNumberValue(c);
                    w.WriteEndArray();
                }
                if (n.Mesh >= 0)
                    w.WriteNumber("mesh", n.Mesh);
                if (n.Rotation != null)
                    WriteFloats(w, "rotation", n.Rotation);
                if (n.Translation != null)
                    WriteFloats(w, "translation", n.Translation);
                WriteExtras(w, n.Extras);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (Meshes.Count > 0)
            {
                w.WriteStartArray("meshes");
                for (int m = 0; m < Meshes.Count; m++)
                {
                    GltfMesh mesh = Meshes[m];
                    w.WriteStartObject();
                    if (mesh.Name != null)
                        w.WriteString("name", mesh.Name);
                    w.WriteStartArray("primitives");
                    for (int p = 0; p < mesh.Primitives.Count; p++)
                    {
                        GltfPrimitive prim = mesh.Primitives[p];
                        (int pos, int nor, int uv, int face, int link) = meshAccessors[m][p];
                        w.WriteStartObject();
                        w.WriteStartObject("attributes");
                        w.WriteNumber("POSITION", pos);
                        if (nor >= 0)
                            w.WriteNumber("NORMAL", nor);
                        if (uv >= 0)
                            w.WriteNumber("TEXCOORD_0", uv);
                        if (face >= 0)
                            w.WriteNumber("_PRIM_FACE", face);
                        if (link >= 0)
                            w.WriteNumber("_PART_LINK", link);
                        w.WriteEndObject();
                        if (prim.Material >= 0)
                            w.WriteNumber("material", prim.Material);
                        w.WriteNumber("mode", 4);
                        WriteExtras(w, prim.Extras);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    WriteExtras(w, mesh.Extras);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (Materials.Count > 0)
            {
                w.WriteStartArray("materials");
                foreach (GltfMaterial mat in Materials)
                {
                    w.WriteStartObject();
                    if (mat.Name != null)
                        w.WriteString("name", mat.Name);
                    w.WriteStartObject("pbrMetallicRoughness");
                    WriteFloats(w, "baseColorFactor", mat.Color);
                    if (mat.BaseColorTexture != null)
                        WriteTextureRef(w, "baseColorTexture", mat.BaseColorTexture);
                    if (mat.MetallicRoughnessTexture != null)
                        WriteTextureRef(w, "metallicRoughnessTexture", mat.MetallicRoughnessTexture);
                    // with a metallic-roughness texture the factors scale it, so the metal is not switched off
                    w.WriteNumber("metallicFactor", mat.MetallicRoughnessTexture != null ? 1 : 0);
                    w.WriteNumber("roughnessFactor", 1);
                    w.WriteEndObject();
                    if (mat.NormalTexture != null)
                        WriteTextureRef(w, "normalTexture", mat.NormalTexture);
                    if (mat.EmissiveTexture != null)
                    {
                        WriteTextureRef(w, "emissiveTexture", mat.EmissiveTexture);
                        w.WriteStartArray("emissiveFactor");
                        w.WriteNumberValue(1); w.WriteNumberValue(1); w.WriteNumberValue(1);
                        w.WriteEndArray();
                    }
                    w.WriteString("alphaMode", mat.AlphaMode);
                    if (mat.AlphaMode == "MASK")
                        w.WriteNumber("alphaCutoff", mat.AlphaCutoff);
                    WriteExtras(w, mat.Extras);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (Textures.Count > 0)
            {
                w.WriteStartArray("images");
                for (int i = 0; i < Images.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", imageViews[i]);
                    w.WriteString("mimeType", "image/png");
                    if (Images[i].Name != null)
                        w.WriteString("name", Images[i].Name);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("samplers");
                w.WriteStartObject();
                w.WriteNumber("magFilter", 9729);
                w.WriteNumber("minFilter", 9987);
                w.WriteNumber("wrapS", 10497);
                w.WriteNumber("wrapT", 10497);
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteStartArray("textures");
                foreach (int source in Textures)
                {
                    w.WriteStartObject();
                    w.WriteNumber("sampler", 0);
                    w.WriteNumber("source", source);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (binLength > 0)
            {
                w.WriteStartArray("buffers");
                w.WriteStartObject();
                w.WriteNumber("byteLength", binLength);
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteStartArray("bufferViews");
                foreach ((int offset, int length, int target) v in views)
                {
                    w.WriteStartObject();
                    w.WriteNumber("buffer", 0);
                    w.WriteNumber("byteOffset", v.offset);
                    w.WriteNumber("byteLength", v.length);
                    if (v.target != 0)
                        w.WriteNumber("target", v.target);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("accessors");
                foreach ((int view, int count, string type, float[] min, float[] max) a in accessors)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", a.view);
                    w.WriteNumber("componentType", 5126);
                    w.WriteNumber("count", a.count);
                    w.WriteString("type", a.type);
                    if (a.min != null)
                    {
                        WriteFloats(w, "max", a.max);
                        WriteFloats(w, "min", a.min);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteTextureRef(Utf8JsonWriter w, string name, GltfTextureRef t)
    {
        w.WriteStartObject(name);
        w.WriteNumber("index", t.Texture);
        if (t.Offset != null)
        {
            w.WriteStartObject("extensions");
            w.WriteStartObject("KHR_texture_transform");
            WriteFloats(w, "offset", t.Offset);
            w.WriteNumber("rotation", t.Rotation);
            WriteFloats(w, "scale", t.Scale);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteFloats(Utf8JsonWriter w, string name, float[] values)
    {
        w.WriteStartArray(name);
        foreach (float f in values)
            w.WriteNumberValue(f);
        w.WriteEndArray();
    }

    private static void WriteExtras(Utf8JsonWriter w, JsonObject extras)
    {
        if (extras == null)
            return;
        w.WritePropertyName("extras");
        extras.WriteTo(w);
    }
}
