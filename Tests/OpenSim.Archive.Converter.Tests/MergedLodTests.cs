/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Converter.Merge;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The levels of detail of a merged part. The viewer figures are written out again here (llvolumemgr.cpp 36-40: tan limits 0.03, 0.06, 0.24;
/// llvovolume.cpp 1598-1611: distance times 1 - lodFactor * 0.1, times pi / 3; llcamera.h 36: 60 degrees); the measurements are made again here by
/// brute force, not with the code under test. Each test builds its own geometry and shares no state, so the class runs in parallel.
/// </summary>
public class MergedLodTests
{
    private const double Fov = Math.PI / 3, Height = 1080, Limit = 1.0;
    private static readonly double[] Tan = { 0.03, 0.06, 0.24 };

    private static double Ppm(double distance) => Height / (2 * Math.Tan(Fov / 2)) / distance;

    // ---------------------------------------------------------------- the viewer's formula

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheViewerSwitchesToALevelWhereTheAngleReachesItsLimit(int lod)
    {
        LodOptions o = new LodOptions();
        double radius = 10;
        double expected = radius * 1.0 / (Tan[lod] * (1 - 0.1) * Math.PI / 3);
        double d = ViewerLod.SwitchDistance(lod, radius, o);
        Assert.Equal(expected, d, 6);
        Assert.Equal(lod, ViewerLod.LevelAt(d * 1.001, radius, o));
        Assert.Equal(lod + 1, ViewerLod.LevelAt(d * 0.999, radius, o));
    }

    [Fact]
    public void TheMeshRadiusIsHalfTheLengthOfTheScaleVector()
    {
        Assert.Equal(0.5 * Math.Sqrt(3 * 100), ViewerLod.Radius(new Vector3(10, 10, 10)), 5);
    }

    [Fact]
    public void ThePixelScaleIsTheWindowHeightOverTheViewHeightAtTheDistance()
    {
        Assert.Equal(1080 / (2 * Math.Tan(Math.PI / 6) * 100), ViewerLod.PixelsPerMeter(100, new LodOptions()), 6);
        Assert.Equal(2 * ViewerLod.PixelsPerMeter(100, new LodOptions()), ViewerLod.PixelsPerMeter(100, new LodOptions { ScreenHeightPixels = 2160 }), 6);
    }

    // ---------------------------------------------------------------- measurements by brute force

    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
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

    /// <summary>The worst distance in metres from a vertex of the full slot to the same slot of a level, and the worst texture coordinate difference there in metres of surface.</summary>
    private static (double Position, double Uv) Measure(MergedGeometry g, List<EncoderFace> level)
    {
        double worstPosition = 0, worstUv = 0;
        for (int slot = 0; slot < g.Faces.Count; slot++)
        {
            EncoderFace high = g.Faces[slot], lod = level[slot];
            if (high.IsEmpty)
                continue;
            Vector3 P(EncoderFace f, int i) => new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]) * g.Scale;
            Vector2 T(EncoderFace f, int i) => new Vector2(f.Uvs[i * 2], f.Uvs[i * 2 + 1]);
            for (int vi = 0; vi < high.VertexCount; vi++)
            {
                Vector3 p = P(high, vi);
                double best = double.MaxValue, bestUv = 0;
                for (int t = 0; t < lod.Indices.Length; t += 3)
                {
                    int i0 = (int)lod.Indices[t], i1 = (int)lod.Indices[t + 1], i2 = (int)lod.Indices[t + 2];
                    Vector3 q = Closest(p, P(lod, i0), P(lod, i1), P(lod, i2), out float u, out float v, out float w);
                    double d = Vector3.Distance(p, q);
                    if (d >= best)
                        continue;
                    best = d;
                    Vector2 uvAt = T(lod, i0) * u + T(lod, i1) * v + T(lod, i2) * w;
                    // metres of surface for a unit of coordinate: the longest ratio of an edge's length to the length of its coordinates
                    double stretch = 0;
                    foreach ((int a, int b) in new[] { (i0, i1), (i1, i2), (i2, i0) })
                    {
                        double lu = Vector2.Distance(T(lod, a), T(lod, b));
                        if (lu > 1e-9)
                            stretch = Math.Max(stretch, Vector3.Distance(P(lod, a), P(lod, b)) / lu);
                    }
                    bestUv = Vector2.Distance(uvAt, T(high, vi)) * stretch;
                }
                worstPosition = Math.Max(worstPosition, best);
                worstUv = Math.Max(worstUv, bestUv);
            }
        }
        return (worstPosition, worstUv);
    }

    private static double Allowed(LodLevel level, MergedGeometry g)
    {
        double d = level.SwitchDistance;
        Assert.Equal(ViewerLod.SwitchDistance(level.Level, ViewerLod.Radius(g.Scale), new LodOptions()), d, 6);
        return Limit / Ppm(d);
    }

    private static int Triangles(IEnumerable<EncoderFace> faces) => faces.Sum(f => f.TriangleCount);

    private static (MergedGeometry g, List<MergeMember> m) Sphere()
    {
        Vector3 size = new Vector3(10, 10, 10);
        return LodKit.Part(size, new[] { size }, new[] { (0, LodKit.Sphere(48, 64, Vector3.Zero, 0.5f)) });
    }

    // ---------------------------------------------------------------- the levels

    [Fact]
    public void ASmoothSphereGetsThreeLevelsInTheOrderMediumLowLowestEachWithinThePixelLimitAndNoLargerThanTheOneAbove()
    {
        (MergedGeometry g, List<MergeMember> m) = Sphere();
        List<LodLevel> levels = MergedLods.Build(g, m);
        Assert.Equal(new[] { 2, 1, 0 }, levels.Select(l => l.Level));
        int above = Triangles(g.Faces);
        foreach (LodLevel level in levels)
        {
            Assert.True(level.Triangles <= above, "level " + level.Level + " has " + level.Triangles + ", above " + above);
            Assert.Equal(level.Triangles, Triangles(level.Faces));
            (double position, double uv) = Measure(g, level.Faces);
            double allowed = Allowed(level, g);
            Assert.True(position <= allowed + 1e-6, "level " + level.Level + " position " + position + " allowed " + allowed);
            Assert.True(uv <= allowed + 1e-6, "level " + level.Level + " uv " + uv + " allowed " + allowed);
            Assert.True(level.ErrorPixels <= Limit + 1e-6, "reported " + level.ErrorPixels);
            above = level.Triangles;
        }
        Assert.True(levels[0].Triangles < Triangles(g.Faces), "the medium level made no saving");
        Assert.True(levels[2].Triangles < levels[0].Triangles, "the lowest level is no smaller than the medium one");
    }

    private static string Bits(EncoderFace f, int v) => string.Join(",", new[] { f.Positions[v * 3], f.Positions[v * 3 + 1], f.Positions[v * 3 + 2], f.Normals[v * 3], f.Normals[v * 3 + 1], f.Normals[v * 3 + 2], f.Uvs[v * 2], f.Uvs[v * 2 + 1] }
        .Select(x => BitConverter.SingleToInt32Bits(x + 0f)));

    [Fact]
    public void EveryVertexOfALevelIsAVertexOfTheFullMeshInTheSameSlot()
    {
        Vector3 size = new Vector3(10, 10, 4);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(size, new[] { size },
            new[] { (0, LodKit.Sphere(24, 32, new Vector3(0, 0, 0), 0.4f)) },
            new[] { (0, LodKit.Grid(20, (x, y) => 0.1f * (float)Math.Sin(6 * x) * (float)Math.Cos(5 * y) + 0.2f, (x, y) => new Vector2(x * x, y))) });
        foreach (LodLevel level in MergedLods.Build(g, m))
            for (int slot = 0; slot < g.Faces.Count; slot++)
            {
                HashSet<string> full = Enumerable.Range(0, g.Faces[slot].VertexCount).Select(v => Bits(g.Faces[slot], v)).ToHashSet();
                for (int v = 0; v < level.Faces[slot].VertexCount; v++)
                    Assert.Contains(Bits(level.Faces[slot], v), full);
            }
    }

    [Fact]
    public void EveryTextureSeamOfTheFullMeshIsStillThereInEachLevelWithItsCoordinates()
    {
        (MergedGeometry g, List<MergeMember> m) = Sphere();
        EncoderFace high = g.Faces[0];
        // a seam: one place with more than one coordinate
        Dictionary<string, HashSet<string>> byPlace = new Dictionary<string, HashSet<string>>();
        for (int v = 0; v < high.VertexCount; v++)
        {
            string place = string.Join(",", new[] { high.Positions[v * 3], high.Positions[v * 3 + 1], high.Positions[v * 3 + 2] }.Select(x => BitConverter.SingleToInt32Bits(x + 0f)));
            if (!byPlace.TryGetValue(place, out HashSet<string> set))
                byPlace[place] = set = new HashSet<string>();
            set.Add(BitConverter.SingleToInt32Bits(high.Uvs[v * 2]) + "," + BitConverter.SingleToInt32Bits(high.Uvs[v * 2 + 1]));
        }
        Assert.Contains(byPlace.Values, s => s.Count > 1);
        foreach (LodLevel level in MergedLods.Build(g, m))
        {
            HashSet<string> kept = new HashSet<string>();
            EncoderFace f = level.Faces[0];
            for (int v = 0; v < f.VertexCount; v++)
                kept.Add(string.Join(",", new[] { f.Positions[v * 3], f.Positions[v * 3 + 1], f.Positions[v * 3 + 2] }.Select(x => BitConverter.SingleToInt32Bits(x + 0f)))
                    + "|" + BitConverter.SingleToInt32Bits(f.Uvs[v * 2]) + "," + BitConverter.SingleToInt32Bits(f.Uvs[v * 2 + 1]));
            foreach (KeyValuePair<string, HashSet<string>> place in byPlace.Where(p => p.Value.Count > 1))
                foreach (string uv in place.Value)
                    Assert.Contains(place.Key + "|" + uv, kept);
        }
    }

    [Fact]
    public void EverySlotKeepsTrianglesInEachLevelAndNoSlotTakesAnotherOnes()
    {
        Vector3 size = new Vector3(12, 12, 6);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(size, new[] { size },
            new[] { (0, LodKit.Sphere(32, 48, new Vector3(0, 0, 0), 0.45f)) },
            new[] { (0, LodKit.Grid(24, (x, y) => 0.05f * x + 0.1f)) });
        int[] full = g.Faces.Select(f => f.TriangleCount).ToArray();
        foreach (LodLevel level in MergedLods.Build(g, m))
        {
            Assert.Equal(g.Faces.Count, level.Faces.Count);
            for (int slot = 0; slot < full.Length; slot++)
                Assert.True(level.Faces[slot].TriangleCount > 0, "level " + level.Level + " slot " + slot + " has no triangles");
            // a level's slot stays inside the box of the same slot of the full mesh
            for (int slot = 0; slot < full.Length; slot++)
            {
                float[] p = g.Faces[slot].Positions, q = level.Faces[slot].Positions;
                for (int axis = 0; axis < 3; axis++)
                {
                    float lo = Enumerable.Range(0, p.Length / 3).Min(i => p[i * 3 + axis]), hi = Enumerable.Range(0, p.Length / 3).Max(i => p[i * 3 + axis]);
                    Assert.All(Enumerable.Range(0, q.Length / 3), i => Assert.InRange(q[i * 3 + axis], lo, hi));
                }
            }
        }
    }

    [Fact]
    public void ABoxCannotLoseATriangleWithoutMovingASeamSoItsLevelsAreTheFullMesh()
    {
        Vector3 size = new Vector3(8, 8, 8);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(size, new[] { size }, new[] { (0, LodKit.Box(Vector3.Zero, Vector3.One)) });
        foreach (LodLevel level in MergedLods.Build(g, m))
        {
            Assert.Equal(12, level.Triangles);
            Assert.Equal(0, level.ErrorPixels, 6);
        }
    }

    [Fact]
    public void ALevelKeepsMoreTrianglesThanTheProfileAimsForWhenAFeatureWouldShowPastThePixelLimit()
    {
        // a 3 m bump on a 20 m sheet: a twenty-seventh of the triangles would flatten it by more than a pixel at the lowest level's distance
        Vector3 size = new Vector3(20, 20, 4);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(size, new[] { size },
            new[] { (0, LodKit.Grid(32, (x, y) => 0.1f + 0.75f * (float)Math.Exp(-((x - 0.5) * (x - 0.5) + (y - 0.5) * (y - 0.5)) / (2 * 0.12 * 0.12)))) });
        List<LodLevel> levels = MergedLods.Build(g, m);
        LodLevel lowest = levels.Single(l => l.Level == 0);
        Assert.True(lowest.Triangles > lowest.TargetTriangles, "the lowest level kept " + lowest.Triangles + " against a target of " + lowest.TargetTriangles);
        foreach (LodLevel level in levels)
        {
            (double position, double uv) = Measure(g, level.Faces);
            double allowed = Allowed(level, g);
            Assert.True(position <= allowed + 1e-6, "level " + level.Level + " position " + position + " allowed " + allowed);
            Assert.True(uv <= allowed + 1e-6, "level " + level.Level + " uv " + uv + " allowed " + allowed);
        }
    }

    [Fact]
    public void ATextureCoordinateThatRunsAwayFromTheSurfaceCountsAsMuchAsAPositionThatDoes()
    {
        // a flat sheet cannot be wrong in position, but with coordinates that bend (u = x cubed times 12) a coarse level interpolates them wrongly
        Vector3 size = new Vector3(20, 20, 1);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(size, new[] { size },
            new[] { (0, LodKit.Grid(32, (x, y) => 0.5f, (x, y) => new Vector2(12 * x * x * x, y))) });
        foreach (LodLevel level in MergedLods.Build(g, m))
        {
            (double position, double uv) = Measure(g, level.Faces);
            double allowed = Allowed(level, g);
            Assert.True(position <= allowed + 1e-6, "position " + position);
            Assert.True(uv <= allowed + 1e-6, "level " + level.Level + " uv " + uv + " allowed " + allowed);
            Assert.True(level.UvErrorMeters <= allowed + 1e-6);
        }
    }

    // ---------------------------------------------------------------- small members

    private static (MergedGeometry g, List<MergeMember> m) BigAndSmall(Vector3 smallSize)
    {
        Vector3 big = new Vector3(20, 20, 20);
        Vector3 offset = new Vector3(0.1f, 0.1f, 0.1f);
        return LodKit.Part(big, new[] { big, smallSize },
            new[] { (0, LodKit.Sphere(24, 32, Vector3.Zero, 0.5f)), (1, LodKit.Sphere(8, 12, offset, 0.5f * smallSize.X / big.X)) });
    }

    [Fact]
    public void AMemberUnderThePixelLimitAtALevelsSwitchDistanceIsLeftOutOfThatLevelAndNoOther()
    {
        // 0.2 m: its box diagonal is 0.346 m; the part's radius is 17.3 m, so the lowest level starts at 612 m (0.53 px), the low level at 306 m (1.06 px)
        (MergedGeometry g, List<MergeMember> m) = BigAndSmall(new Vector3(0.2f, 0.2f, 0.2f));
        List<LodLevel> levels = MergedLods.Build(g, m);
        Assert.Equal(new[] { 0, 0, 1 }, levels.Select(l => l.DroppedMembers));
        // the small member's triangles are in the full mesh and in the medium and low levels, and not in the lowest
        bool HasSmall(EncoderFace f) => Enumerable.Range(0, f.VertexCount).Any(i => Vector3.Distance(new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]), new Vector3(0.1f)) <= 0.0051f);
        Assert.True(HasSmall(g.Faces[0]));
        Assert.True(HasSmall(levels.Single(l => l.Level == 1).Faces[0]));
        Assert.False(HasSmall(levels.Single(l => l.Level == 0).Faces[0]));
    }

    [Fact]
    public void ThePartAloneInItsObjectIsNeverLeftOutWholly()
    {
        Vector3 tiny = new Vector3(0.2f, 0.2f, 0.2f);
        (MergedGeometry g, List<MergeMember> m) = LodKit.Part(tiny, new[] { tiny }, new[] { (0, LodKit.Sphere(8, 12, Vector3.Zero, 0.5f)) });
        foreach (LodLevel level in MergedLods.Build(g, m))
        {
            Assert.Equal(0, level.DroppedMembers);
            Assert.True(level.Triangles > 0);
        }
    }

    [Fact]
    public void AMemberThatIsLeftOutIsNotCountedInTheError()
    {
        (MergedGeometry g, List<MergeMember> m) = BigAndSmall(new Vector3(0.2f, 0.2f, 0.2f));
        LodLevel lowest = MergedLods.Build(g, m).Single(l => l.Level == 0);
        Assert.True(lowest.ErrorPixels <= Limit + 1e-6, "reported " + lowest.ErrorPixels);
    }
}
