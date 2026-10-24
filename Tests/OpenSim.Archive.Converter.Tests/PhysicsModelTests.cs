/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.MeshAsset;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// A converted part must collide as the prim it replaced. The region (Jolt scene) builds a static prim's collision shape from
/// <c>Meshmerizer.CreateMesh(name, shape, size, 32, false, ...)</c> and a static mesh part's from the same call with the mesh
/// flag set (JoltScene.CookMeshShapeCore); the Meshmerizer reads a mesh asset's physics_mesh block before any display level
/// (Meshmerizer.GenerateCoordsAndFacesFromPrimMeshData). These tests make that call for the prim and for the converted part, with
/// separate mesher instances so no cache is shared, and compare the triangles. The Jolt scene itself needs the native physics
/// library and is not called; what it passes to the mesher is what is called here.
/// </summary>
public class PhysicsModelTests
{
    private static KitPart Fixture(string shape, string modifier, string name, string scale = "2,3,4") =>
        new KitPart(name) { Shape = FaceMatrix.Shape(shape, modifier), Scale = scale };

    private static ConvertResult Convert(string source, out string output)
    {
        output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        return new ArchiveConverter(new ConvertOptions { SculptMaps = new FakeSculptMaps() }).Convert(source, output);
    }

    private static Dictionary<Guid, SceneObjectPart> LoadParts(string archive, Dictionary<string, byte[]> assets = null)
    {
        Dictionary<Guid, SceneObjectPart> parts = new Dictionary<Guid, SceneObjectPart>();
        using FileStream fs = File.OpenRead(archive);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        TarArchiveReader tar = new TarArchiveReader(gz);
        byte[] data;
        while ((data = tar.ReadEntry(out string name, out TarArchiveReader.TarEntryType type)) != null)
        {
            if (type != TarArchiveReader.TarEntryType.TYPE_NORMAL_FILE)
                continue;
            if (assets != null && name.StartsWith("assets/", StringComparison.Ordinal) && name.EndsWith("_mesh.llmesh", StringComparison.Ordinal))
                assets[name] = data;
            if (!name.StartsWith("objects/", StringComparison.Ordinal))
                continue;
            foreach (XElement part in XDocument.Parse(Encoding.UTF8.GetString(data)).Descendants("SceneObjectPart"))
            {
                using XmlReader r = XmlReader.Create(new StringReader(part.ToString(SaveOptions.DisableFormatting)));
                r.MoveToContent();
                SceneObjectPart sop = SceneObjectPart.FromXml(r);
                parts[sop.UUID.Guid] = sop;
            }
        }
        return parts;
    }

    /// <summary>The physics mesh of the prim, as the Jolt scene asks for it (shape, size, detail 32, a prim passes false for the mesh flag).</summary>
    private static (float[] positions, int[] indices) RegionMesh(PrimitiveBaseShape shape, Vector3 size, bool meshFlag)
    {
        // not cached: the mesher keeps a mesh for the life of the process, and each test wants its own answer
        IMesh mesh = new Meshmerizer().CreateMesh("jolt-prim", shape, size, 32f, meshFlag, false);
        Assert.NotNull(mesh);
        return (mesh.getVertexListAsFloat(), mesh.getIndexListAsInt());
    }

    /// <summary>Converts the one part and returns it before and after, with its mesh asset's bytes.</summary>
    private static (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) ConvertOne(KitPart part, Dictionary<string, byte[]> archiveAssets = null)
    {
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { part } }, archiveAssets);
        SceneObjectPart before = LoadParts(source)[part.Id];
        ConvertResult r = Convert(source, out string output);
        Assert.True(r.Pass, r.ToText());
        Assert.True(r.Converted.Count == 1, r.ToText());
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        SceneObjectPart after = LoadParts(output, assets)[part.Id];
        // a mesh part's shape never carries the asset's bytes; the region fetches them by id
        byte[] asset = assets["assets/" + r.Converted[0].MeshId.ToString("D") + "_mesh.llmesh"];
        return (before, after, asset, r.Converted[0]);
    }

    private static void AssertSameTriangles((float[] positions, int[] indices) prim, (float[] positions, int[] indices) mesh, Vector3 size)
    {
        // 16-bit positions across the geometry's box: well under 1e-4 of a unit, times the part's size. The mesher puts several
        // vertices a rounding error apart at a sphere's pole and along a seam; the asset's 16-bit positions make them one point
        // and the reader drops the triangles that collapse, which have no area and so no surface.
        float biggest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        Assert.True(MeshValidator.SameCollision(prim.positions, prim.indices, mesh.positions, mesh.indices,
            MeshValidator.PhysicsTolerance * biggest, MeshValidator.CollapsedArea * biggest * biggest, out string why), why);
    }

    public static IEnumerable<object[]> Shapes()
    {
        yield return new object[] { "room", "box", "hollow", "4,4,3" };
        yield return new object[] { "doorway", "box", "hollow+path-cut", "4,4,3" };
        yield return new object[] { "ring", "torus", "plain", "3,3,3" };
        yield return new object[] { "tube", "tube", "plain", "2,2,5" };
        yield return new object[] { "disc", "cylinder", "plain", "4,4,0.25" };
        yield return new object[] { "prism ramp", "prism", "plain", "3,2,1" };
        yield return new object[] { "tapered wedge", "box", "taper", "3,2,1" };
        yield return new object[] { "arch", "cylinder", "hollow+profile-cut", "3,3,3" };
        yield return new object[] { "sphere", "sphere", "plain", "2,2,2" };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AConvertedPartsPhysicsTrianglesAreThePrimsPhysicsTriangles(string label, string shape, string modifier, string scale)
    {
        _ = label;
        KitPart part = Fixture(shape, modifier, "Fixture", scale);
        (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) = ConvertOne(part);

        Assert.True(converted.PhysicsTriangles > 0);
        Assert.Equal(before.Scale, after.Scale);

        // the prim as the region cooks it, and the converted part cooked the way a static mesh part is
        (float[] positions, int[] indices) prim = RegionMesh(before.Shape, before.Scale, false);
        after.Shape.SculptData = (byte[])asset.Clone();
        (float[] positions, int[] indices) mesh = RegionMesh(after.Shape, after.Scale, true);

        Assert.True(mesh.indices.Length <= converted.PhysicsTriangles * 3);   // collapsed slivers are not read back
        AssertSameTriangles(prim, mesh, before.Scale);

        // every reader that can be called yields the shape
        MeshAssetModel decoded = MeshCodec.Decode(asset);
        Assert.NotNull(decoded.PhysicsMesh);
        Assert.True(PhysicsBlock.ReadBoundingVerts(decoded.PhysicsConvex).Count >= 4);
        PhysicsGeometry unit = PrimPhysics.Build(part.Shape, null, null, out string problem);
        Assert.Null(problem);
        Assert.Empty(MeshValidator.Check(asset, FaceBuilder.FromPrim(part.Shape, out _), out MeshValidator.Reading reading, unit));
        Assert.True(reading.RegionTriangles > 0 && reading.RegionTriangles <= converted.PhysicsTriangles);
    }

    [Fact]
    public void ATwistedAndTaperedPartKeepsItsPrimCollision()
    {
        KitPart part = Fixture("box", "hollow+twist", "Twisted", "3,3,3");
        part.Shape.PathScaleX = 140;
        part.Shape.PathScaleY = 60;
        (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) = ConvertOne(part);

        (float[] positions, int[] indices) prim = RegionMesh(before.Shape, before.Scale, false);
        after.Shape.SculptData = asset;
        AssertSameTriangles(prim, RegionMesh(after.Shape, after.Scale, true), before.Scale);
        Assert.Equal(prim.indices.Length / 3, converted.PhysicsTriangles);   // what was written is what the region's mesher made
    }

    [Fact]
    public void WithoutThePhysicsBlockTheRegionCollidesWithAThinnedDisplayLevel()
    {
        // what a converted part did before the physics model: the display faces with no physics_mesh block, which the region's
        // mesher answers from the medium level of detail, a third of the triangles, so a hollow room is not the prim's room
        KitPart part = Fixture("box", "hollow", "Room", "4,4,3");
        string source = EligibilityKit.WriteArchive(part);
        SceneObjectPart before = LoadParts(source)[part.Id];
        List<EncoderFace> faces = FaceBuilder.FromPrim(FaceMatrix.Shape("box", "hollow"), out _);
        byte[] withoutPhysics = new MeshAssetEncoder().Encode(faces, new EncodeOptions());

        PrimitiveBaseShape shape = new PrimitiveBaseShape { SculptEntry = true, SculptType = (byte)SculptType.Mesh, SculptData = withoutPhysics, SculptTexture = UUID.Random() };
        (float[] positions, int[] indices) thinned = RegionMesh(shape, before.Scale, true);
        (float[] positions, int[] indices) prim = RegionMesh(before.Shape, before.Scale, false);
        Assert.NotEqual(prim.indices.Length, thinned.indices.Length);
    }

    [Fact]
    public void AMeshPartWhoseAssetHasNotBeenFetchedYetGetsNoMeshFromTheMesher()
    {
        // the "IMesher returned null" line the Jolt scene logs for every mesh part: the first cook is before the asset arrives
        KitPart part = Fixture("cylinder", "plain", "Disc", "4,4,0.25");
        (SceneObjectPart _, SceneObjectPart after, byte[] asset, ConvertedPart _) = ConvertOne(part);

        Assert.Null(new Meshmerizer().CreateMesh("jolt-prim", after.Shape, after.Scale, 32f, true, false));
        after.Shape.SculptData = asset;
        Assert.NotNull(new Meshmerizer().CreateMesh("jolt-prim", after.Shape, after.Scale, 32f, true, false));
    }

    [Fact]
    public void ARootPartWithNoPhysicsShapeTypeBecomesPrimTypeSoItIsNotFilledByAConvexHull()
    {
        KitPart part = Fixture("box", "hollow", "RootNone");
        part.PhysicsShapeType = 1;
        (SceneObjectPart before, SceneObjectPart after, _, ConvertedPart converted) = ConvertOne(part);
        Assert.Equal((byte)PhysShapeType.none, before.PhysicsShapeType);
        Assert.Equal((byte)PhysShapeType.prim, after.PhysicsShapeType);
        Assert.Equal(1, converted.PhysicsShapeTypeBefore);
        Assert.Equal(0, converted.PhysicsShapeTypeAfter);
    }

    [Fact]
    public void AChildPartWithNoPhysicsShapeTypeKeepsItExactly()
    {
        KitPart root = Fixture("box", "plain", "Root");
        KitPart child = Fixture("box", "hollow", "ChildNone");
        child.LinkNum = 2;
        child.PhysicsShapeType = 1;
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { root, child } });
        ConvertResult r = Convert(source, out string output);
        Assert.True(r.Pass, r.ToText());
        Assert.Equal(2, r.Converted.Count);
        Dictionary<Guid, SceneObjectPart> after = LoadParts(output);
        Assert.Equal((byte)PhysShapeType.none, after[child.Id].PhysicsShapeType);
        ConvertedPart c = r.Converted.Single(x => x.PartId == child.Id);
        Assert.Equal(1, c.PhysicsShapeTypeAfter);
        Assert.True(c.PhysicsTriangles > 0);   // the model is written all the same
    }

    [Theory]
    [InlineData(-1, (byte)PhysShapeType.prim)]
    [InlineData(0, (byte)PhysShapeType.prim)]
    [InlineData(2, (byte)PhysShapeType.convex)]
    public void APrimOrConvexPhysicsShapeTypeIsKept(int stored, byte expected)
    {
        KitPart part = Fixture("box", "hollow", "Typed");
        part.PhysicsShapeType = stored;
        (_, SceneObjectPart after, _, _) = ConvertOne(part);
        Assert.Equal(expected, after.PhysicsShapeType);
    }

    [Fact]
    public void APhantomPartKeepsItsFlagsAndStillCarriesTheModel()
    {
        KitPart part = Fixture("box", "hollow", "Ghost");
        part.Flags = "Phantom";
        (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) = ConvertOne(part);
        Assert.True(before.Flags.HasFlag(PrimFlags.Phantom));
        Assert.Equal(before.Flags, after.Flags);
        Assert.NotNull(MeshCodec.Decode(asset).PhysicsMesh);
        Assert.True(converted.PhysicsTriangles > 0);
    }

    [Fact]
    public void ASculptsPhysicsModelIsTheRegionsSculptMeshNotTheDisplayFace()
    {
        KitPart sculpt = new KitPart("Sculpt") { Shape = FaceMatrix.Shape("sphere", "plain"), Scale = "2,3,4", SculptTexture = Guid.NewGuid() };
        sculpt.Shape.SculptEntry = true;
        sculpt.Shape.SculptType = (int)SculptType.Sphere;
        (SceneObjectPart before, SceneObjectPart after, byte[] asset, ConvertedPart converted) = ConvertOne(sculpt, new Dictionary<string, byte[]> { ["assets/" + sculpt.SculptTexture + "_texture.jp2"] = new byte[] { 1, 2, 3 } });
        Assert.True(converted.PhysicsTriangles > 0);
        after.Shape.SculptData = asset;
        IMesh mesh = new Meshmerizer().CreateMesh("jolt-prim", after.Shape, after.Scale, 32f, true, false);
        Assert.NotNull(mesh);
        Assert.InRange(mesh.getIndexListAsInt().Length / 3, 1, converted.PhysicsTriangles);
    }
}
