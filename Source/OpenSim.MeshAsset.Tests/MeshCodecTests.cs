/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.IO.Compression;
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

public class LlsdBinaryTests
{
    [Fact]
    public void RoundTripsEveryValueKind()
    {
        var map = LlsdBinary.NewMap();
        map["b"] = new byte[] { 1, 2, 3 };
        map["a"] = new List<object?> { 1, 2.5, "x", true, false, null };
        var bytes = LlsdBinary.Write(map);
        var back = (IDictionary<string, object?>)LlsdBinary.Read(bytes, out int used)!;
        Assert.Equal(bytes.Length, used);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])back["b"]!);
        var list = (IList<object?>)back["a"]!;
        Assert.Equal(1, list[0]);
        Assert.Equal(2.5, list[1]);
        Assert.Equal("x", list[2]);
        Assert.Equal(true, list[3]);
        Assert.Equal(false, list[4]);
        Assert.Null(list[5]);
    }

    [Fact]
    public void WritesMapKeysInOrdinalOrderAndBigEndianIntegers()
    {
        var map = LlsdBinary.NewMap();
        map["b"] = 1;
        map["a"] = 258;
        var bytes = LlsdBinary.Write(map);
        // '{' count 2, then k len "a" i 0x00000102, then k len "b" ...
        Assert.Equal(new byte[] { (byte)'{', 0, 0, 0, 2, (byte)'k', 0, 0, 0, 1, (byte)'a', (byte)'i', 0, 0, 1, 2 }, bytes.Take(16).ToArray());
    }

    [Fact]
    public void TruncatedDataIsAFormatErrorNotACrash()
    {
        var bytes = LlsdBinary.Write(new List<object?> { 1, 2, 3 });
        for (int cut = 0; cut < bytes.Length; cut++)
            Assert.Throws<MeshFormatException>(() => LlsdBinary.Read(bytes.AsSpan(0, cut), out _));
    }

    [Fact]
    public void ARealIsEightBytesBigEndian()
    {
        var bytes = LlsdBinary.Write(1.0);
        Assert.Equal(new byte[] { (byte)'r', 0x3F, 0xF0, 0, 0, 0, 0, 0, 0 }, bytes);
    }
}

public class MeshCodecTests
{
    static MeshFace Triangle(bool weights = false) => new()
    {
        Positions = new[] { new Vector3(-1, 0, 2), new Vector3(1, 0, 2), new Vector3(0, 3, -2) },
        Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        Uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1) },
        Indices = new[] { 0, 1, 2 },
        Weights = weights
            ? new[]
            {
                new[] { new Influence(0, 1f) },
                new[] { new Influence(0, 0.5f), new Influence(1, 0.5f) },
                new[] { new Influence(2, 0.4f), new Influence(1, 0.3f), new Influence(0, 0.2f), new Influence(3, 0.1f) },
            }
            : null,
    };

    static MeshAssetModel Model(bool weights = false)
    {
        var m = new MeshAssetModel();
        m.High = new MeshLod();
        m.High.Faces.Add(Triangle(weights));
        m.High.Faces.Add(new MeshFace { NoGeometry = true });
        return m;
    }

    [Fact]
    public void QuantisesPositionsToSixteenBitsOverTheDomain()
    {
        var bytes = MeshCodec.Encode(Model());
        var back = MeshCodec.Decode(bytes);
        var f = back.High!.Faces[0];
        Assert.Equal(3, f.VertexCount);
        double step = 3.0 / 65535; // the largest domain extent is 4 (z), step 4/65535
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs(f.Positions[i].X - Triangle().Positions[i].X) <= 2.0 / 65535 + 1e-6);
            Assert.True(Math.Abs(f.Positions[i].Y - Triangle().Positions[i].Y) <= 3.0 / 65535 + 1e-6);
            Assert.True(Math.Abs(f.Positions[i].Z - Triangle().Positions[i].Z) <= 4.0 / 65535 + 1e-6);
        }
        Assert.True(step > 0);
        Assert.Equal((new Vector3(-1, 0, -2), new Vector3(1, 3, 2)), f.PositionDomain);
        Assert.True(back.High.Faces[1].NoGeometry);
    }

    [Fact]
    public void DomainEndsAreExact()
    {
        var f = MeshCodec.Decode(MeshCodec.Encode(Model())).High!.Faces[0];
        Assert.Equal(new Vector3(-1, 0, 2), f.Positions[0]);
        Assert.Equal(new Vector3(1, 0, 2), f.Positions[1]);
        // y and z of vertex 2 sit on the domain ends; its x is mid-range and lands within one step.
        Assert.Equal((3f, -2f), (f.Positions[2].Y, f.Positions[2].Z));
        Assert.True(Math.Abs(f.Positions[2].X) <= 2.0 / 65535);
        Assert.Equal(1f, f.Uvs![2].Y);
    }

    [Fact]
    public void WeightsEndWithATerminatorOnlyBelowFourInfluences()
    {
        var m = Model(weights: true);
        var back = MeshCodec.Decode(MeshCodec.Encode(m)).High!.Faces[0];
        Assert.Equal(new[] { 1, 2, 4 }, back.Weights!.Select(w => w.Length).ToArray());
        Assert.Equal(new byte[] { 2, 1, 0, 3 }, back.Weights[2].Select(w => w.Joint).ToArray());
        Assert.True(Math.Abs(back.Weights![2][0].Weight - 0.4f) < 1e-4);
    }

    [Fact]
    public void WeightBlockHasTheViewersByteLayout()
    {
        var m = Model(weights: true);
        var bytes = MeshCodec.Encode(m);
        var raw = RawBlock(bytes, "high_lod");
        var faces = (IList<object?>)LlsdBinary.Read(raw, out _)!;
        var w = (byte[])((IDictionary<string, object?>)faces[0]!)["Weights"]!;
        // vertex 0: joint 0, 65535, terminator; vertex 1: two entries + terminator; vertex 2: four entries, no terminator
        Assert.Equal(3 + 1 + 6 + 1 + 12, w.Length);
        Assert.Equal(new byte[] { 0, 0xFF, 0xFF, 0xFF }, w.Take(4).ToArray());
        Assert.Equal(new byte[] { 0, 1 }, new[] { w[4], w[7] });   // vertex 1: joints 0 and 1 follow vertex 0's terminator
        Assert.Equal(new byte[] { 2, 1, 0, 3 }, new[] { w[11], w[14], w[17], w[20] }); // vertex 2: four entries, nothing after them
    }

    [Fact]
    public void SkinRoundTripsEveryField()
    {
        var m = Model(weights: true);
        m.Skin = new MeshSkin
        {
            JointNames = { "mPelvis", "mTorso" },
            InverseBind = { Enumerable.Range(0, 16).Select(i => i * 0.5).ToArray(), MeshSkin.Identity() },
            BindShape = Enumerable.Range(0, 16).Select(i => i * 0.25).ToArray(),
            AltInverseBind = new List<double[]> { MeshSkin.Identity(), MeshSkin.Identity() },
            PelvisOffset = 0.5,
            LockScaleIfJointPosition = true,
        };
        var s = MeshCodec.Decode(MeshCodec.Encode(m)).Skin!;
        Assert.Equal(m.Skin.JointNames, s.JointNames);
        Assert.Equal(m.Skin.InverseBind, s.InverseBind);
        Assert.Equal(m.Skin.BindShape, s.BindShape);
        Assert.Equal(m.Skin.AltInverseBind, s.AltInverseBind);
        Assert.Equal(0.5, s.PelvisOffset);
        Assert.True(s.LockScaleIfJointPosition);
    }

    [Fact]
    public void SkinWithoutOverridesWritesNoAltMatricesNorPelvisOffset()
    {
        var m = Model(weights: true);
        m.Skin = new MeshSkin { JointNames = { "mPelvis" }, InverseBind = { MeshSkin.Identity() } };
        var raw = RawBlock(MeshCodec.Encode(m), "skin");
        var map = (IDictionary<string, object?>)LlsdBinary.Read(raw, out _)!;
        Assert.Equal(new[] { "bind_shape_matrix", "inverse_bind_matrix", "joint_names" }, map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void OffsetsAreRelativeToTheEndOfTheHeader_AndBlocksAreInViewerOrder()
    {
        var m = Model(weights: true);
        m.Skin = new MeshSkin { JointNames = { "mPelvis" }, InverseBind = { MeshSkin.Identity() } };
        m.Low = m.High; // the same level in two slots is still written as two blocks
        var bytes = MeshCodec.Encode(m);
        var header = (IDictionary<string, object?>)LlsdBinary.Read(bytes, out int hl)!;
        int Off(string k) => (int)((IDictionary<string, object?>)header[k]!)["offset"]!;
        int Size(string k) => (int)((IDictionary<string, object?>)header[k]!)["size"]!;
        Assert.Equal(0, Off("skin"));
        Assert.Equal(Size("skin"), Off("low_lod"));
        Assert.Equal(Off("low_lod") + Size("low_lod"), Off("high_lod"));
        Assert.Equal(hl + Size("skin") + Size("low_lod") + Size("high_lod"), bytes.Length);
    }

    [Fact]
    public void ANewAssetHasNoVersionKey_AndADecodedOneKeepsItsOwn()
    {
        var m = Model();
        Assert.Null(m.Version);
        var header = (IDictionary<string, object?>)LlsdBinary.Read(MeshCodec.Encode(m), out _)!;
        Assert.False(header.ContainsKey("version"));

        m.Version = 3;
        var bytes = MeshCodec.Encode(m);
        Assert.Equal(3, MeshCodec.Decode(bytes).Version);
        Assert.Equal(3, ((IDictionary<string, object?>)LlsdBinary.Read(bytes, out _)!)["version"]);
    }

    [Fact]
    public void RepeatLodAboveWritesEveryLevelAsItsOwnBlockAndIsFlagged()
    {
        var m = Model();
        Assert.False(m.LodsRepeated);
        m.RepeatLodAbove();
        Assert.True(m.LodsRepeated);
        var bytes = MeshCodec.Encode(m);
        var header = (IDictionary<string, object?>)LlsdBinary.Read(bytes, out int hl)!;
        var keys = new[] { "lowest_lod", "low_lod", "medium_lod", "high_lod" };
        var entries = keys.Select(k => ((IDictionary<string, object?>)header[k]!)).ToList();
        Assert.Equal(4, entries.Select(e => (int)e["offset"]!).Distinct().Count()); // no shared offsets
        Assert.Equal(1, entries.Select(e => (int)e["size"]!).Distinct().Count());   // each a full copy of the high block
        Assert.Equal(hl + entries.Sum(e => (int)e["size"]!), bytes.Length);
        var back = MeshCodec.Decode(bytes);
        Assert.NotSame(back.High, back.Lowest);
        MeshCompare.Face(back.High!.Faces[0], back.Lowest!.Faces[0], "lowest repeats high");
    }

    [Fact]
    public void RepeatLodAboveKeepsALowerLevelThatIsAlreadyThere()
    {
        var m = Model();
        var low = new MeshLod();
        low.Faces.Add(Triangle(false));
        m.Low = low;
        m.RepeatLodAbove();
        Assert.Same(low, m.Low);
        Assert.NotSame(m.High, m.Medium);
        Assert.NotNull(m.Lowest);
        Assert.NotSame(m.Low, m.Lowest); // lowest copies low
    }

    [Fact]
    public void RefusesAFaceWithMoreThan65535Vertices()
    {
        var m = new MeshAssetModel { High = new MeshLod() };
        m.High.Faces.Add(new MeshFace { Positions = new Vector3[65536], Indices = new[] { 0, 1, 2 } });
        Assert.Throws<MeshFormatException>(() => MeshCodec.Encode(m));
    }

    [Fact]
    public void RefusesMoreThanFourInfluences()
    {
        var f = Triangle(true);
        f.Weights![0] = Enumerable.Range(0, 5).Select(i => new Influence((byte)i, 0.2f)).ToArray();
        var m = new MeshAssetModel { High = new MeshLod() };
        m.High.Faces.Add(f);
        Assert.Throws<MeshFormatException>(() => MeshCodec.Encode(m));
    }

    [Fact]
    public void PhysicsBlocksAndUnknownHeaderKeysPassThrough()
    {
        var m = Model();
        var convex = LlsdBinary.NewMap();
        convex["HullList"] = new byte[] { 4 };
        m.PhysicsConvex = convex;
        m.ExtraHeader["submodel_id"] = 3;
        var back = MeshCodec.Decode(MeshCodec.Encode(m));
        Assert.Equal(new byte[] { 4 }, (byte[])((IDictionary<string, object?>)back.PhysicsConvex!)["HullList"]!);
        Assert.Equal(3, back.ExtraHeader["submodel_id"]);
    }

    [Fact]
    public void ABlockPastTheEndIsAFormatError()
    {
        var bytes = MeshCodec.Encode(Model());
        Assert.Throws<MeshFormatException>(() => MeshCodec.Decode(bytes.Take(bytes.Length - 5).ToArray()));
    }

    /// <summary>Inflates one block of an asset (test helper).</summary>
    internal static byte[] RawBlock(byte[] asset, string key)
    {
        var header = (IDictionary<string, object?>)LlsdBinary.Read(asset, out int hl)!;
        var e = (IDictionary<string, object?>)header[key]!;
        int off = (int)e["offset"]!, size = (int)e["size"]!;
        using var z = new ZLibStream(new MemoryStream(asset, hl + off, size), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }
}
