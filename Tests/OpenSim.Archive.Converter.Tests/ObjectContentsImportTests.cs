/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Restore.Import;
using OpenSim.MeshAsset;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// An object in a part's contents: an "object" item made from a mesh tool's output folder, built as the single-model import builds an object from that folder,
/// stored as an object asset in the form the region reads when it rezzes an object from inventory (a SceneObjectGroup with a RootPart), so a box can hand out
/// rigged garments. Each test writes its own folders and shares nothing, so the class runs in parallel.
/// </summary>
public class ObjectContentsImportTests
{
    private const string FullPermissions = "2147483647";

    /// <summary>The object asset of an item, parsed; the item is asserted to be an object item with its asset in the archive first.</summary>
    private static XElement ObjectAsset(Dictionary<string, byte[]> oar, string name)
    {
        XElement item = ClothingBoxKit.Item(oar, name);
        return XDocument.Parse(Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, item, "_object.xml"))).Root;
    }

    private static XElement InnerPart(XElement group)
    {
        XElement part = group.Element("RootPart")?.Element("SceneObjectPart");
        Assert.True(part != null, "the object asset has no RootPart/SceneObjectPart: " + group.ToString().Substring(0, Math.Min(200, group.ToString().Length)));
        return part;
    }

    /// <summary>The single part of an archive the single-model import wrote from the same folder, with the same avatar and time.</summary>
    private static (XElement Part, Dictionary<string, byte[]> Oar) SingleImport(string folder, string name, string description = "", string baseName = "", bool jointPositions = false)
    {
        string archive = ImportKit.OutputPath();
        ImportOptions options = ClothingBoxKit.Options();
        options.Name = name;
        options.Description = description;
        options.BaseName = baseName;
        options.JointPositions = jointPositions;
        ObjectImporter.Import(folder, archive, options);
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(archive);
        return (ImportKit.Part(oar), oar);
    }

    [Fact]
    public void TheManifestReadsAnObjectWithItsFolderBaseAndJointPositions()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        LinksetManifest manifest = null;
        try { manifest = LinksetManifest.Load(b.ManifestPath); }
        catch (ImportException e) { Assert.Fail("the manifest was refused: " + e.Message); }
        LinksetContent shirt = manifest.Parts[0].Contents.Single(c => c.Name == "Test Shirt Mesh");
        Assert.Equal("object", shirt.Type);
        Assert.Equal(Path.GetFullPath(b.ShirtMesh), shirt.Folder);
        Assert.Equal("shirt", shirt.Base);
        Assert.False(shirt.JointPositions);
        Assert.Equal("a rigged shirt", shirt.Description);
        LinksetContent skirt = manifest.Parts[0].Contents.Single(c => c.Name == "Test Skirt Mesh");
        Assert.Equal(string.Empty, skirt.Base);
        Assert.Null(skirt.JointPositions);
    }

    [Fact]
    public void AnObjectIsAnItemOfInventoryTypeObjectWithAnObjectAsset()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        XElement item = ClothingBoxKit.Item(oar, "Test Shirt Mesh");
        Assert.Equal(((int)AssetType.Object).ToString(), item.Element("Type").Value);
        Assert.Equal(((int)InventoryType.Object).ToString(), item.Element("InvType").Value);
        Assert.Equal("a rigged shirt", item.Element("Description").Value);
        XElement group = ObjectAsset(oar, "Test Shirt Mesh");
        Assert.Equal("SceneObjectGroup", group.Name.LocalName);
        InnerPart(group);
    }

    [Fact]
    public void TheRegionReadsTheObjectAssetAsItReadsAnObjectRezzedFromInventory()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        string xml = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, ClothingBoxKit.Item(oar, "Test Shirt Mesh"), "_object.xml"));
        SceneObjectGroup group = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        Assert.NotNull(group);
        Assert.Equal("Test Shirt Mesh", group.RootPart.Name);
        Assert.Equal((byte)SculptType.Mesh, group.RootPart.Shape.SculptType);
        Assert.Equal(new UUID(ImportKit.Avatar), group.RootPart.CreatorID);
        Assert.Equal(new UUID(ImportKit.Avatar), group.RootPart.OwnerID);
        // the object's centre is its origin: the region puts it where it is rezzed, and a worn one sits on its attachment point
        Assert.Equal(Vector3.Zero, group.RootPart.GroupPosition);
    }

    [Fact]
    public void TheObjectIsBuiltAsTheSingleModelImportBuildsItFromTheSameFolder()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        foreach ((string name, string folder, string baseName, string description) in new[] { ("Test Shirt Mesh", b.ShirtMesh, "shirt", "a rigged shirt"), ("Test Skirt Mesh", b.SkirtMesh, "", ""), ("Test Slab", b.Slab, "", "") })
        {
            XElement inner = InnerPart(ObjectAsset(oar, name));
            (XElement single, Dictionary<string, byte[]> singleOar) = SingleImport(folder, name, description, baseName);
            foreach (string path in new[] { "Shape/SculptTexture/UUID", "Shape/TextureEntry", "Shape/ExtraParams", "Scale/X", "Scale/Y", "Scale/Z", "PhysicsShapeType", "Name", "Description" })
                Assert.True(ImportKit.Text(single, path) == ImportKit.Text(inner, path), name + ": " + path + " is '" + ImportKit.Text(inner, path) + "', the single-model import writes '" + ImportKit.Text(single, path) + "'");
            // every asset the single-model import writes for the folder (the mesh, the pictures, the materials) is in the box's archive, byte for byte
            foreach (KeyValuePair<string, byte[]> asset in singleOar.Where(e => e.Key.StartsWith("assets/", StringComparison.Ordinal)))
            {
                Assert.True(oar.ContainsKey(asset.Key), name + ": the box's archive lacks " + asset.Key);
                Assert.Equal(asset.Value, oar[asset.Key]);
            }
        }
    }

    [Fact]
    public void ARiggedGarmentKeepsItsSkinAndJointPositionsFollowTheItemsSetting()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"
          { ""type"": ""object"", ""folder"": ""shirt-mesh"", ""base"": ""shirt"", ""name"": ""Test Shirt Mesh"", ""jointPositions"": true },
          { ""type"": ""object"", ""folder"": ""skirt-mesh"", ""name"": ""Test Skirt Mesh"", ""jointPositions"": false }");
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        string shirtMesh = ImportKit.Text(InnerPart(ObjectAsset(oar, "Test Shirt Mesh")), "Shape/SculptTexture/UUID");
        string skirtMesh = ImportKit.Text(InnerPart(ObjectAsset(oar, "Test Skirt Mesh")), "Shape/SculptTexture/UUID");
        Assert.Equal(ImportKit.Text(SingleImport(b.ShirtMesh, "Test Shirt Mesh", baseName: "shirt", jointPositions: true).Part, "Shape/SculptTexture/UUID"), shirtMesh);
        Assert.Equal(ImportKit.Text(SingleImport(b.SkirtMesh, "Test Skirt Mesh", jointPositions: false).Part, "Shape/SculptTexture/UUID"), skirtMesh);
        MeshAssetModel shirt = MeshCodec.Decode(oar["assets/" + shirtMesh + "_mesh.llmesh"]);
        Assert.NotNull(shirt.Skin);
        Assert.Equal(RigKit.DefaultNames, shirt.Skin.JointNames.ToArray());
    }

    [Fact]
    public void TheObjectAndItsItemAreCreatedAndOwnedByTheAvatarWithFullPermissions()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        string avatar = ImportKit.Avatar.ToString("D");
        XElement item = ClothingBoxKit.Item(oar, "Test Skirt Mesh");
        Assert.Equal(avatar, item.Element("CreatorID").Element("UUID").Value);
        Assert.Equal(avatar, item.Element("OwnerID").Element("UUID").Value);
        foreach (string mask in new[] { "BasePermissions", "CurrentPermissions", "NextPermissions" })
            Assert.Equal(FullPermissions, item.Element(mask).Value);
        XElement inner = InnerPart(ObjectAsset(oar, "Test Skirt Mesh"));
        Assert.Equal(avatar, ImportKit.Text(inner, "CreatorID/UUID"));
        Assert.Equal(avatar, ImportKit.Text(inner, "OwnerID/UUID"));
        foreach (string mask in new[] { "BaseMask", "OwnerMask", "NextOwnerMask" })
            Assert.Equal(FullPermissions, ImportKit.Text(inner, mask));
    }

    [Fact]
    public void TheObjectsInTheBoxAreNotObjectsOfTheRegion()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        ClothingBoxKit.Item(oar, "Test Shirt Mesh");
        Assert.Single(oar.Keys, k => k.StartsWith("objects/", StringComparison.Ordinal));
        Assert.Equal(5, LinksetKit.Items(LinksetKit.Parts(oar)[0]).Count);
    }

    [Fact]
    public void AnObjectWithoutAFolderIsRefused()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""object"", ""name"": ""Nothing"" }");
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("needs \"folder\"", refusal);
    }

    [Fact]
    public void AnObjectWithAFileIsRefused()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""object"", ""folder"": ""slab"", ""file"": ""shirt.png"", ""name"": ""Test Slab"" }");
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("an object is made from a \"folder\"", refusal);
    }
}
