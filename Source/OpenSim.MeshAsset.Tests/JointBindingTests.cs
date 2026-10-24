/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using System.Xml.Linq;
using OpenSim.MeshAsset.Gltf;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// A rig the way a glTF exporter writes it (bones turned along their length, a collision volume with no scale) against a
/// small invented skeleton with one bone and one scaled, rotated collision volume.
/// </summary>
public class JointBindingTests
{
    const string SkeletonXml = @"<linden_skeleton>
  <bone name=""mTestBone"" pos=""0 0 1"" rot=""0 0 0"" scale=""1 1 1"">
    <collision_volume name=""TEST_VOLUME"" pos=""0 0.1 0.2"" rot=""0 8 0"" scale=""0.05 0.17 0.05""/>
  </bone>
</linden_skeleton>";

    static RestSkeleton Skeleton() => RestSkeleton.Parse(XDocument.Parse(SkeletonXml));

    /// <summary>Joint 0 is the bone, joint 1 the collision volume; each node is turned 90 degrees about X, as a bone along its length is.</summary>
    static SharpGLTF.Schema2.ModelRoot Rig(Vector3 volumeShift = default)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("body");
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(Vector3 p, int joint) =>
            new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(Vector2.Zero), new VertexJoints4(new[] { (joint, 1f) }));
        var prim = mesh.UsePrimitive(new MaterialBuilder("m"));
        // glTF axes: the volume sits at SL (0, 0.1, 1.2) = glTF (0, 1.2, -0.1); the bone at SL (0, 0, 1) = glTF (0, 1, 0)
        prim.AddTriangle(V(new Vector3(0.1f, 1.2f, -0.1f), 1), V(new Vector3(0.0f, 1.3f, -0.1f), 1), V(new Vector3(0.0f, 1.2f, 0.0f), 1));
        prim.AddTriangle(V(new Vector3(0.1f, 1.0f, 0.0f), 0), V(new Vector3(0.0f, 1.1f, 0.0f), 0), V(new Vector3(0.0f, 1.0f, 0.1f), 0));
        var armature = new NodeBuilder("armature");
        var turn = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var bone = armature.CreateNode("mTestBone");
        bone.LocalMatrix = turn * Matrix4x4.CreateTranslation(0, 1, 0);
        var volume = armature.CreateNode("TEST_VOLUME");
        volume.LocalMatrix = turn * Matrix4x4.CreateTranslation(new Vector3(0, 1.2f, -0.1f) + volumeShift);
        var scene = new SceneBuilder();
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, new[] { bone, volume });
        return scene.ToGltf2();
    }

    [Fact]
    public void AnExportersMatricesPoseTheMeshFarFromItsBindPose()
    {
        var (model, _) = GltfToMesh.Convert(Rig());
        var r = SkinningCheck.Pose(model, Skeleton(), 3, useJointPositions: false);
        Assert.True(r.MaxError > 0.05f, $"max error {r.MaxError * 1000:0.0} mm");
        Assert.True(r.Groups[SkinningCheck.CollisionVolumes].Max > 0.05f);
        Assert.True(r.Groups[SkinningCheck.Bones].Max > 0.05f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithTheSkeletonsMatricesTheMeshPosesOnItsBindPose(bool includeJointPositions)
    {
        var (model, _) = GltfToMesh.Convert(Rig(), binding: new GltfToMesh.JointBinding(Skeleton(), includeJointPositions));
        var r = SkinningCheck.Pose(model, Skeleton(), 3, useJointPositions: includeJointPositions);
        Assert.True(r.MaxError < 0.0005f, $"max error {r.MaxError * 1000:0.000} mm");
        Assert.Equal(includeJointPositions, model.Skin!.AltInverseBind != null);
    }

    [Fact]
    public void ACollisionVolumesInverseBindMatrixCarriesItsScaleAndRotation()
    {
        var (model, _) = GltfToMesh.Convert(Rig(), binding: new GltfToMesh.JointBinding(Skeleton()));
        var ibm = SkinningCheck.ToMatrix(model.Skin!.InverseBind[model.Skin.JointNames.IndexOf("TEST_VOLUME")]);
        // the inverse of a 0.05 / 0.17 / 0.05 scale stretches by 20, 5.9 and 20
        Assert.True(MathF.Abs(new Vector3(ibm.M11, ibm.M12, ibm.M13).Length() - 20f) < 1.5f);
        Assert.True(MathF.Abs(new Vector3(ibm.M21, ibm.M22, ibm.M23).Length() - 1f / 0.17f) < 0.2f);
    }

    [Fact]
    public void AJointFileMovedFromTheSkeletonGetsItsPositionInTheAlternateMatrixAndStillPoses()
    {
        var (model, report) = GltfToMesh.Convert(Rig(new Vector3(0, 0, 0.02f)), binding: new GltfToMesh.JointBinding(Skeleton(), true));
        var i = model.Skin!.JointNames.IndexOf("TEST_VOLUME");
        var alt = model.Skin.AltInverseBind![i];
        // the file puts the volume 2 cm further toward glTF +Z (SL -Y): local offset (0, 0.08, 0.2)
        Assert.Equal(0.08, alt[13], 4);
        Assert.Equal(0.2, alt[14], 4);
        Assert.Contains(report.Notes, n => n.StartsWith("joint positions written for 1 of 2"));
        var r = SkinningCheck.Pose(model, Skeleton(), 3, useJointPositions: true);
        Assert.True(r.MaxError < 0.0005f, $"max error {r.MaxError * 1000:0.000} mm");
        // read as the skeleton's own pose (no overrides) the same asset is off by the 2 cm
        Assert.True(SkinningCheck.Pose(model, Skeleton(), 3, useJointPositions: false).MaxError > 0.015f);
    }

    const string ArmSkeletonXml = @"<linden_skeleton>
  <bone name=""mRoot"" pos=""0 0 1"" rot=""0 0 0"" scale=""1 1 1"">
    <bone name=""mArm"" pos=""0 0.1 0"" rot=""0 0 0"" scale=""1 1 1"">
      <collision_volume name=""ARM_VOLUME"" pos=""0 0.1 0"" rot=""0 0 0"" scale=""0.1 0.1 0.1""/>
    </bone>
  </bone>
</linden_skeleton>";

    /// <summary>A mesh weighted to the root bone and to a volume under an arm bone that carries no weight, with the file's arm bone 2 cm out from the skeleton's.</summary>
    static SharpGLTF.Schema2.ModelRoot ArmRig()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("body");
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(Vector3 p, int joint) =>
            new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(Vector2.Zero), new VertexJoints4(new[] { (joint, 1f) }));
        var prim = mesh.UsePrimitive(new MaterialBuilder("m"));
        // SL (x, y, z) is glTF (x, z, -y)
        prim.AddTriangle(V(new Vector3(0.05f, 1.0f, 0.0f), 0), V(new Vector3(0f, 1.05f, 0f), 0), V(new Vector3(0f, 1.0f, 0.05f), 0));
        prim.AddTriangle(V(new Vector3(0.05f, 1.0f, -0.2f), 1), V(new Vector3(0f, 1.05f, -0.2f), 1), V(new Vector3(0f, 1.0f, -0.25f), 1));
        var armature = new NodeBuilder("armature");
        var root = armature.CreateNode("mRoot"); root.LocalMatrix = Matrix4x4.CreateTranslation(0, 1, 0);
        var arm = armature.CreateNode("mArm"); arm.LocalMatrix = Matrix4x4.CreateTranslation(0, 1, -0.12f);
        var volume = armature.CreateNode("ARM_VOLUME"); volume.LocalMatrix = Matrix4x4.CreateTranslation(0, 1, -0.2f);
        var scene = new SceneBuilder();
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, new[] { root, volume, arm });
        return scene.ToGltf2();
    }

    [Fact]
    public void AnUnweightedParentTheFileMovesIsListedSoTheLimbTurnsAboutTheFilesJoint()
    {
        var sk = RestSkeleton.Parse(XDocument.Parse(ArmSkeletonXml));
        var root = ArmRig();
        // the arm bone carries no weight; the file puts it 2 cm further out than the skeleton does
        var (off, _) = GltfToMesh.Convert(root, binding: new GltfToMesh.JointBinding(sk, false));
        var (on, report) = GltfToMesh.Convert(root, binding: new GltfToMesh.JointBinding(sk, true));
        Assert.DoesNotContain("mArm", off.Skin!.JointNames);
        Assert.Contains("mArm", on.Skin!.JointNames);
        Assert.Equal(1, report.PivotJointsAdded);

        var source = MotionEquivalenceTests.ReadSource(root, sk, 0);
        var pose = new MotionEquivalenceTests.PoseSpec("arm", new[] { ("mArm", new Vector3(45, 0, 0)) });
        var expected = MotionEquivalenceTests.PoseSource(source, sk, pose);
        var posedOn = MotionEquivalenceTests.PoseAsset(on, sk, true, pose, out _);
        var posedOff = MotionEquivalenceTests.PoseAsset(off, sk, false, pose, out _);
        Assert.True(MotionEquivalenceTests.Compare(posedOn, expected).Max < 0.0005f, "joint positions on: the limb moves as the file's rig does");
        Assert.True(MotionEquivalenceTests.Compare(posedOff, expected).Max > 0.01f, "joint positions off: the limb turns about the avatar's joint, 2 cm away");
    }

    [Fact]
    public void AnAssetWithoutASkinIsNotTouchedByTheBinding()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("plain");
        var prim = mesh.UsePrimitive(new MaterialBuilder("m"));
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(float x, float y) => new(new VertexPositionNormal(x, y, 0, 0, 0, 1), new VertexTexture1(Vector2.Zero));
        prim.AddTriangle(V(0, 0), V(1, 0), V(0, 1));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        var root = scene.ToGltf2();
        var (a, _) = GltfToMesh.Convert(root);
        var (b, _) = GltfToMesh.Convert(root, binding: new GltfToMesh.JointBinding(Skeleton(), true));
        Assert.Null(b.Skin);
        Assert.Equal(MeshCodec.Encode(a), MeshCodec.Encode(b));
    }
}
