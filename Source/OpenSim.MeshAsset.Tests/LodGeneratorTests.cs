/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using OpenSim.MeshAsset;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.MeshAsset.Tests;

public class LodGeneratorTests
{
    /// <summary>
    /// A UV sphere in two faces (north and south of the equator). The longitude seam has duplicate vertices (so UVs are
    /// discontinuous there), and every vertex is weighted to two joints by height so weights can be checked.
    /// </summary>
    internal static MeshAssetModel Sphere(int lon = 48, int lat = 48, bool weights = true)
    {
        var m = new MeshAssetModel { High = new MeshLod() };
        for (int half = 0; half < 2; half++)
        {
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var w = new List<Influence[]>();
            var idx = new List<int>();
            int rows = lat / 2;
            for (int r = 0; r <= rows; r++)
            {
                int row = half * rows + r;
                double theta = Math.PI * row / lat;
                for (int c = 0; c <= lon; c++) // c == lon repeats c == 0: the UV seam
                {
                    double phi = 2 * Math.PI * c / lon;
                    var n = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta));
                    pos.Add(n * 0.5f); nrm.Add(n); uv.Add(new Vector2((float)c / lon, (float)row / lat));
                    float t = (n.Z + 1) / 2;
                    w.Add(new[] { new Influence(0, 1 - t), new Influence(1, t) });
                }
            }
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < lon; c++)
                {
                    int a = r * (lon + 1) + c, b = a + 1, d = a + lon + 1, e = d + 1;
                    idx.AddRange(new[] { a, d, b, b, d, e });
                }
            m.High.Faces.Add(new MeshFace
            {
                Positions = pos.ToArray(), Normals = nrm.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray(),
                Weights = weights ? w.ToArray() : null, NormalizedScale = new Vector3(2, 2, 2),
            });
        }
        if (weights)
            m.Skin = new MeshSkin { JointNames = { "mPelvis", "mTorso" }, InverseBind = { MeshSkin.Identity(), MeshSkin.Identity() } };
        return m;
    }

    static int Tris(MeshLod l) => l.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);

    [Fact]
    public void TriangleTargetsAreOneThirdOneNinthOneTwentySeventhOfTheHighLevel()
    {
        var m = Sphere();
        int high = Tris(m.High!);
        var results = LodGenerator.GenerateAll(m);
        Assert.True(m.LodsGenerated);
        Assert.Equal(3, results.Count);
        foreach (var r in results)
        {
            int divisor = (int)Math.Pow(3, 3 - r.Lod);
            int target = high / divisor;
            Assert.InRange(r.TargetTriangles, target - 1, target + 1);
            int got = Tris(m.Lod(r.Lod)!);
            Assert.True(got <= target * 1.1, $"lod {r.Lod}: {got} triangles, target {target}"); // meshoptimizer may land a triangle or two over
            Assert.True(got >= target * 0.8, $"lod {r.Lod}: {got} triangles, target {target}");
        }
        Assert.True(Tris(m.Medium!) > Tris(m.Low!) && Tris(m.Low!) > Tris(m.Lowest!));
    }

    [Fact]
    public void FacesStaySeparateAndEveryOutputVertexIsAnInputVertexWithItsOwnAttributes()
    {
        var m = Sphere();
        LodGenerator.GenerateAll(m);
        foreach (int lod in new[] { 0, 1, 2 })
        {
            var made = m.Lod(lod)!;
            Assert.Equal(m.High!.Faces.Count, made.Faces.Count);
            for (int fi = 0; fi < made.Faces.Count; fi++)
            {
                var src = m.High.Faces[fi]; var f = made.Faces[fi];
                Assert.False(f.NoGeometry);
                Assert.Equal(src.NormalizedScale, f.NormalizedScale);
                Assert.NotNull(f.Normals); Assert.NotNull(f.Uvs); Assert.NotNull(f.Weights);
                var inputs = new HashSet<(Vector3, Vector3, Vector2)>();
                for (int k = 0; k < src.VertexCount; k++) inputs.Add((src.Positions[k], src.Normals![k], src.Uvs![k]));
                for (int k = 0; k < f.VertexCount; k++)
                {
                    Assert.Contains((f.Positions[k], f.Normals![k], f.Uvs![k]), inputs);
                    // weights are those of the input vertex at the same place, whichever way the seam copies differ
                    float t = (f.Normals[k].Z + 1) / 2;
                    Assert.Equal(2, f.Weights![k].Length);
                    Assert.Equal(t, f.Weights[k].First(i => i.Joint == 1).Weight, 3);
                }
                foreach (var ix in f.Indices) Assert.InRange(ix, 0, f.VertexCount - 1);
                Assert.Equal(0, f.Indices.Length % 3);
            }
        }
    }

    [Fact]
    public void ALevelStaysCloseToTheShapeOfTheHighLevel()
    {
        var m = Sphere();
        LodGenerator.GenerateAll(m);
        foreach (int lod in new[] { 0, 1, 2 })
            foreach (var f in m.Lod(lod)!.Faces)
                foreach (var p in f.Positions)
                    Assert.InRange(p.Length(), 0.45f, 0.51f); // on or just inside the 0.5 sphere
    }

    [Fact]
    public void AMeshWithoutWeightsOrUvsGeneratesWithoutThem()
    {
        var m = Sphere(weights: false);
        foreach (var f in m.High!.Faces) { f.Uvs = null; f.Normals = null; }
        LodGenerator.GenerateAll(m);
        foreach (var f in m.Low!.Faces) { Assert.Null(f.Weights); Assert.Null(f.Uvs); Assert.Null(f.Normals); }
        Assert.True(Tris(m.Low!) < Tris(m.High));
    }

    [Fact]
    public void ATinyMeshIsKeptAndAFaceThatVanishesBecomesNoGeometry()
    {
        // one triangle in each of two faces: the target is below one triangle each, so the result is the viewer's
        // "at least one triangle", never an empty model
        var m = new MeshAssetModel { High = new MeshLod() };
        for (int i = 0; i < 2; i++)
            m.High.Faces.Add(new MeshFace
            {
                Positions = new[] { new Vector3(0, 0, i), new Vector3(1, 0, i), new Vector3(0, 1, i) },
                Indices = new[] { 0, 1, 2 },
            });
        LodGenerator.GenerateAll(m);
        Assert.True(Tris(m.Lowest!) >= 1);
        Assert.Equal(2, m.Lowest!.Faces.Count);
    }

    [Fact]
    public void GeneratedLevelsEncodeAsSeparateBlocksAndDecodeWithOurReader()
    {
        var m = Sphere();
        LodGenerator.GenerateAll(m);
        var bytes = MeshCodec.Encode(m);
        var header = (IDictionary<string, object?>)LlsdBinary.Read(bytes, out _)!;
        var offsets = new[] { "lowest_lod", "low_lod", "medium_lod", "high_lod" }.Select(k => (int)((IDictionary<string, object?>)header[k]!)["offset"]!).ToList();
        Assert.Equal(4, offsets.Distinct().Count());
        Assert.False(header.ContainsKey("version"));

        var back = MeshCodec.Decode(bytes);
        for (int lod = 0; lod < 4; lod++)
        {
            Assert.Equal(Tris(m.Lod(lod)!), Tris(back.Lod(lod)!));
            Assert.All(back.Lod(lod)!.Faces, f => Assert.NotNull(f.Weights));
        }
    }

    [Fact]
    public void LibreMetaverseReadsEveryGeneratedLevel()
    {
        var m = Sphere();
        LodGenerator.GenerateAll(m);
        var bytes = MeshCodec.Encode(m);
        var levels = new[] { (DetailLevel.Low, 0), (DetailLevel.Medium, 1), (DetailLevel.High, 2), (DetailLevel.Highest, 3) };
        foreach (var (level, lod) in levels)
        {
            var a = new AssetMesh(UUID.Random(), bytes);
            Assert.True(a.Decode(), "AssetMesh.Decode");
            Assert.True(FacetedMesh.TryDecodeFromAsset(new Primitive(), a, level, out var mesh), $"decode {level}");
            Assert.Equal(2, mesh!.Faces.Count);
            Assert.Equal(Tris(m.Lod(lod)!), mesh.Faces.Sum(f => f.Indices.Count / 3));
        }
    }

    [Fact]
    public void GeneratedLevelsCostLessThanRepeatedOnes()
    {
        var gen = Sphere(); LodGenerator.GenerateAll(gen);
        var rep = Sphere(); rep.RepeatLodAbove();
        var g = StreamingCost.FromAsset(MeshCodec.Encode(gen));
        var r = StreamingCost.FromAsset(MeshCodec.Encode(rep));
        Assert.True(g.ChargedTriangles < r.ChargedTriangles, $"{g.ChargedTriangles} vs {r.ChargedTriangles}");
        Assert.True(g.RadiusWeighted(1f) < r.RadiusWeighted(1f));
    }

    [Fact]
    public void TheCharacterProfileAimsForTheMediansOfHandAuthoredLevels()
    {
        var m = Sphere(lon: 96, lat: 96);
        int high = Tris(m.High!);
        var results = LodGenerator.GenerateAll(m, profile: LodGenerator.Profile.Character);
        Assert.Equal(3, results.Count);
        Assert.InRange(results.Single(r => r.Lod == 2).TargetTriangles, high * 0.25 - 1, high * 0.25 + 1); // the target is a whole number
        Assert.InRange(results.Single(r => r.Lod == 0).TargetTriangles, high * 0.0035848 - 1, high * 0.0035848 + 1);
        // the low level has a floor of the viewer's 1/27 of the high level, above the corpus median
        Assert.InRange(results.Single(r => r.Lod == 1).TargetTriangles, high / 27.0 - 1, high / 27.0 + 1);
        Assert.InRange(Tris(m.Medium!), high * 0.25 * 0.8, high * 0.25 * 1.1);
        Assert.True(Tris(m.Low!) >= 1 && Tris(m.Low!) < Tris(m.Medium!));
        Assert.True(Tris(m.Lowest!) < Tris(m.Low!));
    }

    [Fact]
    public void ARiggedMeshDefaultsToTheCharacterProfileAndAnUnriggedOneToTheViewers()
    {
        Assert.Equal(LodGenerator.Profile.Character, LodGenerator.DefaultProfile(Sphere(weights: true)));
        Assert.Equal(LodGenerator.Profile.Viewer, LodGenerator.DefaultProfile(Sphere(weights: false)));
    }

    [Fact]
    public void TheViewerProfileKeepsTheOneThirdOneNinthOneTwentySeventhFractions()
    {
        Assert.Equal(1f / 3, LodGenerator.Fraction(LodGenerator.Profile.Viewer, 2), 5);
        Assert.Equal(1f / 9, LodGenerator.Fraction(LodGenerator.Profile.Viewer, 1), 5);
        Assert.Equal(1f / 27, LodGenerator.Fraction(LodGenerator.Profile.Viewer, 0), 5);
    }
}
