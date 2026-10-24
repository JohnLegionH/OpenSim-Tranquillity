/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using Path = System.IO.Path;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// Our encoder's output read by LibreMetaverse's FacetedMesh decoder (3.1.4, the one the web viewer gateway uses),
/// a decoder written by someone else. Positions and UVs are compared after the decoder's own 16-bit expansion.
/// </summary>
public class IndependentDecoderTests
{
    static FacetedMesh Decode(byte[] asset, DetailLevel level = DetailLevel.Highest)
    {
        var a = new AssetMesh(UUID.Random(), asset);
        Assert.True(a.Decode(), "AssetMesh.Decode");
        Assert.True(FacetedMesh.TryDecodeFromAsset(new Primitive(), a, level, out var mesh), "FacetedMesh.TryDecodeFromAsset");
        return mesh!;
    }

    static Vector3 N(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);

    static MeshAssetModel RiggedModel()
    {
        var f = new MeshFace
        {
            Positions = new[] { new Vector3(-0.5f, -0.25f, 0), new Vector3(0.5f, -0.5f, 0.25f), new Vector3(0, 0.5f, 0.5f), new Vector3(0.25f, 0.25f, -0.5f) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, Vector3.Normalize(new Vector3(1, 1, 1)) },
            Uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(0.5f, 0.5f) },
            Indices = new[] { 0, 1, 2, 2, 1, 3 },
            Weights = new[]
            {
                new[] { new Influence(0, 1f) },
                new[] { new Influence(0, 0.6f), new Influence(1, 0.4f) },
                new[] { new Influence(2, 0.5f), new Influence(1, 0.25f), new Influence(0, 0.25f) },
                new[] { new Influence(3, 0.4f), new Influence(2, 0.3f), new Influence(1, 0.2f), new Influence(0, 0.1f) },
            },
            NormalizedScale = new Vector3(2, 3, 4),
        };
        var m = new MeshAssetModel { High = new MeshLod() };
        m.High.Faces.Add(f);
        m.Skin = new MeshSkin
        {
            JointNames = { "mPelvis", "mTorso", "mChest", "mNeck" },
            InverseBind = Enumerable.Range(0, 4).Select(i => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -i * 0.5, 0, 0.25, 1 }).ToList(),
            BindShape = new double[] { 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 0.5, 0.25, 1, 1 },
        };
        return m;
    }

    [Fact]
    public void ReadsFacesPositionsUvsIndicesAndWeights()
    {
        var model = RiggedModel();
        var mesh = Decode(MeshCodec.Encode(model));
        var ours = MeshCodec.Decode(MeshCodec.Encode(model)).High!.Faces[0];
        var theirs = Assert.Single(mesh.Faces);
        Assert.Equal(4, theirs.Vertices.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Vector3.Distance(N(theirs.Vertices[i].Position), ours.Positions[i]) < 1e-4f, $"position {i}");
            Assert.True(Vector3.Distance(N(theirs.Vertices[i].Normal), ours.Normals![i]) < 1e-3f, $"normal {i}");
            Assert.True(Vector2.Distance(new Vector2(theirs.Vertices[i].TexCoord.X, theirs.Vertices[i].TexCoord.Y), ours.Uvs![i]) < 1e-4f, $"uv {i}");
        }
        Assert.Equal(ours.Indices, theirs.Indices.Select(x => (int)x).ToArray());
        Assert.Equal(4, theirs.Weights.Count);
        for (int i = 0; i < 4; i++)
        {
            var w = theirs.Weights[i];
            var expect = ours.Weights![i];
            var got = new[] { (w.Joint0, w.Weight0), (w.Joint1, w.Weight1), (w.Joint2, w.Weight2), (w.Joint3, w.Weight3) }.Take(expect.Length).ToArray();
            for (int k = 0; k < expect.Length; k++)
            {
                Assert.Equal(expect[k].Joint, got[k].Item1);
                Assert.True(Math.Abs(expect[k].Weight - got[k].Item2) < 2e-3f, $"weight {i}/{k}: {expect[k].Weight} vs {got[k].Item2}");
            }
        }
    }

    [Fact]
    public void ReadsTheSkinBlock()
    {
        var model = RiggedModel();
        var mesh = Decode(MeshCodec.Encode(model));
        var sd = mesh.SkinData;
        Assert.Equal(model.Skin!.JointNames, sd.JointNames.ToList());
        Assert.Equal(model.Skin.InverseBind.SelectMany(x => x).Select(x => (float)x).ToArray(), sd.InverseBindMatrices);
        Assert.Equal(model.Skin.BindShape.Select(x => (float)x).ToArray(), sd.BindShapeMatrix);
    }

    [Fact]
    public void ReadsEveryLevelOfDetailOfARepeatedHighBlock()
    {
        var model = RiggedModel();
        model.RepeatLodAbove();
        var bytes = MeshCodec.Encode(model);
        foreach (var level in new[] { DetailLevel.Highest, DetailLevel.High, DetailLevel.Medium, DetailLevel.Low })
            Assert.Equal(4, Decode(bytes, level).Faces[0].Vertices.Count);
    }

    [Fact]
    public void LibreMetaverseLeavesOutAFaceMarkedNoGeometry()
    {
        // It does not fail on the marker, but it drops the face, so later faces move down one slot in its list.
        var model = RiggedModel();
        model.High!.Faces.Add(new MeshFace { NoGeometry = true });
        var mesh = Decode(MeshCodec.Encode(model));
        Assert.Single(mesh.Faces);
    }

    [CorpusTheory]
    [MemberData(nameof(Rows))]
    public void ReadsOurReEncodeOfARealAssetLikeTheOriginal(string file)
    {
        var original = File.ReadAllBytes(Path.Combine(Refs.MeshCorpus, file));
        var ours = MeshCodec.Encode(MeshCodec.Decode(original));
        var a = Decode(original);
        var b = Decode(ours);
        Assert.Equal(a.Faces.Count, b.Faces.Count);
        for (int f = 0; f < a.Faces.Count; f++)
        {
            Assert.Equal(a.Faces[f].Vertices.Count, b.Faces[f].Vertices.Count);
            Assert.Equal(a.Faces[f].Indices, b.Faces[f].Indices);
            for (int i = 0; i < a.Faces[f].Vertices.Count; i++)
            {
                Assert.Equal(a.Faces[f].Vertices[i].Position, b.Faces[f].Vertices[i].Position);
                Assert.Equal(a.Faces[f].Vertices[i].TexCoord, b.Faces[f].Vertices[i].TexCoord);
            }
        }
        Assert.Equal(a.SkinData?.JointNames, b.SkinData?.JointNames);
        Assert.Equal(a.SkinData?.InverseBindMatrices, b.SkinData?.InverseBindMatrices);
        Assert.Equal(a.SkinData?.BindShapeMatrix, b.SkinData?.BindShapeMatrix);
    }

    public static IEnumerable<object[]> Rows() => Refs.CorpusRows();
}
