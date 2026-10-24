/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

public class PhysicsBlockTests
{
    static float Support(IEnumerable<Vector3> pts, Vector3 d) => pts.Max(p => Vector3.Dot(p, d));

    [Fact]
    public void HullOfACubeWithPointsInsideIsTheEightCorners()
    {
        var pts = new List<Vector3>();
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++) pts.Add(new Vector3(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f) * 0.9f);
        foreach (var x in new[] { -0.5f, 0.5f }) foreach (var y in new[] { -0.5f, 0.5f }) foreach (var z in new[] { -0.5f, 0.5f }) pts.Add(new Vector3(x, y, z));
        var hull = ConvexHull.Compute(pts, 256, out bool box);
        Assert.False(box);
        Assert.Equal(8, hull.Count);
    }

    [Fact]
    public void HullSupportsTheSamePointsInEveryDirection()
    {
        var m = LodGeneratorTests.Sphere();
        var pts = m.High!.Faces.SelectMany(f => f.Positions).ToList();
        var hull = ConvexHull.Compute(pts, 256, out _);
        Assert.InRange(hull.Count, 4, 256);
        var rnd = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            var d = Vector3.Normalize(new Vector3(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f));
            Assert.Equal(Support(pts, d), Support(hull, d), 0.03); // capped at 256 points: it may fall short of the true hull by a few percent of the radius
        }
    }

    [Fact]
    public void MoreThan256HullVerticesAreCoarsenedToFit()
    {
        var rnd = new Random(11);
        var pts = Enumerable.Range(0, 5000).Select(_ => Vector3.Normalize(new Vector3(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f)) * 0.5f).ToList();
        var hull = ConvexHull.Compute(pts, 256, out bool box);
        Assert.False(box);
        Assert.InRange(hull.Count, 20, 256);
        foreach (var p in hull) Assert.InRange(p.Length(), 0.499f, 0.501f); // each is one of the real points
    }

    [Fact]
    public void FlatPointsGiveTheBoundingBoxCorners()
    {
        var pts = new List<Vector3> { new(0, 0, 1), new(2, 0, 1), new(0, 3, 1), new(2, 3, 1), new(1, 1, 1) };
        var hull = ConvexHull.Compute(pts, 256, out bool box);
        Assert.True(box);
        Assert.Equal(8, hull.Count);
        Assert.Contains(new Vector3(0, 0, 1), hull);
        Assert.Contains(new Vector3(2, 3, 1), hull);
    }

    [Fact]
    public void TheBlockHasMinMaxAndBoundingVertsOnly_AndSurvivesEncodeAndDecode()
    {
        var m = LodGeneratorTests.Sphere(weights: false);
        LodGenerator.GenerateAll(m);
        Assert.Null(m.PhysicsConvex);
        var r = PhysicsBlock.AddBoundingHull(m);
        Assert.Equal("low", r.SourceLevel);
        Assert.Equal(m.Low!.Faces.Sum(f => f.VertexCount), r.SourceVertices);
        Assert.InRange(r.HullVertices, 4, 256);

        var bytes = MeshCodec.Encode(m);
        var header = (IDictionary<string, object?>)LlsdBinary.Read(bytes, out _)!;
        Assert.True(header.ContainsKey("physics_convex"));
        Assert.False(header.ContainsKey("physics_mesh"));

        var back = MeshCodec.Decode(bytes);
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(back.PhysicsConvex);
        Assert.Equal(new[] { "BoundingVerts", "Max", "Min" }, map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(r.HullVertices * 6, ((byte[])map["BoundingVerts"]!).Length);

        // the points come back to within one 16-bit step of the box
        var read = PhysicsBlock.ReadBoundingVerts(back.PhysicsConvex);
        var hull = ConvexHull.Compute(m.Low.Faces.SelectMany(f => f.Positions).ToList(), 256, out _);
        Assert.Equal(hull.Count, read.Count);
        foreach (var p in hull) Assert.Contains(read, q => Vector3.Distance(p, q) < 1e-4f);
    }

    [Fact]
    public void TheHullComesFromTheLowLevelNotTheHighOne()
    {
        var m = LodGeneratorTests.Sphere(weights: false);
        m.Low = new MeshLod();
        m.Low.Faces.Add(new MeshFace { Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) }, Indices = new[] { 0, 1, 2, 0, 1, 3 } });
        PhysicsBlock.AddBoundingHull(m);
        var read = PhysicsBlock.ReadBoundingVerts(m.PhysicsConvex);
        Assert.Equal(4, read.Count);
    }
}
