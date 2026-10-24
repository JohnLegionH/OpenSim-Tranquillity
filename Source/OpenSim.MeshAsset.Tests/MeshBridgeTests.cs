/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using OpenSim.MeshAsset.Gltf;
using OpenSim.MeshAsset;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace OpenSim.MeshAsset.Tests;

public class MeshBridgeTests
{
    static readonly MaterialBuilder Mat = new("m");

    /// <summary>One skinned primitive with the given vertices (glTF axes) and (joint, weight) lists; joints sit at the given glTF positions.</summary>
    static SharpGLTF.Schema2.ModelRoot Skinned(Vector3[] pos, (int, float)[][] weights, Vector3[] jointAt, int faces = 1, string[]? names = null)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints8>("body");
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints8> V(int i) =>
            new(new VertexPositionNormal(pos[i], Vector3.UnitZ), new VertexTexture1(new Vector2(0.25f * i, 0.1f)), new VertexJoints8(weights[i]));
        for (int f = 0; f < faces; f++)
            mesh.UsePrimitive(new MaterialBuilder("m" + f)).AddTriangle(V(0), V(1), V(2));
        var armature = new NodeBuilder("armature");
        var joints = jointAt.Select((p, i) =>
        {
            var j = armature.CreateNode(names?[i] ?? $"mJoint{i}");
            j.LocalTransform = Matrix4x4.CreateTranslation(p);
            return j;
        }).ToArray();
        var scene = new SceneBuilder();
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, joints);
        return scene.ToGltf2();
    }

    static readonly Vector3[] Tri = { new(0, 1, 0), new(1, 1, 0), new(0, 3, 2) };

    [Fact]
    public void RotatesGltfAxesIntoSlAxesAndFlipsV()
    {
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (0, 1f) } }, new[] { Vector3.Zero });
        var (model, _) = GltfToMesh.Convert(root);
        var f = model.High!.Faces[0];
        // un-normalise with the bind shape matrix, which is what a skinned mesh uses
        var shape = model.Skin!.BindShape.Select(x => (float)x).ToArray();
        var m = new Matrix4x4(shape[0], shape[1], shape[2], shape[3], shape[4], shape[5], shape[6], shape[7], shape[8], shape[9], shape[10], shape[11], shape[12], shape[13], shape[14], shape[15]);
        for (int i = 0; i < 3; i++)
        {
            var p = Vector3.Transform(f.Positions[i], m);
            var expect = new Vector3(Tri[i].X, -Tri[i].Z, Tri[i].Y);   // (x, y, z) glTF -> (x, -z, y) SL
            Assert.True(Vector3.Distance(p, expect) < 1e-5f, $"vertex {i}: {p} vs {expect}");
            Assert.Equal(1f - 0.1f, f.Uvs![i].Y, 4);
        }
    }

    [Fact]
    public void NormalisesIntoTheUnitBox()
    {
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (0, 1f) } }, new[] { Vector3.Zero });
        var (model, report) = GltfToMesh.Convert(root);
        foreach (var p in model.High!.Faces[0].Positions)
            Assert.True(Math.Abs(p.X) <= 0.5001f && Math.Abs(p.Y) <= 0.5001f && Math.Abs(p.Z) <= 0.5001f);
        Assert.Equal(model.High.Faces[0].NormalizedScale, report.NormalizedSize);
    }

    [Fact]
    public void InverseBindMatrixKeepsTheJointLocalPosition()
    {
        // A joint at (0, 1, 0) in glTF; vertex (1, 1, 0) is 1 m to its +X side in joint space either way round.
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (0, 1f) } }, new[] { new Vector3(0, 1, 0) });
        var (model, _) = GltfToMesh.Convert(root);
        var f = model.High!.Faces[0];
        Matrix4x4 M(double[] a) => new((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5], (float)a[6], (float)a[7], (float)a[8], (float)a[9], (float)a[10], (float)a[11], (float)a[12], (float)a[13], (float)a[14], (float)a[15]);
        var skinMatrix = M(model.Skin!.BindShape) * M(model.Skin.InverseBind[0]);
        // v_stored * bindShape * inverseBind = the vertex in the joint's own space, in the viewer's row-vector convention.
        // Joint space is the glTF joint space: (x, y - 1, z) for the glTF vertex (x, y, z).
        for (int i = 0; i < 3; i++)
        {
            var local = Vector3.Transform(f.Positions[i], skinMatrix);
            var expect = Tri[i] - new Vector3(0, 1, 0);
            Assert.True(Vector3.Distance(local, expect) < 1e-5f, $"vertex {i}: {local} vs {expect}");
        }
    }

    [Fact]
    public void KeepsTheFourHeaviestInfluencesAndRenormalises()
    {
        var w = new[]
        {
            new[] { (0, 0.3f), (1, 0.25f), (2, 0.2f), (3, 0.15f), (4, 0.1f) },
            new[] { (0, 1f) },
            new[] { (4, 0.5f), (3, 0.5f) },
        };
        var root = Skinned(Tri, w, Enumerable.Repeat(Vector3.Zero, 5).ToArray());
        var (model, report) = GltfToMesh.Convert(root);
        var infl = model.High!.Faces[0].Weights![0];
        Assert.Equal(4, infl.Length);
        Assert.Equal(1f, infl.Sum(x => x.Weight), 4);
        Assert.Equal(1, report.VerticesTrimmedToFour);
        Assert.Equal(0.1, report.LargestDroppedWeight, 4);
        Assert.Contains(report.Notes, n => n.Contains("more than 4 influences"));
    }

    [Fact]
    public void DropsFacesBeyondEightAndSaysSo()
    {
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (0, 1f) } }, new[] { Vector3.Zero }, faces: 10);
        var (model, report) = GltfToMesh.Convert(root);
        Assert.Equal(8, model.High!.Faces.Count);
        Assert.Equal(10, report.FacesInFile);
        Assert.Equal(2, report.FacesDropped);
        Assert.Contains(report.Notes, n => n.Contains("dropped"));
    }

    [Fact]
    public void LeavesOutJointsWithNoWeightAndTheExporterPlaceholder()
    {
        var w = new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (2, 1f) } };
        var root = Skinned(Tri, w, new[] { Vector3.Zero, Vector3.One, Vector3.Zero }, names: new[] { "mPelvis", "mTorso", "neutral_bone" });
        var (model, report) = GltfToMesh.Convert(root, ignoredJoint: "neutral_bone");
        Assert.Equal(new[] { "mPelvis" }, model.Skin!.JointNames);
        Assert.Equal(2, report.JointsDropped);
        Assert.Equal(1, report.VerticesWithoutWeight);
        Assert.Empty(model.High!.Faces[0].Weights![2]);
    }

    [Fact]
    public void WithASkeletonDropsJointsItDoesNotKnow()
    {
        var w = new[] { new[] { (0, 0.5f), (1, 0.5f) }, new[] { (0, 1f) }, new[] { (0, 1f) } };
        var root = Skinned(Tri, w, new[] { Vector3.Zero, Vector3.One }, names: new[] { "mPelvis", "customBone" });
        var (model, report) = GltfToMesh.Convert(root, name => name is "mPelvis" or "mTorso");
        Assert.Equal(new[] { "mPelvis" }, model.Skin!.JointNames);
        Assert.Single(model.High!.Faces[0].Weights![0]);
        Assert.Equal(1f, model.High.Faces[0].Weights![0][0].Weight, 4);
        Assert.Equal(1, report.JointsDropped);
    }

    [Fact]
    public void OutputEncodesAndDecodesBack()
    {
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 0.5f), (1, 0.5f) }, new[] { (1, 1f) } }, new[] { Vector3.Zero, new Vector3(0, 1, 0) });
        var (model, _) = GltfToMesh.Convert(root);
        model.RepeatLodAbove();
        var back = MeshCodec.Decode(MeshCodec.Encode(model));
        Assert.Equal(model.Skin!.JointNames, back.Skin!.JointNames);
        Assert.Equal(3, back.High!.Faces[0].VertexCount);
        Assert.NotSame(back.High, back.Lowest);
        Assert.Equal(back.High.Faces[0].VertexCount, back.Lowest!.Faces[0].VertexCount);
    }

    [Fact]
    public void AnUnskinnedMeshIsNormalisedAndTheReportSaysWhatTheObjectNeeds()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("cube");
        var p = mesh.UsePrimitive(Mat);
        p.AddTriangle(
            new(new VertexPositionNormal(new Vector3(0, 0, 0), Vector3.UnitZ), new VertexTexture1(Vector2.Zero)),
            new(new VertexPositionNormal(new Vector3(2, 0, 0), Vector3.UnitZ), new VertexTexture1(Vector2.UnitX)),
            new(new VertexPositionNormal(new Vector3(0, 4, 0), Vector3.UnitZ), new VertexTexture1(Vector2.UnitY)));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        var (model, report) = GltfToMesh.Convert(scene.ToGltf2());
        Assert.Null(model.Skin);
        Assert.Equal(new Vector3(2, 1, 4), report.NormalizedSize);   // glTF y 4 becomes SL z 4; glTF z 0 becomes SL y 0, whose size is taken as 1
        Assert.Contains(report.Notes, n => n.StartsWith("unskinned"));
    }

    [Fact]
    public void ADecodedAssetBecomesAGlbWithTheSameFaces()
    {
        var root = Skinned(Tri, new[] { new[] { (0, 1f) }, new[] { (0, 1f) }, new[] { (1, 1f) } }, new[] { Vector3.Zero, new Vector3(0, 1, 0) }, faces: 2);
        var (model, _) = GltfToMesh.Convert(root);
        var glb = MeshToGltf.Build(MeshCodec.Decode(MeshCodec.Encode(model)));
        Assert.Equal(2, glb.LogicalMeshes[0].Primitives.Count);
        Assert.Single(glb.LogicalSkins);
        var back = glb.LogicalMeshes[0].Primitives[0].GetVertexAccessor("POSITION").AsVector3Array();
        Assert.Equal(3, back.Count);
        Assert.True(Vector3.Distance(back.OrderBy(v => v.Y).Last(), new Vector3(0, 3, 2)) < 1e-3f);
    }
}
