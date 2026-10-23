/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Written from reading the Second Life viewer's rule for sculpted prims (LLVolume::sculpt, sculptGenerateMapVertices,
// sculpt_calc_mesh_resolution and LLVolumeFace::createSide in indra/llmath/llvolume.cpp, public source of the viewer).
// No viewer code is copied; the rule is described in comments where it is applied.

namespace OpenSim.Archive.GltfExport;

/// <summary>The sculpt map's pixels, rows from the top of the image as an image editor shows it, three bytes (red, green, blue) per pixel.</summary>
public sealed class SculptMap
{
    public int Width, Height;
    public byte[] Rgb;
}

/// <summary>What the viewer would build from a sculpt map, or why it shows a placeholder instead.</summary>
public sealed class SculptResult
{
    /// <summary>The face (face number 0), or null when the viewer shows its placeholder sphere.</summary>
    public PrimFace Face;
    /// <summary>Why there is no face.</summary>
    public string Problem;
    /// <summary>Vertex grid: rows along the path, columns along the profile, seam vertices included.</summary>
    public int Rows, Columns;
    public float SurfaceArea;
    public int DegenerateTrianglesDropped;
    /// <summary>The vertex grid (x, y, z per vertex, row by row) and its normals, kept for checks.</summary>
    public float[] Grid, GridNormals;
}

/// <summary>
/// Builds a sculpted prim's face from its map the way the viewer does at its highest level of detail. The shape comes
/// entirely from the map: each vertex of a grid is one pixel (red, green, blue become x, y, z, 0 to 255 becoming -0.5 to
/// 0.5, so the vertices are in the unit cube the part's size scales).
/// </summary>
public static class SculptGeometry
{
    public const int TypeNone = 0, TypeSphere = 1, TypeTorus = 2, TypePlane = 3, TypeCylinder = 4, TypeMesh = 5;
    public const int FlagInvert = 0x40, FlagMirror = 0x80;

    // The viewer builds a sculpt with 6, 8, 16 or 32 sides per direction by level of detail; the highest is 32, so a grid
    // never holds more than 32 x 32 quads (1024 vertices before the seam), and never more than a quarter of the map's pixels.
    private const int HighestSides = 32;
    // A map whose surface is smaller or larger than this is not shown as a sculpt: the viewer draws a placeholder sphere.
    private const float MinSurfaceArea = 0.002f;
    private const float MaxSurfaceArea = 384f;

    /// <summary>
    /// Path and profile sizes (rows and columns of quads) for a map: as many vertices as the level of detail allows
    /// and the map can supply, with the grid's shape following the map's.
    /// </summary>
    public static void Resolution(int width, int height, out int pathSides, out int profileSides)
    {
        int maxLod = HighestSides * HighestSides;
        int maxMap = width * height / 4;
        int vertices = maxMap > 0 ? Math.Min(maxLod, maxMap) : maxLod;
        float ratio = width == 0 || height == 0 ? 1f : (float)width / (float)height;

        int s = (int)MathF.Sqrt((float)vertices / ratio);
        s = Math.Max(s, 4);
        int t = vertices / s;
        t = Math.Max(t, 4);
        s = vertices / t;
        pathSides = Math.Max(s, 1);
        profileSides = Math.Max(t, 1);
    }

    public static SculptResult Build(SculptMap map, int sculptType)
    {
        SculptResult result = new SculptResult();
        if (map == null || map.Width < 1 || map.Height < 1 || map.Rgb == null || map.Rgb.Length < map.Width * map.Height * 3)
        {
            result.Problem = "the sculpt map has no pixel data";
            return result;
        }

        int stitching = sculptType & 7;
        bool invert = (sculptType & FlagInvert) != 0;
        bool mirror = (sculptType & FlagMirror) != 0;
        // either flag alone reads the map from the other side; both together read it normally (and the mirror still flips x)
        bool reverseHorizontal = invert ? !mirror : mirror;

        Resolution(map.Width, map.Height, out int pathSides, out int profileSides);
        // a closed ring of N sides has N + 1 points, the last repeating the first
        int rows = pathSides + 1, cols = profileSides + 1;
        result.Rows = rows;
        result.Columns = cols;

        // vertex grid
        float[] grid = new float[rows * cols * 3];
        for (int s = 0; s < rows; s++)
        {
            for (int t = 0; t < cols; t++)
            {
                int reversedT = reverseHorizontal ? cols - t - 1 : t;
                uint x = (uint)((float)reversedT / (cols - 1) * (float)map.Width);
                uint y = (uint)((float)s / (rows - 1) * (float)map.Height);

                if (y == 0 && stitching == TypeSphere)
                    x = (uint)(map.Width / 2);   // the first row is pinched to one point

                if (y == (uint)map.Height)       // past the last row
                {
                    y = stitching == TypeTorus ? 0u : (uint)(map.Height - 1);   // a torus wraps to the first row
                    if (stitching == TypeSphere)
                        x = (uint)(map.Width / 2);
                }

                if (x == (uint)map.Width)        // past the last column
                    x = stitching == TypeSphere || stitching == TypeTorus || stitching == TypeCylinder ? 0u : (uint)(map.Width - 1);

                // The viewer's rows count from the bottom of the picture (its image buffers start at the bottom row),
                // while the map's pixels are held from the top, so row y of the viewer is row Height - 1 - y here.
                int pixel = ((map.Height - 1 - (int)y) * map.Width + (int)x) * 3;
                const float scale = 1f / 255f;
                float px = map.Rgb[pixel] * scale - 0.5f;
                float py = map.Rgb[pixel + 1] * scale - 0.5f;
                float pz = map.Rgb[pixel + 2] * scale - 0.5f;
                if (mirror)
                    px = -px;

                int o = (s * cols + t) * 3;
                grid[o] = px; grid[o + 1] = py; grid[o + 2] = pz;
            }
        }

        // a map with almost no variation, or one that spans an absurd size, is shown as a placeholder sphere
        result.SurfaceArea = Area(grid, rows, cols);
        if (result.SurfaceArea < MinSurfaceArea || result.SurfaceArea > MaxSurfaceArea)
        {
            result.Problem = "the sculpt map's surface area (" + result.SurfaceArea.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) +
                ") is outside the range the viewer accepts (" + MinSurfaceArea.ToString(System.Globalization.CultureInfo.InvariantCulture) + " to " +
                MaxSurfaceArea.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); the viewer draws a placeholder sphere";
            return result;
        }

        float[] normals = Normals(grid, rows, cols, stitching);
        result.Grid = grid;
        result.GridNormals = normals;

        // texture coordinates, v up as the viewer has them: u along the profile (reversed with the horizontal reading), v along the path
        int quads = (rows - 1) * (cols - 1);
        List<float> pos = new List<float>(quads * 18), nor = new List<float>(quads * 18), uv = new List<float>(quads * 12);
        int dropped = 0;
        for (int t = 0; t < rows - 1; t++)
        {
            for (int s = 0; s < cols - 1; s++)
            {
                // two triangles per quad, in the viewer's index order: (bottom left, top right, top left), (bottom left, bottom right, top right)
                int bl = s + cols * t, tr = s + 1 + cols * (t + 1), tl = s + cols * (t + 1), br = s + 1 + cols * t;
                AddTriangle(grid, normals, rows, cols, reverseHorizontal, bl, tr, tl, pos, nor, uv, ref dropped);
                AddTriangle(grid, normals, rows, cols, reverseHorizontal, bl, br, tr, pos, nor, uv, ref dropped);
            }
        }

        result.DegenerateTrianglesDropped = dropped;
        if (pos.Count == 0)
        {
            result.Problem = "the sculpt map gives no triangle with an area";
            return result;
        }
        result.Face = new PrimFace
        {
            Number = 0,
            Kind = PrimFaceKind.Outer,
            MesherNumber = 0,
            Positions = pos.ToArray(),
            Normals = nor.ToArray(),
            Uvs = uv.ToArray()
        };
        return result;
    }

    private static void AddTriangle(float[] grid, float[] normals, int rows, int cols, bool reverseHorizontal, int a, int b, int c,
        List<float> pos, List<float> nor, List<float> uv, ref int dropped)
    {
        int[] idx = { a, b, c };
        // a triangle whose corners are all one point (the pinched pole) has no area
        if (Same(grid, a, b) && Same(grid, b, c))
        {
            dropped++;
            return;
        }
        float ax = grid[b * 3] - grid[a * 3], ay = grid[b * 3 + 1] - grid[a * 3 + 1], az = grid[b * 3 + 2] - grid[a * 3 + 2];
        float bx = grid[c * 3] - grid[a * 3], by = grid[c * 3 + 1] - grid[a * 3 + 1], bz = grid[c * 3 + 2] - grid[a * 3 + 2];
        float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        if (cx == 0f && cy == 0f && cz == 0f)
        {
            dropped++;
            return;
        }
        foreach (int i in idx)
        {
            pos.Add(grid[i * 3]); pos.Add(grid[i * 3 + 1]); pos.Add(grid[i * 3 + 2]);
            nor.Add(normals[i * 3]); nor.Add(normals[i * 3 + 1]); nor.Add(normals[i * 3 + 2]);
            int row = i / cols, col = i % cols;
            float u = (float)col / (cols - 1);
            if (reverseHorizontal)
                u = 1f - u;
            float vUp = (float)row / (rows - 1);
            // glTF's v is 0 at the top of the image, the viewer's is 1 there
            uv.Add(u); uv.Add(1f - vUp);
        }
    }

    private static bool Same(float[] g, int a, int b) => g[a * 3] == g[b * 3] && g[a * 3 + 1] == g[b * 3 + 1] && g[a * 3 + 2] == g[b * 3 + 2];

    /// <summary>The summed area of the grid's quads, each as half the sum of two cross-product lengths.</summary>
    private static float Area(float[] g, int rows, int cols)
    {
        float area = 0;
        for (int s = 0; s < rows - 1; s++)
            for (int t = 0; t < cols - 1; t++)
            {
                int p1 = s * cols + t, p2 = (s + 1) * cols + t, p3 = s * cols + t + 1, p4 = (s + 1) * cols + t + 1;
                area += (CrossLength(g, p1, p2, p1, p3) + CrossLength(g, p4, p2, p4, p3)) / 2f;
            }
        return area;
    }

    private static float CrossLength(float[] g, int a1, int a2, int b1, int b2)
    {
        float ax = g[a1 * 3] - g[a2 * 3], ay = g[a1 * 3 + 1] - g[a2 * 3 + 1], az = g[a1 * 3 + 2] - g[a2 * 3 + 2];
        float bx = g[b1 * 3] - g[b2 * 3], by = g[b1 * 3 + 1] - g[b2 * 3 + 1], bz = g[b1 * 3 + 2] - g[b2 * 3 + 2];
        float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    /// <summary>
    /// Vertex normals as the viewer makes them: every triangle adds its (unnormalised) normal to its three vertices, the
    /// top-right corner of each quad twice; then the normals along a seam are summed, and a sphere's two pole rows each
    /// take the average of their row.
    /// </summary>
    private static float[] Normals(float[] g, int rows, int cols, int stitching)
    {
        float[] n = new float[rows * cols * 3];

        int triangle = 0;
        for (int t = 0; t < rows - 1; t++)
            for (int s = 0; s < cols - 1; s++)
            {
                int bl = s + cols * t, tr = s + 1 + cols * (t + 1), tl = s + cols * (t + 1), br = s + 1 + cols * t;
                Accumulate(g, n, bl, tr, tl, triangle++);
                Accumulate(g, n, bl, br, tr, triangle++);
            }

        if (stitching == TypeSphere)
        {
            AverageRow(n, cols, 0);
            AverageRow(n, cols, rows - 1);
        }

        if (stitching == TypeSphere || stitching == TypeTorus || stitching == TypeCylinder)
        {
            // the first and last vertex of each row are one point
            for (int r = 0; r < rows; r++)
                SumPair(n, r * cols, r * cols + cols - 1);
        }

        if (stitching == TypeTorus)
        {
            // the first and last row are one ring
            for (int c = 0; c < cols; c++)
                SumPair(n, c, cols * (rows - 1) + c);
        }

        for (int i = 0; i < rows * cols; i++)
        {
            float x = n[i * 3], y = n[i * 3 + 1], z = n[i * 3 + 2];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 1e-20f)
            {
                n[i * 3] = x / len; n[i * 3 + 1] = y / len; n[i * 3 + 2] = z / len;
            }
            else
            {
                n[i * 3] = 0; n[i * 3 + 1] = 0; n[i * 3 + 2] = 1;
            }
        }
        return n;
    }

    private static void Accumulate(float[] g, float[] n, int i0, int i1, int i2, int triangleNumber)
    {
        // (p0 - p1) x (p0 - p2)
        float ax = g[i0 * 3] - g[i1 * 3], ay = g[i0 * 3 + 1] - g[i1 * 3 + 1], az = g[i0 * 3 + 2] - g[i1 * 3 + 2];
        float bx = g[i0 * 3] - g[i2 * 3], by = g[i0 * 3 + 1] - g[i2 * 3 + 1], bz = g[i0 * 3 + 2] - g[i2 * 3 + 2];
        float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;

        float[] v0 = { n[i0 * 3] + cx, n[i0 * 3 + 1] + cy, n[i0 * 3 + 2] + cz };
        float[] v1 = { n[i1 * 3] + cx, n[i1 * 3 + 1] + cy, n[i1 * 3 + 2] + cz };
        float[] v2 = { n[i2 * 3] + cx, n[i2 * 3 + 1] + cy, n[i2 * 3 + 2] + cz };
        // the viewer evens out the quad's two triangles by adding the normal once more to the top-right corner
        float[] extra = triangleNumber % 2 == 0 ? v1 : v2;
        extra[0] += cx; extra[1] += cy; extra[2] += cz;

        Store(n, i0, v0); Store(n, i1, v1); Store(n, i2, v2);
    }

    private static void Store(float[] n, int i, float[] v)
    {
        n[i * 3] = v[0]; n[i * 3 + 1] = v[1]; n[i * 3 + 2] = v[2];
    }

    private static void AverageRow(float[] n, int cols, int row)
    {
        float x = 0, y = 0, z = 0;
        for (int c = 0; c < cols; c++)
        {
            int i = row * cols + c;
            x += n[i * 3]; y += n[i * 3 + 1]; z += n[i * 3 + 2];
        }
        for (int c = 0; c < cols; c++)
        {
            int i = row * cols + c;
            n[i * 3] = x; n[i * 3 + 1] = y; n[i * 3 + 2] = z;
        }
    }

    private static void SumPair(float[] n, int a, int b)
    {
        float x = n[a * 3] + n[b * 3], y = n[a * 3 + 1] + n[b * 3 + 1], z = n[a * 3 + 2] + n[b * 3 + 2];
        n[a * 3] = x; n[a * 3 + 1] = y; n[a * 3 + 2] = z;
        n[b * 3] = x; n[b * 3 + 1] = y; n[b * 3 + 2] = z;
    }
}
