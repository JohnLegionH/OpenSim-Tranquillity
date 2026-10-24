/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;

namespace OpenSim.MeshAsset;

/// <summary>
/// The physics block the Linden Lab viewer writes for an uploaded object when the creator supplies no physics model:
/// one convex hull of the vertices of the Low level of detail, at most 256 points, stored as BoundingVerts in a
/// physics_convex block (documented in FORMAT.md). There is no hull list and no physics_mesh block.
/// </summary>
public static class PhysicsBlock
{
    public const int MaxHullVertices = 256;

    /// <summary>What was written, for reports.</summary>
    public sealed record Result(string SourceLevel, int SourceVertices, int HullVertices, bool BoxFallback);

    /// <summary>
    /// Sets <see cref="MeshAssetModel.PhysicsConvex"/> to the bounding hull of the Low level of detail (else Medium, else High:
    /// the viewer's order). Call it after the levels of detail exist.
    /// </summary>
    public static Result AddBoundingHull(MeshAssetModel model)
    {
        (string name, MeshLod? lod) = model.Low != null ? ("low", model.Low) : model.Medium != null ? ("medium", model.Medium) : ("high", model.High);
        if (lod == null) throw new InvalidOperationException("the model has no level of detail to take a physics hull from");
        var points = lod.Faces.Where(f => !f.NoGeometry).SelectMany(f => f.Positions).ToList();
        if (points.Count == 0) throw new InvalidOperationException("the level of detail has no vertices");
        var hull = ConvexHull.Compute(points, MaxHullVertices, out bool box);
        model.PhysicsConvex = ToLlsd(hull);
        return new Result(name, points.Count, hull.Count, box);
    }

    /// <summary>The physics_convex map: Min and Max over the points, and the points as 16-bit values inside that box.</summary>
    public static SortedDictionary<string, object?> ToLlsd(IReadOnlyList<Vector3> hull)
    {
        var min = hull[0]; var max = hull[0];
        foreach (var p in hull) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        double rx = Range(min.X, max.X), ry = Range(min.Y, max.Y), rz = Range(min.Z, max.Z);
        var bytes = new byte[hull.Count * 6];
        int at = 0;
        void Put(double v) { var q = (ushort)Math.Clamp((long)Math.Round(v * 65535), 0L, 65535L); bytes[at++] = (byte)q; bytes[at++] = (byte)(q >> 8); }
        foreach (var p in hull) { Put((p.X - (double)min.X) / rx); Put((p.Y - (double)min.Y) / ry); Put((p.Z - (double)min.Z) / rz); }
        var m = LlsdBinary.NewMap();
        m["BoundingVerts"] = bytes;
        m["Max"] = new List<object?> { (double)max.X, (double)max.Y, (double)max.Z };
        m["Min"] = new List<object?> { (double)min.X, (double)min.Y, (double)min.Z };
        return m;
    }

    static double Range(float min, float max) => (double)max - min == 0 ? 1.0 : (double)max - min;

    /// <summary>Reads the points back out of a physics_convex map written by <see cref="ToLlsd"/> (or the viewer).</summary>
    public static List<Vector3> ReadBoundingVerts(object? physicsConvex)
    {
        var result = new List<Vector3>();
        if (physicsConvex is not IDictionary<string, object?> m || m["BoundingVerts"] is not byte[] b) return result;
        Vector3 Vec(object? o, Vector3 fallback) => o is IList<object?> l && l.Count == 3 ? new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])) : fallback;
        var min = Vec((m.TryGetValue("Min", out var mn) ? mn : null), new Vector3(-0.5f)); var max = Vec((m.TryGetValue("Max", out var mx) ? mx : null), new Vector3(0.5f));
        for (int i = 0; i + 5 < b.Length; i += 6)
            result.Add(new Vector3(
                min.X + (max.X - min.X) * (b[i] | b[i + 1] << 8) / 65535f,
                min.Y + (max.Y - min.Y) * (b[i + 2] | b[i + 3] << 8) / 65535f,
                min.Z + (max.Z - min.Z) * (b[i + 4] | b[i + 5] << 8) / 65535f));
        return result;
    }
}

/// <summary>The vertices of the convex hull of a point set (incremental construction), capped at a vertex count.</summary>
public static class ConvexHull
{
    sealed class Face
    {
        public int A, B, C;
        public Vector3 N; public float D;
        public bool Dead;
    }

    /// <summary>
    /// The hull's vertices. When the points are flat or in a line there is no hull; the eight corners of their bounding box
    /// are returned instead, as the viewer does (<paramref name="box"/> says so). Above <paramref name="maxVertices"/> the points
    /// are thinned on a coarser grid until the hull fits.
    /// </summary>
    public static List<Vector3> Compute(IReadOnlyList<Vector3> points, int maxVertices, out bool box)
    {
        var pts = points.Distinct().ToList();
        var min = pts[0]; var max = pts[0];
        foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        float extent = Math.Max(Math.Max(max.X - min.X, max.Y - min.Y), max.Z - min.Z);

        // Over the cap, the points are thinned to one per cell of a grid (the one furthest from the centre, so the hull keeps its
        // reach) and the cell grows until the hull fits. The result is the hull of some of the real points, never wider than the mesh.
        var centre = (min + max) / 2;
        List<Vector3>? Try(float cell)
        {
            var hull = Build(cell == 0 ? pts : Thin(pts, cell, centre), extent);
            return hull;
        }

        float lo = pts.Count > 4096 ? extent / 128f : 0;
        var first = Try(lo);
        if (first == null) { box = true; return Box(min, max); }
        box = false;
        if (first.Count <= maxVertices) return first;

        // grow the cell until the hull fits, then narrow in on the finest cell that still fits
        float hi = lo == 0 ? extent / 128f : lo * 1.5f;
        List<Vector3>? fit = null;
        for (int i = 0; i < 24 && fit == null; i++, lo = hi, hi *= 1.5f)
        {
            var h = Try(hi);
            if (h != null && h.Count <= maxVertices) fit = h;
        }
        if (fit == null) { box = true; return Box(min, max); }
        for (int i = 0; i < 6; i++)
        {
            float mid = (lo + hi) / 2;
            var h = Try(mid);
            if (h != null && h.Count <= maxVertices) { fit = h; hi = mid; } else lo = mid;
        }
        return fit;
    }

    static List<Vector3> Thin(List<Vector3> pts, float cell, Vector3 centre)
    {
        var best = new Dictionary<(int, int, int), Vector3>();
        foreach (var p in pts)
        {
            var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            if (!best.TryGetValue(key, out var q) || Vector3.DistanceSquared(p, centre) > Vector3.DistanceSquared(q, centre)) best[key] = p;
        }
        return best.Values.ToList();
    }

    static List<Vector3> Box(Vector3 min, Vector3 max) => new()
    {
        new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
        new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
    };

    static List<Vector3>? Build(List<Vector3> p, float extent)
    {
        if (p.Count < 4 || extent <= 0) return null;
        float eps = extent * 1e-6f;

        // initial tetrahedron: two far-apart points, the one furthest from their line, the one furthest from that plane
        int i0 = 0, i1 = 0;
        for (int i = 1; i < p.Count; i++) { if (p[i].X < p[i0].X) i0 = i; if (p[i].X > p[i1].X) i1 = i; }
        if (i0 == i1) i1 = (i0 + 1) % p.Count;
        int i2 = -1; float best = eps;
        for (int i = 0; i < p.Count; i++)
        {
            float d = Vector3.Cross(p[i1] - p[i0], p[i] - p[i0]).Length();
            if (d > best) { best = d; i2 = i; }
        }
        if (i2 < 0) return null;
        var n0 = Vector3.Normalize(Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]));
        int i3 = -1; best = eps;
        for (int i = 0; i < p.Count; i++)
        {
            float d = MathF.Abs(Vector3.Dot(n0, p[i] - p[i0]));
            if (d > best) { best = d; i3 = i; }
        }
        if (i3 < 0) return null;

        var faces = new List<Face>();
        Vector3 centre = (p[i0] + p[i1] + p[i2] + p[i3]) / 4;
        void Add(int a, int b, int c)
        {
            var n = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            float len = n.Length();
            if (len == 0) return;
            n /= len;
            float d = Vector3.Dot(n, p[a]);
            if (Vector3.Dot(n, centre) - d > 0) { (b, c) = (c, b); n = -n; d = -d; }
            faces.Add(new Face { A = a, B = b, C = c, N = n, D = d });
        }
        Add(i0, i1, i2); Add(i0, i1, i3); Add(i0, i2, i3); Add(i1, i2, i3);

        for (int i = 0; i < p.Count; i++)
        {
            if (i == i0 || i == i1 || i == i2 || i == i3) continue;
            var visible = new List<Face>();
            foreach (var f in faces) if (!f.Dead && Vector3.Dot(f.N, p[i]) - f.D > eps) visible.Add(f);
            if (visible.Count == 0) continue;

            // the horizon: edges of visible faces that no other visible face uses in the opposite direction
            var edges = new Dictionary<(int, int), int>();
            foreach (var f in visible)
            {
                f.Dead = true;
                foreach (var e in new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) }) edges[e] = 1;
            }
            var horizon = edges.Keys.Where(e => !edges.ContainsKey((e.Item2, e.Item1))).ToList();
            foreach (var (a, b) in horizon)
            {
                var n = Vector3.Cross(p[b] - p[a], p[i] - p[a]);
                float len = n.Length();
                if (len == 0) continue;
                n /= len;
                faces.Add(new Face { A = a, B = b, C = i, N = n, D = Vector3.Dot(n, p[a]) });
            }
            if (faces.Count > 4096) faces.RemoveAll(f => f.Dead);
        }

        var used = new HashSet<int>();
        foreach (var f in faces) if (!f.Dead) { used.Add(f.A); used.Add(f.B); used.Add(f.C); }
        return used.OrderBy(x => x).Select(x => p[x]).ToList();
    }
}
