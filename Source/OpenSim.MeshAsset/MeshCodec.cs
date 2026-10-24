/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace OpenSim.MeshAsset;

/// <summary>What <see cref="MeshCodec.Decode(byte[], MeshDecodeOptions?)"/> may leave unread. Every flag defaults to reading everything.</summary>
public sealed class MeshDecodeOptions
{
    /// <summary>Read only the highest display level present (high, then medium, low, lowest); the others stay null.</summary>
    public bool HighestLevelOnly { get; set; }
    /// <summary>Do not read the physics_convex and physics_mesh blocks.</summary>
    public bool SkipPhysics { get; set; }
    /// <summary>Do not read the per-vertex skin weights of a face; <see cref="MeshFace.Weights"/> stays null.</summary>
    public bool SkipWeights { get; set; }
}

/// <summary>Reads and writes the Linden Lab mesh asset: a binary LLSD header, then zlib-compressed binary LLSD blocks.</summary>
public static class MeshCodec
{
    static readonly string[] LodKeys = { "lowest_lod", "low_lod", "medium_lod", "high_lod" };
    const int MaxBlockBytes = 512 * 1024 * 1024;

    // ---------------------------------------------------------------- encode

    public static byte[] Encode(MeshAssetModel model)
    {
        var blocks = new List<byte[]>();
        var offsets = new Dictionary<string, (int Offset, int Size)>();
        int cur = 0;

        void Add(string key, byte[] data)
        {
            offsets[key] = (cur, data.Length);
            blocks.Add(data);
            cur += data.Length;
        }

        if (model.Skin != null) Add("skin", Zip(LlsdBinary.Write(SkinToLlsd(model.Skin))));
        if (model.PhysicsConvex != null) Add("physics_convex", Zip(LlsdBinary.Write(model.PhysicsConvex)));

        // Every level of detail is written as its own block, even when two entries hold the same geometry: the viewer
        // writes a "use LOD above" level as a full copy and never lets two header entries share an offset.
        for (int i = 0; i < MeshAssetModel.LodCount; i++)
        {
            var lod = model.Lod(i);
            if (lod == null) continue;
            Add(LodKeys[i], Zip(LlsdBinary.Write(LodToLlsd(lod))));
        }
        if (!LodKeys.Any(offsets.ContainsKey)) throw new InvalidOperationException("the model has no level of detail");
        if (model.PhysicsMesh != null) Add("physics_mesh", Zip(LlsdBinary.Write(model.PhysicsMesh)));

        var header = LlsdBinary.NewMap();
        foreach (var kv in model.ExtraHeader) header[kv.Key] = kv.Value;
        if (model.Version is int version) header["version"] = version;
        foreach (var kv in offsets) header[kv.Key] = Entry(kv.Value);

        using var ms = new MemoryStream();
        ms.Write(LlsdBinary.Write(header));
        foreach (var b in blocks) ms.Write(b);
        return ms.ToArray();
    }

    static SortedDictionary<string, object?> Entry((int Offset, int Size) e)
    {
        var m = LlsdBinary.NewMap();
        m["offset"] = e.Offset;
        m["size"] = e.Size;
        return m;
    }

    static byte[] Zip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    static List<object?> RealList(Vector3 v) => new() { (double)v.X, (double)v.Y, (double)v.Z };
    static List<object?> RealList(Vector2 v) => new() { (double)v.X, (double)v.Y };

    static ushort Q(double x) => (ushort)Math.Clamp((long)Math.Round(x), 0L, 65535L);
    static double Range(float min, float max) => (double)max - min == 0 ? 1.0 : (double)max - min;

    static void PutU16(byte[] buf, ref int pos, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), v);
        pos += 2;
    }

    static SortedDictionary<string, object?> Domain(List<object?> min, List<object?> max)
    {
        var d = LlsdBinary.NewMap();
        d["Min"] = min;
        d["Max"] = max;
        return d;
    }

    static List<object?> LodToLlsd(MeshLod lod)
    {
        // The box around every face with geometry: used by faces that do not carry their own domain.
        Vector3? lodMin = null, lodMax = null;
        foreach (var f in lod.Faces)
        {
            if (f.NoGeometry || f.Positions.Length < 3) continue;
            foreach (var p in f.Positions)
            {
                lodMin = lodMin == null ? p : Vector3.Min(lodMin.Value, p);
                lodMax = lodMax == null ? p : Vector3.Max(lodMax.Value, p);
            }
        }

        var faces = new List<object?>();
        foreach (var f in lod.Faces)
        {
            var map = LlsdBinary.NewMap();
            faces.Add(map);
            if (f.NoGeometry || f.VertexCount < 3)
            {
                map["NoGeometry"] = true;
                continue;
            }
            int n = f.VertexCount;
            if (n > 65535) throw new MeshFormatException($"a face has {n} vertices; the asset stores 16-bit indices");
            if (f.Normals != null && f.Normals.Length != n) throw new MeshFormatException("normal count differs from vertex count");
            if (f.Uvs != null && f.Uvs.Length != n) throw new MeshFormatException("UV count differs from vertex count");
            if (f.Weights != null && f.Weights.Length != n) throw new MeshFormatException("weight count differs from vertex count");
            foreach (var ix in f.Indices)
                if (ix < 0 || ix >= n) throw new MeshFormatException($"index {ix} is outside the face's {n} vertices");

            var (pmin, pmax) = f.PositionDomain ?? (lodMin!.Value, lodMax!.Value);
            map["PositionDomain"] = Domain(RealList(pmin), RealList(pmax));
            if (f.NormalizedScale is { } ns) map["NormalizedScale"] = RealList(ns);

            var pos = new byte[n * 6];
            int pp = 0;
            double rx = Range(pmin.X, pmax.X), ry = Range(pmin.Y, pmax.Y), rz = Range(pmin.Z, pmax.Z);
            foreach (var p in f.Positions)
            {
                PutU16(pos, ref pp, Q((p.X - (double)pmin.X) / rx * 65535));
                PutU16(pos, ref pp, Q((p.Y - (double)pmin.Y) / ry * 65535));
                PutU16(pos, ref pp, Q((p.Z - (double)pmin.Z) / rz * 65535));
            }
            map["Position"] = pos;

            if (f.Normals != null)
            {
                var nb = new byte[n * 6];
                int np = 0;
                foreach (var v in f.Normals)
                {
                    PutU16(nb, ref np, Q((v.X + 1.0) * 0.5 * 65535));
                    PutU16(nb, ref np, Q((v.Y + 1.0) * 0.5 * 65535));
                    PutU16(nb, ref np, Q((v.Z + 1.0) * 0.5 * 65535));
                }
                map["Normal"] = nb;
            }

            if (f.Uvs != null)
            {
                Vector2 umin, umax;
                if (f.UvDomain is { } d) (umin, umax) = d;
                else
                {
                    umin = umax = f.Uvs[0];
                    foreach (var t in f.Uvs) { umin = Vector2.Min(umin, t); umax = Vector2.Max(umax, t); }
                }
                map["TexCoord0Domain"] = Domain(RealList(umin), RealList(umax));
                var tb = new byte[n * 4];
                int tp = 0;
                double ux = Range(umin.X, umax.X), uy = Range(umin.Y, umax.Y);
                foreach (var t in f.Uvs)
                {
                    PutU16(tb, ref tp, Q((t.X - (double)umin.X) / ux * 65535));
                    PutU16(tb, ref tp, Q((t.Y - (double)umin.Y) / uy * 65535));
                }
                map["TexCoord0"] = tb;
            }

            var ib = new byte[f.Indices.Length * 2];
            int ip = 0;
            foreach (var ix in f.Indices) PutU16(ib, ref ip, (ushort)ix);
            map["TriangleList"] = ib;

            if (f.Weights != null)
            {
                using var wm = new MemoryStream();
                var b2 = new byte[2];
                foreach (var infl in f.Weights)
                {
                    if (infl.Length > 4) throw new MeshFormatException("a vertex has more than 4 influences");
                    foreach (var w in infl)
                    {
                        if (w.Joint == 0xFF) throw new MeshFormatException("joint index 255 is the list terminator");
                        wm.WriteByte(w.Joint);
                        BinaryPrimitives.WriteUInt16LittleEndian(b2, Q(w.Weight * 65535.0));
                        wm.Write(b2);
                    }
                    // A vertex with four influences has no terminator; fewer than four end with 0xFF.
                    if (infl.Length < 4) wm.WriteByte(0xFF);
                }
                map["Weights"] = wm.ToArray();
            }
        }
        return faces;
    }

    static List<object?> MatrixList(double[] m) => m.Select(x => (object?)x).ToList();

    static SortedDictionary<string, object?> SkinToLlsd(MeshSkin s)
    {
        if (s.InverseBind.Count != s.JointNames.Count) throw new MeshFormatException("skin has a different number of joints and inverse bind matrices");
        var m = LlsdBinary.NewMap();
        m["joint_names"] = s.JointNames.Select(x => (object?)x).ToList();
        m["inverse_bind_matrix"] = s.InverseBind.Select(x => (object?)MatrixList(x)).ToList();
        m["bind_shape_matrix"] = MatrixList(s.BindShape);
        if (s.AltInverseBind != null)
        {
            m["alt_inverse_bind_matrix"] = s.AltInverseBind.Select(x => (object?)MatrixList(x)).ToList();
            if (s.LockScaleIfJointPosition) m["lock_scale_if_joint_position"] = true;
            m["pelvis_offset"] = s.PelvisOffset ?? 0.0;
        }
        return m;
    }

    // ---------------------------------------------------------------- decode

    /// <summary>Decodes the whole asset.</summary>
    public static MeshAssetModel Decode(byte[] data) => Decode(data, null);

    /// <summary>
    /// Decodes the asset, leaving out the parts <paramref name="options"/> says the caller will not use. The parts left out
    /// are not read at all, which keeps a large asset's memory use down; with no options everything is read.
    /// </summary>
    public static MeshAssetModel Decode(byte[] data, MeshDecodeOptions? options)
    {
        bool highestOnly = options?.HighestLevelOnly ?? false;
        bool skipPhysics = options?.SkipPhysics ?? false;
        bool skipWeights = options?.SkipWeights ?? false;
        var headerObj = LlsdBinary.Read(data, out int headerLen);
        if (headerObj is not IDictionary<string, object?> header) throw new MeshFormatException("the asset header is not a map");

        var model = new MeshAssetModel();
        model.Version = header.TryGetValue("version", out var ver) && ver is int vi ? vi : null;

        byte[]? Block(string key, out (int, int) at)
        {
            at = default;
            if (!header.TryGetValue(key, out var e) || e is not IDictionary<string, object?> em) return null;
            if (!(em.TryGetValue("offset", out var o) && o is int off && em.TryGetValue("size", out var sz) && sz is int size)) return null;
            if (size <= 0) return null;
            if (off < 0 || (long)headerLen + off + size > data.Length)
                throw new MeshFormatException($"block {key} (offset {off}, size {size}) runs past the end of the asset ({data.Length} bytes, header {headerLen})");
            at = (off, size);
            return Unzip(data.AsSpan(headerLen + off, size));
        }

        var known = new HashSet<string>(StringComparer.Ordinal) { "version", "skin", "physics_convex", "physics_mesh" };
        foreach (var k in LodKeys) known.Add(k);

        var skinRaw = Block("skin", out _);
        if (skinRaw != null) model.Skin = SkinFromLlsd(LlsdBinary.Read(skinRaw, out _));
        var convex = skipPhysics ? null : Block("physics_convex", out _);
        if (convex != null) model.PhysicsConvex = LlsdBinary.Read(convex, out _);

        var decoded = new Dictionary<(int, int), MeshLod>();
        bool haveLod = false;
        for (int i = MeshAssetModel.LodCount - 1; i >= 0; i--)
        {
            // the highest level present is the last one needed when only that level was asked for
            if (highestOnly && haveLod) break;
            var raw = Block(LodKeys[i], out var at);
            if (raw == null) continue;
            haveLod = true;
            if (!decoded.TryGetValue(at, out var lod))
            {
                lod = LodFromLlsd(LlsdBinary.Read(raw, out _), LodKeys[i], model.Warnings, skipWeights);
                decoded[at] = lod;
            }
            model.SetLod(i, lod);
        }
        var phys = skipPhysics ? null : Block("physics_mesh", out _);
        if (phys != null) model.PhysicsMesh = LlsdBinary.Read(phys, out _);

        foreach (var kv in header)
            if (!known.Contains(kv.Key)) model.ExtraHeader[kv.Key] = kv.Value;

        if (model.High == null && model.Medium == null && model.Low == null && model.Lowest == null)
            throw new MeshFormatException("the asset has no level of detail");
        return model;
    }

    static byte[] Unzip(ReadOnlySpan<byte> z)
    {
        try
        {
            using var src = new MemoryStream(z.ToArray());
            using var zs = new ZLibStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = zs.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                if (dst.Length > MaxBlockBytes) throw new MeshFormatException("a block inflates past the 512 MiB limit");
            }
            return dst.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new MeshFormatException("a block is not valid zlib data: " + e.Message);
        }
    }

    static double[] Doubles(object? o, int expect, string what)
    {
        if (o is not IList<object?> l || l.Count != expect) throw new MeshFormatException($"{what} is not a list of {expect} numbers");
        var r = new double[expect];
        for (int i = 0; i < expect; i++)
            r[i] = l[i] switch { double d => d, int n => n, _ => throw new MeshFormatException($"{what} holds a non-number") };
        return r;
    }

    static MeshSkin SkinFromLlsd(object? o)
    {
        if (o is not IDictionary<string, object?> m) throw new MeshFormatException("the skin block is not a map");
        var s = new MeshSkin();
        if (m.TryGetValue("joint_names", out var jn) && jn is IList<object?> names)
            s.JointNames = names.Select(x => x as string ?? throw new MeshFormatException("a joint name is not a string")).ToList();
        if (m.TryGetValue("inverse_bind_matrix", out var ib) && ib is IList<object?> ibl)
            s.InverseBind = ibl.Select(x => Doubles(x, 16, "inverse_bind_matrix")).ToList();
        if (m.TryGetValue("bind_shape_matrix", out var bs)) s.BindShape = Doubles(bs, 16, "bind_shape_matrix");
        if (m.TryGetValue("alt_inverse_bind_matrix", out var ab) && ab is IList<object?> abl)
            s.AltInverseBind = abl.Select(x => Doubles(x, 16, "alt_inverse_bind_matrix")).ToList();
        if (m.TryGetValue("pelvis_offset", out var po)) s.PelvisOffset = po switch { double d => d, int i => i, _ => null };
        if (m.TryGetValue("lock_scale_if_joint_position", out var ls) && ls is bool b) s.LockScaleIfJointPosition = b;
        return s;
    }

    static Vector3 V3(object? o, string what)
    {
        var d = Doubles(o, 3, what);
        return new Vector3((float)d[0], (float)d[1], (float)d[2]);
    }

    static Vector2 V2(object? o, string what)
    {
        var d = Doubles(o, 2, what);
        return new Vector2((float)d[0], (float)d[1]);
    }

    static MeshLod LodFromLlsd(object? o, string lodName, List<string> warnings, bool skipWeights = false)
    {
        if (o is not IList<object?> faces) throw new MeshFormatException($"{lodName} is not a list of faces");
        var lod = new MeshLod();
        int fi = 0;
        foreach (var fo in faces)
        {
            var face = new MeshFace();
            lod.Faces.Add(face);
            string tag = $"{lodName} face {fi++}";
            if (fo is not IDictionary<string, object?> m) throw new MeshFormatException($"{tag} is not a map");
            if (m.ContainsKey("NoGeometry")) { face.NoGeometry = true; continue; }

            byte[] Bin(string k) => m.TryGetValue(k, out var v) && v is byte[] b ? b : Array.Empty<byte>();
            var pos = Bin("Position");
            var idx = Bin("TriangleList");
            if (pos.Length % 6 != 0) throw new MeshFormatException($"{tag}: Position is not a whole number of vertices");
            int n = pos.Length / 6;
            if (idx.Length % 2 != 0) throw new MeshFormatException($"{tag}: TriangleList has an odd byte count");
            if (idx.Length / 2 % 3 != 0) warnings.Add($"{tag}: index count {idx.Length / 2} is not a multiple of 3");

            if (!(m.TryGetValue("PositionDomain", out var pd) && pd is IDictionary<string, object?> pdm))
                throw new MeshFormatException($"{tag}: no PositionDomain");
            var pmin = V3(pdm["Min"], "PositionDomain.Min");
            var pmax = V3(pdm["Max"], "PositionDomain.Max");
            face.PositionDomain = (pmin, pmax);
            if (m.TryGetValue("NormalizedScale", out var ns)) face.NormalizedScale = V3(ns, "NormalizedScale");

            face.Positions = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                double x = BinaryPrimitives.ReadUInt16LittleEndian(pos.AsSpan(i * 6, 2)) / 65535.0;
                double y = BinaryPrimitives.ReadUInt16LittleEndian(pos.AsSpan(i * 6 + 2, 2)) / 65535.0;
                double z = BinaryPrimitives.ReadUInt16LittleEndian(pos.AsSpan(i * 6 + 4, 2)) / 65535.0;
                face.Positions[i] = new Vector3(
                    (float)(x * ((double)pmax.X - pmin.X) + pmin.X),
                    (float)(y * ((double)pmax.Y - pmin.Y) + pmin.Y),
                    (float)(z * ((double)pmax.Z - pmin.Z) + pmin.Z));
            }

            var norm = Bin("Normal");
            if (norm.Length > 0)
            {
                if (norm.Length != n * 6) throw new MeshFormatException($"{tag}: Normal has {norm.Length / 6} entries for {n} vertices");
                face.Normals = new Vector3[n];
                for (int i = 0; i < n; i++)
                    face.Normals[i] = new Vector3(
                        (float)(BinaryPrimitives.ReadUInt16LittleEndian(norm.AsSpan(i * 6, 2)) / 65535.0 * 2 - 1),
                        (float)(BinaryPrimitives.ReadUInt16LittleEndian(norm.AsSpan(i * 6 + 2, 2)) / 65535.0 * 2 - 1),
                        (float)(BinaryPrimitives.ReadUInt16LittleEndian(norm.AsSpan(i * 6 + 4, 2)) / 65535.0 * 2 - 1));
            }

            var tc = Bin("TexCoord0");
            if (tc.Length > 0)
            {
                if (tc.Length != n * 4) throw new MeshFormatException($"{tag}: TexCoord0 has {tc.Length / 4} entries for {n} vertices");
                if (!(m.TryGetValue("TexCoord0Domain", out var td) && td is IDictionary<string, object?> tdm))
                    throw new MeshFormatException($"{tag}: TexCoord0 without TexCoord0Domain");
                var tmin = V2(tdm["Min"], "TexCoord0Domain.Min");
                var tmax = V2(tdm["Max"], "TexCoord0Domain.Max");
                face.UvDomain = (tmin, tmax);
                face.Uvs = new Vector2[n];
                for (int i = 0; i < n; i++)
                    face.Uvs[i] = new Vector2(
                        (float)(BinaryPrimitives.ReadUInt16LittleEndian(tc.AsSpan(i * 4, 2)) / 65535.0 * ((double)tmax.X - tmin.X) + tmin.X),
                        (float)(BinaryPrimitives.ReadUInt16LittleEndian(tc.AsSpan(i * 4 + 2, 2)) / 65535.0 * ((double)tmax.Y - tmin.Y) + tmin.Y));
            }

            face.Indices = new int[idx.Length / 2];
            for (int i = 0; i < face.Indices.Length; i++)
            {
                face.Indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(idx.AsSpan(i * 2, 2));
                if (face.Indices[i] >= n) warnings.Add($"{tag}: index {face.Indices[i]} is outside {n} vertices");
            }

            if (!skipWeights && m.TryGetValue("Weights", out var wo) && wo is byte[] wb)
                face.Weights = ReadWeights(wb, n, tag);
        }
        return lod;
    }

    static Influence[][] ReadWeights(byte[] w, int vertices, string tag)
    {
        var result = new Influence[vertices][];
        int p = 0;
        for (int v = 0; v < vertices; v++)
        {
            var list = new List<Influence>(4);
            while (true)
            {
                if (p >= w.Length) throw new MeshFormatException($"{tag}: Weights end inside vertex {v}");
                byte joint = w[p++];
                if (joint == 0xFF) break;
                if (p + 2 > w.Length) throw new MeshFormatException($"{tag}: Weights end inside an influence of vertex {v}");
                ushort u = BinaryPrimitives.ReadUInt16LittleEndian(w.AsSpan(p, 2));
                p += 2;
                list.Add(new Influence(joint, (float)(u / 65535.0)));
                if (list.Count == 4) break;
            }
            result[v] = list.ToArray();
        }
        if (p != w.Length) throw new MeshFormatException($"{tag}: Weights hold {w.Length - p} bytes more than {vertices} vertices use");
        return result;
    }
}
