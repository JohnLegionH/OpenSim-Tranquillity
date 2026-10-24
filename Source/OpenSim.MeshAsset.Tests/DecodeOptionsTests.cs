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

public class DecodeOptionsTests
{
    static MeshFace Face(float z, bool weights) => new()
    {
        Positions = new[] { new Vector3(-1, 0, z), new Vector3(1, 0, z), new Vector3(0, 3, z) },
        Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        Uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1) },
        Indices = new[] { 0, 1, 2 },
        Weights = weights ? new[] { new[] { new Influence(0, 1f) }, new[] { new Influence(1, 1f) }, new[] { new Influence(2, 1f) } } : null,
    };

    static byte[] Asset()
    {
        var m = new MeshAssetModel();
        foreach (var (i, z) in new[] { (3, 3f), (2, 2f), (1, 1f), (0, 0f) })
        {
            var lod = new MeshLod();
            lod.Faces.Add(Face(z, weights: true));
            m.SetLod(i, lod);
        }
        m.Skin = new MeshSkin { JointNames = { "a", "b", "c" } };
        for (int j = 0; j < 3; j++) m.Skin.InverseBind.Add(MeshSkin.Identity());
        var convex = LlsdBinary.NewMap();
        convex["HullList"] = new byte[] { 4 };
        m.PhysicsConvex = convex;
        return MeshCodec.Encode(m);
    }

    [Fact]
    public void NoOptionsReadsEverythingAsBefore()
    {
        var m = MeshCodec.Decode(Asset(), null);
        Assert.NotNull(m.Lowest);
        Assert.NotNull(m.Low);
        Assert.NotNull(m.Medium);
        Assert.NotNull(m.High);
        Assert.NotNull(m.PhysicsConvex);
        Assert.NotNull(m.High!.Faces[0].Weights);
        Assert.NotNull(m.Skin);
    }

    [Fact]
    public void HighestLevelOnlyLeavesTheLowerLevelsUnread()
    {
        var m = MeshCodec.Decode(Asset(), new MeshDecodeOptions { HighestLevelOnly = true });
        Assert.NotNull(m.High);
        Assert.Equal(3f, m.High!.Faces[0].Positions[0].Z);
        Assert.Null(m.Medium);
        Assert.Null(m.Low);
        Assert.Null(m.Lowest);
        Assert.NotNull(m.Skin);
    }

    [Fact]
    public void HighestLevelOnlyTakesTheNextLevelDownWhenHighIsMissing()
    {
        var m = MeshCodec.Decode(Asset(), null);
        m.High = null;
        var back = MeshCodec.Decode(MeshCodec.Encode(m), new MeshDecodeOptions { HighestLevelOnly = true });
        Assert.Null(back.High);
        Assert.Equal(2f, back.Medium!.Faces[0].Positions[0].Z);
        Assert.Null(back.Low);
        Assert.Null(back.Lowest);
    }

    [Fact]
    public void SkipPhysicsAndSkipWeightsLeaveThemOut()
    {
        var m = MeshCodec.Decode(Asset(), new MeshDecodeOptions { SkipPhysics = true, SkipWeights = true });
        Assert.Null(m.PhysicsConvex);
        Assert.Null(m.PhysicsMesh);
        Assert.All(new[] { m.Lowest, m.Low, m.Medium, m.High }, lod => Assert.Null(lod!.Faces[0].Weights));
        Assert.NotNull(m.High);
    }
}
