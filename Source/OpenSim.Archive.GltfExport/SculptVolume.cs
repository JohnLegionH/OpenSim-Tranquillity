/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Written from reading the Second Life viewer's rule for the faces of a sculpted prim that has a profile cut, a hollow or an
// open path (LLVolume::sculpt, LLProfile::generate, LLProfile::genNGon, LLProfile::addHole, LLPath::genNGon,
// LLVolume::createVolumeFaces, LLVolumeFace::createSide and createCap in indra/llmath/llvolume.cpp, public source of the
// viewer). No viewer code is copied; the rule is described in comments where it is applied.

using System.Globalization;

namespace OpenSim.Archive.GltfExport;

public static partial class SculptGeometry
{
    // The viewer draws a sculpt at detail 4 when it is closest: a circle hole gets 6 * 4 sides.
    private const float HighestDetail = 4f;
    private const int MinDetailFaces = 6;

    private enum Role { Cap, Side, ProfileEnd }

    private sealed class ProfileFace
    {
        public PrimFaceKind Kind;
        public Role Role;
        public bool Top;
        public int BeginS, Count;
        public bool Flat;
        public bool Inner;
    }

    /// <summary>
    /// True when the viewer gives this sculpt more than the one outer face: its profile is cut or hollow, or its path is
    /// open. Only the circle profile on a circular path (what a sculpt is made with) is described; any other combination
    /// keeps the one face.
    /// </summary>
    public static bool HasProfileFaces(PrimParams p)
    {
        if (!Described(p))
            return false;
        return p.ProfileHollow > 0f || p.ProfileBegin > 0f || p.ProfileEnd < 1f || ViewerFaceRule.PathIsOpen(p);
    }

    /// <summary>A circle profile on a circular path: the combination <see cref="BuildVolume"/> generates the viewer's grid for.</summary>
    internal static bool Described(PrimParams p) => p != null && p.ProfileCurve == 0 && (p.PathCurve & 0xF0) == 0x20;

    /// <summary>
    /// A sculpt's faces by the viewer's rule. Without a cut, a hollow or an open path it is <see cref="Build"/>. With one, the
    /// viewer generates the profile and the path with the map's side counts, takes every grid vertex from the map as usual,
    /// and cuts the grid into the profile's faces: the cap at the far end of an open path (face 0), the outer wall, the inner
    /// wall of a hollow, the cap at the near end, and the two flat faces along a profile cut. Each face keeps its number even
    /// when another has no area.
    /// </summary>
    public static SculptResult BuildVolume(SculptMap map, int sculptType, PrimParams p)
    {
        // The viewer's own rows and columns are generated whenever the profile and path are circles, even for a plain sculpt: the
        // path and the ring step by a float that does not always add up to exactly 1, which can leave one row more than the sides.
        if (!Described(p))
            return Build(map, sculptType);

        SculptResult result = new SculptResult();
        if (map == null || map.Width < 1 || map.Height < 1 || map.Rgb == null || map.Rgb.Length < map.Width * map.Height * 3)
        {
            result.Problem = "the sculpt map has no pixel data";
            return result;
        }

        int stitching = sculptType & 7;
        bool invert = (sculptType & FlagInvert) != 0;
        bool mirror = (sculptType & FlagMirror) != 0;
        bool reverseHorizontal = invert ? !mirror : mirror;

        Resolution(map.Width, map.Height, out int pathSides, out int profileSides);
        bool pathOpen = ViewerFaceRule.PathIsOpen(p);
        List<float> texT = PathRows(p.PathBegin, p.PathEnd, pathSides);
        List<float[]> profile = new List<float[]>();   // x, y, z (the profile parameter) per point
        List<ProfileFace> faces = ProfileFaces(p, profileSides, pathOpen, profile, out bool profileOpen);

        int rows = texT.Count, cols = profile.Count;
        result.Rows = rows;
        result.Columns = cols;
        result.ViewerFaceCount = faces.Count;
        if (rows < 2 || cols < 2)
        {
            result.Problem = "the profile or path has too few points";
            return result;
        }

        float[] grid = SampleGrid(map, stitching, mirror, reverseHorizontal, rows, cols);
        result.SurfaceArea = Area(grid, rows, cols);
        if (result.SurfaceArea < MinSurfaceArea || result.SurfaceArea > MaxSurfaceArea)
        {
            result.Problem = "the sculpt map's surface area (" + result.SurfaceArea.ToString("0.#####", CultureInfo.InvariantCulture) +
                ") is outside the range the viewer accepts (" + MinSurfaceArea.ToString(CultureInfo.InvariantCulture) + " to " +
                MaxSurfaceArea.ToString(CultureInfo.InvariantCulture) + "); the viewer draws a placeholder sphere";
            return result;
        }
        result.Grid = grid;

        bool hollow = p.ProfileHollow > 0f;
        for (int number = 0; number < faces.Count; number++)
        {
            ProfileFace f = faces[number];
            PrimFace built = f.Role == Role.Cap
                ? BuildCap(f, number, grid, rows, cols, profile, hollow, profileOpen, result)
                : BuildSide(f, number, grid, rows, cols, profile, texT, reverseHorizontal, stitching, profileOpen, result);
            if (built != null && built.Positions.Length > 0)
                result.Faces.Add(built);
        }

        if (result.Faces.Count == 0)
        {
            result.Problem = "the sculpt map gives no triangle with an area";
            return result;
        }
        result.Face = result.Faces[0];
        return result;
    }

    // ------------------------------------------------------------------ path and profile points

    /// <summary>
    /// The path's rows as the viewer generates a circular path with <paramref name="sides"/> sides: the cut start, every
    /// multiple of 1 / sides below the cut end, and the cut end. Returns each row's texture parameter.
    /// </summary>
    internal static List<float> PathRows(float begin, float end, int sides)
    {
        List<float> t = new List<float>();
        float step = 1f / sides;
        float v = begin;
        t.Add(v);
        v += step;
        v = ((int)(v * sides)) / (float)sides;   // snapped, so a cut does not move the other rows
        while (v < end)
        {
            t.Add(v);
            v += step;
        }
        t.Add(end);
        return t;
    }

    private static readonly float[] NGonScale = { 1f, 1f, 1f, 0.5f, 0.707107f, 0.53f, 0.525f, 0.5f };

    /// <summary>
    /// The points of a regular n-gon ring between the profile cut's ends: a first point where the cut starts, every corner
    /// between, and a last point where the cut ends. A cut ring that has no hollow gets the centre as a last point.
    /// </summary>
    private static void NGon(List<float[]> into, float begin, float end, int sides, bool addCenter, out bool open)
    {
        float scale = 0.5f;
        float step = 1f / sides, angStep = 2f * MathF.PI * step;
        int totalSides = (int)MathF.Round(sides);
        if (totalSides < 8)
            scale = NGonScale[Math.Max(totalSides, 0)];

        float tFirst = MathF.Floor(begin * sides) / sides;
        float t = tFirst, ang = 2f * MathF.PI * t;
        float[] p1 = { MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t };
        t += step;
        ang += angStep;
        float[] p2 = { MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t };

        float fraction = (begin - tFirst) * sides;
        if (fraction < 0.9999f)
            into.Add(Lerp(p1, p2, fraction));

        while (t < end)
        {
            p1 = new[] { MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t };
            into.Add(p1);
            t += step;
            ang += angStep;
        }

        fraction = (end - (t - step)) * sides;
        p2 = new[] { MathF.Cos(ang) * scale, MathF.Sin(ang) * scale, t };
        if (fraction > 0.0001f)
            into.Add(Lerp(p1, p2, fraction));

        open = (end - begin) < 0.99f;
        if (open && addCenter)
            into.Add(new[] { 0f, 0f, 0f });
    }

    private static float[] Lerp(float[] a, float[] b, float f) => new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f };

    /// <summary>The faces of a circle profile in the viewer's order, with the profile points they index.</summary>
    private static List<ProfileFace> ProfileFaces(PrimParams p, int sides, bool pathOpen, List<float[]> profile, out bool open)
    {
        float hollow = p.ProfileHollow;
        bool hasHollow = hollow > 0f;
        int holeType = p.HoleType;
        float circleDetail = MinDetailFaces * HighestDetail;
        if (hasHollow && holeType == 0x20)
            circleDetail = MathF.Ceiling(circleDetail / 4f) * 4f;

        List<ProfileFace> faces = new List<ProfileFace>();
        NGon(profile, p.ProfileBegin, p.ProfileEnd, sides, !hasHollow, out open);
        int total = profile.Count;

        if (pathOpen)
            faces.Add(new ProfileFace { Kind = PrimFaceKind.Top, Role = Role.Cap, Top = true, BeginS = 0, Count = total });

        // an open ring with no hollow ends in the centre point, which the outer wall does not include
        faces.Add(new ProfileFace { Kind = PrimFaceKind.Outer, Role = Role.Side, BeginS = 0, Count = open && !hasHollow ? total - 1 : total });

        int totalOut = total;
        if (hasHollow)
        {
            // the hole: another ring (4 or 3 sides for a square or triangle hole, flat; else the circle's), scaled by the hollow
            // in all of its components (the profile parameter too) and walked the other way round
            bool flat = holeType == 0x20 || holeType == 0x30;
            int holeSides = holeType == 0x20 ? 4 : holeType == 0x30 ? 3 : (int)circleDetail;
            List<float[]> ring = new List<float[]>();
            NGon(ring, p.ProfileBegin, p.ProfileEnd, holeSides, false, out _);
            for (int i = ring.Count - 1; i >= 0; i--)
                profile.Add(new[] { ring[i][0] * hollow, ring[i][1] * hollow, ring[i][2] * hollow });
            faces.Add(new ProfileFace { Kind = PrimFaceKind.Inner, Role = Role.Side, BeginS = totalOut, Count = profile.Count - totalOut, Flat = flat, Inner = true });
            if (pathOpen)
                faces[0].Count *= 2;
        }

        total = profile.Count;
        if (pathOpen)
            faces.Add(new ProfileFace { Kind = PrimFaceKind.Bottom, Role = Role.Cap, Top = false, BeginS = 0, Count = total });

        if (open)
        {
            faces.Add(new ProfileFace { Kind = PrimFaceKind.ProfileCutBegin, Role = Role.ProfileEnd, BeginS = total - 1, Count = 2, Flat = true });
            faces.Add(new ProfileFace { Kind = PrimFaceKind.ProfileCutEnd, Role = Role.ProfileEnd, BeginS = hasHollow ? totalOut - 1 : total - 2, Count = 2, Flat = true });
        }
        return faces;
    }

    // ------------------------------------------------------------------ faces

    private static PrimFace BuildSide(ProfileFace f, int number, float[] grid, int rows, int cols, List<float[]> profile, List<float> texT,
        bool reverseHorizontal, int stitching, bool profileOpen, SculptResult result)
    {
        bool innerFlat = f.Inner && f.Flat && f.Count > 2;
        int numS = innerFlat ? f.Count * 2 : f.Count;
        int perRow = innerFlat ? f.Count : numS;
        float beginStex = MathF.Floor(profile[f.BeginS][2]);

        float[] pos = new float[rows * numS * 3];
        float[] uvUp = new float[rows * numS * 2];
        int cur = 0;
        for (int t = 0; t < rows; t++)
        {
            float tt = texT[t];
            for (int s = 0; s < perRow; s++)
            {
                float ss;
                if (f.Role == Role.ProfileEnd)
                    ss = s != 0 ? 1f : 0f;
                else
                {
                    int index = f.BeginS + s;
                    if (index >= profile.Count)
                        ss = f.Flat ? 1f - beginStex : 1f;
                    else if (!f.Flat)
                        ss = profile[index][2];
                    else
                        ss = profile[index][2] - beginStex;
                }
                if (reverseHorizontal)
                    ss = 1f - ss;

                int column = f.BeginS + s;
                int source = column >= cols ? column - cols + cols * t : column + cols * t;
                Put(pos, uvUp, ref cur, grid, source, ss, tt);
                if (innerFlat && s > 0)
                    Put(pos, uvUp, ref cur, grid, source, ss, tt);
            }
            if (innerFlat)
            {
                // one more vertex closes the ring: the last point of an open ring, else the first
                int s = profileOpen ? perRow - 1 : 0;
                int source = f.BeginS + s + cols * t;
                float ss = profile[f.BeginS + s][2] - beginStex;
                Put(pos, uvUp, ref cur, grid, source, ss, tt);
            }
        }

        float[] normals = Normals(pos, rows, numS, stitching);
        return Triangulate(number, f.Kind, pos, normals, uvUp, rows, numS, result);
    }

    private static void Put(float[] pos, float[] uvUp, ref int cur, float[] grid, int source, float u, float v)
    {
        pos[cur * 3] = grid[source * 3]; pos[cur * 3 + 1] = grid[source * 3 + 1]; pos[cur * 3 + 2] = grid[source * 3 + 2];
        uvUp[cur * 2] = u; uvUp[cur * 2 + 1] = v;
        cur++;
    }

    /// <summary>A face's quads as the viewer's two triangles each; texture coordinates are turned to glTF's v.</summary>
    private static PrimFace Triangulate(int number, PrimFaceKind kind, float[] pos, float[] normals, float[] uvUp, int rows, int cols, SculptResult result)
    {
        List<float> p = new List<float>(), n = new List<float>(), uv = new List<float>();
        for (int t = 0; t < rows - 1; t++)
            for (int s = 0; s < cols - 1; s++)
            {
                int bl = s + cols * t, tr = s + 1 + cols * (t + 1), tl = s + cols * (t + 1), br = s + 1 + cols * t;
                Corner(pos, normals, uvUp, bl, tr, tl, p, n, uv, result);
                Corner(pos, normals, uvUp, bl, br, tr, p, n, uv, result);
            }
        return new PrimFace { Number = number, Kind = kind, MesherNumber = number, Positions = p.ToArray(), Normals = n.ToArray(), Uvs = uv.ToArray() };
    }

    private static void Corner(float[] pos, float[] normals, float[] uvUp, int a, int b, int c, List<float> p, List<float> n, List<float> uv, SculptResult result)
    {
        if (Same(pos, a, b) && Same(pos, b, c))
        {
            result.DegenerateTrianglesDropped++;
            return;
        }
        float ax = pos[b * 3] - pos[a * 3], ay = pos[b * 3 + 1] - pos[a * 3 + 1], az = pos[b * 3 + 2] - pos[a * 3 + 2];
        float bx = pos[c * 3] - pos[a * 3], by = pos[c * 3 + 1] - pos[a * 3 + 1], bz = pos[c * 3 + 2] - pos[a * 3 + 2];
        if (ay * bz - az * by == 0f && az * bx - ax * bz == 0f && ax * by - ay * bx == 0f)
        {
            result.DegenerateTrianglesDropped++;
            return;
        }
        foreach (int i in new[] { a, b, c })
        {
            p.Add(pos[i * 3]); p.Add(pos[i * 3 + 1]); p.Add(pos[i * 3 + 2]);
            n.Add(normals[i * 3]); n.Add(normals[i * 3 + 1]); n.Add(normals[i * 3 + 2]);
            uv.Add(uvUp[i * 2]); uv.Add(1f - uvUp[i * 2 + 1]);
        }
    }

    /// <summary>
    /// A cap: the profile's points taken from the grid's last row (the cap at the far end, face 0) or first row, one flat
    /// normal, texture coordinates from the profile's own x and y. A solid cap that is not cut gets its centre as one more
    /// vertex and is a fan round it; a cut solid cap is a fan round its centre point; a hollow cap is a strip between the
    /// outer and inner rings. (The viewer's choice of diagonals in the strip is by the shorter one; the same is done here.)
    /// </summary>
    private static PrimFace BuildCap(ProfileFace f, int number, float[] grid, int rows, int cols, List<float[]> profile, bool hollow, bool profileOpen, SculptResult result)
    {
        int n = profile.Count;
        int rowStart = f.Top ? (rows - 1) * cols : 0;
        bool addCenter = !hollow && !profileOpen;
        int count = n + (addCenter ? 1 : 0);
        float[] pos = new float[count * 3], uv = new float[count * 2];
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        float uMin = float.MaxValue, vMin = float.MaxValue, uMax = float.MinValue, vMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            int src = rowStart + i;
            float x = grid[src * 3], y = grid[src * 3 + 1], z = grid[src * 3 + 2];
            pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
            // the viewer's v is up; the underside is mirrored
            float u = profile[i][0] + 0.5f, v = f.Top ? profile[i][1] + 0.5f : 0.5f - profile[i][1];
            uv[i * 2] = u; uv[i * 2 + 1] = v;
            uMin = MathF.Min(uMin, u); uMax = MathF.Max(uMax, u);
            vMin = MathF.Min(vMin, v); vMax = MathF.Max(vMax, v);
        }
        if (addCenter)
        {
            pos[n * 3] = (minX + maxX) / 2; pos[n * 3 + 1] = (minY + maxY) / 2; pos[n * 3 + 2] = (minZ + maxZ) / 2;
            uv[n * 2] = (uMin + uMax) / 2; uv[n * 2 + 1] = (vMin + vMax) / 2;
        }

        List<int> index = new List<int>();
        if (hollow)
        {
            int pt1 = 0, pt2 = count - 1;
            while (pt2 - pt1 > 1)
            {
                float[] p1 = profile[pt1], p2 = profile[pt2], pa = profile[pt1 + 1], pb = profile[pt2 - 1];
                float a1a2 = Area3(p1, pa, p2), a1ba = Area3(p1, pb, pa), a21b = Area3(p2, p1, pb), a2ab = Area3(p2, pa, pb);
                bool tri1a2 = a1a2 >= 0 && a2ab >= 0;
                bool tri21b = a21b >= 0 && a1ba >= 0;
                bool use1a2;
                if (!tri1a2) use1a2 = false;
                else if (!tri21b) use1a2 = true;
                else use1a2 = Dist2(p1, pa) < Dist2(p2, pb);
                if (use1a2)
                {
                    index.AddRange(f.Top ? new[] { pt1, pt1 + 1, pt2 } : new[] { pt1, pt2, pt1 + 1 });
                    pt1++;
                }
                else
                {
                    index.AddRange(f.Top ? new[] { pt1, pt2 - 1, pt2 } : new[] { pt1, pt2, pt2 - 1 });
                    pt2--;
                }
            }
        }
        else
        {
            for (int i = 0; i < count - 2; i++)
            {
                index.Add(count - 1);
                if (f.Top) { index.Add(i); index.Add(i + 1); }
                else { index.Add(i + 1); index.Add(i); }
            }
        }

        // one flat normal for the whole cap, from its first triangle
        float nx = 0, ny = 0, nz = 1;
        if (index.Count >= 3)
        {
            int i0 = index[0], i1 = index[1], i2 = index[2];
            float ax = pos[i1 * 3] - pos[i0 * 3], ay = pos[i1 * 3 + 1] - pos[i0 * 3 + 1], az = pos[i1 * 3 + 2] - pos[i0 * 3 + 2];
            float bx = pos[i2 * 3] - pos[i0 * 3], by = pos[i2 * 3 + 1] - pos[i0 * 3 + 1], bz = pos[i2 * 3 + 2] - pos[i0 * 3 + 2];
            nx = ay * bz - az * by; ny = az * bx - ax * bz; nz = ax * by - ay * bx;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-10f) { nx /= len; ny /= len; nz /= len; }
            else { nx = 0; ny = 0; nz = nz >= 0 ? 1 : -1; }
        }

        List<float> p = new List<float>(), nr = new List<float>(), t = new List<float>();
        for (int k = 0; k + 2 < index.Count; k += 3)
        {
            int a = index[k], b = index[k + 1], c = index[k + 2];
            if (Same(pos, a, b) && Same(pos, b, c))
            {
                result.DegenerateTrianglesDropped++;
                continue;
            }
            foreach (int i in new[] { a, b, c })
            {
                p.Add(pos[i * 3]); p.Add(pos[i * 3 + 1]); p.Add(pos[i * 3 + 2]);
                nr.Add(nx); nr.Add(ny); nr.Add(nz);
                t.Add(uv[i * 2]); t.Add(1f - uv[i * 2 + 1]);
            }
        }
        return new PrimFace { Number = number, Kind = f.Kind, MesherNumber = number, Positions = p.ToArray(), Normals = nr.ToArray(), Uvs = t.ToArray() };
    }

    private static float Area3(float[] a, float[] b, float[] c) =>
        (a[0] * b[1] - b[0] * a[1]) + (b[0] * c[1] - c[0] * b[1]) + (c[0] * a[1] - a[0] * c[1]);

    private static float Dist2(float[] a, float[] b) => (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);
}
