/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// Real mesh assets read from the assets folders of OAR archives (kept outside the repository in the reference
/// folder). Skipped when the folder is absent.
/// </summary>
public class RealAssetTests
{
    public static IEnumerable<object[]> Rows() => Refs.CorpusRows();

    [CorpusTheory]
    [MemberData(nameof(Rows))]
    public void DecodeEncodeDecodeKeepsEverything(string file)
    {
        var original = File.ReadAllBytes(Path.Combine(Refs.MeshCorpus, file));
        var first = MeshCodec.Decode(original);
        var encoded = MeshCodec.Encode(first);
        var second = MeshCodec.Decode(encoded);
        MeshCompare.AssertSame(first, second);
    }

    [CorpusFact]
    public void AtLeastTenAssetsIncludingRiggedAndMultiFace()
    {
        var models = Refs.CorpusFiles().Select(f => MeshCodec.Decode(File.ReadAllBytes(f))).ToList();
        Assert.True(models.Count >= 10, $"{models.Count} assets");
        Assert.Contains(models, m => m.Skin != null);
        Assert.Contains(models, m => m.Skin == null);
        Assert.Contains(models, m => m.High!.Faces.Count > 1);
        Assert.Contains(models, m => m.High!.Faces.Count == 1);
    }
}

/// <summary>Deep comparison of two decoded meshes: every quantised value, index, weight and skin field.</summary>
static class MeshCompare
{
    public static void AssertSame(MeshAssetModel a, MeshAssetModel b)
    {
        Assert.Equal(a.Version, b.Version);
        for (int i = 0; i < MeshAssetModel.LodCount; i++)
        {
            var x = a.Lod(i); var y = b.Lod(i);
            Assert.Equal(x == null, y == null);
            if (x == null) continue;
            Assert.Equal(x.Faces.Count, y!.Faces.Count);
            for (int f = 0; f < x.Faces.Count; f++) Face(x.Faces[f], y.Faces[f], $"lod {i} face {f}");
        }
        Assert.Equal(a.Skin == null, b.Skin == null);
        if (a.Skin != null)
        {
            Assert.Equal(a.Skin.JointNames, b.Skin!.JointNames);
            Assert.Equal(a.Skin.InverseBind.Count, b.Skin.InverseBind.Count);
            for (int k = 0; k < a.Skin.InverseBind.Count; k++) Assert.Equal(a.Skin.InverseBind[k], b.Skin.InverseBind[k]);
            Assert.Equal(a.Skin.BindShape, b.Skin.BindShape);
            Assert.Equal(a.Skin.AltInverseBind?.Count, b.Skin.AltInverseBind?.Count);
            if (a.Skin.AltInverseBind != null)
                for (int k = 0; k < a.Skin.AltInverseBind.Count; k++) Assert.Equal(a.Skin.AltInverseBind[k], b.Skin.AltInverseBind![k]);
            Assert.Equal(a.Skin.PelvisOffset, b.Skin.PelvisOffset);
            Assert.Equal(a.Skin.LockScaleIfJointPosition, b.Skin.LockScaleIfJointPosition);
        }
        Assert.Equal(Convert.ToHexString(a.PhysicsConvex == null ? Array.Empty<byte>() : LlsdBinary.Write(a.PhysicsConvex)),
                     Convert.ToHexString(b.PhysicsConvex == null ? Array.Empty<byte>() : LlsdBinary.Write(b.PhysicsConvex)));
        Assert.Equal(Convert.ToHexString(a.PhysicsMesh == null ? Array.Empty<byte>() : LlsdBinary.Write(a.PhysicsMesh)),
                     Convert.ToHexString(b.PhysicsMesh == null ? Array.Empty<byte>() : LlsdBinary.Write(b.PhysicsMesh)));
    }

    public static void Face(MeshFace x, MeshFace y, string tag)
    {
        Assert.True(x.NoGeometry == y.NoGeometry, tag + " NoGeometry");
        if (x.NoGeometry) return;
        Assert.True(x.Positions.SequenceEqual(y.Positions), tag + " positions");
        Assert.True((x.Normals == null) == (y.Normals == null), tag + " normals present");
        if (x.Normals != null) Assert.True(x.Normals.SequenceEqual(y.Normals!), tag + " normals");
        Assert.True((x.Uvs == null) == (y.Uvs == null), tag + " uvs present");
        if (x.Uvs != null) Assert.True(x.Uvs.SequenceEqual(y.Uvs!), tag + " uvs");
        Assert.True(x.Indices.SequenceEqual(y.Indices), tag + " indices");
        Assert.True((x.Weights == null) == (y.Weights == null), tag + " weights present");
        if (x.Weights != null)
        {
            Assert.Equal(x.Weights.Length, y.Weights!.Length);
            for (int v = 0; v < x.Weights.Length; v++)
                Assert.True(x.Weights[v].SequenceEqual(y.Weights[v]), $"{tag} weights of vertex {v}");
        }
        Assert.Equal(x.PositionDomain, y.PositionDomain);
        Assert.Equal(x.UvDomain, y.UvDomain);
        Assert.Equal(x.NormalizedScale, y.NormalizedScale);
    }
}

/// <summary>Writes a per-asset account of the round trip, including byte differences, next to the reference data.</summary>
public class RealAssetReport
{
    [CorpusFact]
    public void WritesTheRoundTripReport()
    {
        var lines = new List<string> { "file\tbytes\tlods\tfaces\tverts\tskin_joints\tweights\tphysics\tourBytes\tlodBlocksRawEqual\tskinRawEqual\tphysicsRawEqual\tcompressedEqual\theaderKeys" };
        foreach (var f in Refs.CorpusFiles())
        {
            var orig = File.ReadAllBytes(f);
            var m = MeshCodec.Decode(orig);
            var ours = MeshCodec.Encode(m);
            string Raw(byte[] a, string key) => Convert.ToHexString(SafeBlock(a, key));
            string[] lodKeys = { "lowest_lod", "low_lod", "medium_lod", "high_lod" };
            bool lodEq = lodKeys.All(k => Raw(orig, k) == Raw(ours, k));
            bool skinEq = Raw(orig, "skin") == Raw(ours, "skin");
            bool physEq = Raw(orig, "physics_convex") == Raw(ours, "physics_convex") && Raw(orig, "physics_mesh") == Raw(ours, "physics_mesh");
            var header = (IDictionary<string, object?>)LlsdBinary.Read(orig, out _)!;
            var faces = m.High!.Faces;
            lines.Add(string.Join('\t', Path.GetFileName(f), orig.Length,
                lodKeys.Count(k => header.ContainsKey(k)), faces.Count, faces.Sum(x => x.VertexCount),
                m.Skin?.JointNames.Count ?? 0, faces.Any(x => x.Weights != null),
                (header.ContainsKey("physics_convex") ? "convex " : "") + (header.ContainsKey("physics_mesh") ? "mesh" : ""),
                ours.Length, lodEq, skinEq, physEq, orig.AsSpan().SequenceEqual(ours), string.Join(',', header.Keys)));
        }
        File.WriteAllLines(Path.Combine(Refs.Root, "work", "roundtrip-report.tsv"), lines);
    }

    static byte[] SafeBlock(byte[] asset, string key)
    {
        var header = (IDictionary<string, object?>)LlsdBinary.Read(asset, out _)!;
        return header.ContainsKey(key) ? MeshCodecTests.RawBlock(asset, key) : Array.Empty<byte>();
    }
}
