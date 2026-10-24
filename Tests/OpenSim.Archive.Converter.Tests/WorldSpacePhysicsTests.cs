/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// A converted part must collide where the prim did, in the world: the region cooks a static prim's collision shape from
/// <c>Meshmerizer.CreateMesh(name, shape, size, 32, false, ...)</c> and a static mesh part's from the same call with the mesh flag
/// set, and places either at the part's position and rotation. These tests take both meshes at the part's real scale, put both
/// at the same rotation and position, and compare the world-space bounding boxes and every triangle within a millimetre. The local
/// space comparison in <see cref="PhysicsModelTests"/> would not notice a shape that is right in its own frame and wrong once
/// the scale, rotation and offset of a cut shape are applied.
/// </summary>
public class WorldSpacePhysicsTests
{
    private readonly ITestOutputHelper _out;

    public WorldSpacePhysicsTests(ITestOutputHelper output) { _out = output; }

    private const float OneMillimetre = 0.001f;

    // a ring stood on its edge with its hole along x, 2 m up, in the middle of a region (the course's rotation)
    private static readonly Quaternion OnEdge = new Quaternion(-0.5f, -0.5f, 0.5f, 0.5f);
    private static readonly Vector3 Place = new Vector3(110f, 130f, 25f);

    private static ShapeData Cut(string shape, Action<PrimParams> change)
    {
        PrimParams p = FaceMatrix.Make(shape, "plain");
        change(p);
        return FaceMatrix.Stored(p);
    }

    public static IEnumerable<object[]> Shapes()
    {
        // label, shape, scale; the shapes are made in Cut() below by label
        foreach (string label in new[]
        {
            "half torus", "full torus", "cut box", "cut cylinder", "cut tube", "dimpled sphere", "half ring",
            "quarter torus", "thin box", "hollow box", "hollow cylinder", "prism", "tapered box", "twisted torus"
        })
            yield return new object[] { label };
    }

    private static (ShapeData shape, string scale) Make(string label) => label switch
    {
        "half torus" => (Cut("torus", p => { p.PathBegin = 0.25f; p.PathEnd = 0.75f; }), "7,7,0.3"),
        "full torus" => (Cut("torus", p => { }), "6,6,0.3"),
        "cut box" => (Cut("box", p => { p.PathBegin = 0.2f; p.PathEnd = 0.8f; }), "4,2,3"),
        "cut cylinder" => (Cut("cylinder", p => { p.PathBegin = 0.25f; p.PathEnd = 0.75f; }), "3,3,5"),
        "cut tube" => (Cut("tube", p => { p.PathBegin = 0.25f; p.PathEnd = 0.75f; }), "6,6,0.5"),
        "dimpled sphere" => (Cut("sphere", p => { p.ProfileBegin = 0.2f; p.ProfileEnd = 0.8f; }), "3,3,3"),
        "half ring" => (Cut("ring", p => { p.PathBegin = 0.25f; p.PathEnd = 0.75f; }), "7,7,0.3"),
        "quarter torus" => (Cut("torus", p => { p.PathBegin = 0f; p.PathEnd = 0.25f; }), "5,5,0.4"),
        "thin box" => (Cut("box", p => { }), "7,7,0.3"),
        "hollow box" => (Cut("box", p => p.ProfileHollow = 0.4f), "7,2,0.3"),
        "hollow cylinder" => (Cut("cylinder", p => p.ProfileHollow = 0.4f), "3,3,8"),
        "prism" => (Cut("prism", p => { }), "1.87,5.77,4"),
        "tapered box" => (Cut("box", p => { p.PathTaperX = 0.3f; p.PathScaleX = 0.7f; }), "7,3,0.3"),
        "twisted torus" => (Cut("torus", p => p.PathTwist = 0.25f), "7,5,0.3"),
        _ => throw new ArgumentException(label)
    };

    private static float[] ToWorld(float[] positions, Quaternion q, Vector3 at)
    {
        float[] w = new float[positions.Length];
        for (int i = 0; i < positions.Length; i += 3)
        {
            Vector3 v = new Vector3(positions[i], positions[i + 1], positions[i + 2]) * q + at;
            w[i] = v.X; w[i + 1] = v.Y; w[i + 2] = v.Z;
        }
        return w;
    }

    private static (Vector3 min, Vector3 max) Box(float[] p)
    {
        Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
        for (int i = 0; i < p.Length; i += 3)
        {
            min = Vector3.Min(min, new Vector3(p[i], p[i + 1], p[i + 2]));
            max = Vector3.Max(max, new Vector3(p[i], p[i + 1], p[i + 2]));
        }
        return (min, max);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AConvertedPartsCollisionGeometryIsThePrimsInTheWorldToAMillimetre(string label)
    {
        (ShapeData shape, string scale) = Make(label);
        KitPart part = new KitPart("Fixture") { Shape = shape, Scale = scale };
        (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) = PhysicsModelTests.ConvertOne(part);
        Assert.Equal(before.Scale, after.Scale);

        (float[] positions, int[] indices) prim = PhysicsModelTests.RegionMesh(before.Shape, before.Scale, false);
        after.Shape.SculptData = (byte[])asset.Clone();
        (float[] positions, int[] indices) mesh = PhysicsModelTests.RegionMesh(after.Shape, after.Scale, true);

        float[] primWorld = ToWorld(prim.positions, OnEdge, Place), meshWorld = ToWorld(mesh.positions, OnEdge, Place);
        (Vector3 pmin, Vector3 pmax) = Box(primWorld);
        (Vector3 cmin, Vector3 cmax) = Box(meshWorld);
        _out.WriteLine($"{label} scale {scale}: prim {prim.indices.Length / 3} triangles box {pmin}..{pmax}; converted {mesh.indices.Length / 3} triangles box {cmin}..{cmax}");

        Assert.True((pmin - cmin).Length() <= OneMillimetre && (pmax - cmax).Length() <= OneMillimetre,
            $"{label}: world box of the prim {pmin}..{pmax}, of the converted part {cmin}..{cmax}");
        Assert.True(MeshValidator.SameCollision(primWorld, prim.indices, meshWorld, mesh.indices, OneMillimetre, 1e-6f, out string why),
            $"{label}: {why}");
    }
}
