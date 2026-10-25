/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenSim.Archive.Restore;

/// <summary>One triangle-list primitive of a glTF mesh, read into flat arrays.</summary>
public sealed class GlbPrimitive
{
    /// <summary>x, y, z for each vertex (glTF axes, Y up).</summary>
    public float[] Positions;
    public float[] Normals;
    public float[] Uvs;
    /// <summary>The application attribute _PRIM_FACE, one value per vertex; null when the primitive has none.</summary>
    public float[] FaceNumbers;
    /// <summary>Three vertex numbers for each triangle.</summary>
    public int[] Indices;
    public int Material = -1;
    public JsonObject Extras;
}

public sealed class GlbMesh
{
    public string Name;
    public List<GlbPrimitive> Primitives = new List<GlbPrimitive>();
    public JsonObject Extras;
}

public sealed class GlbNode
{
    public int Index;
    public string Name;
    public float[] Translation, Rotation, Scale;
    public int Mesh = -1;
    public List<int> Children = new List<int>();
    public JsonObject Extras;
}

/// <summary>A glTF material as far as the restore needs it for a part that was added: its colour, alpha mode and base colour image.</summary>
public sealed class GlbMaterial
{
    public string Name;
    public JsonObject Extras;
    /// <summary>baseColorFactor (r, g, b, a); white when the material gives none.</summary>
    public float[] BaseColor = { 1f, 1f, 1f, 1f };
    public string AlphaMode = "OPAQUE";
    /// <summary>The encoded base colour image (PNG or JPEG) the material points at; null when it has none or the image is a file next to the .glb.</summary>
    public byte[] Image;
    /// <summary>The material gives a picture an offset, scale or rotation (the KHR_texture_transform extension).</summary>
    public bool HasTextureTransform;
}

/// <summary>A binary glTF file read as far as the restore needs: nodes with their extras, meshes with their primitives, and the material extras.</summary>
public sealed class GlbFile
{
    public List<GlbNode> Nodes = new List<GlbNode>();
    public List<GlbMesh> Meshes = new List<GlbMesh>();
    public List<JsonObject> MaterialExtras = new List<JsonObject>();
    public List<GlbMaterial> Materials = new List<GlbMaterial>();

    public static GlbFile Read(byte[] data)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546C67)
            throw new InvalidDataException("not a binary glTF file (no glTF header)");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version != 2)
            throw new InvalidDataException("glTF version " + version + " is not supported (version 2 is)");
        string json = null;
        int binAt = -1, binLength = 0;
        int at = 12;
        while (at + 8 <= data.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (at + 8 + length > data.Length)
                throw new InvalidDataException("the glTF file is cut short");
            if (type == 0x4E4F534A && json == null)
                json = Encoding.UTF8.GetString(data, at + 8, length);
            else if (type == 0x004E4942 && binAt < 0)
            {
                binAt = at + 8;
                binLength = length;
            }
            at += 8 + ((length + 3) & ~3);
        }
        if (json == null)
            throw new InvalidDataException("the glTF file has no JSON chunk");

        JsonNode root = JsonNode.Parse(json);
        GlbFile file = new GlbFile();
        JsonArray accessors = root["accessors"] as JsonArray ?? new JsonArray();
        JsonArray views = root["bufferViews"] as JsonArray ?? new JsonArray();

        if (root["materials"] is JsonArray materials)
            foreach (JsonNode m in materials)
            {
                file.MaterialExtras.Add(m?["extras"] as JsonObject);
                file.Materials.Add(ReadMaterial(m, root, views, data, binAt, binLength));
            }

        if (root["meshes"] is JsonArray meshes)
            foreach (JsonNode m in meshes)
            {
                GlbMesh mesh = new GlbMesh { Name = (string)m["name"], Extras = m["extras"] as JsonObject };
                foreach (JsonNode p in (JsonArray)m["primitives"] ?? new JsonArray())
                {
                    int mode = p["mode"] == null ? 4 : (int)p["mode"];
                    if (mode != 4)
                        throw new InvalidDataException("a primitive is not a triangle list (mode " + mode + ")");
                    JsonNode a = p["attributes"];
                    GlbPrimitive prim = new GlbPrimitive
                    {
                        Positions = ReadFloats(a["POSITION"], 3, accessors, views, data, binAt, binLength),
                        Normals = a["NORMAL"] == null ? null : ReadFloats(a["NORMAL"], 3, accessors, views, data, binAt, binLength),
                        Uvs = a["TEXCOORD_0"] == null ? null : ReadFloats(a["TEXCOORD_0"], 2, accessors, views, data, binAt, binLength),
                        FaceNumbers = a["_PRIM_FACE"] == null ? null : ReadFloats(a["_PRIM_FACE"], 1, accessors, views, data, binAt, binLength),
                        Material = p["material"] == null ? -1 : (int)p["material"],
                        Extras = p["extras"] as JsonObject
                    };
                    int vertices = prim.Positions.Length / 3;
                    prim.Indices = p["indices"] == null ? Enumerable.Range(0, vertices - vertices % 3).ToArray() : ReadIndices(p["indices"], accessors, views, data, binAt, binLength);
                    foreach (int i in prim.Indices)
                        if (i < 0 || i >= vertices)
                            throw new InvalidDataException("a primitive indexes a vertex that does not exist");
                    if (prim.Indices.Length % 3 != 0)
                        throw new InvalidDataException("a primitive's index count is not a multiple of three");
                    mesh.Primitives.Add(prim);
                }
                file.Meshes.Add(mesh);
            }

        if (root["nodes"] is JsonArray nodes)
            for (int i = 0; i < nodes.Count; i++)
            {
                JsonNode n = nodes[i];
                GlbNode node = new GlbNode
                {
                    Index = i,
                    Name = (string)n["name"],
                    Translation = Floats(n["translation"], 3),
                    Rotation = Floats(n["rotation"], 4),
                    Scale = Floats(n["scale"], 3),
                    Mesh = n["mesh"] == null ? -1 : (int)n["mesh"],
                    Extras = n["extras"] as JsonObject
                };
                if (n["children"] is JsonArray children)
                    foreach (JsonNode c in children)
                        node.Children.Add((int)c);
                file.Nodes.Add(node);
            }
        return file;
    }

    private static GlbMaterial ReadMaterial(JsonNode m, JsonNode root, JsonArray views, byte[] data, int binAt, int binLength)
    {
        GlbMaterial result = new GlbMaterial { Name = (string)m?["name"], Extras = m?["extras"] as JsonObject };
        if (m == null)
            return result;
        float[] color = Floats(m["pbrMetallicRoughness"]?["baseColorFactor"], 4);
        if (color != null)
            result.BaseColor = color;
        if (m["alphaMode"] != null)
            result.AlphaMode = (string)m["alphaMode"];
        JsonNode tex = m["pbrMetallicRoughness"]?["baseColorTexture"];
        foreach (JsonNode info in new[] { tex, m["pbrMetallicRoughness"]?["metallicRoughnessTexture"], m["normalTexture"], m["occlusionTexture"], m["emissiveTexture"] })
            if (info?["extensions"]?["KHR_texture_transform"] != null)
                result.HasTextureTransform = true;
        if (tex?["index"] == null)
            return result;
        try
        {
            JsonNode texture = root["textures"]?[(int)tex["index"]];
            JsonNode image = texture?["source"] == null ? null : root["images"]?[(int)texture["source"]];
            if (image == null)
                return result;
            if (image["bufferView"] != null)
            {
                JsonNode view = views[(int)image["bufferView"]];
                int offset = view["byteOffset"] == null ? 0 : (int)view["byteOffset"], length = (int)view["byteLength"];
                if (binAt >= 0 && offset >= 0 && length >= 0 && (long)offset + length <= binLength)
                    result.Image = data.AsSpan(binAt + offset, length).ToArray();
            }
            else if (image["uri"] is JsonNode uri && ((string)uri).StartsWith("data:", StringComparison.Ordinal))
            {
                string text = (string)uri;
                int comma = text.IndexOf(',');
                if (comma > 0 && text.Substring(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                    result.Image = Convert.FromBase64String(text.Substring(comma + 1));
            }
        }
        catch (Exception e) when (e is InvalidCastException || e is ArgumentOutOfRangeException || e is FormatException || e is NullReferenceException)
        {
            // a material that names an image that cannot be read has no image
        }
        return result;
    }

    private static float[] Floats(JsonNode n, int count)
    {
        if (n is not JsonArray a || a.Count != count)
            return null;
        float[] r = new float[count];
        for (int i = 0; i < count; i++)
            r[i] = (float)(double)a[i];
        return r;
    }

    private static readonly Dictionary<string, int> s_widths = new Dictionary<string, int> { ["SCALAR"] = 1, ["VEC2"] = 2, ["VEC3"] = 3, ["VEC4"] = 4 };

    private static (int offset, int stride, int count, int components, int componentType, bool normalized) Locate(JsonNode accessorIndex, JsonArray accessors, JsonArray views, int binLength)
    {
        JsonNode acc = accessors[(int)accessorIndex] ?? throw new InvalidDataException("a primitive names an accessor that does not exist");
        if (acc["sparse"] != null)
            throw new InvalidDataException("sparse accessors are not supported");
        if (acc["bufferView"] == null)
            throw new InvalidDataException("an accessor has no buffer view");
        JsonNode view = views[(int)acc["bufferView"]];
        int components = s_widths[(string)acc["type"]];
        int componentType = (int)acc["componentType"];
        int size = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException("unknown component type " + componentType) };
        int viewOffset = view["byteOffset"] == null ? 0 : (int)view["byteOffset"];
        int accOffset = acc["byteOffset"] == null ? 0 : (int)acc["byteOffset"];
        int stride = view["byteStride"] == null ? components * size : (int)view["byteStride"];
        int count = (int)acc["count"];
        long end = (long)viewOffset + accOffset + (long)(count == 0 ? 0 : count - 1) * stride + components * size;
        if (end > binLength)
            throw new InvalidDataException("an accessor reaches past the end of the binary chunk");
        return (viewOffset + accOffset, stride, count, components, componentType, acc["normalized"] != null && (bool)acc["normalized"]);
    }

    private static float[] ReadFloats(JsonNode accessorIndex, int expectedComponents, JsonArray accessors, JsonArray views, byte[] data, int binAt, int binLength)
    {
        var l = Locate(accessorIndex, accessors, views, binLength);
        if (l.components != expectedComponents)
            throw new InvalidDataException("an attribute has " + l.components + " components where " + expectedComponents + " are expected");
        float[] r = new float[l.count * l.components];
        for (int i = 0; i < l.count; i++)
            for (int c = 0; c < l.components; c++)
            {
                int p = binAt + l.offset + i * l.stride;
                r[i * l.components + c] = l.componentType switch
                {
                    5126 => BitConverter.ToSingle(data, p + c * 4),
                    5121 => l.normalized ? data[p + c] / 255f : data[p + c],
                    5123 => l.normalized ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + c * 2)) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + c * 2)),
                    5120 => l.normalized ? Math.Max(-1f, (sbyte)data[p + c] / 127f) : (sbyte)data[p + c],
                    5122 => l.normalized ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + c * 2)) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + c * 2)),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + c * 4)),
                    _ => 0f
                };
            }
        return r;
    }

    private static int[] ReadIndices(JsonNode accessorIndex, JsonArray accessors, JsonArray views, byte[] data, int binAt, int binLength)
    {
        var l = Locate(accessorIndex, accessors, views, binLength);
        if (l.components != 1)
            throw new InvalidDataException("the index accessor is not a scalar");
        int[] r = new int[l.count];
        for (int i = 0; i < l.count; i++)
        {
            int p = binAt + l.offset + i * l.stride;
            r[i] = l.componentType switch
            {
                5121 => data[p],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)),
                5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p))),
                _ => throw new InvalidDataException("indices have component type " + l.componentType)
            };
        }
        return r;
    }

    // ---- extras helpers

    public static string Str(JsonObject o, string key) => o != null && o[key] is JsonValue v && v.TryGetValue(out string s) ? s : null;

    public static double? Num(JsonObject o, string key)
    {
        if (o == null || o[key] is not JsonValue v)
            return null;
        if (v.TryGetValue(out double d))
            return d;
        if (v.TryGetValue(out int i))
            return i;
        if (v.TryGetValue(out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return null;
    }

    public static bool? Bool(JsonObject o, string key) => o != null && o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}
