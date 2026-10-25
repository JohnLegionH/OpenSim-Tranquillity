/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json.Nodes;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>Adds nodes, meshes and materials to a binary glTF file the way a modelling tool would, without a modelling tool.</summary>
internal sealed class GlbEdit
{
    public JsonNode Json;
    private readonly MemoryStream m_bin = new MemoryStream();

    private GlbEdit() { }

    public static byte[] Apply(byte[] glb, Action<GlbEdit> edit)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        GlbEdit g = new GlbEdit { Json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength)) };
        int binAt = 20 + ((jsonLength + 3) & ~3) + 8;
        int binLength = BitConverter.ToInt32(glb, binAt - 8);
        g.m_bin.Write(glb, binAt, binLength);
        edit(g);
        byte[] bin = g.m_bin.ToArray();
        g.Json["buffers"][0]["byteLength"] = bin.Length;
        byte[] j = Encoding.UTF8.GetBytes(g.Json.ToJsonString());
        int jp = (j.Length + 3) & ~3, bp = (bin.Length + 3) & ~3;
        using MemoryStream ms = new MemoryStream();
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(0x46546C67); w.Write(2); w.Write(12 + 8 + jp + 8 + bp);
            w.Write(jp); w.Write(0x4E4F534A); w.Write(j); for (int i = j.Length; i < jp; i++) w.Write((byte)0x20);
            w.Write(bp); w.Write(0x004E4942); w.Write(bin); for (int i = bin.Length; i < bp; i++) w.Write((byte)0);
        }
        return ms.ToArray();
    }

    public JsonArray Array(string name)
    {
        if (Json[name] is not JsonArray a)
            Json[name] = a = new JsonArray();
        return a;
    }

    public int ObjectNode => Json["nodes"].AsArray().ToList().FindIndex(n => (string)n["extras"]?["kind"] == "object");

    private int View(byte[] data, int target = 0)
    {
        while (m_bin.Length % 4 != 0)
            m_bin.WriteByte(0);
        int offset = (int)m_bin.Length;
        m_bin.Write(data);
        JsonObject view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
        if (target != 0)
            view["target"] = target;
        Array("bufferViews").Add(view);
        return Json["bufferViews"].AsArray().Count - 1;
    }

    private int Accessor(float[] data, string type, int components)
    {
        byte[] bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        int view = View(bytes, 34962);
        JsonObject a = new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = data.Length / components, ["type"] = type };
        if (type == "VEC3" && data.Length >= 3)
        {
            JsonArray min = new JsonArray(float.MaxValue, float.MaxValue, float.MaxValue), max = new JsonArray(float.MinValue, float.MinValue, float.MinValue);
            for (int c = 0; c < 3; c++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = c; i < data.Length; i += 3) { lo = Math.Min(lo, data[i]); hi = Math.Max(hi, data[i]); }
                min[c] = lo; max[c] = hi;
            }
            a["min"] = min; a["max"] = max;
        }
        Array("accessors").Add(a);
        return Json["accessors"].AsArray().Count - 1;
    }

    private int Indices(uint[] data)
    {
        byte[] bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        int view = View(bytes, 34963);
        Array("accessors").Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5125, ["count"] = data.Length, ["type"] = "SCALAR" });
        return Json["accessors"].AsArray().Count - 1;
    }

    /// <summary>A material with a colour and, when given, a picture; returns its index.</summary>
    public int Material(float[] color, string alphaMode = "OPAQUE", byte[] picture = null)
    {
        JsonObject pbr = new JsonObject { ["baseColorFactor"] = new JsonArray(color[0], color[1], color[2], color[3]) };
        if (picture != null)
        {
            int view = View(picture);
            Array("images").Add(new JsonObject { ["bufferView"] = view, ["mimeType"] = "image/png" });
            Array("textures").Add(new JsonObject { ["source"] = Json["images"].AsArray().Count - 1 });
            pbr["baseColorTexture"] = new JsonObject { ["index"] = Json["textures"].AsArray().Count - 1 };
        }
        Array("materials").Add(new JsonObject { ["name"] = "Material", ["pbrMetallicRoughness"] = pbr, ["alphaMode"] = alphaMode });
        return Json["materials"].AsArray().Count - 1;
    }

    /// <summary>A mesh of one primitive; with <paramref name="faceNumber"/> every vertex carries that _PRIM_FACE value. Returns the mesh's index.</summary>
    public int AddMesh(string name, float[] positions, float[] normals, float[] uvs, uint[] indices, int material = -1, int faceNumber = -1)
    {
        JsonObject attributes = new JsonObject
        {
            ["POSITION"] = Accessor(positions, "VEC3", 3),
            ["NORMAL"] = Accessor(normals, "VEC3", 3),
            ["TEXCOORD_0"] = Accessor(uvs, "VEC2", 2)
        };
        if (faceNumber >= 0)
        {
            float[] numbers = new float[positions.Length / 3];
            System.Array.Fill(numbers, (float)faceNumber);
            attributes["_PRIM_FACE"] = Accessor(numbers, "SCALAR", 1);
        }
        JsonObject primitive = new JsonObject { ["attributes"] = attributes, ["indices"] = Indices(indices), ["mode"] = 4 };
        if (material >= 0)
            primitive["material"] = material;
        Array("meshes").Add(new JsonObject { ["name"] = name, ["primitives"] = new JsonArray(primitive) });
        return Json["meshes"].AsArray().Count - 1;
    }

    /// <summary>A new node with one mesh of one primitive, made a child of the object node; returns the node's index.</summary>
    public int MeshNode(string name, float[] positions, float[] normals, float[] uvs, uint[] indices, int material = -1, float[] translation = null, float[] rotation = null, float[] scale = null)
    {
        JsonObject node = new JsonObject { ["name"] = name, ["mesh"] = AddMesh(name, positions, normals, uvs, indices, material) };
        if (translation != null) node["translation"] = new JsonArray(translation[0], translation[1], translation[2]);
        if (rotation != null) node["rotation"] = new JsonArray(rotation[0], rotation[1], rotation[2], rotation[3]);
        if (scale != null) node["scale"] = new JsonArray(scale[0], scale[1], scale[2]);
        Array("nodes").Add(node);
        int index = Json["nodes"].AsArray().Count - 1;
        JsonNode parent = Json["nodes"][ObjectNode];
        if (parent["children"] is not JsonArray children)
            parent["children"] = children = new JsonArray();
        children.Add(index);
        return index;
    }

    /// <summary>A cube of the given edge length centred on the node's origin, with a face for each side (24 vertices).</summary>
    public int Cube(string name, float edge, int material = -1, float[] translation = null, float[] rotation = null, float[] scale = null)
    {
        float h = edge / 2f;
        List<float> p = new List<float>(), n = new List<float>(), t = new List<float>();
        List<uint> idx = new List<uint>();
        (float[] normal, float[] u, float[] v)[] sides =
        {
            (new[] { 0f, 0f, 1f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }),
            (new[] { 0f, 0f, -1f }, new[] { -1f, 0f, 0f }, new[] { 0f, 1f, 0f }),
            (new[] { 1f, 0f, 0f }, new[] { 0f, 0f, -1f }, new[] { 0f, 1f, 0f }),
            (new[] { -1f, 0f, 0f }, new[] { 0f, 0f, 1f }, new[] { 0f, 1f, 0f }),
            (new[] { 0f, 1f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, -1f }),
            (new[] { 0f, -1f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 1f })
        };
        foreach (var s in sides)
        {
            uint b = (uint)(p.Count / 3);
            foreach ((int a, int c) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                for (int k = 0; k < 3; k++)
                    p.Add((s.normal[k] + a * s.u[k] + c * s.v[k]) * h);
                n.AddRange(s.normal);
                t.Add((a + 1) / 2f); t.Add(1f - (c + 1) / 2f);
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        return MeshNode(name, p.ToArray(), n.ToArray(), t.ToArray(), idx.ToArray(), material, translation, rotation, scale);
    }

    /// <summary>A flat grid of n by n squares, every vertex its own (so (n + 1)^2 vertices and 2 n^2 triangles), one metre across.</summary>
    public static (float[] p, float[] n, float[] t, uint[] i) Grid(int n)
    {
        int w = n + 1;
        float[] p = new float[w * w * 3], nor = new float[w * w * 3], t = new float[w * w * 2];
        for (int z = 0; z < w; z++)
            for (int x = 0; x < w; x++)
            {
                int v = z * w + x;
                p[v * 3] = x / (float)n - 0.5f; p[v * 3 + 1] = 0f; p[v * 3 + 2] = z / (float)n - 0.5f;
                nor[v * 3 + 1] = 1f;
                t[v * 2] = x / (float)n; t[v * 2 + 1] = z / (float)n;
            }
        uint[] idx = new uint[n * n * 6];
        int k = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                uint a = (uint)(z * w + x), b = a + 1, c = a + (uint)w, d = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        return (p, nor, t, idx);
    }

    /// <summary>Takes a part's node out of the file the way deleting an object in a modelling tool does.</summary>
    public void RemovePart(string partUuid)
    {
        JsonArray nodes = Json["nodes"].AsArray();
        int at = nodes.ToList().FindIndex(n => (string)n["extras"]?["partUuid"] == partUuid);
        foreach (JsonNode n in nodes)
            if (n["children"] is JsonArray c)
                for (int i = c.Count - 1; i >= 0; i--)
                    if ((int)c[i] == at)
                        c.RemoveAt(i);
        JsonObject node = nodes[at].AsObject();
        node.Remove("mesh");
        node["extras"].AsObject().Remove("partUuid");
    }

    public JsonNode Part(string partUuid) => Json["nodes"].AsArray().First(n => (string)n["extras"]?["partUuid"] == partUuid);
}
