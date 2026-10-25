/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Restore;

/// <summary>How far two copies of a part's geometry may differ and still count as the same geometry.</summary>
public sealed class GeometryTolerance
{
    /// <summary>Largest difference in any one coordinate of a vertex, in metres (glTF axes, the part's size applied).</summary>
    public float Position { get; set; } = DefaultPosition;
    /// <summary>Largest difference in any one component of a vertex normal.</summary>
    public float Normal { get; set; } = DefaultNormal;
    /// <summary>Largest difference in either texture coordinate of a vertex.</summary>
    public float Uv { get; set; } = DefaultUv;

    // Set from a measurement of Blender 5.1's glTF import and export with no edit, over 639 parts: it changed no position and no
    // texture coordinate, moved a node by at most 2e-6 m, and changed a vertex normal by at most 0.012 (only on very thin
    // triangles). These limits sit well above that and well below what an edit does (a face moved a millimetre, or a
    // curved surface switched between smooth and flat shading, changes a normal by 0.2 or more).
    public const float DefaultPosition = 1e-4f;
    public const float DefaultNormal = 5e-2f;
    public const float DefaultUv = 1e-4f;
}

/// <summary>The triangles of one part's node, grouped by prim face number. Each triangle is 24 floats: three corners of position, normal and texture coordinate.</summary>
public sealed class PartGeometry
{
    public const int TriangleFloats = 24;

    /// <summary>Triangles by face number; the face number is the _PRIM_FACE value of the triangle's first vertex.</summary>
    public SortedDictionary<int, List<float[]>> Faces { get; } = new SortedDictionary<int, List<float[]>>();
    /// <summary>For each triangle in <see cref="Faces"/>, the glTF material index of its primitive (-1 for none); same order.</summary>
    public SortedDictionary<int, List<int>> Materials { get; } = new SortedDictionary<int, List<int>>();
    /// <summary>Triangles whose three vertices do not all carry the same face number.</summary>
    public int MixedTriangles { get; private set; }
    /// <summary>Primitives that give no way to tell which prim face their triangles belong to.</summary>
    public int PrimitivesWithoutFaceNumbers { get; private set; }
    public int TriangleCount => Faces.Values.Sum(f => f.Count);

    /// <summary>Reads the geometry of a node. A scale on the node is applied to the vertices, as a viewer would show them.</summary>
    public static PartGeometry FromNode(GlbFile file, GlbNode node)
    {
        PartGeometry g = new PartGeometry();
        if (node.Mesh < 0 || node.Mesh >= file.Meshes.Count)
            return g;
        float sx = 1f, sy = 1f, sz = 1f;
        if (node.Scale != null)
        {
            sx = node.Scale[0]; sy = node.Scale[1]; sz = node.Scale[2];
        }
        bool scaled = Math.Abs(sx - 1f) > 1e-6f || Math.Abs(sy - 1f) > 1e-6f || Math.Abs(sz - 1f) > 1e-6f;

        foreach (GlbPrimitive p in file.Meshes[node.Mesh].Primitives)
        {
            int primFace = -1;
            if (p.FaceNumbers == null)
            {
                double? e = GlbFile.Num(p.Extras, "primFace");
                if (e.HasValue)
                    primFace = (int)Math.Round(e.Value);
                else
                {
                    g.PrimitivesWithoutFaceNumbers++;
                    primFace = -1;
                }
            }
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                float[] tri = new float[TriangleFloats];
                int face = primFace;
                for (int c = 0; c < 3; c++)
                {
                    int v = p.Indices[t + c];
                    int o = c * 8;
                    tri[o] = p.Positions[v * 3] * sx;
                    tri[o + 1] = p.Positions[v * 3 + 1] * sy;
                    tri[o + 2] = p.Positions[v * 3 + 2] * sz;
                    if (p.Normals != null)
                    {
                        float nx = p.Normals[v * 3], ny = p.Normals[v * 3 + 1], nz = p.Normals[v * 3 + 2];
                        if (scaled)
                        {
                            nx /= sx != 0 ? sx : 1f; ny /= sy != 0 ? sy : 1f; nz /= sz != 0 ? sz : 1f;
                            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                            if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; }
                        }
                        tri[o + 3] = nx; tri[o + 4] = ny; tri[o + 5] = nz;
                    }
                    if (p.Uvs != null)
                    {
                        tri[o + 6] = p.Uvs[v * 2];
                        tri[o + 7] = p.Uvs[v * 2 + 1];
                    }
                    if (p.FaceNumbers != null)
                    {
                        int f = (int)Math.Round(p.FaceNumbers[v]);
                        if (c == 0)
                            face = f;
                        else if (f != face)
                            g.MixedTriangles++;
                    }
                }
                if (p.Normals == null)
                    FlatNormal(tri);
                if (!g.Faces.TryGetValue(face, out List<float[]> list))
                {
                    g.Faces[face] = list = new List<float[]>();
                    g.Materials[face] = new List<int>();
                }
                list.Add(tri);
                g.Materials[face].Add(p.Material);
            }
        }
        return g;
    }

    private static void FlatNormal(float[] tri)
    {
        float ax = tri[8] - tri[0], ay = tri[9] - tri[1], az = tri[10] - tri[2];
        float bx = tri[16] - tri[0], by = tri[17] - tri[1], bz = tri[18] - tri[2];
        float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-20f) { nx /= len; ny /= len; nz /= len; }
        for (int c = 0; c < 3; c++)
        {
            tri[c * 8 + 3] = nx; tri[c * 8 + 4] = ny; tri[c * 8 + 5] = nz;
        }
    }
}

/// <summary>The outcome of comparing two copies of a part's geometry.</summary>
public sealed class GeometryComparison
{
    public bool Equal { get; set; }
    /// <summary>Why they differ; null when they are equal.</summary>
    public string Difference { get; set; }
    /// <summary>The largest differences found among the triangles that were matched, so a run reports how far a tool such as Blender moved the values.</summary>
    public float MaxPosition { get; set; }
    public float MaxNormal { get; set; }
    public float MaxUv { get; set; }
    public int Triangles { get; set; }
}

public static class GeometryComparer
{
    /// <summary>
    /// Compares the geometry of <paramref name="original"/> with <paramref name="edited"/> face by face. Vertex order, triangle
    /// order and which corner a triangle starts at do not matter (a tool that re-exports a mesh may change all three); the
    /// triangle's winding, its positions, its normals and its texture coordinates do, each within the tolerance.
    /// </summary>
    public static GeometryComparison Compare(PartGeometry original, PartGeometry edited, GeometryTolerance tolerance)
    {
        GeometryComparison result = new GeometryComparison { Equal = true };
        if (!original.Faces.Keys.SequenceEqual(edited.Faces.Keys))
        {
            result.Equal = false;
            result.Difference = "the faces are " + Join(edited.Faces.Keys) + " and were " + Join(original.Faces.Keys);
            return result;
        }
        foreach (int face in original.Faces.Keys)
        {
            List<float[]> a = original.Faces[face], b = edited.Faces[face];
            if (a.Count != b.Count)
            {
                result.Equal = false;
                result.Difference = "face " + face + " has " + b.Count + " triangles and had " + a.Count;
                return result;
            }
            string why = MatchFace(a, b, tolerance, result);
            if (why != null)
            {
                result.Equal = false;
                result.Difference = "face " + face + ": " + why;
                return result;
            }
            result.Triangles += a.Count;
        }
        return result;
    }

    private static string Join(IEnumerable<int> faces) => "[" + string.Join(", ", faces) + "]";

    private static string MatchFace(List<float[]> a, List<float[]> b, GeometryTolerance tol, GeometryComparison stats)
    {
        float cell = Math.Max(2f * tol.Position, 1e-3f);
        Dictionary<(long, long, long), List<int>> grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < b.Count; i++)
        {
            var key = Cell(b[i], cell);
            if (!grid.TryGetValue(key, out List<int> list))
                grid[key] = list = new List<int>();
            list.Add(i);
        }
        for (int i = 0; i < a.Count; i++)
        {
            var (cx, cy, cz) = Cell(a[i], cell);
            // Of the unused triangles near this one that agree within the tolerance, the closest takes it, so a loose tolerance
            // does not let one triangle take another's partner.
            List<int> bestList = null;
            int bestAt = -1;
            float bestScore = float.MaxValue, bestDp = 0, bestDn = 0, bestDu = 0;
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int> list))
                            continue;
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (!SameTriangle(a[i], b[list[k]], tol, out float dp, out float dn, out float du))
                                continue;
                            float score = dp + du + dn;
                            if (score < bestScore)
                            {
                                bestScore = score; bestList = list; bestAt = k; bestDp = dp; bestDn = dn; bestDu = du;
                                if (score == 0f)
                                    break;
                            }
                        }
                    }
            if (bestList == null)
                return "a triangle has no counterpart within the tolerance";
            // a matched triangle leaves its cell, so a crowded cell (a pole of a sculpt, say) shrinks as it is used up
            bestList[bestAt] = bestList[bestList.Count - 1];
            bestList.RemoveAt(bestList.Count - 1);
            stats.MaxPosition = Math.Max(stats.MaxPosition, bestDp);
            stats.MaxNormal = Math.Max(stats.MaxNormal, bestDn);
            stats.MaxUv = Math.Max(stats.MaxUv, bestDu);
        }
        return null;
    }

    /// <summary>Twice the area below which a triangle counts as collapsed (square metres).</summary>
    private const float CollapsedArea = 1e-8f;

    private static float Area(float[] t)
    {
        float ax = t[8] - t[0], ay = t[9] - t[1], az = t[10] - t[2];
        float bx = t[16] - t[0], by = t[17] - t[1], bz = t[18] - t[2];
        float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    private static (long, long, long) Cell(float[] t, float cell)
    {
        float x = (t[0] + t[8] + t[16]) / 3f, y = (t[1] + t[9] + t[17]) / 3f, z = (t[2] + t[10] + t[18]) / 3f;
        return ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell));
    }

    private static bool SameTriangle(float[] a, float[] b, GeometryTolerance tol, out float dp, out float dn, out float du)
    {
        dp = dn = du = 0;
        // A triangle with no area is never drawn, and a tool that re-exports the mesh (Blender does) rebuilds its normals from
        // nothing: the positions and texture coordinates of such a triangle still have to match, its normals are not compared.
        bool collapsed = Area(a) < CollapsedArea;
        for (int r = 0; r < 3; r++)
        {
            float p = 0, n = 0, u = 0;
            bool ok = true;
            for (int c = 0; c < 3 && ok; c++)
            {
                int oa = c * 8, ob = ((c + r) % 3) * 8;
                for (int k = 0; k < 3; k++)
                {
                    p = Math.Max(p, Math.Abs(a[oa + k] - b[ob + k]));
                    n = Math.Max(n, Math.Abs(a[oa + 3 + k] - b[ob + 3 + k]));
                }
                for (int k = 0; k < 2; k++)
                    u = Math.Max(u, Math.Abs(a[oa + 6 + k] - b[ob + 6 + k]));
                ok = p <= tol.Position && (collapsed || n <= tol.Normal) && u <= tol.Uv;
            }
            if (ok)
            {
                dp = p; dn = collapsed ? 0f : n; du = u;
                return true;
            }
        }
        return false;
    }
}
