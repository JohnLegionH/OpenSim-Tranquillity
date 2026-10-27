/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SkiaSharp;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>A .glb material for the import fixtures: a name, a colour, and optional pictures (PNG bytes).</summary>
internal sealed class ImportMaterial
{
    public string Name;
    public float[] Color = { 1f, 1f, 1f, 1f };
    public byte[] Picture;
    public byte[] Normal;
}

/// <summary>
/// A model folder as the mesh tools write one: a .glb for the full mesh and one for each lower level of detail, a physics .glb and the pictures inside the
/// files. The model is a slab (0.4 x 0.1 x 0.2 m, standing on y = 0 in the glTF axes); each side is a grid of n x n squares, with the top as the
/// material "top" and the other sides as "side". The levels use n = 4, 3, 2 and 1, and the lowest has no top.
/// </summary>
internal static class ImportKit
{
    public const string Name = "slab";
    public static readonly Guid Avatar = Guid.Parse("0e5d1b52-8c3a-4f67-9d21-6a7b3c4d5e8f");
    public static readonly int[] GridPerLevel = { 4, 3, 2, 1 };

    public sealed class Geo
    {
        public List<float> Pos = new List<float>(), Nor = new List<float>(), Uv = new List<float>();
        public List<uint> Idx = new List<uint>();
        public int Triangles => Idx.Count / 3;
    }

    /// <summary>Triangles of a slab at grid n with or without its top.</summary>
    public static int SlabTriangles(int n, bool top) => 2 * n * n * (top ? 6 : 5);

    /// <summary>The sides of a box with half sizes (hx, hy, hz) at (cx, cy, cz): centre, then the two edge vectors whose cross product is the outward normal. Index 0 is the top.</summary>
    private static (float[] c, float[] u, float[] v)[] Sides(float cx, float cy, float cz, float hx, float hy, float hz) => new[]
    {
        (new[] { cx, cy + hy, cz }, new[] { 0f, 0f, hz }, new[] { hx, 0f, 0f }),
        (new[] { cx, cy - hy, cz }, new[] { hx, 0f, 0f }, new[] { 0f, 0f, hz }),
        (new[] { cx + hx, cy, cz }, new[] { 0f, hy, 0f }, new[] { 0f, 0f, hz }),
        (new[] { cx - hx, cy, cz }, new[] { 0f, 0f, hz }, new[] { 0f, hy, 0f }),
        (new[] { cx, cy, cz + hz }, new[] { hx, 0f, 0f }, new[] { 0f, hy, 0f }),
        (new[] { cx, cy, cz - hz }, new[] { 0f, hy, 0f }, new[] { hx, 0f, 0f }),
    };

    /// <summary>Adds the sides of a box that <paramref name="take"/> accepts (by side number) to <paramref name="g"/>, each as a grid of n x n squares with its own vertices.</summary>
    public static void AddBox(Geo g, int n, Func<int, bool> take, float cx = 0f, float cy = 0.1f, float cz = 0f, float hx = 0.2f, float hy = 0.1f, float hz = 0.05f)
    {
        var sides = Sides(cx, cy, cz, hx, hy, hz);
        for (int s = 0; s < sides.Length; s++)
        {
            if (!take(s))
                continue;
            (float[] c, float[] u, float[] v) = sides[s];
            float nx = u[1] * v[2] - u[2] * v[1], ny = u[2] * v[0] - u[0] * v[2], nz = u[0] * v[1] - u[1] * v[0];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            nx /= len; ny /= len; nz /= len;
            uint start = (uint)(g.Pos.Count / 3);
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float a = 2f * i / n - 1f, b = 2f * j / n - 1f;
                    g.Pos.Add(c[0] + a * u[0] + b * v[0]); g.Pos.Add(c[1] + a * u[1] + b * v[1]); g.Pos.Add(c[2] + a * u[2] + b * v[2]);
                    g.Nor.Add(nx); g.Nor.Add(ny); g.Nor.Add(nz);
                    g.Uv.Add((float)i / n); g.Uv.Add((float)j / n);
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    uint v00 = start + (uint)(j * (n + 1) + i), v10 = v00 + 1, v01 = v00 + (uint)(n + 1), v11 = v01 + 1;
                    g.Idx.AddRange(new[] { v00, v10, v11, v00, v11, v01 });
                }
        }
    }

    public static byte[] Png(byte r, byte g, byte b, int size = 8)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, new SKColor((byte)((r + x * 7) & 255), (byte)((g + y * 11) & 255), b, 255));
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A binary glTF with one primitive for each (material, geometry) pair. The materials the primitives name are written with their pictures inside the file.</summary>
    public static byte[] Glb(IReadOnlyList<(ImportMaterial Material, Geo Geometry)> prims, bool uvs = true) =>
        GlbNodes(new[] { (Name: "level", Translation: (float[])null, Rotation: (float[])null, Prims: prims) }, uvs);

    /// <summary>A binary glTF with several top-level mesh nodes, each with its own name, optional translation and rotation (glTF axes) and primitives.</summary>
    public static byte[] GlbNodes(IReadOnlyList<(string Name, float[] Translation, float[] Rotation, IReadOnlyList<(ImportMaterial Material, Geo Geometry)> Prims)> nodeList, bool uvs = true)
    {
        List<byte> bin = new List<byte>();
        JsonArray views = new JsonArray(), accessors = new JsonArray(), materials = new JsonArray(), images = new JsonArray(), textures = new JsonArray(), primitives = new JsonArray();
        int View(byte[] data)
        {
            while (bin.Count % 4 != 0)
                bin.Add(0);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Count, ["byteLength"] = data.Length });
            bin.AddRange(data);
            return views.Count - 1;
        }
        int Accessor(float[] values, int width, string type, bool bounds)
        {
            byte[] bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            JsonObject a = new JsonObject { ["bufferView"] = View(bytes), ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type };
            if (bounds)
            {
                JsonArray min = new JsonArray(), max = new JsonArray();
                for (int c = 0; c < width; c++)
                {
                    min.Add(Enumerable.Range(0, values.Length / width).Min(i => values[i * width + c]));
                    max.Add(Enumerable.Range(0, values.Length / width).Max(i => values[i * width + c]));
                }
                a["min"] = min; a["max"] = max;
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }
        int Texture(byte[] png)
        {
            images.Add(new JsonObject { ["bufferView"] = View(png), ["mimeType"] = "image/png" });
            textures.Add(new JsonObject { ["source"] = images.Count - 1 });
            return textures.Count - 1;
        }
        Dictionary<ImportMaterial, int> done = new Dictionary<ImportMaterial, int>();
        JsonArray nodeArray = new JsonArray(), meshArray = new JsonArray(), sceneNodes = new JsonArray();
        foreach ((string nodeName, float[] translation, float[] rotation, IReadOnlyList<(ImportMaterial Material, Geo Geometry)> prims) in nodeList)
        {
        primitives = new JsonArray();
        foreach ((ImportMaterial m, Geo g) in prims)
        {
            int materialIndex = -1;
            if (m != null && !done.TryGetValue(m, out materialIndex))
            {
                JsonObject pbr = new JsonObject { ["baseColorFactor"] = new JsonArray(m.Color.Select(x => (JsonNode)x).ToArray()), ["metallicFactor"] = 0 };
                if (m.Picture != null)
                    pbr["baseColorTexture"] = new JsonObject { ["index"] = Texture(m.Picture) };
                JsonObject mat = new JsonObject { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = true };
                if (m.Normal != null)
                    mat["normalTexture"] = new JsonObject { ["index"] = Texture(m.Normal) };
                materials.Add(mat);
                done[m] = materialIndex = materials.Count - 1;
            }
            JsonObject attributes = new JsonObject { ["POSITION"] = Accessor(g.Pos.ToArray(), 3, "VEC3", true), ["NORMAL"] = Accessor(g.Nor.ToArray(), 3, "VEC3", false) };
            if (uvs)
                attributes["TEXCOORD_0"] = Accessor(g.Uv.ToArray(), 2, "VEC2", false);
            byte[] idx = new byte[g.Idx.Count * 4];
            Buffer.BlockCopy(g.Idx.ToArray(), 0, idx, 0, idx.Length);
            accessors.Add(new JsonObject { ["bufferView"] = View(idx), ["componentType"] = 5125, ["count"] = g.Idx.Count, ["type"] = "SCALAR" });
            JsonObject prim = new JsonObject { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["mode"] = 4 };
            if (materialIndex >= 0)
                prim["material"] = materialIndex;
            primitives.Add(prim);
        }
        meshArray.Add(new JsonObject { ["name"] = nodeName, ["primitives"] = primitives });
        JsonObject node = new JsonObject { ["name"] = nodeName, ["mesh"] = meshArray.Count - 1 };
        if (translation != null) node["translation"] = new JsonArray(translation.Select(x => (JsonNode)x).ToArray());
        if (rotation != null) node["rotation"] = new JsonArray(rotation.Select(x => (JsonNode)x).ToArray());
        nodeArray.Add(node);
        sceneNodes.Add(nodeArray.Count - 1);
        }
        while (bin.Count % 4 != 0)
            bin.Add(0);
        JsonObject root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodeArray,
            ["meshes"] = meshArray,
            ["accessors"] = accessors, ["bufferViews"] = views, ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Count })
        };
        if (materials.Count > 0) root["materials"] = materials;
        if (images.Count > 0) { root["images"] = images; root["textures"] = textures; }
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPadded = (json.Length + 3) & ~3;
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonPadded + 8 + bin.Count));
        w.Write((uint)jsonPadded); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonPadded; i++) w.Write((byte)0x20);
        w.Write((uint)bin.Count); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return ms.ToArray();
    }

    public enum PhysicsKind { Box, Ell }

    public sealed class Folder
    {
        public string Path;
        public ImportMaterial Top, Side;
    }

    /// <summary>Writes the model folder. With <paramref name="normalMap"/> the side material carries a normal map.</summary>
    public static Folder Write(bool normalMap = false, PhysicsKind physics = PhysicsKind.Box, bool withPhysics = true, string name = Name, string into = null)
    {
        string folder = into ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ImportMaterial top = new ImportMaterial { Name = "top", Color = new[] { 0.2f, 0.4f, 0.6f, 1f }, Picture = Png(200, 40, 10) };
        ImportMaterial side = new ImportMaterial { Name = "side", Color = new[] { 1f, 1f, 1f, 1f }, Picture = Png(30, 90, 150), Normal = normalMap ? Png(128, 128, 255) : null };
        string[] files = { name + ".glb", name + "_lod2.glb", name + "_lod1.glb", name + "_lod0.glb" };
        for (int level = 0; level < 4; level++)
        {
            int n = GridPerLevel[level];
            Geo topGeo = new Geo(), sideGeo = new Geo();
            if (level < 3)
                AddBox(topGeo, n, s => s == 0);
            AddBox(sideGeo, n, s => s != 0);
            List<(ImportMaterial, Geo)> prims = new List<(ImportMaterial, Geo)>();
            if (level < 3)
                prims.Add((top, topGeo));
            prims.Add((side, sideGeo));
            File.WriteAllBytes(System.IO.Path.Combine(folder, files[level]), Glb(prims));
        }
        if (withPhysics)
        {
            Geo p = new Geo();
            if (physics == PhysicsKind.Box)
                AddBox(p, 1, _ => true);
            else
            {
                // two boxes, the second sitting on half of the first: an L that is not convex
                AddBox(p, 1, _ => true, 0f, 0.05f, 0f, 0.2f, 0.05f, 0.05f);
                AddBox(p, 1, _ => true, 0.1f, 0.15f, 0f, 0.1f, 0.05f, 0.05f);
            }
            File.WriteAllBytes(System.IO.Path.Combine(folder, name + "_physics.glb"), Glb(new[] { ((ImportMaterial)null, p) }, uvs: false));
        }
        return new Folder { Path = folder, Top = top, Side = side };
    }

    /// <summary>The entries of an archive by name.</summary>
    public static Dictionary<string, byte[]> ReadOar(string path)
    {
        Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using GZipStream gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using TarReader tar = new TarReader(gz);
        TarEntry e;
        while ((e = tar.GetNextEntry()) != null)
        {
            if (e.EntryType != TarEntryType.RegularFile && e.EntryType != TarEntryType.V7RegularFile)
                continue;
            using MemoryStream ms = new MemoryStream();
            e.DataStream.CopyTo(ms);
            entries[e.Name] = ms.ToArray();
        }
        return entries;
    }

    public static string OutputPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N") + ".oar");

    public static XElement Part(Dictionary<string, byte[]> oar)
    {
        string name = oar.Keys.Single(k => k.StartsWith("objects/", StringComparison.Ordinal) && k.EndsWith(".xml", StringComparison.Ordinal));
        return XDocument.Parse(Encoding.UTF8.GetString(oar[name])).Root.Element("SceneObjectPart");
    }

    public static string Text(XElement part, string path)
    {
        XElement e = part;
        foreach (string step in path.Split('/'))
            e = e?.Element(step);
        return e?.Value;
    }
}
