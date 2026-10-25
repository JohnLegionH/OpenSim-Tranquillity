/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenSim.Archive.Converter;
using OpenSim.Framework.Serialization;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>The converter with cleaning on (the default) and off: the faces it builds, the mesh asset's levels of detail, and the bytes.</summary>
public class CleanConvertTests
{
    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);

    /// <summary>The corners of a face's triangles, in order.</summary>
    private static (float[] p, float[] n, float[] t) Corners(EncoderFace f)
    {
        float[] p = new float[f.Indices.Length * 3], n = new float[f.Indices.Length * 3], t = new float[f.Indices.Length * 2];
        for (int i = 0; i < f.Indices.Length; i++)
        {
            int v = (int)f.Indices[i];
            Array.Copy(f.Positions, v * 3, p, i * 3, 3);
            Array.Copy(f.Normals, v * 3, n, i * 3, 3);
            Array.Copy(f.Uvs, v * 2, t, i * 2, 2);
        }
        return (p, n, t);
    }

    public static IEnumerable<object[]> Cases() => FaceMatrix.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void CleaningKeepsEveryFaceAndNumberAndEverySurvivingCornerWithinTheTolerances(string shape, string modifier)
    {
        var s = FaceMatrix.Shape(shape, modifier);
        List<EncoderFace> before = FaceBuilder.FromPrim(s, out _, clean: false), after = FaceBuilder.FromPrim(s, out _, clean: true);
        Assert.Equal(before.Count, after.Count);
        int vertsBefore = 0, vertsAfter = 0;
        for (int f = 0; f < before.Count; f++)
        {
            Assert.Equal(before[f].IsEmpty, after[f].IsEmpty);
            vertsBefore += before[f].VertexCount; vertsAfter += after[f].VertexCount;
            if (before[f].IsEmpty)
                continue;
            var b = Corners(before[f]);
            var a = Corners(after[f]);
            int next = 0;
            for (int t = 0; t < a.p.Length / 9; t++)
            {
                bool found = false;
                while (next < b.p.Length / 9 && !found)
                {
                    bool all = true;
                    for (int c = 0; c < 3 && all; c++)
                    {
                        int bi = next * 3 + c, ai = t * 3 + c;
                        all = Bits(b.t[bi * 2]) == Bits(a.t[ai * 2]) && Bits(b.t[bi * 2 + 1]) == Bits(a.t[ai * 2 + 1]);
                        for (int k = 0; k < 3 && all; k++)
                            all = Math.Abs(b.p[bi * 3 + k] - a.p[ai * 3 + k]) <= 1e-6f && Math.Abs(b.n[bi * 3 + k] - a.n[ai * 3 + k]) <= 1e-4f;
                    }
                    found = all;
                    next++;
                }
                Assert.True(found, "face " + f + " triangle " + t + " matches no triangle of the uncleaned face");
            }
        }
        Assert.True(vertsAfter <= vertsBefore);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ACleanedFaceHasNoVertexTheToleranceWouldWeldAndNoVertexNoTriangleUses(string shape, string modifier)
    {
        foreach (EncoderFace f in FaceBuilder.FromPrim(FaceMatrix.Shape(shape, modifier), out _, clean: true))
        {
            bool[] used = new bool[f.VertexCount];
            foreach (uint i in f.Indices)
                used[i] = true;
            Assert.All(used, u => Assert.True(u));
            for (int i = 0; i < f.VertexCount; i++)
                for (int j = i + 1; j < f.VertexCount; j++)
                {
                    bool same = Bits(f.Uvs[i * 2]) == Bits(f.Uvs[j * 2]) && Bits(f.Uvs[i * 2 + 1]) == Bits(f.Uvs[j * 2 + 1]);
                    for (int k = 0; k < 3 && same; k++)
                        same = Math.Abs(f.Positions[i * 3 + k] - f.Positions[j * 3 + k]) <= 1e-6f && Math.Abs(f.Normals[i * 3 + k] - f.Normals[j * 3 + k]) <= 1e-4f;
                    Assert.False(same, "vertices " + i + " and " + j + " agree");
                }
        }
    }

    // ------------------------------------------------------------ the converted asset

    private static (ConvertResult result, MeshAssetModel model) ConvertOne(bool clean, string shape = "sphere", string modifier = "hollow")
    {
        KitPart part = new KitPart("Fixture") { Id = Guid.Parse("c1ea0000-0000-4000-8000-000000000002"), Shape = FaceMatrix.Shape(shape, modifier), Scale = "2,3,4" };
        string source = EligibilityKit.WriteArchive(part);
        string output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        ConvertResult r = new ArchiveConverter(new ConvertOptions { Clean = clean, SculptMaps = new FakeSculptMaps() }).Convert(source, output);
        Assert.True(r.Pass, r.ToText());
        using FileStream fs = File.OpenRead(output);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        TarArchiveReader tar = new TarArchiveReader(gz);
        byte[] data;
        while ((data = tar.ReadEntry(out string name, out TarArchiveReader.TarEntryType type)) != null)
            if (name.EndsWith("_mesh.llmesh", StringComparison.Ordinal))
                return (r, MeshCodec.Decode(data));
        throw new InvalidOperationException("no mesh asset in the converted archive");
    }

    [Fact]
    public void EveryLevelOfDetailOfACleanedMeshKeepsTheFaceCount()
    {
        (ConvertResult r, MeshAssetModel m) = ConvertOne(clean: true, "torus", "hollow");
        int faces = r.Converted.Single().Faces;
        Assert.True(faces >= 2);
        for (int l = 0; l < MeshAssetModel.LodCount; l++)
            Assert.Equal(faces, m.Lod(l).Faces.Count);
    }

    [Fact]
    public void ACleanedMeshHasFewerVerticesAndTheSameTextureCoordinatesAsAnUncleanedOne()
    {
        (_, MeshAssetModel off) = ConvertOne(clean: false, "torus", "hollow");
        (_, MeshAssetModel on) = ConvertOne(clean: true, "torus", "hollow");
        Assert.Equal(off.High.Faces.Count, on.High.Faces.Count);
        Assert.True(on.High.Faces.Sum(f => f.VertexCount) < off.High.Faces.Sum(f => f.VertexCount));
        for (int f = 0; f < off.High.Faces.Count; f++)
        {
            // every texture coordinate the cleaned face holds is one the uncleaned face held, to the asset's 16-bit step
            HashSet<(int, int)> had = off.High.Faces[f].Uvs.Select(u => ((int)Math.Round(u.X * 30000), (int)Math.Round(u.Y * 30000))).ToHashSet();
            Assert.All(on.High.Faces[f].Uvs, u => Assert.Contains(((int)Math.Round(u.X * 30000), (int)Math.Round(u.Y * 30000)), had));
        }
    }

    [Fact]
    public void WithCleaningOffTheMeshIsByteForByteWhatTheConverterWroteBeforeCleaningExisted()
    {
        // the mesh's asset id is derived from its bytes
        Assert.Equal(Guid.Parse("9ffa004c-be33-84a0-a0fc-d9acc5befb3e"), ConvertOne(false).result.Converted.Single().MeshId);
    }

    [Fact]
    public void TheSameInputGivesTheSameMeshTwice()
    {
        Assert.Equal(ConvertOne(true).result.Converted.Single().MeshId, ConvertOne(true).result.Converted.Single().MeshId);
    }
}
