/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Numerics;
using OpenSim.Archive.Converter;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>A mesh of 175 000 triangles must be rebuilt in well under a minute.</summary>
public class LargeMeshTests
{
    /// <summary>Points on a sphere, in a fixed order, as the vertices of a smooth shell are.</summary>
    private static List<Vector3> Sphere(int count)
    {
        List<Vector3> points = new List<Vector3>(count);
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < count; i++)
        {
            double y = 1 - 2.0 * (i + 0.5) / count, r = Math.Sqrt(1 - y * y), a = i * golden;
            points.Add(new Vector3((float)(Math.Cos(a) * r * 0.5), (float)(y * 0.5), (float)(Math.Sin(a) * r * 0.5)));
        }
        return points;
    }

    private static T Within<T>(TimeSpan limit, Func<T> work, out TimeSpan took)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Task<T> task = Task.Run(work);
        bool done = task.Wait(limit);
        took = clock.Elapsed;
        Assert.True(done, "not finished after " + limit);
        return task.Result;
    }

    [Fact]
    public void TheBoundingHullOfAThickPointShellIsFoundQuicklyAndFitsTheLimit()
    {
        List<Vector3> points = Sphere(40000);
        List<Vector3> hull = Within(TimeSpan.FromSeconds(20), () => ConvexHull.Compute(points, PhysicsBlock.MaxHullVertices, out _), out TimeSpan took);
        Assert.InRange(hull.Count, 8, PhysicsBlock.MaxHullVertices);
        Assert.All(hull, p => Assert.True(p.Length() <= 0.5001f));
        Assert.True(took < TimeSpan.FromSeconds(20), took.ToString());
    }

    /// <summary>A disc made of thousands of points that are flat to within a few millionths of its width, as a water surface or a leaf is.</summary>
    private static List<Vector3> NearlyFlatDisc(int count, float thickness)
    {
        Random random = new Random(12345);
        List<Vector3> points = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
        {
            double r = Math.Sqrt(random.NextDouble()) * 0.5, a = random.NextDouble() * Math.PI * 2;
            points.Add(new Vector3((float)(Math.Cos(a) * r), (float)((random.NextDouble() - 0.5) * thickness), (float)(Math.Sin(a) * r)));
        }
        return points;
    }

    [Theory]
    [InlineData(3e-6f)]
    [InlineData(2e-5f)]
    [InlineData(2e-4f)]
    public void TheBoundingHullOfANearlyFlatPointSetIsFoundQuicklyOrFallsBackToItsBox(float thickness)
    {
        List<Vector3> points = NearlyFlatDisc(9000, thickness);
        List<Vector3> hull = Within(TimeSpan.FromSeconds(20), () => ConvexHull.Compute(points, PhysicsBlock.MaxHullVertices, out _), out TimeSpan took);
        Assert.InRange(hull.Count, 4, PhysicsBlock.MaxHullVertices);
        Assert.True(took < TimeSpan.FromSeconds(20), took.ToString());
    }

    [Fact]
    public void AMeshOf175000TrianglesIsEncodedInUnderThirtySeconds()
    {
        // a sphere of 300 rings and 300 segments: 180 000 triangles, 90 601 vertices before the split at 65 532
        int n = 300, w = n + 1;
        float[] pos = new float[w * w * 3], nor = new float[w * w * 3], uv = new float[w * w * 2];
        for (int r = 0; r < w; r++)
            for (int s = 0; s < w; s++)
            {
                double theta = Math.PI * r / n, phi = 2 * Math.PI * s / n;
                Vector3 p = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta));
                int v = r * w + s;
                pos[v * 3] = p.X * 0.5f; pos[v * 3 + 1] = p.Y * 0.5f; pos[v * 3 + 2] = p.Z * 0.5f;
                nor[v * 3] = p.X; nor[v * 3 + 1] = p.Y; nor[v * 3 + 2] = p.Z;
                uv[v * 2] = s / (float)n; uv[v * 2 + 1] = r / (float)n;
            }
        uint[] idx = new uint[n * n * 6];
        int k = 0;
        for (int r = 0; r < n; r++)
            for (int s = 0; s < n; s++)
            {
                uint a = (uint)(r * w + s), b = a + 1, c = a + (uint)w, d = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b; idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        // the viewer's uploader cuts such a face in two before it is encoded
        List<EncoderFace> faces = FaceBuilder.ToMeshFaces(new GltfExport.PrimFace
        {
            Number = 0, Kind = GltfExport.PrimFaceKind.Outer, MesherNumber = 0,
            Positions = idx.SelectMany(i => new[] { pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] }).ToArray(),
            Normals = idx.SelectMany(i => new[] { nor[i * 3], nor[i * 3 + 1], nor[i * 3 + 2] }).ToArray(),
            Uvs = idx.SelectMany(i => new[] { uv[i * 2], uv[i * 2 + 1] }).ToArray()
        });
        Assert.Equal(2, faces.Count);

        byte[] asset = Within(TimeSpan.FromSeconds(30), () => new MeshAssetEncoder().Encode(faces, new EncodeOptions()), out TimeSpan took);
        Assert.True(took < TimeSpan.FromSeconds(30), took.ToString());
        MeshAssetModel back = MeshCodec.Decode(asset);
        Assert.Equal(2, back.High.Faces.Count);
        Assert.Equal(180000, back.High.Faces.Sum(f => f.Indices.Length / 3));
    }
}
