/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Restore.Import;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using SkiaSharp;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// oar-import: a folder of .glb files (a level of detail each, a physics shape, pictures inside the files) becomes an archive holding one mesh object.
/// Each test writes its own folder and archive and shares nothing, so the class runs in parallel with others.
/// </summary>
public class ObjectImportTests
{
    private static ImportOptions Options(string name = "Test Slab", string description = "A slab for the import tests") => new ImportOptions
    {
        Name = name, Description = description, Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600
    };

    private static (ImportResult Result, Dictionary<string, byte[]> Oar, ImportKit.Folder Folder) Run(bool normalMap = false, ImportKit.PhysicsKind physics = ImportKit.PhysicsKind.Box)
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap, physics);
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.Import(folder.Path, output, Options());
        Assert.True(File.Exists(output), "the archive was not written");
        return (result, ImportKit.ReadOar(output), folder);
    }

    private static MeshAssetModel Mesh(ImportResult result, Dictionary<string, byte[]> oar) => MeshCodec.Decode(oar["assets/" + result.MeshId.ToString("D") + "_mesh.llmesh"]);

    private static int Triangles(MeshLod level) => level.Faces.Where(f => !f.NoGeometry).Sum(f => f.Indices.Length / 3);

    private static byte[] Pixels(byte[] encoded, bool jpeg2000 = false)
    {
        SKBitmap bitmap;
        Assert.True((jpeg2000 ? SkiaImageUtils.TryDecodeFromJ2K(encoded, out bitmap) : SkiaImageUtils.TryDecodeFromBytes(encoded, out bitmap)) && bitmap != null);
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
            return rgba.Bytes;
    }

    [Fact]
    public void ImportWritesOneObjectOnePartNamedAndCreditedToTheAvatarWithFullPermissions()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        Assert.True(result.Written);
        Assert.Equal("archive.xml", oar.Keys.First());
        Assert.Single(oar.Keys, k => k.StartsWith("objects/", StringComparison.Ordinal));
        XElement part = ImportKit.Part(oar);
        Assert.Equal("Test Slab", ImportKit.Text(part, "Name"));
        Assert.Equal("A slab for the import tests", ImportKit.Text(part, "Description"));
        string avatar = ImportKit.Avatar.ToString("D");
        Assert.Equal(avatar, ImportKit.Text(part, "CreatorID/UUID"));
        Assert.Equal(avatar, ImportKit.Text(part, "OwnerID/UUID"));
        Assert.Equal(avatar, ImportKit.Text(part, "LastOwnerID/UUID"));
        // copy 0x8000, modify 0x4000 and transfer 0x2000 are set in the base, owner and next owner masks
        foreach (string mask in new[] { "BaseMask", "OwnerMask", "NextOwnerMask" })
        {
            uint bits = uint.Parse(ImportKit.Text(part, mask), CultureInfo.InvariantCulture);
            Assert.Equal(0xE000u, bits & 0xE000u);
        }
        Assert.Equal("9", ImportKit.Text(part, "Shape/PCode"));
        Assert.Equal("5", ImportKit.Text(part, "Shape/SculptType"));
        Assert.Equal(result.MeshId.ToString("D"), ImportKit.Text(part, "Shape/SculptTexture/UUID"));
        Assert.Empty(part.Element("TaskInventory")?.Elements() ?? Enumerable.Empty<XElement>());
    }

    [Fact]
    public void ImportMeshHasAllFourLevelsWithTheTriangleCountsOfTheInputFiles()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        MeshAssetModel model = Mesh(result, oar);
        Assert.NotNull(model.High); Assert.NotNull(model.Medium); Assert.NotNull(model.Low); Assert.NotNull(model.Lowest);
        Assert.Equal(ImportKit.SlabTriangles(4, true), Triangles(model.High));
        Assert.Equal(ImportKit.SlabTriangles(3, true), Triangles(model.Medium));
        Assert.Equal(ImportKit.SlabTriangles(2, true), Triangles(model.Low));
        Assert.Equal(ImportKit.SlabTriangles(1, false), Triangles(model.Lowest));
        Assert.Equal(new[] { "high", "medium", "low", "lowest" }, result.Levels.Select(l => l.Name));
        Assert.Equal(new[] { ImportKit.SlabTriangles(4, true), ImportKit.SlabTriangles(3, true), ImportKit.SlabTriangles(2, true), ImportKit.SlabTriangles(1, false) }, result.Levels.Select(l => l.Triangles));
    }

    [Fact]
    public void ALowerLevelWithoutAMaterialLeavesThatFaceEmptyAndKeepsTheFaceNumbers()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        MeshAssetModel model = Mesh(result, oar);
        Assert.Equal(2, result.Faces);
        // the lowest level has no top: face 0 (top) is an empty slot and face 1 (side) holds all its triangles
        Assert.Equal(2, model.Lowest.Faces.Count);
        Assert.True(model.Lowest.Faces[0].NoGeometry);
        Assert.False(model.Lowest.Faces[1].NoGeometry);
        Assert.Equal(ImportKit.SlabTriangles(1, false), model.Lowest.Faces[1].Indices.Length / 3);
        Assert.Equal(2, model.High.Faces.Count);
        Assert.False(model.High.Faces[0].NoGeometry);
    }

    [Fact]
    public void ABoxPhysicsFileIsOneConvexHullAndTheShapeTypeIsConvex()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run(physics: ImportKit.PhysicsKind.Box);
        MeshAssetModel model = Mesh(result, oar);
        List<List<Vector3>> hulls = PhysicsBlock.ReadHullList(model.PhysicsConvex);
        Assert.Single(hulls);
        Assert.Equal(8, hulls[0].Count);
        Assert.Null(model.PhysicsMesh);
        Assert.Equal("convex hull", result.Physics);
        Assert.Equal("2", ImportKit.Text(ImportKit.Part(oar), "PhysicsShapeType"));
    }

    [Fact]
    public void AConcavePhysicsFileIsATrianglePhysicsMeshAndTheShapeTypeIsPrim()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run(physics: ImportKit.PhysicsKind.Ell);
        MeshAssetModel model = Mesh(result, oar);
        Assert.NotNull(model.PhysicsMesh);
        Assert.Equal("triangle mesh", result.Physics);
        Assert.Equal(24, result.PhysicsTriangles);
        Assert.Equal("0", ImportKit.Text(ImportKit.Part(oar), "PhysicsShapeType"));
    }

    [Fact]
    public void FacesTakeTheColourAndPictureTheGlbGives()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, ImportKit.Folder folder) = Run();
        XElement part = ImportKit.Part(oar);
        Primitive.TextureEntry te = new Primitive.TextureEntry(Convert.FromBase64String(ImportKit.Text(part, "Shape/TextureEntry")), 0, Convert.FromBase64String(ImportKit.Text(part, "Shape/TextureEntry")).Length);
        Primitive.TextureEntryFace top = te.FaceTextures[0] ?? te.DefaultTexture, side = te.FaceTextures[1] ?? te.DefaultTexture;
        Assert.InRange(top.RGBA.R, 0.2f - 0.005f, 0.2f + 0.005f);
        Assert.InRange(top.RGBA.G, 0.4f - 0.005f, 0.4f + 0.005f);
        Assert.InRange(top.RGBA.B, 0.6f - 0.005f, 0.6f + 0.005f);
        Assert.InRange(side.RGBA.R, 0.995f, 1f);
        foreach ((Primitive.TextureEntryFace face, ImportMaterial material) in new[] { (top, folder.Top), (side, folder.Side) })
        {
            string asset = "assets/" + face.TextureID + "_texture.jp2";
            Assert.True(oar.ContainsKey(asset), "no texture asset " + asset);
            Assert.Equal(Pixels(material.Picture), Pixels(oar[asset], jpeg2000: true));
        }
        Assert.Equal(2, result.Textures.Count(t => t.Role == "picture"));
    }

    [Fact]
    public void ANormalMapIsCarriedAsALegacyMaterialOnTheFace()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, ImportKit.Folder folder) = Run(normalMap: true);
        XElement part = ImportKit.Part(oar);
        byte[] teBytes = Convert.FromBase64String(ImportKit.Text(part, "Shape/TextureEntry"));
        Primitive.TextureEntry te = new Primitive.TextureEntry(teBytes, 0, teBytes.Length);
        Primitive.TextureEntryFace top = te.FaceTextures[0] ?? te.DefaultTexture, side = te.FaceTextures[1] ?? te.DefaultTexture;
        Assert.Equal(UUID.Zero, top.MaterialID);
        Assert.NotEqual(UUID.Zero, side.MaterialID);

        string materialAsset = "assets/" + side.MaterialID + "_material.xml";
        Assert.True(oar.ContainsKey(materialAsset), "no material asset " + materialAsset);
        // the region names a legacy material by the MD5 of its bytes
        using (MD5 md5 = MD5.Create())
            Assert.Equal(side.MaterialID, new UUID(md5.ComputeHash(oar[materialAsset]), 0));
        OSDMap map = (OSDMap)OSDParser.Deserialize(oar[materialAsset]);
        UUID normal = map["NormMap"].AsUUID();
        Assert.NotEqual(UUID.Zero, normal);
        Assert.Equal(UUID.Zero, map["SpecMap"].AsUUID());
        Assert.Equal(0u, map["DiffuseAlphaMode"].AsUInteger());
        Assert.Equal(Pixels(folder.Side.Normal), Pixels(oar["assets/" + normal + "_texture.jp2"], jpeg2000: true));
        Assert.Contains(result.Textures, t => t.Role == "normal map" && t.Id == normal.Guid);
        Assert.Equal(new[] { side.MaterialID.Guid }, result.Materials);
    }

    [Fact]
    public void TheSizeIsTheBoxAroundTheHighLevelAndThePositionKeepsTheModelOrigin()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        XElement part = ImportKit.Part(oar);
        // glTF x, y, z are the viewer's x, -z, y: the slab is 0.4 wide, 0.1 deep and 0.2 high, standing on the origin
        float F(string path) => float.Parse(ImportKit.Text(part, path), CultureInfo.InvariantCulture);
        Assert.Equal(0.4f, F("Scale/X"), 4);
        Assert.Equal(0.1f, F("Scale/Y"), 4);
        Assert.Equal(0.2f, F("Scale/Z"), 4);
        Assert.Equal(10f, F("GroupPosition/X"), 4);
        Assert.Equal(20f, F("GroupPosition/Y"), 4);
        Assert.Equal(30.1f, F("GroupPosition/Z"), 4);
        Assert.Equal(new Vector3(0.4f, 0.1f, 0.2f), result.Size);
        Assert.Equal(1f, F("RotationOffset/W"), 4);
    }

    [Fact]
    public void TheSameInputsGiveTheSameArchive()
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap: true);
        string a = ImportKit.OutputPath(), b = ImportKit.OutputPath();
        ObjectImporter.Import(folder.Path, a, Options());
        ObjectImporter.Import(folder.Path, b, Options());
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void ALevelWithAMaterialTheFullMeshLacksIsRefusedAndNothingIsWritten()
    {
        ImportKit.Folder folder = ImportKit.Write();
        ImportKit.Geo geo = new ImportKit.Geo();
        ImportKit.AddBox(geo, 1, _ => true);
        File.WriteAllBytes(Path.Combine(folder.Path, ImportKit.Name + "_lod1.glb"), ImportKit.Glb(new[] { (new ImportMaterial { Name = "stranger", Picture = ImportKit.Png(1, 2, 3) }, geo) }));
        string output = ImportKit.OutputPath();
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder.Path, output, Options()));
        Assert.Contains("stranger", e.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AFolderWithoutTheFullMeshIsRefused()
    {
        ImportKit.Folder folder = ImportKit.Write();
        string moved = Path.Combine(folder.Path, ImportKit.Name + ".glb");
        File.Move(moved, moved + ".aside");
        string output = ImportKit.OutputPath();
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder.Path, output, Options()));
        Assert.Contains(ImportKit.Name + ".glb", e.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnOutputThatExistsIsNeverOverwritten()
    {
        ImportKit.Folder folder = ImportKit.Write();
        string output = ImportKit.OutputPath();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, "mine");
        Assert.Throws<ImportException>(() => ObjectImporter.Import(folder.Path, output, Options()));
        Assert.Equal("mine", File.ReadAllText(output));
    }

    [Fact]
    public void WithoutAPhysicsFileTheBoundingHullIsUsed()
    {
        ImportKit.Folder folder = ImportKit.Write(withPhysics: false);
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.Import(folder.Path, output, Options());
        Assert.Equal("bounding hull", result.Physics);
        MeshAssetModel model = MeshCodec.Decode(ImportKit.ReadOar(output)["assets/" + result.MeshId.ToString("D") + "_mesh.llmesh"]);
        Assert.Null(model.PhysicsMesh);
        Assert.NotNull(model.PhysicsConvex);
    }
}
