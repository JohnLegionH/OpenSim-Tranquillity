/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>One face after cleaning: an indexed triangle list.</summary>
public sealed class CleanedFace
{
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Positions;
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Normals;
    /// <summary>u, v for each vertex.</summary>
    public float[] Uvs;
    /// <summary>Three vertex numbers for each triangle.</summary>
    public int[] Indices;
    /// <summary>True when every triangle of the face was degenerate: the face is returned as it came in.</summary>
    public bool KeptAsWas;
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Removes what a prim's or sculpt's mesher leaves in a face without changing how the face looks or is textured: vertices
/// that repeat another vertex of the face, vertices no triangle uses, and triangles with no area.
/// <list type="bullet">
/// <item>Two vertices are one when their texture coordinates are bit for bit equal, their positions are within
/// <see cref="PositionTolerance"/> and their normals within <see cref="NormalTolerance"/> in every component. Two vertices
/// at one position with different texture coordinates (a texture seam) stay two.</item>
/// <item>A triangle is dropped when two of its corners are one vertex after welding, or its area is below
/// <see cref="MinTriangleArea"/>. This covers the pole of a sculpt map, where a whole row of the map is one point.</item>
/// <item>A vertex is kept only if a surviving triangle uses it; vertices are numbered in order of first use.</item>
/// <item>A face whose triangles would all be dropped is returned as it came in.</item>
/// </list>
/// A surviving triangle's corners keep the texture coordinates of the corners they came from exactly, and positions and
/// normals within the tolerances (each corner is compared with the vertex it was welded into, so the difference never adds up).
/// The same input always gives the same output.
/// </summary>
public static class GeometryCleaner
{
    /// <summary>Largest difference in any one coordinate for two positions to be one vertex, in the face's own units.</summary>
    public const float PositionTolerance = 1e-6f;
    /// <summary>Largest difference in any one component for two normals to be one.</summary>
    public const float NormalTolerance = 1e-4f;
    /// <summary>Area below which a triangle counts as having none (square units of the face's own units).</summary>
    public const double MinTriangleArea = 1e-12;

    private const double CellSize = 2.0 * PositionTolerance;

    private readonly struct Cell : IEquatable<Cell>
    {
        public readonly int U, V;
        public readonly long X, Y, Z;
        public Cell(int u, int v, long x, long y, long z) { U = u; V = v; X = x; Y = y; Z = z; }
        public bool Equals(Cell o) => U == o.U && V == o.V && X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(U, V, X, Y, Z);
    }

    /// <param name="indices">Three vertex numbers for each triangle; null for a triangle soup (every three vertices are a triangle).</param>
    public static CleanedFace Clean(float[] positions, float[] normals, float[] uvs, int[] indices = null)
    {
        int n = positions.Length / 3;
        if (normals.Length < n * 3 || uvs.Length < n * 2)
            throw new ArgumentException("every vertex needs a normal and a texture coordinate");
        int[] tris = indices ?? Enumerable.Range(0, n - n % 3).ToArray();
        int triangleCount = tris.Length / 3;

        // weld in order of first use, so the result does not depend on vertices no triangle uses
        int[] rep = new int[n];
        Array.Fill(rep, -1);
        int[] chain = new int[n];
        Dictionary<Cell, int> head = new Dictionary<Cell, int>();

        int Weld(int v)
        {
            if (rep[v] >= 0)
                return rep[v];
            float px = positions[v * 3], py = positions[v * 3 + 1], pz = positions[v * 3 + 2];
            int ub = Bits(uvs[v * 2]), vb = Bits(uvs[v * 2 + 1]);
            long cx = (long)Math.Floor(px / CellSize), cy = (long)Math.Floor(py / CellSize), cz = (long)Math.Floor(pz / CellSize);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (head.TryGetValue(new Cell(ub, vb, cx + dx, cy + dy, cz + dz), out int w))
                            for (; w >= 0; w = chain[w])
                                if (Math.Abs(px - positions[w * 3]) <= PositionTolerance && Math.Abs(py - positions[w * 3 + 1]) <= PositionTolerance && Math.Abs(pz - positions[w * 3 + 2]) <= PositionTolerance
                                    && Math.Abs(normals[v * 3] - normals[w * 3]) <= NormalTolerance && Math.Abs(normals[v * 3 + 1] - normals[w * 3 + 1]) <= NormalTolerance && Math.Abs(normals[v * 3 + 2] - normals[w * 3 + 2]) <= NormalTolerance)
                                    return rep[v] = w;
            Cell own = new Cell(ub, vb, cx, cy, cz);
            chain[v] = head.TryGetValue(own, out int first) ? first : -1;
            head[own] = v;
            return rep[v] = v;
        }

        List<int> kept = new List<int>(triangleCount * 3);
        for (int t = 0; t < triangleCount; t++)
        {
            int a = Weld(tris[t * 3]), b = Weld(tris[t * 3 + 1]), c = Weld(tris[t * 3 + 2]);
            if (a == b || b == c || a == c || Area(positions, a, b, c) < MinTriangleArea)
                continue;
            kept.Add(a); kept.Add(b); kept.Add(c);
        }

        if (kept.Count == 0)
            return new CleanedFace { Positions = positions, Normals = normals, Uvs = uvs, Indices = tris, KeptAsWas = triangleCount > 0 };

        int[] number = new int[n];
        Array.Fill(number, -1);
        List<int> order = new List<int>();
        int[] outIndices = new int[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            int v = kept[i];
            if (number[v] < 0)
            {
                number[v] = order.Count;
                order.Add(v);
            }
            outIndices[i] = number[v];
        }

        CleanedFace result = new CleanedFace { Positions = new float[order.Count * 3], Normals = new float[order.Count * 3], Uvs = new float[order.Count * 2], Indices = outIndices };
        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            Array.Copy(positions, v * 3, result.Positions, i * 3, 3);
            Array.Copy(normals, v * 3, result.Normals, i * 3, 3);
            Array.Copy(uvs, v * 2, result.Uvs, i * 2, 2);
        }
        return result;
    }

    // x + 0 turns a negative zero into a positive one, so the two compare equal.
    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);

    private static double Area(float[] p, int a, int b, int c)
    {
        double ux = (double)p[b * 3] - p[a * 3], uy = (double)p[b * 3 + 1] - p[a * 3 + 1], uz = (double)p[b * 3 + 2] - p[a * 3 + 2];
        double vx = (double)p[c * 3] - p[a * 3], vy = (double)p[c * 3 + 1] - p[a * 3 + 1], vz = (double)p[c * 3 + 2] - p[a * 3 + 2];
        double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(x * x + y * y + z * z);
    }
}
