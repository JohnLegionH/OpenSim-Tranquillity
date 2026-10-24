/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using Xunit;
using MesherPath = OpenSim.Region.PhysicsModules.Meshing.PrimMesher.Path;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// The Y shear of a circular path, against the Second Life viewer's rule (LLPath::genNGon in indra/llmath/llvolume.cpp,
/// lines 1296-1299, 1331-1334 and 1358-1361): a path point is
/// (shearX * s + skew term, cos(a) * r + shearY * s, s) with s = sin(a) * r, a = 2 pi * revolutions * t and r the radius at t
/// (0.5 * (1 - hole size Y), moved by the radius offset). The shear moves the point; it does not change the angle a, the
/// profile's rotation, or the texture coordinate (mTexT = t, lines 1305, 1339, 1365). The mesher used to turn the angle by the
/// shear, so the path swung round and v ran from -0.029 to 0.971 at shear 0.2.
/// </summary>
public class ViewerYShearPathTests
{
    private const float Tolerance = 2e-4f;

    private static PrimParams Shape(string shape, float shearX, float shearY, float revolutions, float begin, float end, float hole = 0.25f, float offset = 0f, float hollow = 0f)
    {
        PrimParams p = new PrimParams
        {
            ProfileEnd = 1f, PathBegin = begin, PathEnd = end, PathScaleX = 1f, PathScaleY = hole, PathRevolutions = revolutions,
            PathCurve = 32, PathShearX = shearX, PathShearY = shearY, PathRadiusOffset = offset, ProfileHollow = hollow
        };
        switch (shape)
        {
            case "torus": p.ProfileCurve = 0; break;
            case "tube": p.ProfileCurve = 1; break;
            case "ring": p.ProfileCurve = 3; break;
            case "sphere": p.ProfileCurve = 5; p.PathScaleY = 1f; break;
            default: throw new ArgumentException(shape);
        }
        return p;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "sphere", "torus", "tube", "ring" })
            foreach (float shearY in new[] { -0.5f, -0.2f, 0.1f, 0.2f, 0.5f })
                foreach ((float rev, float begin, float end) in new[] { (1f, 0f, 1f), (1f, 0.1f, 0.8f), (1.5f, 0f, 1f) })
                    yield return new object[] { shape, shearY, rev, begin, end };
    }

    // the viewer's radius at path parameter t: start and end radii as in LLPath::genNGon (1257-1269), lerp at 1293
    private static float Radius(float hole, float offset, float t)
    {
        float start = 0.5f * (1f - hole), end = start;
        if (offset < 0f) start *= 1f + offset; else end *= 1f - offset;
        return start + (end - start) * t;
    }

    private static float PathZ(float hole, float offset, float revolutions, float t)
        => MathF.Sin(2f * MathF.PI * revolutions * t) * Radius(hole, offset, t);

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheShearMovesEveryVertexAlongYByShearTimesThePathPointsZAndChangesNothingElse(string shape, float shearY, float rev, float begin, float end)
    {
        PrimParams plainParams = Shape(shape, 0.3f, 0f, rev, begin, end);
        PrimParams shearedParams = Shape(shape, 0.3f, shearY, rev, begin, end);
        float hole = plainParams.PathScaleY;

        List<PrimFace> plain = PrimGeometry.BuildFaces(plainParams);
        List<PrimFace> sheared = PrimGeometry.BuildFaces(shearedParams);

        // same faces, same triangles: the shear does not change the angle, so the path has the same points
        Assert.Equal(plain.Select(f => f.Number), sheared.Select(f => f.Number));
        for (int f = 0; f < plain.Count; f++)
        {
            Assert.Equal(plain[f].Positions.Length, sheared[f].Positions.Length);

            // texture coordinates are the profile position and the path parameter, neither of which the shear touches
            for (int i = 0; i < plain[f].Uvs.Length; i++)
                Assert.Equal(plain[f].Uvs[i], sheared[f].Uvs[i], Tolerance);

            for (int vtx = 0; vtx < plain[f].Positions.Length / 3; vtx++)
            {
                // the path parameter of the vertex: the end caps sit at the cut's end and begin; a side has v = 1 - t in the
                // exporter's glTF convention (the viewer's v is 1 minus this)
                float t = plain[f].Kind switch
                {
                    PrimFaceKind.Top => end,
                    PrimFaceKind.Bottom => begin,
                    _ => 1f - plain[f].Uvs[vtx * 2 + 1]
                };
                float expectedDy = shearY * PathZ(hole, 0f, rev, t);

                Assert.Equal(plain[f].Positions[vtx * 3], sheared[f].Positions[vtx * 3], Tolerance);
                Assert.Equal(plain[f].Positions[vtx * 3 + 1] + expectedDy, sheared[f].Positions[vtx * 3 + 1], Tolerance);
                Assert.Equal(plain[f].Positions[vtx * 3 + 2], sheared[f].Positions[vtx * 3 + 2], Tolerance);
            }
        }
    }

    [Theory]
    [InlineData("torus", 0.2f, 1f)]
    [InlineData("torus", -0.4f, 1f)]
    [InlineData("tube", 0.2f, 1f)]
    [InlineData("tube", 0.5f, 1.5f)]
    public void EachRingIsCentredOnThePathPointTheViewerGives(string shape, float shearY, float rev)
    {
        const float shearX = 0.3f;
        PrimParams p = Shape(shape, shearX, shearY, rev, 0f, 1f);
        float hole = p.PathScaleY;

        // the profile of a torus (24 sides) and of a tube (4) is centred on its path point, so the mean of a ring's distinct
        // vertices is the path point; a ring is the vertices that share a texture v
        List<PrimFace> faces = PrimGeometry.BuildFaces(p);
        float[] positions = faces.Where(f => f.Kind == PrimFaceKind.Outer).SelectMany(f => f.Positions).ToArray();
        float[] uvs = faces.Where(f => f.Kind == PrimFaceKind.Outer).SelectMany(f => f.Uvs).ToArray();
        var rings = new SortedDictionary<int, HashSet<(int, int, int)>>();
        var sums = new Dictionary<int, float[]>();
        for (int vtx = 0; vtx < positions.Length / 3; vtx++)
        {
            int key = (int)MathF.Round(uvs[vtx * 2 + 1] * 10000f);
            (int, int, int) at = ((int)MathF.Round(positions[vtx * 3] * 10000f), (int)MathF.Round(positions[vtx * 3 + 1] * 10000f), (int)MathF.Round(positions[vtx * 3 + 2] * 10000f));
            if (!rings.TryGetValue(key, out HashSet<(int, int, int)> set))
            {
                rings[key] = set = new HashSet<(int, int, int)>();
                sums[key] = new float[3];
            }
            if (set.Add(at))
            {
                sums[key][0] += positions[vtx * 3];
                sums[key][1] += positions[vtx * 3 + 1];
                sums[key][2] += positions[vtx * 3 + 2];
            }
        }

        Assert.True(rings.Count >= 24);
        foreach (var ring in rings)
        {
            float t = 1f - ring.Key / 10000f;
            float r = Radius(hole, 0f, t);
            float a = 2f * MathF.PI * rev * t;
            float s = MathF.Sin(a) * r;
            float n = ring.Value.Count;
            Assert.Equal(shearX * s, sums[ring.Key][0] / n, 1e-3f);
            Assert.Equal(MathF.Cos(a) * r + shearY * s, sums[ring.Key][1] / n, 1e-3f);
            Assert.Equal(s, sums[ring.Key][2] / n, 1e-3f);
        }
    }

    [Fact]
    public void TheShearIsAppliedToTheRadiusOffsetAndHollowPathsTheSameWay()
    {
        PrimParams plain = Shape("torus", 0f, 0f, 1f, 0f, 1f, 0.5f, 0.3f, 0.4f);
        PrimParams sheared = Shape("torus", 0f, 0.3f, 1f, 0f, 1f, 0.5f, 0.3f, 0.4f);
        List<PrimFace> a = PrimGeometry.BuildFaces(plain), b = PrimGeometry.BuildFaces(sheared);
        Assert.Equal(a.Select(f => f.Number), b.Select(f => f.Number));
        for (int f = 0; f < a.Count; f++)
            for (int vtx = 0; vtx < a[f].Positions.Length / 3; vtx++)
            {
                float t = a[f].Kind switch { PrimFaceKind.Top => 1f, PrimFaceKind.Bottom => 0f, _ => 1f - a[f].Uvs[vtx * 2 + 1] };
                Assert.Equal(a[f].Positions[vtx * 3 + 1] + 0.3f * PathZ(0.5f, 0.3f, 1f, t), b[f].Positions[vtx * 3 + 1], Tolerance);
            }
    }

    // ------------------------------------------------------------ physics meshing keeps its earlier rule

    [Theory]
    [InlineData(0.2f)]
    [InlineData(-0.4f)]
    public void WithoutViewerModeTheShearStillTurnsTheAngleAsBefore(float shearY)
    {
        const float hole = 0.25f;
        MesherPath path = new MesherPath { holeSizeX = 1f, holeSizeY = hole, topShearY = shearY, pathCutBegin = 0f, pathCutEnd = 1f, revolutions = 1f, stepsPerRevolution = 24 };
        path.Create(PathType.Circular, 24);

        // the rule physics meshing has always used: both end angles are moved by 0.9 * shear, and the point and the profile's
        // rotation by the whole shear
        float startAngle = -shearY * 0.9f;
        PathNode first = path.pathNodes[0];
        float radius = 0.5f - hole * 0.5f;
        Assert.Equal((1f + MathF.Abs(shearY) * 0.25f) * MathF.Cos(startAngle) * radius, first.position.Y, 1e-5f);
        Assert.Equal(MathF.Sin(startAngle + shearY) * radius, first.position.Z, 1e-5f);
        Assert.Equal(startAngle / (2f * MathF.PI), first.percentOfPath, 1e-5f);

        // and the same input in viewer mode starts at the path's own start, at (0, r, 0) with the viewer's angle of 0
        MesherPath viewer = new MesherPath { holeSizeX = 1f, holeSizeY = hole, topShearY = shearY, pathCutBegin = 0f, pathCutEnd = 1f, revolutions = 1f, stepsPerRevolution = 24, viewerMode = true };
        viewer.Create(PathType.Circular, 24);
        Assert.Equal(0f, viewer.pathNodes[0].percentOfPath, 1e-6f);
        Assert.Equal(radius, viewer.pathNodes[0].position.Y, 1e-5f);
        Assert.Equal(0f, viewer.pathNodes[0].position.Z, 1e-6f);
    }
}
