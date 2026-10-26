/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The screen the levels of detail of a merged part are checked on, and how much a level may differ from the full mesh there.</summary>
public sealed class LodOptions
{
    /// <summary>The most a level may differ from the full mesh, in screen pixels, at the distance the viewer switches to it.</summary>
    public double PixelLimit { get; set; } = 1.0;
    /// <summary>Height of the window in pixels (a larger window shows the same difference in more pixels).</summary>
    public double ScreenHeightPixels { get; set; } = 1080;
    /// <summary>Vertical field of view in radians; the viewer's default is 60 degrees.</summary>
    public double FieldOfView { get; set; } = Math.PI / 3;
    /// <summary>The viewer's object detail setting (RenderVolumeLODFactor).</summary>
    public double LodFactor { get; set; } = 1.0;
}

/// <summary>
/// The viewer's choice of level of detail for a mesh, written from what it does. References, all in the viewer source:
/// indra/newview/llvovolume.cpp calcLOD (1507-1650) and computeLODDetail (1451-1465): the level comes from tan = lodFactor * radius / distance,
/// where distance is the camera distance times sDistanceFactor (llviewercontrol.cpp 322-323: 1 - lodFactor * 0.1) times pi/3 (llvovolume.cpp 1611);
/// indra/llmath/llvolumemgr.cpp 33-40 (getDetailFromTan 328-340): level 0 (lowest) up to tan 0.03, 1 (low) up to 0.06, 2 (medium) up to 0.24, else 3 (high);
/// the radius is the volume's scale times its LOD scale bias (llvovolume.cpp 1566; llvolume.cpp 1967 sets 1, 2055 sets 0.5).
/// </summary>
public static class ViewerLod
{
    /// <summary>Largest tan of the angle at which each level (0 lowest .. 3 high) is shown: 0.03, 0.06, 0.24 and 3.0 (llvolumemgr.cpp 36-40).</summary>
    public static readonly double[] Thresholds = { 0.03, 0.06, 0.24, 3.0 };

    /// <summary>The radius the viewer uses for a mesh of this size. The smaller bias (0.5) is taken: a smaller radius switches levels at a shorter distance, so a given error shows in more pixels.</summary>
    public static double Radius(Vector3 scale) => 0.5 * scale.Length();

    /// <summary>The camera distance, in metres, at which the viewer starts to draw level <paramref name="lod"/> (the nearest it is drawn).</summary>
    public static double SwitchDistance(int lod, double radius, LodOptions options) => options.LodFactor * radius / (Thresholds[lod] * DistanceFactor(options) * Math.PI / 3);

    /// <summary>The level the viewer draws at a camera distance.</summary>
    public static int LevelAt(double distance, double radius, LodOptions options)
    {
        double tan = options.LodFactor * radius / (distance * DistanceFactor(options) * Math.PI / 3);
        for (int i = 0; i < Thresholds.Length - 1; i++)
            if (tan <= Thresholds[i])
                return i;
        return Thresholds.Length - 1;
    }

    /// <summary>The viewer scales every distance by 1 - lodFactor * 0.1 before it compares angles (llviewercontrol.cpp 323, llvovolume.cpp 1598).</summary>
    private static double DistanceFactor(LodOptions options) => 1 - options.LodFactor * 0.1;

    /// <summary>Screen pixels in one metre across the view at a camera distance (the viewer's vertical field of view over the window height).</summary>
    public static double PixelsPerMeter(double distance, LodOptions options) => options.ScreenHeightPixels / (2 * Math.Tan(options.FieldOfView / 2)) / distance;
}

/// <summary>One lower level of detail of a merged part, with what was checked about it.</summary>
public sealed class LodLevel
{
    /// <summary>0 lowest, 1 low, 2 medium.</summary>
    public int Level { get; set; }
    public List<EncoderFace> Faces { get; set; } = new List<EncoderFace>();
    public int Triangles { get; set; }
    /// <summary>Triangles aimed for (a third, ninth or twenty-seventh of the full mesh) and what was kept after the error check asked for more.</summary>
    public int TargetTriangles { get; set; }
    public double SwitchDistance { get; set; }
    /// <summary>The most this level may differ from the full mesh, in metres, at the switch distance (the pixel limit there).</summary>
    public double AllowedMeters { get; set; }
    /// <summary>The measured differences from the full mesh, in metres: of a vertex from the surface of its slot, and of a texture coordinate (converted by how far the slot's surface runs per unit of coordinate).</summary>
    public double PositionErrorMeters { get; set; }
    public double UvErrorMeters { get; set; }
    public double ErrorPixels { get; set; }
    public int DroppedMembers { get; set; }
    public string Method { get; set; } = "";
}

/// <summary>
/// The lower levels of detail of a merged part. Each is made from the full mesh: members whose screen size at the level's switch distance is under
/// the pixel limit are left out, and each slot is simplified on its own, with the vertices on texture seams and on the slot's border kept where
/// they are, so every vertex of a level is a vertex of the full mesh in the same slot and no corner takes another corner's coordinate. The level
/// keeps as few triangles as the viewer's profile aims for (a third, a ninth, a twenty-seventh) unless a measured difference from the full mesh
/// would exceed the pixel limit at the switch distance; then it keeps more, up to the level above.
/// </summary>
public static class MergedLods
{
    /// <summary>
    /// How much a difference in a texture coordinate counts against a difference in position when the simplifier chooses what to collapse (positions
    /// are in the part's unit box and coordinates in texture units, so a coordinate usually runs for a few metres: measured on a 10 m textured sphere,
    /// 16 keeps the coordinate error and the position error of the chosen collapses about level).
    /// </summary>
    private const float UvWeight = 16f;

    /// <summary>Distances closer than this, in metres, are the same place (a vertex on a seam is as near to the triangles on both sides).</summary>
    private const double Tie = 1e-6;

    /// <summary>One slot as it is worked on: its arrays, and the member each vertex came from (-1 for none).</summary>
    private sealed class Slot
    {
        public float[] Pos = Array.Empty<float>(), Nor = Array.Empty<float>(), Uv = Array.Empty<float>();
        public uint[] Idx = Array.Empty<uint>();
        public int[] Owner = Array.Empty<int>();
        public int Vertices => Pos.Length / 3;
        public int Triangles => Idx.Length / 3;
    }

    /// <summary>The levels medium, low and lowest, in that order.</summary>
    public static List<LodLevel> Build(MergedGeometry geometry, IReadOnlyList<MergeMember> members, LodOptions options = null)
    {
        options ??= new LodOptions();
        List<Slot> full = FromGeometry(geometry);
        double radius = ViewerLod.Radius(geometry.Scale);
        HashSet<int> present = full.SelectMany(s => s.Owner.Where(o => o >= 0)).ToHashSet();
        List<Slot> above = full;
        HashSet<int> dropped = new HashSet<int>();
        List<LodLevel> levels = new List<LodLevel>();
        for (int lod = 2; lod >= 0; lod--)
        {
            double distance = ViewerLod.SwitchDistance(lod, radius, options);
            double ppm = ViewerLod.PixelsPerMeter(distance, options);
            double allowed = options.PixelLimit / ppm;

            // a member whose screen size (the length of its scale vector) at this distance is under the limit is left out; the largest one stays
            HashSet<int> under = present.Where(m => m < members.Count && members[m].Scale.Length() * ppm < options.PixelLimit).ToHashSet();
            if (under.Count == present.Count && present.Count > 0)
                under.Remove(present.OrderByDescending(m => members[m].Scale.Length()).First());
            dropped.UnionWith(under);
            List<Slot> reference = Drop(full, dropped), baseSlots = Drop(above, dropped);
            int referenceTriangles = reference.Sum(s => s.Triangles), baseTriangles = baseSlots.Sum(s => s.Triangles);
            double fraction = 1.0 / Math.Pow(3, 3 - lod);
            int target = Math.Max(1, (int)Math.Round(referenceTriangles * fraction));

            List<Slot> chosen = baseSlots;
            string method;
            (double position, double uv) error = (0, 0);
            double aim = target;
            for (; ; )
            {
                if (aim >= baseTriangles)
                {
                    chosen = baseSlots;
                    error = Measure(reference, chosen, geometry.Scale);
                    method = "kept whole: no smaller mesh stays within the limit";
                    if (dropped.Count > 0)
                        method += "; left out " + dropped.Count + " small member(s)";
                    break;
                }
                double ratio = aim / baseTriangles;
                List<Slot> candidate = baseSlots.Select(s => SimplifySlot(s, ratio)).ToList();
                error = Measure(reference, candidate, geometry.Scale);
                if (error.position <= allowed && error.uv <= allowed)
                {
                    chosen = candidate;
                    int kept = candidate.Sum(s => s.Triangles);
                    method = aim <= target ? "simplified to " + kept + " of " + referenceTriangles + " triangles" : "simplified to " + kept + " triangles, more than the profile's " + target + ": less would show past the limit";
                    if (dropped.Count > 0)
                        method += "; left out " + dropped.Count + " small member(s)";
                    break;
                }
                aim *= 1.4;
            }

            levels.Add(new LodLevel
            {
                Level = lod, Faces = chosen.Select(ToFace).ToList(), Triangles = chosen.Sum(s => s.Triangles), TargetTriangles = target, SwitchDistance = distance, AllowedMeters = allowed,
                PositionErrorMeters = error.position, UvErrorMeters = error.uv, ErrorPixels = Math.Max(error.position, error.uv) * ppm, DroppedMembers = dropped.Count, Method = method
            });
            above = chosen;
        }
        return levels;
    }

    private static EncoderFace ToFace(Slot s) => new EncoderFace { Positions = s.Pos, Normals = s.Nor, Uvs = s.Uv, Indices = s.Idx };

    private static List<Slot> FromGeometry(MergedGeometry g)
    {
        List<Slot> slots = new List<Slot>();
        for (int i = 0; i < g.Faces.Count; i++)
        {
            EncoderFace f = g.Faces[i];
            int[] owner = new int[f.VertexCount];
            Array.Fill(owner, -1);
            foreach (FaceRun run in g.Runs)
            {
                if (run.Slot != i)
                    continue;
                for (int k = run.FirstIndex; k < run.FirstIndex + run.IndexCount; k++)
                {
                    int v = (int)f.Indices[k];
                    if (owner[v] < 0)
                        owner[v] = run.Member;
                }
            }
            slots.Add(new Slot { Pos = f.Positions, Nor = f.Normals, Uv = f.Uvs, Idx = f.Indices, Owner = owner });
        }
        return slots;
    }

    /// <summary>The slots without the triangles of the given members (a triangle belongs to the member its first vertex came from).</summary>
    private static List<Slot> Drop(List<Slot> slots, HashSet<int> dropped)
    {
        if (dropped.Count == 0)
            return slots;
        return slots.Select(s =>
        {
            List<uint> keep = new List<uint>();
            for (int t = 0; t < s.Idx.Length; t += 3)
                if (!dropped.Contains(s.Owner[(int)s.Idx[t]]))
                {
                    keep.Add(s.Idx[t]);
                    keep.Add(s.Idx[t + 1]);
                    keep.Add(s.Idx[t + 2]);
                }
            return keep.Count == s.Idx.Length ? s : Compact(s, keep.ToArray());
        }).ToList();
    }

    /// <summary>The slot with only the vertices the given indices use, in order of first use.</summary>
    private static Slot Compact(Slot s, uint[] indices)
    {
        if (indices.Length == 0)
            return new Slot();
        int[] map = new int[s.Vertices];
        Array.Fill(map, -1);
        List<int> used = new List<int>();
        uint[] idx = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            int v = (int)indices[i];
            if (map[v] < 0)
            {
                map[v] = used.Count;
                used.Add(v);
            }
            idx[i] = (uint)map[v];
        }
        Slot r = new Slot { Idx = idx, Pos = new float[used.Count * 3], Nor = new float[used.Count * 3], Uv = new float[used.Count * 2], Owner = new int[used.Count] };
        for (int k = 0; k < used.Count; k++)
        {
            int v = used[k];
            Array.Copy(s.Pos, v * 3, r.Pos, k * 3, 3);
            Array.Copy(s.Nor, v * 3, r.Nor, k * 3, 3);
            Array.Copy(s.Uv, v * 2, r.Uv, k * 2, 2);
            r.Owner[k] = s.Owner[v];
        }
        return r;
    }

    // ---------------------------------------------------------------- simplifying a slot

    private static Slot SimplifySlot(Slot s, double ratio)
    {
        if (s.Triangles == 0)
            return s;
        int target = Math.Max(3, (int)Math.Floor(s.Idx.Length * ratio));
        if (target >= s.Idx.Length)
            return s;
        uint[] result = OpenSim.MeshAsset.SeamSimplifier.Simplify(s.Pos, s.Uv, s.Idx, Locked(s), target, UvWeight);
        return result == null || result.Length < 3 ? s : Compact(s, result);
    }

    /// <summary>
    /// The vertices that must stay: those at a place where the slot has more than one texture coordinate (a seam), another normal turned away by
    /// more than sixty degrees (a hard edge) or another member's vertex, and those at a place on the slot's border (an edge only one triangle uses).
    /// </summary>
    private static bool[] Locked(Slot s)
    {
        int n = s.Vertices;
        Dictionary<(int, int, int), int> places = new Dictionary<(int, int, int), int>();
        int[] placeOf = new int[n];
        List<int> first = new List<int>();
        List<bool> lockPlace = new List<bool>();
        for (int v = 0; v < n; v++)
        {
            (int, int, int) key = (BitConverter.SingleToInt32Bits(s.Pos[v * 3] + 0f), BitConverter.SingleToInt32Bits(s.Pos[v * 3 + 1] + 0f), BitConverter.SingleToInt32Bits(s.Pos[v * 3 + 2] + 0f));
            if (!places.TryGetValue(key, out int p))
            {
                p = first.Count;
                places[key] = p;
                first.Add(v);
                lockPlace.Add(false);
            }
            placeOf[v] = p;
            int f = first[p];
            if (f == v)
                continue;
            bool sameUv = BitConverter.SingleToInt32Bits(s.Uv[v * 2] + 0f) == BitConverter.SingleToInt32Bits(s.Uv[f * 2] + 0f)
                && BitConverter.SingleToInt32Bits(s.Uv[v * 2 + 1] + 0f) == BitConverter.SingleToInt32Bits(s.Uv[f * 2 + 1] + 0f);
            float dot = s.Nor[v * 3] * s.Nor[f * 3] + s.Nor[v * 3 + 1] * s.Nor[f * 3 + 1] + s.Nor[v * 3 + 2] * s.Nor[f * 3 + 2];
            if (!sameUv || dot < 0.5f || s.Owner[v] != s.Owner[f])
                lockPlace[p] = true;
        }
        Dictionary<(int, int), int> edges = new Dictionary<(int, int), int>();
        void Edge(int a, int b)
        {
            if (a == b)
                return;
            (int, int) key = a < b ? (a, b) : (b, a);
            edges[key] = edges.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        for (int t = 0; t < s.Idx.Length; t += 3)
        {
            int a = placeOf[(int)s.Idx[t]], b = placeOf[(int)s.Idx[t + 1]], c = placeOf[(int)s.Idx[t + 2]];
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        foreach (KeyValuePair<(int, int), int> e in edges)
            if (e.Value == 1)
            {
                lockPlace[e.Key.Item1] = true;
                lockPlace[e.Key.Item2] = true;
            }
        bool[] locked = new bool[n];
        for (int v = 0; v < n; v++)
            locked[v] = lockPlace[placeOf[v]];
        return locked;
    }

    // ---------------------------------------------------------------- measuring a level against the full mesh

    /// <summary>
    /// The worst distance, in metres, from a vertex of each slot of the reference to the same slot of the level, and the worst difference in
    /// texture coordinate at the nearest point, in metres of surface (the distance the nearest triangle's surface runs for that difference of
    /// coordinate). A slot the level has lost gives an infinite difference.
    /// </summary>
    private static (double Position, double Uv) Measure(List<Slot> reference, List<Slot> level, Vector3 scale)
    {
        double worstPosition = 0, worstUv = 0;
        for (int i = 0; i < reference.Count; i++)
        {
            Slot r = reference[i], l = level[i];
            if (r.Triangles == 0)
                continue;
            if (l.Triangles == 0)
                return (double.MaxValue, double.MaxValue);
            if (ReferenceEquals(r, l))
                continue;
            Surface surface = new Surface(l, r, scale);
            bool[] used = new bool[r.Vertices];
            foreach (uint index in r.Idx)
                used[index] = true;
            for (int v = 0; v < r.Vertices; v++)
            {
                if (!used[v])
                    continue;
                (double d, double uv) = surface.Nearest(new Vector3(r.Pos[v * 3], r.Pos[v * 3 + 1], r.Pos[v * 3 + 2]) * scale, new Vector2(r.Uv[v * 2], r.Uv[v * 2 + 1]));
                worstPosition = Math.Max(worstPosition, d);
                worstUv = Math.Max(worstUv, uv);
            }
        }
        return (worstPosition, worstUv);
    }

    /// <summary>The triangles of a slot in metres, in a grid of cells, for finding the nearest one to a point.</summary>
    private sealed class Surface
    {
        private readonly Vector3[] _p;
        private readonly Vector2[] _t;
        private readonly int[] _i;
        private readonly Vector3[] _dPdu, _dPdv;
        private readonly double[] _stretch;
        private readonly Dictionary<int, List<int>> _cells = new Dictionary<int, List<int>>();
        private readonly Vector3 _min;
        private readonly float _cell;
        private readonly int _n;
        private int[] _stamp;
        private int _clock;

        public Surface(Slot s, Slot reference, Vector3 scale)
        {
            _p = new Vector3[s.Vertices];
            _t = new Vector2[s.Vertices];
            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
            for (int v = 0; v < s.Vertices; v++)
            {
                _p[v] = new Vector3(s.Pos[v * 3], s.Pos[v * 3 + 1], s.Pos[v * 3 + 2]) * scale;
                _t[v] = new Vector2(s.Uv[v * 2], s.Uv[v * 2 + 1]);
                lo = Vector3.Min(lo, _p[v]);
                hi = Vector3.Max(hi, _p[v]);
            }
            for (int v = 0; v < reference.Vertices; v++)
            {
                Vector3 q = new Vector3(reference.Pos[v * 3], reference.Pos[v * 3 + 1], reference.Pos[v * 3 + 2]) * scale;
                lo = Vector3.Min(lo, q);
                hi = Vector3.Max(hi, q);
            }
            _i = new int[s.Idx.Length];
            for (int k = 0; k < _i.Length; k++)
                _i[k] = (int)s.Idx[k];
            int triangles = _i.Length / 3;
            _n = Math.Clamp((int)Math.Ceiling(Math.Cbrt(triangles)) * 2, 1, 48);
            float extent = Math.Max(1e-6f, Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z)));
            _min = lo;
            _cell = extent / _n * 1.0001f;
            _stamp = new int[triangles];
            _stretch = new double[triangles];
            _dPdu = new Vector3[triangles];
            _dPdv = new Vector3[triangles];
            for (int t = 0; t < triangles; t++)
            {
                int a = _i[t * 3], b = _i[t * 3 + 1], c = _i[t * 3 + 2];
                double stretch = 0;
                foreach ((int x, int y) in new[] { (a, b), (b, c), (c, a) })
                {
                    double lu = Vector2.Distance(_t[x], _t[y]);
                    if (lu > 1e-9)
                        stretch = Math.Max(stretch, Vector3.Distance(_p[x], _p[y]) / lu);
                }
                _stretch[t] = stretch;
                // how the surface runs with the coordinates on this triangle: the position changes by dPdu for a unit of u and dPdv for a unit of v
                Vector2 t1 = _t[b] - _t[a], t2 = _t[c] - _t[a];
                Vector3 e1 = _p[b] - _p[a], e2 = _p[c] - _p[a];
                float det = t1.X * t2.Y - t2.X * t1.Y;
                if (Math.Abs(det) > 1e-12f)
                {
                    _dPdu[t] = (e1 * t2.Y - e2 * t1.Y) / det;
                    _dPdv[t] = (e2 * t1.X - e1 * t2.X) / det;
                }
                Vector3 tlo = Vector3.Min(_p[a], Vector3.Min(_p[b], _p[c])), thi = Vector3.Max(_p[a], Vector3.Max(_p[b], _p[c]));
                (int x0, int y0, int z0) = Cell(tlo);
                (int x1, int y1, int z1) = Cell(thi);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int key = (z * _n + y) * _n + x;
                            if (!_cells.TryGetValue(key, out List<int> list))
                                _cells[key] = list = new List<int>();
                            list.Add(t);
                        }
            }
        }

        private (int, int, int) Cell(Vector3 p) => (Math.Clamp((int)((p.X - _min.X) / _cell), 0, _n - 1), Math.Clamp((int)((p.Y - _min.Y) / _cell), 0, _n - 1), Math.Clamp((int)((p.Z - _min.Z) / _cell), 0, _n - 1));

        /// <summary>The distance from the point to the nearest triangle, and the texture coordinate difference there in metres of surface.</summary>
        public (double Distance, double Uv) Nearest(Vector3 p, Vector2 uv)
        {
            (int cx, int cy, int cz) = Cell(p);
            double best = double.MaxValue, bestUv = 0;
            _clock++;
            for (int k = 0; k < _n; k++)
            {
                for (int z = Math.Max(0, cz - k); z <= Math.Min(_n - 1, cz + k); z++)
                    for (int y = Math.Max(0, cy - k); y <= Math.Min(_n - 1, cy + k); y++)
                        for (int x = Math.Max(0, cx - k); x <= Math.Min(_n - 1, cx + k); x++)
                        {
                            if (Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) != k)
                                continue;
                            if (!_cells.TryGetValue((z * _n + y) * _n + x, out List<int> list))
                                continue;
                            foreach (int t in list)
                            {
                                if (_stamp[t] == _clock)
                                    continue;
                                _stamp[t] = _clock;
                                int a = _i[t * 3], b = _i[t * 3 + 1], c = _i[t * 3 + 2];
                                Vector3 q = ClosestPoint(p, _p[a], _p[b], _p[c], out float u, out float v, out float w);
                                double d = Vector3.Distance(p, q);
                                Vector2 off = _t[a] * u + _t[b] * v + _t[c] * w - uv;
                                // the difference of coordinates as metres of surface: along the triangle's own run of surface; with no usable run (the coordinates are a line), its longest run
                                double e = _dPdu[t] == Vector3.Zero && _dPdv[t] == Vector3.Zero ? off.Length() * _stretch[t] : (_dPdu[t] * off.X + _dPdv[t] * off.Y).Length();
                                // at a seam the vertex is as near to the triangles on both sides: it is right if one of them draws its coordinate
                                if (d < best - Tie)
                                {
                                    best = d;
                                    bestUv = e;
                                }
                                else if (d <= best + Tie)
                                {
                                    best = Math.Min(best, d);
                                    bestUv = Math.Min(bestUv, e);
                                }
                            }
                        }
                // every cell of the next ring is at least k cells away from the point's own cell edge
                if (best <= k * (double)_cell)
                    break;
            }
            return (best, bestUv);
        }
    }

    /// <summary>The nearest point of a triangle to a point, with its barycentric weights (the usual region test).</summary>
    private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { u = 1; v = 0; w = 0; return a; }
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { u = 0; v = 1; w = 0; return b; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float t = d1 / (d1 - d3); u = 1 - t; v = t; w = 0; return a + t * ab; }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { u = 0; v = 0; w = 1; return c; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float t = d2 / (d2 - d6); u = 1 - t; v = 0; w = t; return a + t * ac; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float t = (d4 - d3) / ((d4 - d3) + (d5 - d6)); u = 0; v = 1 - t; w = t; return b + t * (c - b); }
        float denom = 1f / (va + vb + vc);
        v = vb * denom; w = vc * denom; u = 1 - v - w;
        return a + ab * v + ac * w;
    }
}
