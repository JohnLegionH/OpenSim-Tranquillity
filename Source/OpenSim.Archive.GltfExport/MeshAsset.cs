/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Written from the mesh asset format as the Second Life viewer reads it (LLVolume::unpackVolumeFacesInternal in
// indra/llmath/llvolume.cpp) and the Second Life wiki's Mesh/Mesh Asset Format page. No viewer code is copied.

using System.IO.Compression;
using OpenMetaverse.StructuredData;

namespace OpenSim.Archive.GltfExport;

/// <summary>One submesh of a mesh asset: a prim face's triangles. Positions are in the asset's own units (the unit cube the part's size scales).</summary>
public sealed class MeshFace
{
    /// <summary>The face number: the submesh's position in the level of detail.</summary>
    public int Number;
    public float[] Positions;    // x, y, z per vertex
    public float[] Normals;      // x, y, z per vertex; zero when the asset has none
    public float[] Uvs;          // u, v per vertex, v up as stored
    public int[] Indices;
    public bool HasNormals, HasUvs;
    public bool NoGeometry;
}

/// <summary>Rigging an asset carries.</summary>
public sealed class MeshSkin
{
    public List<string> JointNames = new List<string>();
    public float[] BindShapeMatrix;
    public int InverseBindMatrices;
}

public sealed class MeshModel
{
    public List<MeshFace> Faces = new List<MeshFace>();
    public MeshSkin Skin;
    /// <summary>Which level of detail blocks the asset holds, for the record.</summary>
    public List<string> Blocks = new List<string>();
    public bool HasPhysics;
}

/// <summary>Decodes the highest level of detail of a mesh asset.</summary>
public static class MeshAsset
{
    /// <summary>A mesh asset has at most this many faces; later submeshes are not faces of the part.</summary>
    public const int MaxFaces = 8;

    private const int MaxBlockBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The asset begins with a binary LLSD map naming each block by offset (from the end of that map) and size; each block is
    /// zlib-compressed binary LLSD. The display levels are lowest_lod, low_lod, medium_lod and high_lod; physics and the
    /// skin are other blocks. The highest level present is read.
    /// </summary>
    public static bool TryDecode(byte[] asset, out MeshModel model, out string problem)
    {
        model = null;
        problem = null;
        if (asset == null || asset.Length < 8)
        {
            problem = "the mesh asset is empty";
            return false;
        }

        OSDMap header;
        long start;
        try
        {
            using MemoryStream ms = new MemoryStream(asset, false);
            OSD osd = OSDParser.DeserializeLLSDBinary(ms);
            header = osd as OSDMap;
            start = ms.Position;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = "the mesh asset's header could not be read: " + e.Message;
            return false;
        }
        if (header == null)
        {
            problem = "the mesh asset's header is not a map";
            return false;
        }

        model = new MeshModel { HasPhysics = header.ContainsKey("physics_mesh") || header.ContainsKey("physics_shape") || header.ContainsKey("physics_convex") };
        foreach (string name in new[] { "lowest_lod", "low_lod", "medium_lod", "high_lod", "physics_convex", "physics_mesh", "skin" })
            if (header.ContainsKey(name) && header[name] is OSDMap)
                model.Blocks.Add(name);

        string level = new[] { "high_lod", "medium_lod", "low_lod", "lowest_lod" }.FirstOrDefault(n => header.ContainsKey(n) && header[n] is OSDMap);
        if (level == null)
        {
            problem = "the mesh asset has no display level of detail";
            model = null;
            return false;
        }

        OSD block;
        if (!TryReadBlock(asset, start, (OSDMap)header[level], out block, out problem))
        {
            model = null;
            return false;
        }
        OSDArray submeshes = block as OSDArray;
        if (submeshes == null || submeshes.Count == 0)
        {
            problem = "the mesh asset's " + level + " block holds no faces";
            model = null;
            return false;
        }

        for (int i = 0; i < submeshes.Count; i++)
        {
            MeshFace face = DecodeFace(submeshes[i] as OSDMap, i);
            model.Faces.Add(face);
        }

        if (header.ContainsKey("skin") && header["skin"] is OSDMap skinBlock
            && TryReadBlock(asset, start, skinBlock, out OSD skinOsd, out _) && skinOsd is OSDMap skinMap)
        {
            MeshSkin skin = new MeshSkin();
            if (skinMap.ContainsKey("joint_names") && skinMap["joint_names"] is OSDArray names)
                foreach (OSD n in names)
                    skin.JointNames.Add(n.AsString());
            if (skinMap.ContainsKey("bind_shape_matrix") && skinMap["bind_shape_matrix"] is OSDArray bind && bind.Count == 16)
            {
                skin.BindShapeMatrix = new float[16];
                for (int i = 0; i < 16; i++)
                    skin.BindShapeMatrix[i] = (float)bind[i].AsReal();
            }
            if (skinMap.ContainsKey("inverse_bind_matrix") && skinMap["inverse_bind_matrix"] is OSDArray inverse)
                skin.InverseBindMatrices = inverse.Count;
            model.Skin = skin;
        }
        return true;
    }

    private static bool TryReadBlock(byte[] asset, long start, OSDMap entry, out OSD block, out string problem)
    {
        block = null;
        problem = null;
        long offset = entry.ContainsKey("offset") ? entry["offset"].AsInteger() : -1;
        long size = entry.ContainsKey("size") ? entry["size"].AsInteger() : -1;
        if (offset < 0 || size <= 0 || start + offset + size > asset.Length)
        {
            problem = "a block of the mesh asset lies outside the asset (offset " + offset + ", size " + size + ")";
            return false;
        }
        try
        {
            using MemoryStream input = new MemoryStream(asset, (int)(start + offset), (int)size, false);
            using ZLibStream zlib = new ZLibStream(input, CompressionMode.Decompress);
            using MemoryStream output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxBlockBytes)
                {
                    problem = "a block of the mesh asset expands beyond " + MaxBlockBytes / (1024 * 1024) + " MB";
                    return false;
                }
            }
            output.Position = 0;
            block = OSDParser.DeserializeLLSDBinary(output);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = "a block of the mesh asset could not be decoded: " + e.Message;
            return false;
        }
    }

    private static MeshFace DecodeFace(OSDMap sub, int number)
    {
        MeshFace face = new MeshFace { Number = number };
        if (sub == null || sub.ContainsKey("NoGeometry") || !sub.ContainsKey("Position") || !sub.ContainsKey("TriangleList"))
        {
            face.NoGeometry = true;
            return face;
        }

        byte[] pos = sub["Position"].AsBinary();
        byte[] norm = sub.ContainsKey("Normal") ? sub["Normal"].AsBinary() : Array.Empty<byte>();
        byte[] tc = sub.ContainsKey("TexCoord0") ? sub["TexCoord0"].AsBinary() : Array.Empty<byte>();
        byte[] idx = sub["TriangleList"].AsBinary();

        // indices are 16-bit; a trailing partial triangle is dropped
        int indexCount = idx.Length / 2;
        indexCount -= indexCount % 3;
        int vertexCount = pos.Length / 6;
        if (indexCount < 3 || vertexCount < 1)
        {
            face.NoGeometry = true;
            return face;
        }

        float[] posMin = Domain(sub, "PositionDomain", "Min", 3, -0.5f), posMax = Domain(sub, "PositionDomain", "Max", 3, 0.5f);
        float[] tcMin = Domain(sub, "TexCoord0Domain", "Min", 2, 0f), tcMax = Domain(sub, "TexCoord0Domain", "Max", 2, 1f);

        face.Positions = new float[vertexCount * 3];
        for (int v = 0; v < vertexCount; v++)
            for (int c = 0; c < 3; c++)
                face.Positions[v * 3 + c] = U16(pos, (v * 3 + c) * 2) / 65535f * (posMax[c] - posMin[c]) + posMin[c];

        face.Normals = new float[vertexCount * 3];
        if (norm.Length >= vertexCount * 6)
        {
            face.HasNormals = true;
            for (int i = 0; i < vertexCount * 3; i++)
                face.Normals[i] = U16(norm, i * 2) / 65535f * 2f - 1f;
        }

        face.Uvs = new float[vertexCount * 2];
        if (tc.Length >= vertexCount * 4)
        {
            face.HasUvs = true;
            for (int v = 0; v < vertexCount; v++)
                for (int c = 0; c < 2; c++)
                    face.Uvs[v * 2 + c] = U16(tc, (v * 2 + c) * 2) / 65535f * (tcMax[c] - tcMin[c]) + tcMin[c];
        }

        face.Indices = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
            face.Indices[i] = U16(idx, i * 2);
        return face;
    }

    private static int U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);

    private static float[] Domain(OSDMap sub, string domain, string end, int n, float fallback)
    {
        float[] r = new float[n];
        Array.Fill(r, fallback);
        if (sub.ContainsKey(domain) && sub[domain] is OSDMap d && d.ContainsKey(end) && d[end] is OSDArray a)
            for (int i = 0; i < n && i < a.Count; i++)
                r[i] = (float)a[i].AsReal();
        return r;
    }
}

/// <summary>What the part's sculpt type flags do to a mesh, and the mesh as triangles per face.</summary>
public static class MeshGeometry
{
    /// <summary>
    /// Faces for the exporter, one per submesh that has geometry, numbered by submesh, at most <see cref="MeshAsset.MaxFaces"/>.
    /// The viewer applies the part's mirror and invert flags to a mesh: mirror reflects positions and normals through the
    /// origin and reverses the triangles, invert turns the normals around and reverses the triangles; both together keep
    /// the triangle order.
    /// </summary>
    public static List<PrimFace> ToFaces(MeshModel model, int sculptType, out int beyondLimit, out int missingNormals, out int missingUvs, out int badTriangles)
    {
        bool mirror = (sculptType & SculptGeometry.FlagMirror) != 0;
        bool invert = (sculptType & SculptGeometry.FlagInvert) != 0;
        bool reverse = mirror ^ invert;

        beyondLimit = 0;
        missingNormals = 0;
        missingUvs = 0;
        badTriangles = 0;
        List<PrimFace> result = new List<PrimFace>();
        foreach (MeshFace mf in model.Faces)
        {
            if (mf.Number >= MeshAsset.MaxFaces)
            {
                beyondLimit++;
                continue;
            }
            if (mf.NoGeometry)
                continue;

            int vertices = mf.Positions.Length / 3;
            List<int> triangles = new List<int>();
            for (int i = 0; i + 2 < mf.Indices.Length; i += 3)
            {
                int a = mf.Indices[i], b = mf.Indices[i + 1], c = mf.Indices[i + 2];
                if (a >= vertices || b >= vertices || c >= vertices)
                {
                    badTriangles++;
                    continue;
                }
                if (reverse)
                    (b, c) = (c, b);
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
            if (triangles.Count == 0)
                continue;

            PrimFace f = new PrimFace
            {
                Number = mf.Number,
                Kind = PrimFaceKind.Outer,
                MesherNumber = mf.Number,
                Positions = new float[triangles.Count * 3],
                Normals = new float[triangles.Count * 3],
                Uvs = new float[triangles.Count * 2]
            };
            float flip = mirror ? -1f : 1f;
            float flipNormal = (mirror ? -1f : 1f) * (invert ? -1f : 1f);
            for (int k = 0; k < triangles.Count; k++)
            {
                int v = triangles[k];
                for (int c = 0; c < 3; c++)
                {
                    f.Positions[k * 3 + c] = mf.Positions[v * 3 + c] * flip;
                    f.Normals[k * 3 + c] = mf.Normals[v * 3 + c] * flipNormal;
                }
                // the asset's v is up; glTF's is down
                f.Uvs[k * 2] = mf.Uvs[v * 2];
                f.Uvs[k * 2 + 1] = 1f - mf.Uvs[v * 2 + 1];
            }

            if (!mf.HasNormals)
            {
                // an asset without normals: flat normals from the triangles
                missingNormals++;
                for (int k = 0; k + 2 < triangles.Count; k += 3)
                {
                    float ax = f.Positions[(k + 1) * 3] - f.Positions[k * 3], ay = f.Positions[(k + 1) * 3 + 1] - f.Positions[k * 3 + 1], az = f.Positions[(k + 1) * 3 + 2] - f.Positions[k * 3 + 2];
                    float bx = f.Positions[(k + 2) * 3] - f.Positions[k * 3], by = f.Positions[(k + 2) * 3 + 1] - f.Positions[k * 3 + 1], bz = f.Positions[(k + 2) * 3 + 2] - f.Positions[k * 3 + 2];
                    float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-20f) { nx /= len; ny /= len; nz /= len; } else { nx = 0; ny = 0; nz = 1; }
                    for (int j = 0; j < 3; j++)
                    {
                        f.Normals[(k + j) * 3] = nx; f.Normals[(k + j) * 3 + 1] = ny; f.Normals[(k + j) * 3 + 2] = nz;
                    }
                }
            }
            else
            {
                for (int k = 0; k < triangles.Count; k++)
                {
                    float x = f.Normals[k * 3], y = f.Normals[k * 3 + 1], z = f.Normals[k * 3 + 2];
                    float len = MathF.Sqrt(x * x + y * y + z * z);
                    if (len > 1e-20f) { f.Normals[k * 3] = x / len; f.Normals[k * 3 + 1] = y / len; f.Normals[k * 3 + 2] = z / len; }
                }
            }
            if (!mf.HasUvs)
                missingUvs++;
            result.Add(f);
        }
        return result;
    }
}

/// <summary>
/// The bind shape matrix of a rigged mesh. The viewer holds it as 16 numbers in rows, a point as a row vector multiplied on
/// the left, translation in the fourth row (llmodel.cpp 1667-1677), and draws a rigged mesh with that matrix in place of the
/// part's size: positions through it, normals through its inverse transpose (llface.cpp 1555-1583).
/// </summary>
internal static class BindShape
{
    public static void Apply(List<PrimFace> faces, float[] m)
    {
        if (m == null || m.Length < 16)
            return;
        // the normal matrix: the inverse transpose of the upper 3 x 3, when it can be inverted
        bool haveNormalMatrix = Invert3(m, out float[] inv);
        foreach (PrimFace f in faces)
        {
            for (int i = 0; i + 2 < f.Positions.Length; i += 3)
            {
                float x = f.Positions[i], y = f.Positions[i + 1], z = f.Positions[i + 2];
                f.Positions[i] = x * m[0] + y * m[4] + z * m[8] + m[12];
                f.Positions[i + 1] = x * m[1] + y * m[5] + z * m[9] + m[13];
                f.Positions[i + 2] = x * m[2] + y * m[6] + z * m[10] + m[14];
                if (!haveNormalMatrix)
                    continue;
                // row vector times the inverse transpose = (inverse times the column vector)
                float nx = f.Normals[i], ny = f.Normals[i + 1], nz = f.Normals[i + 2];
                float ox = nx * inv[0] + ny * inv[1] + nz * inv[2];
                float oy = nx * inv[3] + ny * inv[4] + nz * inv[5];
                float oz = nx * inv[6] + ny * inv[7] + nz * inv[8];
                float len = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                if (len > 1e-12f) { ox /= len; oy /= len; oz /= len; }
                f.Normals[i] = ox; f.Normals[i + 1] = oy; f.Normals[i + 2] = oz;
            }
        }
    }

    /// <summary>The inverse of the upper 3 x 3 of a row-major 4 x 4, row-major; false when it is singular.</summary>
    private static bool Invert3(float[] m, out float[] inv)
    {
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        inv = new float[9];
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-18)
            return false;
        double k = 1.0 / det;
        inv[0] = (float)((e * i - f * h) * k); inv[1] = (float)((c * h - b * i) * k); inv[2] = (float)((b * f - c * e) * k);
        inv[3] = (float)((f * g - d * i) * k); inv[4] = (float)((a * i - c * g) * k); inv[5] = (float)((c * d - a * f) * k);
        inv[6] = (float)((d * h - e * g) * k); inv[7] = (float)((b * g - a * h) * k); inv[8] = (float)((a * e - b * d) * k);
        return true;
    }
}
