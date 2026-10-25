/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>The geometry cleaner: duplicate and loose vertices and zero-area triangles go; texture coordinates, faces and everything else stay.</summary>
public class GeometryCleanerTests
{
    private sealed class Soup
    {
        public List<float> P = new List<float>(), N = new List<float>(), T = new List<float>();
        public int Triangles => P.Count / 9;

        public Soup Tri((float x, float y, float z, float u, float v) a, (float x, float y, float z, float u, float v) b, (float x, float y, float z, float u, float v) c,
            float nz = 1f)
        {
            foreach (var q in new[] { a, b, c })
            {
                P.Add(q.x); P.Add(q.y); P.Add(q.z);
                N.Add(0); N.Add(0); N.Add(nz);
                T.Add(q.u); T.Add(q.v);
            }
            return this;
        }
    }

    private static (float x, float y, float z, float u, float v) V(float x, float y, float z, float u, float v) => (x, y, z, u, v);

    /// <summary>A flat n x n grid of quads (two triangles each), the corners listed for every triangle as an exporter lists them.</summary>
    private static Soup Grid(int n)
    {
        Soup s = new Soup();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var a = V(i, j, 0, i / (float)n, j / (float)n);
                var b = V(i + 1, j, 0, (i + 1) / (float)n, j / (float)n);
                var c = V(i + 1, j + 1, 0, (i + 1) / (float)n, (j + 1) / (float)n);
                var d = V(i, j + 1, 0, i / (float)n, (j + 1) / (float)n);
                s.Tri(a, b, c).Tri(a, c, d);
            }
        return s;
    }

    private static CleanedFace Clean(Soup s) => GeometryCleaner.Clean(s.P.ToArray(), s.N.ToArray(), s.T.ToArray());

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);

    /// <summary>
    /// Every triangle of the cleaned face must be one of the soup's triangles, in the soup's order, with its corners' texture
    /// coordinates bit for bit, positions within 1e-6 and normals within 1e-4. Returns how many of the soup's triangles were left out.
    /// </summary>
    private static int AssertSurvivorsMatch(Soup before, CleanedFace after)
    {
        int next = 0, left = 0;
        for (int t = 0; t < after.TriangleCount; t++)
        {
            bool found = false;
            while (next < before.Triangles && !found)
            {
                bool all = true;
                for (int c = 0; c < 3 && all; c++)
                {
                    int bi = next * 3 + c, ai = after.Indices[t * 3 + c];
                    all = Bits(before.T[bi * 2]) == Bits(after.Uvs[ai * 2]) && Bits(before.T[bi * 2 + 1]) == Bits(after.Uvs[ai * 2 + 1]);
                    for (int k = 0; k < 3 && all; k++)
                        all = Math.Abs(before.P[bi * 3 + k] - after.Positions[ai * 3 + k]) <= 1e-6f && Math.Abs(before.N[bi * 3 + k] - after.Normals[ai * 3 + k]) <= 1e-4f;
                }
                if (all)
                    found = true;
                else
                    left++;
                next++;
            }
            Assert.True(found, "triangle " + t + " of the cleaned face matches no triangle of the original");
        }
        return left + (before.Triangles - next);
    }

    [Fact]
    public void DuplicateVerticesAreWeldedAndEverySurvivingCornerKeepsItsValues()
    {
        Soup s = Grid(4);
        // 2 x 16 triangles; a grid of 5 x 5 points
        CleanedFace c = Clean(s);
        Assert.False(c.KeptAsWas);
        Assert.Equal(25, c.VertexCount);
        Assert.Equal(32, c.TriangleCount);
        Assert.Equal(0, AssertSurvivorsMatch(s, c));
    }

    [Fact]
    public void VerticesWithinTheTolerancesAndTheSameTextureCoordinateAreWeldedAndOnesBeyondThemAreNot()
    {
        // two triangles share the corner at the origin: one copy is 5e-7 m off with a normal 5e-5 off, one is 2e-6 m off, one has a normal 2e-4 off
        (float, float, float, float, float) o = V(0, 0, 0, 0.5f, 0.5f);
        Soup s = new Soup().Tri(o, V(1, 0, 0, 1, 0), V(0, 1, 0, 0, 1));
        s.Tri(V(5e-7f, 0, 0, 0.5f, 0.5f), V(-1, 0, 0, 0, 0), V(0, -1, 0, 0, 0.25f));
        s.N[s.N.Count - 9 + 2] = 1f - 5e-5f; // the near copy's normal
        s.Tri(V(2e-6f, 0, 0, 0.5f, 0.5f), V(0, 0, 1, 0, 0), V(1, 1, 1, 1, 1));
        s.Tri(V(0, 0, 0, 0.5f, 0.5f), V(0, 0, -1, 0, 0), V(-1, -1, -1, 1, 1), nz: 0.9998f);
        CleanedFace c = Clean(s);
        // the origin corner appears four times: the 5e-7 copy joins the first, the 2e-6 copy and the normal 2e-4 copy do not
        Assert.Equal(12 - 1, c.VertexCount);
        Assert.Equal(4, c.TriangleCount);
        Assert.Equal(0, AssertSurvivorsMatch(s, c));
    }

    [Fact]
    public void ASeamIsNeverWeldedBecauseTwoVerticesThatShareAPositionKeepTheirOwnTextureCoordinates()
    {
        // the same point twice with texture coordinates one unit apart, and twice with coordinates one float step apart
        float u2 = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(0.25f) + 1);
        Soup s = new Soup().Tri(V(0, 0, 0, 0f, 0f), V(1, 0, 0, 0.5f, 0f), V(0, 1, 0, 0f, 0.5f))
                           .Tri(V(0, 0, 0, 1f, 0f), V(-1, 0, 0, 0.5f, 0f), V(0, -1, 0, 1f, 0.5f))
                           .Tri(V(0, 0, 0, 0.25f, 0.25f), V(0, 0, 1, 0.5f, 0.5f), V(1, 1, 1, 0.75f, 0.75f))
                           .Tri(V(0, 0, 0, u2, 0.25f), V(0, 0, -1, 0.5f, 0.5f), V(-1, -1, -1, 0.75f, 0.75f));
        CleanedFace c = Clean(s);
        int atOrigin = 0;
        for (int i = 0; i < c.VertexCount; i++)
            if (c.Positions[i * 3] == 0 && c.Positions[i * 3 + 1] == 0 && c.Positions[i * 3 + 2] == 0)
                atOrigin++;
        Assert.Equal(4, atOrigin);
        Assert.Equal(4, c.TriangleCount);
        Assert.Equal(0, AssertSurvivorsMatch(s, c));
    }

    [Fact]
    public void OnlyZeroAreaTrianglesAreRemoved()
    {
        Soup s = Grid(2);
        // a triangle with two corners at one point, one whose area is 1e-13 m2, and a thin one of 1e-9 m2 that stays
        s.Tri(V(0, 0, 0, 0, 0), V(0, 0, 0, 0, 0), V(1, 1, 0, 1, 1));
        s.Tri(V(0, 0, 0, 0.3f, 0.3f), V(2e-7f, 0, 0, 0.4f, 0.3f), V(0, 5e-7f, 0, 0.3f, 0.4f));
        s.Tri(V(0, 0, 0, 0.6f, 0.6f), V(1, 0, 0, 0.7f, 0.6f), V(0, 2e-9f, 0, 0.6f, 0.7f));
        CleanedFace c = Clean(s);
        Assert.Equal(8 + 1, c.TriangleCount); // 8 from the grid, the thin one; the two collapsed ones are gone
        Assert.Equal(2, AssertSurvivorsMatch(s, c));
    }

    [Fact]
    public void VerticesThatNoTriangleUsesAreRemoved()
    {
        float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 9, 9, 9, 1, 1, 0 };
        float[] nor = { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        float[] uv = { 0, 0, 1, 0, 0, 1, 0.5f, 0.5f, 1, 1 };
        CleanedFace c = GeometryCleaner.Clean(pos, nor, uv, new[] { 0, 1, 2, 2, 1, 4 });
        Assert.Equal(4, c.VertexCount);
        Assert.Equal(2, c.TriangleCount);
        Assert.DoesNotContain(9f, c.Positions);
    }

    [Fact]
    public void AFaceWhoseTrianglesAreAllDegenerateIsKeptAsItWas()
    {
        Soup s = new Soup().Tri(V(0, 0, 0, 0, 0), V(0, 0, 0, 0.5f, 0), V(1, 0, 0, 1, 0)).Tri(V(2, 2, 2, 0, 0), V(2, 2, 2, 1, 0), V(2, 2, 2, 0, 1));
        CleanedFace c = Clean(s);
        Assert.True(c.KeptAsWas);
        Assert.Equal(s.P, c.Positions.ToList());
        Assert.Equal(s.N, c.Normals.ToList());
        Assert.Equal(s.T, c.Uvs.ToList());
        Assert.Equal(2, c.TriangleCount);
    }

    [Fact]
    public void TheSameInputGivesTheSameOutputEveryTime()
    {
        Soup s = Grid(6);
        s.Tri(V(0, 0, 0, 0, 0), V(0, 0, 0, 0, 0), V(1, 1, 0, 1, 1));
        CleanedFace a = Clean(s), b = Clean(s);
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Normals, b.Normals);
        Assert.Equal(a.Uvs, b.Uvs);
        Assert.Equal(a.Indices, b.Indices);
    }
}
