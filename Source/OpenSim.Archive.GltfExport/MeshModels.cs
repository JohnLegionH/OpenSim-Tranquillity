/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The exporter's view of a mesh asset. The asset is decoded by the shared OpenSim.MeshAsset library (MeshCodec); this file
// only picks the level of detail the viewer draws first and lays the faces out the way the glTF writer wants them.
// Format knowledge: LLVolume::unpackVolumeFacesInternal in indra/llmath/llvolume.cpp and the Second Life wiki's
// Mesh/Mesh Asset Format page. No viewer code is copied.

using Shared = OpenSim.MeshAsset;

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

    // the exporter draws the highest level only; the other levels, the physics shape and the weights stay in the copied asset
    private static readonly Shared.MeshDecodeOptions DrawingOnly = new Shared.MeshDecodeOptions { HighestLevelOnly = true, SkipPhysics = true, SkipWeights = true };

    /// <summary>
    /// Decodes the asset with the shared library and takes the highest level of detail present (high, then medium, low,
    /// lowest). A damaged asset is reported through <paramref name="problem"/>, never thrown.
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

        Shared.MeshAssetModel decoded;
        try
        {
            decoded = Shared.MeshCodec.Decode(WithoutLlsdHeaderLine(asset), DrawingOnly);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = HasNoDisplayLevel(asset)
                ? "the mesh asset has no display level of detail"
                : "the mesh asset could not be decoded: " + e.Message;
            return false;
        }

        Shared.MeshLod level = decoded.High ?? decoded.Medium ?? decoded.Low ?? decoded.Lowest;
        if (level == null || level.Faces.Count == 0)
        {
            problem = "the mesh asset's display level of detail holds no faces";
            return false;
        }

        // the blocks the header names, for the record; the decode above left the unused ones unread
        IDictionary<string, object> header = Shared.LlsdBinary.Read(WithoutLlsdHeaderLine(asset), out _) as IDictionary<string, object>;
        MeshModel result = new MeshModel
        {
            HasPhysics = header != null && (header.ContainsKey("physics_mesh") || header.ContainsKey("physics_shape") || header.ContainsKey("physics_convex"))
        };
        foreach (string name in new[] { "lowest_lod", "low_lod", "medium_lod", "high_lod", "physics_convex", "physics_mesh", "skin" })
            if (header != null && header.TryGetValue(name, out object entry) && entry is IDictionary<string, object>)
                result.Blocks.Add(name);

        for (int i = 0; i < level.Faces.Count; i++)
            result.Faces.Add(ToFace(level.Faces[i], i));

        if (decoded.Skin != null)
        {
            MeshSkin skin = new MeshSkin { JointNames = new List<string>(decoded.Skin.JointNames), InverseBindMatrices = decoded.Skin.InverseBind.Count };
            if (decoded.Skin.BindShape != null && decoded.Skin.BindShape.Length == 16)
            {
                skin.BindShapeMatrix = new float[16];
                for (int i = 0; i < 16; i++)
                    skin.BindShapeMatrix[i] = (float)decoded.Skin.BindShape[i];
            }
            result.Skin = skin;
        }
        model = result;
        return true;
    }

    /// <summary>
    /// Some writers put the textual line "&lt;? llsd/binary ?&gt;" before the header map (the library the exporter used before
    /// accepted it). The shared decoder expects the map first, and block offsets count from the end of the map, so the line is
    /// skipped when it is there.
    /// </summary>
    private static byte[] WithoutLlsdHeaderLine(byte[] asset)
    {
        if (asset.Length < 20 || asset[0] != (byte)'<' || asset[1] != (byte)'?')
            return asset;
        int limit = Math.Min(asset.Length, 64);
        for (int i = 2; i < limit; i++)
        {
            if (asset[i] != 10)
                continue;
            string line = System.Text.Encoding.ASCII.GetString(asset, 0, i);
            return line.Contains("llsd/binary", StringComparison.OrdinalIgnoreCase) ? asset[(i + 1)..] : asset;
        }
        return asset;
    }

    /// <summary>True when the header is a readable map that names none of the four display levels, so that is what is reported.</summary>
    private static bool HasNoDisplayLevel(byte[] asset)
    {
        try
        {
            if (Shared.LlsdBinary.Read(WithoutLlsdHeaderLine(asset), out _) is not IDictionary<string, object> header)
                return false;
            return !header.ContainsKey("high_lod") && !header.ContainsKey("medium_lod") && !header.ContainsKey("low_lod") && !header.ContainsKey("lowest_lod");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static MeshFace ToFace(Shared.MeshFace source, int number)
    {
        MeshFace face = new MeshFace { Number = number };
        // a trailing partial triangle is dropped
        int indexCount = source.Indices.Length - source.Indices.Length % 3;
        if (source.NoGeometry || indexCount < 3 || source.Positions.Length < 1)
        {
            face.NoGeometry = true;
            return face;
        }

        int vertices = source.Positions.Length;
        face.Positions = new float[vertices * 3];
        for (int v = 0; v < vertices; v++)
        {
            face.Positions[v * 3] = source.Positions[v].X;
            face.Positions[v * 3 + 1] = source.Positions[v].Y;
            face.Positions[v * 3 + 2] = source.Positions[v].Z;
        }

        face.Normals = new float[vertices * 3];
        if (source.Normals != null && source.Normals.Length == vertices)
        {
            face.HasNormals = true;
            for (int v = 0; v < vertices; v++)
            {
                face.Normals[v * 3] = source.Normals[v].X;
                face.Normals[v * 3 + 1] = source.Normals[v].Y;
                face.Normals[v * 3 + 2] = source.Normals[v].Z;
            }
        }

        face.Uvs = new float[vertices * 2];
        if (source.Uvs != null && source.Uvs.Length == vertices)
        {
            face.HasUvs = true;
            for (int v = 0; v < vertices; v++)
            {
                face.Uvs[v * 2] = source.Uvs[v].X;
                face.Uvs[v * 2 + 1] = source.Uvs[v].Y;
            }
        }

        face.Indices = new int[indexCount];
        Array.Copy(source.Indices, face.Indices, indexCount);
        return face;
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
