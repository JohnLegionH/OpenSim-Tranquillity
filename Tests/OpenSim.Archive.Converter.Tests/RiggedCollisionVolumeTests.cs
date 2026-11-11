/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Restore;
using OpenSim.Archive.Restore.Import;
using OpenSim.ArchiveInspector;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A rigged part weighted to a collision volume (a fitted-mesh joint). The viewer poses such a joint with the volume's own rotation and scale, so the
/// inverse bind matrix has to be the inverse of that frame at the volume's rest position, whatever orientation or scale the modelling tool's bone had.
/// The expected matrices are worked out here from the viewer's published volume frames (BELLY: 0.09 0.13 0.15, turned 8 degrees about y, 0.028 0 0.04
/// from mTorso), not read back from the code under test. Each test writes its own folders, so the class runs in parallel.
/// </summary>
public class RiggedCollisionVolumeTests
{
    private static ImportOptions Options(bool jointPositions = false) => new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600, JointPositions = jointPositions };

    private static (ImportResult Result, MeshAssetModel Mesh) Import(RigKit.Options kit, bool jointPositions = false)
    {
        string folder = RigKit.Write(kit);
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.Import(folder, output, Options(jointPositions));
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        string meshKey = oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal));
        return (result, MeshCodec.Decode(oar[meshKey]));
    }

    private static double[] Flat(Matrix4x4 i) => new double[] { i.M11, i.M12, i.M13, i.M14, i.M21, i.M22, i.M23, i.M24, i.M31, i.M32, i.M33, i.M34, i.M41, i.M42, i.M43, i.M44 };

    /// <summary>The inverse of a frame (scale, then turn, then move), as the asset stores it: row vectors, translation in 12 to 14.</summary>
    private static double[] InverseOfFrame(Vector3 scale, Matrix4x4 turn, Vector3 position)
    {
        Matrix4x4 world = Matrix4x4.CreateScale(scale) * turn * Matrix4x4.CreateTranslation(position);
        Assert.True(Matrix4x4.Invert(world, out Matrix4x4 inverse));
        return Flat(inverse);
    }

    private static Matrix4x4 TurnY(double degrees) => Matrix4x4.CreateRotationY((float)(degrees * Math.PI / 180.0));

    private static void AssertMatrix(double[] expected, double[] actual, double tolerance = 1e-5)
    {
        Assert.Equal(16, actual.Length);
        for (int i = 0; i < 16; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, "matrix value " + i + ": expected " + expected[i] + ", got " + actual[i]);
    }

    // mTorso is joint 1 of the file, at height 1.1 (viewer axes: 0, 0, 1.1); the file puts its third joint at 1.25, which a volume does not use
    private static readonly Vector3 BellyAt = new Vector3(0.028f, 0f, 1.1f + 0.04f);

    [Fact]
    public void ACollisionVolumeJointCarriesTheVolumesOwnScaleAndTurnAtItsRestPositionUnderItsBone()
    {
        (ImportResult _, MeshAssetModel mesh) = Import(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "BELLY" } });
        Assert.Equal("BELLY", mesh.Skin.JointNames[2]);
        AssertMatrix(InverseOfFrame(new Vector3(0.09f, 0.13f, 0.15f), TurnY(8.0), BellyAt), mesh.Skin.InverseBind[2]);
    }

    [Fact]
    public void TheOrientationOfTheFilesBoneForAVolumeIsIgnored()
    {
        (ImportResult _, MeshAssetModel mesh) = Import(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "BELLY" }, RotateDegrees = new[] { 0.0, 0.0, 45.0 } });
        AssertMatrix(InverseOfFrame(new Vector3(0.09f, 0.13f, 0.15f), TurnY(8.0), BellyAt), mesh.Skin.InverseBind[2]);
    }

    [Fact]
    public void AVolumeWhoseBoneIsNotInTheFileTakesTheFilesOwnPositionOfTheVolumeAndTheFileIsToldSo()
    {
        (ImportResult result, MeshAssetModel mesh) = Import(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "L_UPPER_ARM" } });
        // L_UPPER_ARM is 0.05 0.17 0.05, turned -5 degrees about x; its bone, mShoulderLeft, is not in the file, so the volume stays where the file has it (0, 0, 1.25)
        double[] expected = InverseOfFrame(new Vector3(0.05f, 0.17f, 0.05f), Matrix4x4.CreateRotationX((float)(-5.0 * Math.PI / 180.0)), new Vector3(0f, 0f, 1.25f));
        AssertMatrix(expected, mesh.Skin.InverseBind[2]);
        Assert.Contains(result.Notes, n => n.Contains("L_UPPER_ARM") && n.Contains("mShoulderLeft"));
    }

    [Fact]
    public void AskedForJointPositionsAVolumeIsGivenItsOffsetFromItsBone()
    {
        (ImportResult _, MeshAssetModel mesh) = Import(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "BELLY" } }, jointPositions: true);
        double[] alt = mesh.Skin.AltInverseBind[2];
        Assert.Equal(0.028, alt[12], 5);
        Assert.Equal(0.0, alt[13], 5);
        Assert.Equal(0.04, alt[14], 5);
    }

    [Fact]
    public void TheCheckPassesForARiggedPartWithACollisionVolume()
    {
        string folder = RigKit.Write(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "BELLY" } });
        string archive = ImportKit.OutputPath();
        ImportOptions options = new ImportOptions { Name = "Volume Round Trip", Avatar = ImportKit.Avatar, Position = new Vector3(40f, 50f, 60f), Created = 1767225600 };
        ObjectImporter.Import(folder, archive, options);
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        ImportCheckResult check = ImportCheck.Run(archive, folder, options, work);
        Assert.True(check.Passed, check.ToText());
        Assert.Contains(check.Lines, l => l.What == "skin and weights" && l.Ok);
    }

    [Fact]
    public void TheTableHoldsTheViewersTwentySixCollisionVolumesWithTheirFrames()
    {
        Assert.Equal(26, CollisionVolumeFrames.Frames.Count);
        foreach (string name in ViewerJointNames.CollisionVolumes)
            Assert.True(CollisionVolumeFrames.Frames.ContainsKey(name), name);
        CollisionVolumeFrame chest = CollisionVolumeFrames.Frames["CHEST"];
        Assert.Equal("mChest", chest.Parent);
        Assert.Equal(new Vector3(0.028f, 0f, 0.07f), chest.Position);
        Assert.Equal(new Vector3(0f, -10f, 0f), chest.RotationDegrees);
        Assert.Equal(new Vector3(0.11f, 0.15f, 0.2f), chest.Scale);
        CollisionVolumeFrame hand = CollisionVolumeFrames.Frames["L_HAND"];
        Assert.Equal("mWristLeft", hand.Parent);
        Assert.Equal(new Vector3(-3f, 0f, -10f), hand.RotationDegrees);
    }
}
