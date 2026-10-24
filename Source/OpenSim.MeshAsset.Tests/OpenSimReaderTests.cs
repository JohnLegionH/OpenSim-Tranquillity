/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// OpenSim's own mesh reader (the physics mesher, which falls back to a display level of detail when an asset has
/// no physics mesh), run on the in-tree Meshmerizer without a scene. The reading method is private, so it is
/// invoked by reflection; its arguments and results are the real types. It reads positions and triangles only,
/// so skin and weights are not covered here.
/// </summary>
public class OpenSimReaderTests
{
    static (List<Vector3> Coords, List<(int, int, int)> Faces, List<Vector3> Bounding) Read(byte[] asset, Vector3 size)
    {
        var mesher = new Meshmerizer();
        var method = typeof(Meshmerizer).GetMethod("GenerateCoordsAndFacesFromPrimMeshData", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var shape = new PrimitiveBaseShape { SculptData = asset };
        var args = new object?[] { "test", shape, new OpenMetaverse.Vector3(size.X, size.Y, size.Z), null, null, null, null };
        Assert.True((bool)method.Invoke(mesher, args)!, "OpenSim's reader returned false");

        var coords = ((List<Coord>)args[3]!).Select(c => new Vector3(c.X, c.Y, c.Z)).ToList();
        var faces = ((List<Face>)args[4]!).Select(f => (f.v1, f.v2, f.v3)).ToList();
        var bounding = ((List<OpenMetaverse.Vector3>?)args[6] ?? new()).Select(v => new Vector3(v.X, v.Y, v.Z)).ToList();
        return (coords, faces, bounding);
    }

    [Fact]
    public void ReadsOurHighOnlyAssetPositionsAndTriangles()
    {
        var f = new MeshFace
        {
            Positions = new[] { new Vector3(-0.5f, -0.25f, 0), new Vector3(0.5f, -0.5f, 0.25f), new Vector3(0, 0.5f, 0.5f), new Vector3(0.25f, 0.25f, -0.5f) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Uvs = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One },
            Indices = new[] { 0, 1, 2, 2, 1, 3 },
        };
        var m = new MeshAssetModel { High = new MeshLod() };
        m.High.Faces.Add(f);
        var (coords, faces, _) = Read(MeshCodec.Encode(m), new Vector3(2, 3, 4));
        Assert.Equal(4, coords.Count);
        for (int i = 0; i < 4; i++)
            Assert.True(Vector3.Distance(coords[i], f.Positions[i] * new Vector3(2, 3, 4)) < 1e-3f, $"coord {i}: {coords[i]}");
        Assert.Equal(new[] { (0, 1, 2), (2, 1, 3) }, faces.ToArray());
    }

    [CorpusFact]
    public void ReadsARealAssetAndOurReEncodeAlike()
    {
        var file = Refs.CorpusFiles().First(p => new FileInfo(p).Length is > 100_000 and < 400_000 && MeshCodec.Decode(File.ReadAllBytes(p)).Skin != null);
        var original = File.ReadAllBytes(file);
        var a = Read(original, Vector3.One);
        var b = Read(MeshCodec.Encode(MeshCodec.Decode(original)), Vector3.One);
        Assert.Equal(a.Coords, b.Coords);
        Assert.Equal(a.Faces, b.Faces);
    }

    [Fact]
    public void ReadsTheBoundingHullWeWrite_AndStillMeshesTheDisplayLevel()
    {
        var m = LodGeneratorTests.Sphere(weights: false);
        LodGenerator.GenerateAll(m);
        var result = PhysicsBlock.AddBoundingHull(m);
        var (coords, faces, bounding) = Read(MeshCodec.Encode(m), new Vector3(1, 1, 1));
        Assert.NotEmpty(coords); // OpenSim has no physics_mesh to use here, so it meshes the medium level
        Assert.Equal(m.Medium!.Faces.Sum(f => f.TriangleCount), faces.Count);
        Assert.Equal(result.HullVertices, bounding.Count);
        var expected = PhysicsBlock.ReadBoundingVerts(m.PhysicsConvex);
        for (int i = 0; i < bounding.Count; i++)
            Assert.True(Vector3.Distance(bounding[i], expected[i]) < 1e-4f, $"bounding vertex {i}");
    }
}
