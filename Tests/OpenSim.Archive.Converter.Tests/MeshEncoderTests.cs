/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.MeshAsset;
using Xunit;
using LmAssetMesh = LibreMetaverse.Assets.AssetMesh;
using LmDetail = LibreMetaverse.Rendering.DetailLevel;
using LmFacetedMesh = LibreMetaverse.Rendering.FacetedMesh;
using LmPrimitive = LibreMetaverse.Primitive;
using LmUuid = LibreMetaverse.UUID;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The real encoder: the faces in order, lower levels of detail made with the viewer profile, a bounding-hull physics block,
/// the same bytes for the same faces, and a validity check that decodes the result with three readers (OpenSim.MeshAsset,
/// OpenSim's physics mesher and LibreMetaverse) and stops the converter from writing an archive when one fails.
/// Test classes here touch no process-wide state, so they run in parallel.
/// </summary>
public class MeshEncoderTests
{
    private static List<EncoderFace> Faces(string shape, string modifier) =>
        FaceBuilder.FromPrim(FaceMatrix.Shape(shape, modifier), out _);

    private static readonly (string shape, string modifier)[] Shapes =
    {
        ("box", "plain"), ("box", "hollow"), ("box", "hollow+profile-cut"), ("cylinder", "plain"), ("cylinder", "hollow"),
        ("prism", "plain"), ("sphere", "plain"), ("sphere", "path-cut"), ("torus", "plain"), ("torus", "hollow+twist"),
        ("tube", "plain"), ("ring", "taper"), ("box", "twist"), ("cylinder", "taper"), ("sphere", "taper")
    };

    public static IEnumerable<object[]> Cases() => Shapes.Select(s => new object[] { s.shape, s.modifier });

    private static readonly MeshAssetEncoder Encoder = new MeshAssetEncoder();

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFacesComeBackInOrderWithTheirVerticesAndTriangles(string shape, string modifier)
    {
        List<EncoderFace> faces = Faces(shape, modifier);
        MeshAssetModel model = MeshCodec.Decode(Encoder.Encode(faces, new EncodeOptions()));

        Assert.Equal(faces.Count, model.High.Faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            MeshFace got = model.High.Faces[i];
            if (faces[i].IsEmpty)
            {
                Assert.True(got.NoGeometry || got.TriangleCount == 0, "face " + i + " should be an empty slot");
                continue;
            }
            Assert.Equal(faces[i].VertexCount, got.VertexCount);
            Assert.Equal(faces[i].TriangleCount, got.TriangleCount);
            Assert.Equal(faces[i].Indices.Select(x => (int)x), got.Indices);
            for (int v = 0; v < got.VertexCount; v++)
            {
                // the asset stores 16-bit values inside the face's box
                Assert.True(Math.Abs(got.Positions[v].X - faces[i].Positions[v * 3]) < 2e-4f, "position x");
                Assert.True(Math.Abs(got.Positions[v].Y - faces[i].Positions[v * 3 + 1]) < 2e-4f, "position y");
                Assert.True(Math.Abs(got.Positions[v].Z - faces[i].Positions[v * 3 + 2]) < 2e-4f, "position z");
                Assert.True(Math.Abs(got.Uvs[v].X - faces[i].Uvs[v * 2]) < 2e-4f, "u");
                Assert.True(Math.Abs(got.Uvs[v].Y - faces[i].Uvs[v * 2 + 1]) < 2e-4f, "v");
            }
        }
    }

    [Fact]
    public void AFaceWithNoTrianglesStaysAnEmptySlotAtEveryLevel()
    {
        // some shapes list a face that has no area (the cut faces of a sphere lie on its axis); find them in the probe matrix
        List<(string shape, string modifier)> withEmpty = new List<(string, string)>();
        foreach (string shape in FaceMatrix.Shapes)
            foreach (string modifier in FaceMatrix.Modifiers.Select(m => m.name))
            {
                List<EncoderFace> f = Faces(shape, modifier);
                if (f.Any(x => x.IsEmpty) && f.Any(x => !x.IsEmpty))
                    withEmpty.Add((shape, modifier));
            }
        Assert.NotEmpty(withEmpty);
        foreach ((string shape, string modifier) in withEmpty)
        {
            List<EncoderFace> faces = Faces(shape, modifier);
            byte[] bytes = Encoder.Encode(faces, new EncodeOptions());
            Assert.Empty(MeshValidator.Check(bytes, faces, out _));
            MeshAssetModel model = MeshCodec.Decode(bytes);
            for (int level = 0; level < MeshAssetModel.LodCount; level++)
                Assert.Equal(faces.Count, model.Lod(level).Faces.Count);
            for (int i = 0; i < faces.Count; i++)
                if (faces[i].IsEmpty)
                    Assert.True(model.High.Faces[i].NoGeometry || model.High.Faces[i].TriangleCount == 0, shape + " / " + modifier + " face " + i);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryLevelOfDetailIsThereAndTheLowerOnesAreSmallerOrEqual(string shape, string modifier)
    {
        List<EncoderFace> faces = Faces(shape, modifier);
        MeshAssetModel model = MeshCodec.Decode(Encoder.Encode(faces, new EncodeOptions()));
        int[] triangles = Enumerable.Range(0, MeshAssetModel.LodCount).Select(l =>
        {
            Assert.NotNull(model.Lod(l));
            Assert.Equal(faces.Count, model.Lod(l).Faces.Count);
            return model.Lod(l).Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        }).ToArray();
        // index 0 lowest .. 3 high
        Assert.Equal(faces.Sum(f => f.TriangleCount), triangles[3]);
        for (int l = 0; l < 3; l++)
            Assert.True(triangles[l] <= triangles[l + 1], "level " + l + " has more triangles than the level above it");
        Assert.True(triangles[0] >= 1);
    }

    [Fact]
    public void TheLowerLevelsFollowTheViewerProfile()
    {
        // a cylinder at the highest detail is a few dozen triangles; the profile aims at a third, a ninth and a twenty-seventh
        List<EncoderFace> faces = Faces("torus", "plain");
        MeshAssetModel model = MeshCodec.Decode(Encoder.Encode(faces, new EncodeOptions()));
        int high = model.High.Faces.Sum(f => f.TriangleCount);
        int medium = model.Medium.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        int low = model.Low.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        Assert.InRange(medium, 1, (int)(high / 3f * 1.8f) + 1);
        Assert.InRange(low, 1, (int)(high / 9f * 1.8f) + 1);
        Assert.True(medium < high && low < medium);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePhysicsBlockIsTheBoundingHullOfTheLowLevel(string shape, string modifier)
    {
        List<EncoderFace> faces = Faces(shape, modifier);
        MeshAssetModel model = MeshCodec.Decode(Encoder.Encode(faces, new EncodeOptions()));

        Assert.NotNull(model.PhysicsConvex);
        Assert.Null(model.PhysicsMesh);
        List<System.Numerics.Vector3> hull = PhysicsBlock.ReadBoundingVerts(model.PhysicsConvex);
        Assert.InRange(hull.Count, 4, PhysicsBlock.MaxHullVertices);

        // every hull point lies on or inside the box of the low level's vertices (the hull is made from them)
        List<System.Numerics.Vector3> low = model.Low.Faces.Where(f => !f.NoGeometry).SelectMany(f => f.Positions).ToList();
        System.Numerics.Vector3 min = low.Aggregate(System.Numerics.Vector3.Min), max = low.Aggregate(System.Numerics.Vector3.Max);
        foreach (System.Numerics.Vector3 p in hull)
        {
            Assert.True(p.X >= min.X - 1e-3f && p.X <= max.X + 1e-3f && p.Y >= min.Y - 1e-3f && p.Y <= max.Y + 1e-3f && p.Z >= min.Z - 1e-3f && p.Z <= max.Z + 1e-3f);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheSameFacesAlwaysGiveTheSameBytes(string shape, string modifier)
    {
        byte[] first = Encoder.Encode(Faces(shape, modifier), new EncodeOptions());
        for (int run = 0; run < 5; run++)
        {
            // the faces are built again each time, so nothing carried over between runs can make the bytes agree
            byte[] again = new MeshAssetEncoder().Encode(Faces(shape, modifier), new EncodeOptions());
            Assert.Equal(first, again);
        }
        Assert.Equal(MeshId.FromBytes(first), MeshId.FromBytes(Encoder.Encode(Faces(shape, modifier), new EncodeOptions())));
    }

    [Fact]
    public void TheSameFacesGiveTheSameBytesFromSeveralThreadsAtOnce()
    {
        byte[] want = Encoder.Encode(Faces("sphere", "plain"), new EncodeOptions());
        byte[][] got = new byte[8][];
        Parallel.For(0, got.Length, i => got[i] = new MeshAssetEncoder().Encode(Faces("sphere", "plain"), new EncodeOptions()));
        Assert.All(got, g => Assert.Equal(want, g));
    }

    // ------------------------------------------------------------ the validity check

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryEncodedMeshPassesTheValidityCheck(string shape, string modifier)
    {
        List<EncoderFace> faces = Faces(shape, modifier);
        byte[] bytes = Encoder.Encode(faces, new EncodeOptions());
        List<string> problems = MeshValidator.Check(bytes, faces, out MeshValidator.Reading reading);
        Assert.Empty(problems);
        Assert.Equal(faces.Count, reading.Faces);
        Assert.Equal(faces.Sum(f => f.TriangleCount), reading.HighTriangles);
        Assert.True(reading.RegionVertices > 0 && reading.RegionTriangles > 0);
    }

    [Fact]
    public void OpenSimsMesherReadsTheMediumLevelAsMeshOneDoes()
    {
        // with no physics mesh in the asset, the region's mesher falls back to the medium level for its physics shape
        List<EncoderFace> faces = Faces("torus", "plain");
        byte[] bytes = Encoder.Encode(faces, new EncodeOptions());
        MeshAssetModel model = MeshCodec.Decode(bytes);
        MeshValidator.Check(bytes, faces, out MeshValidator.Reading reading);
        Assert.Equal(model.Medium.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount), reading.RegionTriangles);
    }

    [Fact]
    public void ADamagedAssetFailsTheCheck()
    {
        List<EncoderFace> faces = Faces("box", "plain");
        byte[] bytes = Encoder.Encode(faces, new EncodeOptions());
        Assert.NotEmpty(MeshValidator.Check(bytes.Take(bytes.Length / 2).ToArray(), faces, out _));
        Assert.NotEmpty(MeshValidator.Check(new byte[] { 1, 2, 3, 4 }, faces, out _));
    }

    [Fact]
    public void AMeshWithAnotherFaceCountFailsTheCheck()
    {
        List<EncoderFace> faces = Faces("box", "plain");
        byte[] bytes = Encoder.Encode(faces.Take(faces.Count - 1).ToList(), new EncodeOptions());
        List<string> problems = MeshValidator.Check(bytes, faces, out _);
        Assert.Contains(problems, p => p.Contains("faces"));
    }

    private sealed class DropsALevelEncoder : IMeshEncoder
    {
        public string Name => "test encoder that leaves the lowest level out";
        public bool ProducesValidMesh => true;
        public byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options)
        {
            MeshAssetModel m = MeshCodec.Decode(new MeshAssetEncoder().Encode(faces, options));
            m.Lowest = null;
            return MeshCodec.Encode(m);
        }
    }

    [Fact]
    public void AMissingLevelOfDetailFailsTheCheck()
    {
        List<EncoderFace> faces = Faces("cylinder", "plain");
        // the codec fills a missing level on write only when asked to; if it does not, the check must see the gap
        byte[] bytes = new DropsALevelEncoder().Encode(faces, new EncodeOptions());
        MeshAssetModel decoded = MeshCodec.Decode(bytes);
        List<string> problems = MeshValidator.Check(bytes, faces, out _);
        if (decoded.Lowest == null)
            Assert.Contains(problems, p => p.Contains("lowest"));
        else
            Assert.Empty(problems); // the codec wrote the level above in its place, which is a complete asset
    }

    private sealed class WrongFaceCountEncoder : IMeshEncoder
    {
        public string Name => "test encoder that drops the last face";
        public bool ProducesValidMesh => true;
        public byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options) =>
            new MeshAssetEncoder().Encode(faces.Take(faces.Count - 1).ToList(), options);
    }

    [Fact]
    public void TheConverterWritesNothingWhenAMeshFailsAndNamesThePart()
    {
        KitPart good = new KitPart("Good", 0) { Shape = FaceMatrix.Shape("cylinder", "plain"), Scale = "1,1,1" };
        KitPart other = new KitPart("Other", 0) { Shape = FaceMatrix.Shape("box", "plain"), Scale = "1,1,1" };
        string source = EligibilityKit.WriteArchive(good, other);
        string output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        byte[] sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));

        ConvertResult r = new ArchiveConverter(new ConvertOptions { Encoder = new WrongFaceCountEncoder() }).Convert(source, output);

        Assert.False(File.Exists(output));
        Assert.False(r.Pass);
        Assert.Equal(2, r.Invalid.Count);
        Assert.Contains(r.Invalid, p => p.Name == "Good" && p.Problems.Any(x => x.Contains("faces")));
        Assert.Contains("NOTHING WAS WRITTEN", r.ToText());
        Assert.Contains("Good", r.ToText());
        Assert.Equal(sourceHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void TheConverterUsesTheRealEncoderByDefaultAndEveryMeshPasses()
    {
        string source = EligibilityKit.WriteArchive(
            new KitPart("A", 0) { Shape = FaceMatrix.Shape("torus", "plain"), Scale = "2,2,2" },
            new KitPart("B", 0) { Shape = FaceMatrix.Shape("sphere", "plain"), Scale = "2,2,2" },
            new KitPart("C", 0) { Shape = FaceMatrix.Shape("box", "hollow"), Scale = "2,2,2" });
        string output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        ConvertResult r = new ArchiveConverter().Convert(source, output);

        Assert.True(r.Pass, r.ToText());
        Assert.True(r.EncoderValid);
        Assert.Equal(3, r.MeshesAdded);
        Assert.Equal(3, r.MeshesValidated);
        Assert.Empty(r.Invalid);
        Assert.True(r.MeshBytes > 0);
        Assert.True(r.OutputBytes > 0 && r.SourceBytes > 0);
    }

    [Fact]
    public void TheStandInEncoderIsOnlyUsedWhenAskedForAndIsReportedAsNotValid()
    {
        string source = EligibilityKit.WriteArchive(new KitPart("A", 0) { Shape = FaceMatrix.Shape("box", "plain"), Scale = "1,1,1" });
        string output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        ConvertResult r = new ArchiveConverter(new ConvertOptions { Encoder = new StandInMeshEncoder() }).Convert(source, output);
        Assert.False(r.EncoderValid);
        Assert.Contains("NOT VALID MESHES", r.ToText());
        Assert.Equal(0, r.MeshesValidated);
    }

    // ------------------------------------------------------------ an independent decoder

    private static LmFacetedMesh DecodeWithLibreMetaverse(byte[] asset, LmDetail level)
    {
        LmAssetMesh a = new LmAssetMesh(LmUuid.Random(), asset);
        Assert.True(a.Decode(), "LibreMetaverse AssetMesh.Decode");
        Assert.True(LmFacetedMesh.TryDecodeFromAsset(new LmPrimitive(), a, level, out LmFacetedMesh mesh), "LibreMetaverse FacetedMesh.TryDecodeFromAsset at " + level);
        return mesh;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LibreMetaverseDecodesEveryLevelAsTheAssetHoldsIt(string shape, string modifier)
    {
        List<EncoderFace> faces = Faces(shape, modifier);
        byte[] bytes = Encoder.Encode(faces, new EncodeOptions());
        MeshAssetModel model = MeshCodec.Decode(bytes);

        // LibreMetaverse's levels are the asset's blocks from the top: Highest is the high block, then medium, low and lowest.
        // It leaves a face with no triangles out of its list (so later faces move down a slot); the viewer's reduction to
        // 1/27 of the triangles can leave a face with none, so each level is compared with the faces that have geometry.
        (LmDetail level, int block)[] levels = { (LmDetail.Highest, 3), (LmDetail.High, 2), (LmDetail.Medium, 1), (LmDetail.Low, 0) };
        foreach ((LmDetail level, int block) in levels)
        {
            List<MeshFace> held = model.Lod(block).Faces.Where(f => !f.NoGeometry && f.TriangleCount > 0).ToList();
            LmFacetedMesh m = DecodeWithLibreMetaverse(bytes, level);
            Assert.Equal(held.Count, m.Faces.Count);
            for (int i = 0; i < held.Count; i++)
            {
                Assert.Equal(held[i].VertexCount, m.Faces[i].Vertices.Count);
                Assert.Equal(held[i].TriangleCount, m.Faces[i].Indices.Count / 3);
            }
        }

        // the high level has every face the prim has that has area, in order
        LmFacetedMesh top = DecodeWithLibreMetaverse(bytes, LmDetail.Highest);
        Assert.Equal(faces.Count(f => !f.IsEmpty), top.Faces.Count);
    }
}
