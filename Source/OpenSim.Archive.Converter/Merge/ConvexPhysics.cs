/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// Decides whether a part's collision triangles are exactly a convex solid, and gives its hull. A merged part carries a hull for each member
/// (a hull list in its physics_convex block) only when every member is exactly convex: a convex hull of a hollow, cut or concave member would
/// fill what the member leaves empty.
/// </summary>
public static class ConvexPhysics
{
    /// <summary>The most vertices one hull of a hull list may have (a hull list stores each hull's count in a byte, 0 standing for 256).</summary>
    public const int MaxHullVertices = 256;

    /// <summary>Past this many distinct vertices the hull builder thins its points, so the hull would not be exact; such a member keeps the triangle mesh.</summary>
    private const int MaxExactPoints = 4096;

    private const double Relative = 1e-5, Weld = 1e-6;

    /// <summary>The distinct places of the shape (vertices closer than a millionth of its size are one), by vertex.</summary>
    private static (List<Vector3> Places, int[] PlaceOf, double Diagonal) Places(PhysicsGeometry g)
    {
        int n = g.VertexCount;
        Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
        Vector3[] v = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = new Vector3(g.Positions[i * 3], g.Positions[i * 3 + 1], g.Positions[i * 3 + 2]);
            lo = Vector3.Min(lo, v[i]);
            hi = Vector3.Max(hi, v[i]);
        }
        double diagonal = n == 0 ? 0 : Vector3.Distance(lo, hi);
        double cell = Math.Max(diagonal * Weld, 1e-12);
        Dictionary<(long, long, long), int> index = new Dictionary<(long, long, long), int>();
        List<Vector3> places = new List<Vector3>();
        int[] placeOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            (long, long, long) key = ((long)Math.Round(v[i].X / cell), (long)Math.Round(v[i].Y / cell), (long)Math.Round(v[i].Z / cell));
            if (!index.TryGetValue(key, out int p))
            {
                p = places.Count;
                index[key] = p;
                places.Add(v[i]);
            }
            placeOf[i] = p;
        }
        return (places, placeOf, diagonal);
    }

    /// <summary>
    /// True when the triangles are the closed surface of a convex solid: every edge is shared by exactly two triangles, and the plane of every
    /// triangle has the whole shape on one side of it (within a hundred-thousandth of the shape's size).
    /// </summary>
    public static bool IsConvex(PhysicsGeometry g)
    {
        if (g == null || g.TriangleCount < 4 || g.VertexCount < 4)
            return false;
        (List<Vector3> places, int[] placeOf, double diagonal) = Places(g);
        if (places.Count < 4 || diagonal <= 0 || (long)g.TriangleCount * places.Count > 50_000_000)
            return false;
        double eps = diagonal * Relative;
        Vector3 centroid = Vector3.Zero;
        foreach (Vector3 p in places)
            centroid += p;
        centroid /= places.Count;

        Dictionary<(int, int), int> edges = new Dictionary<(int, int), int>();
        void Edge(int a, int b)
        {
            (int, int) key = a < b ? (a, b) : (b, a);
            edges[key] = edges.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        int counted = 0;
        for (int t = 0; t < g.Indices.Length; t += 3)
        {
            int a = placeOf[g.Indices[t]], b = placeOf[g.Indices[t + 1]], c = placeOf[g.Indices[t + 2]];
            if (a == b || b == c || a == c)
                continue; // a triangle with a collapsed side has no area
            Vector3 normal = Vector3.Cross(places[b] - places[a], places[c] - places[a]);
            float length = normal.Length();
            if (length <= eps * eps)
                continue; // a sliver in a line
            normal /= length;
            double d = Vector3.Dot(normal, places[a]), side = Vector3.Dot(normal, centroid) - d;
            if (Math.Abs(side) <= eps)
                return false; // the middle of the shape lies on a face: flat, or not a solid
            double sign = side > 0 ? 1 : -1;
            foreach (Vector3 p in places)
                if (sign * (Vector3.Dot(normal, p) - d) < -eps)
                    return false; // a vertex on the far side of this face from the middle of the shape
            Edge(a, b); Edge(b, c); Edge(c, a);
            counted++;
        }
        return counted >= 4 && edges.Values.All(c => c == 2);
    }

    /// <summary>The vertices of the convex hull of the shape (at most <see cref="MaxHullVertices"/>), or null when it has none or cannot be held exactly.</summary>
    public static List<Vector3> Hull(PhysicsGeometry g)
    {
        if (g == null || g.VertexCount < 4)
            return null;
        List<Vector3> places = Places(g).Places;
        if (places.Count < 4 || places.Count > MaxExactPoints)
            return null;
        List<Vector3> hull = ConvexHull.Compute(places, int.MaxValue, out bool box);
        return box || hull == null || hull.Count < 4 || hull.Count > MaxHullVertices ? null : hull;
    }
}
