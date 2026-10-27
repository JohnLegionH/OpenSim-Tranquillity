/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Restore.Import;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using SkiaSharp;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// oar-import with a manifest: one linked object from a mesh tool's folder and the nodes of a multi-node .glb, each part with its own place, name,
/// description and contents (scripts, notecards, textures). Each test writes its own folder and archive and shares nothing, so the class runs in parallel.
/// </summary>
public class LinksetImportTests
{
    private static ImportOptions Options() => new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600 };

    private static (ImportResult Result, Dictionary<string, byte[]> Oar, LinksetKit.Built Built) Run()
    {
        LinksetKit.Built built = LinksetKit.Write();
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), output, Options());
        Assert.True(File.Exists(output), "the archive was not written");
        return (result, ImportKit.ReadOar(output), built);
    }

    private static float F(XElement part, string path) => float.Parse(ImportKit.Text(part, path), CultureInfo.InvariantCulture);

    private static byte[] Pixels(byte[] encoded, bool jpeg2000)
    {
        SKBitmap bitmap;
        Assert.True((jpeg2000 ? SkiaImageUtils.TryDecodeFromJ2K(encoded, out bitmap) : SkiaImageUtils.TryDecodeFromBytes(encoded, out bitmap)) && bitmap != null);
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
            return rgba.Bytes;
    }

    // ------------------------------------------------------------------ the manifest

    [Fact]
    public void TheManifestListsThePartsRootFirstWithTheirSourcesPlacesAndContents()
    {
        LinksetKit.Built built = LinksetKit.Write();
        LinksetManifest m = LinksetManifest.Load(built.ManifestPath);
        Assert.Equal("Test House", m.Name);
        Assert.Equal("A house for the import tests", m.Description);
        Assert.Equal(new[] { "Shell", "Door", "Window" }, m.Parts.Select(p => p.Name));
        Assert.Equal(new[] { "the walls", "front door leaf", "side window" }, m.Parts.Select(p => p.Description));
        Assert.True(m.Parts[0].Model.IsFolder);
        Assert.Equal(Path.GetFullPath(Path.Combine(built.Folder, "shell")), Path.GetFullPath(m.Parts[0].Model.Folder));
        Assert.Equal(Path.GetFullPath(Path.Combine(built.Folder, "building.glb")), Path.GetFullPath(m.Parts[1].Model.Glb));
        Assert.Equal("Door", m.Parts[1].Model.Node);
        Assert.Equal(new[] { 0.3f, 0.2f, 0f }, m.Parts[1].Position);
        Assert.Null(m.Parts[2].Position);
        Assert.Null(m.Parts[2].Rotation);
        Assert.Equal(new[] { "notecard" }, m.Parts[0].Contents.Select(c => c.Type));
        Assert.Equal(new[] { "script", "texture" }, m.Parts[1].Contents.Select(c => c.Type));
        Assert.Equal(new[] { "Door script", "Badge" }, m.Parts[1].Contents.Select(c => c.Name));
        Assert.Empty(m.Parts[2].Contents);
    }

    [Fact]
    public void AKeyTheManifestDoesNotKnowIsRefusedByName()
    {
        LinksetKit.Built built = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"", ""positon"": [1, 2, 3] } ] }");
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(built.ManifestPath));
        Assert.Contains("positon", e.Message);
    }

    [Fact]
    public void APartWithNoSourceOrTwoSourcesIsRefused()
    {
        LinksetKit.Built none = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"" } ] }");
        Assert.Contains("A", Assert.Throws<ImportException>(() => LinksetManifest.Load(none.ManifestPath)).Message);
        LinksetKit.Built both = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"", ""glb"": ""building.glb"", ""node"": ""Door"" } ] }");
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(both.ManifestPath));
        Assert.Contains("A", e.Message);
    }

    [Fact]
    public void AManifestWithNoPartsAnUnknownContentTypeOrAMissingFileIsRefused()
    {
        LinksetKit.Built empty = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [] }");
        Assert.Throws<ImportException>(() => LinksetManifest.Load(empty.ManifestPath));
        LinksetKit.Built type = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"", ""contents"": [ { ""type"": ""sound"", ""file"": ""readme.txt"" } ] } ] }");
        Assert.Contains("sound", Assert.Throws<ImportException>(() => LinksetManifest.Load(type.ManifestPath)).Message);
        LinksetKit.Built file = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"", ""contents"": [ { ""type"": ""script"", ""file"": ""nothing-here.lsl"" } ] } ] }");
        Assert.Contains("nothing-here.lsl", Assert.Throws<ImportException>(() => LinksetManifest.Load(file.ManifestPath)).Message);
    }

    [Fact]
    public void ATurnedRootPartIsRefusedBecauseTheChildrenAreRelativeToIt()
    {
        LinksetKit.Built built = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"", ""rotation"": [0, 0, 0.7071068, 0.7071068] } ] }");
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(built.ManifestPath));
        Assert.Contains("root", e.Message);
    }

    // ------------------------------------------------------------------ the object

    [Fact]
    public void ImportWritesOneObjectWithEveryPartInLinkOrderWithItsNameAndDescription()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        Assert.True(result.Written);
        Assert.Single(oar.Keys, k => k.StartsWith("objects/", StringComparison.Ordinal));
        List<XElement> parts = LinksetKit.Parts(oar);
        Assert.Equal(new[] { "Shell", "Door", "Window" }, parts.Select(p => ImportKit.Text(p, "Name")));
        Assert.Equal(new[] { "the walls", "front door leaf", "side window" }, parts.Select(p => ImportKit.Text(p, "Description")));
        Assert.Equal(new[] { "1", "2", "3" }, parts.Select(p => ImportKit.Text(p, "LinkNum")));
        Assert.Equal(new[] { "Shell", "Door", "Window" }, result.Parts.Select(p => p.Name));
        Assert.Equal(new[] { 1, 2, 3 }, result.Parts.Select(p => p.LinkNumber));
        Assert.Equal("Test House", result.Name);
    }

    [Fact]
    public void ChildPartsSitAtTheirPlaceAndTurnRelativeToTheRootPartAndTheRootStandsAtThePosition()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        List<XElement> parts = LinksetKit.Parts(oar);
        // the root: the model origin at the position, so its centre (0, 0, 0.1) above it
        Assert.Equal(10f, F(parts[0], "GroupPosition/X"), 4);
        Assert.Equal(20f, F(parts[0], "GroupPosition/Y"), 4);
        Assert.Equal(30.1f, F(parts[0], "GroupPosition/Z"), 4);
        foreach (XElement p in parts)
            Assert.Equal(30.1f, F(p, "GroupPosition/Z"), 4);
        // the door: centre at its position (0.3, 0.2, 0) plus the model centre (0, 0, 0.1), minus the root centre (0, 0, 0.1)
        Assert.Equal(0.3f, F(parts[1], "OffsetPosition/X"), 4);
        Assert.Equal(0.2f, F(parts[1], "OffsetPosition/Y"), 4);
        Assert.Equal(0f, F(parts[1], "OffsetPosition/Z"), 4);
        Assert.Equal(1f, F(parts[1], "RotationOffset/W"), 4);
        // the window: the node's own place (1, 2, 0) and 90 degrees about z; its model centre (0.1, 0, 0.1) turns to (0, 0.1, 0.1)
        Assert.Equal(1f, F(parts[2], "OffsetPosition/X"), 4);
        Assert.Equal(2.1f, F(parts[2], "OffsetPosition/Y"), 4);
        Assert.Equal(0f, F(parts[2], "OffsetPosition/Z"), 4);
        Assert.Equal(0f, F(parts[2], "RotationOffset/X"), 4);
        Assert.Equal(0f, F(parts[2], "RotationOffset/Y"), 4);
        Assert.Equal(LinksetKit.Sqrt12, F(parts[2], "RotationOffset/Z"), 4);
        Assert.Equal(LinksetKit.Sqrt12, F(parts[2], "RotationOffset/W"), 4);
        Assert.Equal(0f, F(parts[0], "OffsetPosition/X"), 4);
        Assert.Equal(new Vector3(0.3f, 0.2f, 0f), result.Parts[1].Offset);
    }

    [Fact]
    public void EachPartIsSizedByItsOwnModelAndHasItsOwnMeshAssetWithFourLevels()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, _) = Run();
        List<XElement> parts = LinksetKit.Parts(oar);
        Assert.Equal(0.4f, F(parts[0], "Scale/X"), 4);
        Assert.Equal(0.1f, F(parts[1], "Scale/X"), 4);
        Assert.Equal(0.02f, F(parts[1], "Scale/Y"), 4);
        Assert.Equal(0.2f, F(parts[1], "Scale/Z"), 4);
        Assert.Equal(0.1f, F(parts[2], "Scale/X"), 4);
        Assert.Equal(parts.Count, parts.Select(p => ImportKit.Text(p, "Shape/SculptTexture/UUID")).Distinct().Count());
        foreach (XElement p in parts)
        {
            Assert.Equal("5", ImportKit.Text(p, "Shape/SculptType"));
            string asset = "assets/" + ImportKit.Text(p, "Shape/SculptTexture/UUID") + "_mesh.llmesh";
            Assert.True(oar.ContainsKey(asset), "no mesh asset " + asset);
            MeshAssetModel model = MeshCodec.Decode(oar[asset]);
            Assert.NotNull(model.High); Assert.NotNull(model.Medium); Assert.NotNull(model.Low); Assert.NotNull(model.Lowest);
        }
        // a node has no lower levels of its own, so each level is the full mesh
        MeshAssetModel doorMesh = MeshCodec.Decode(oar["assets/" + ImportKit.Text(parts[1], "Shape/SculptTexture/UUID") + "_mesh.llmesh"]);
        int full = doorMesh.High.Faces.Where(f => !f.NoGeometry).Sum(f => f.Indices.Length / 3);
        Assert.Equal(12, full);
        Assert.Equal(full, doorMesh.Lowest.Faces.Where(f => !f.NoGeometry).Sum(f => f.Indices.Length / 3));
        Assert.Contains(result.Notes, n => n.Contains("Door") && n.Contains("level"));
    }

    [Fact]
    public void EveryPartIsCreditedToTheAvatarWithFullPermissions()
    {
        (_, Dictionary<string, byte[]> oar, _) = Run();
        string avatar = ImportKit.Avatar.ToString("D");
        foreach (XElement p in LinksetKit.Parts(oar))
        {
            Assert.Equal(avatar, ImportKit.Text(p, "CreatorID/UUID"));
            Assert.Equal(avatar, ImportKit.Text(p, "OwnerID/UUID"));
            Assert.Equal(avatar, ImportKit.Text(p, "LastOwnerID/UUID"));
            foreach (string mask in new[] { "BaseMask", "OwnerMask", "NextOwnerMask" })
                Assert.Equal(0xE000u, uint.Parse(ImportKit.Text(p, mask), CultureInfo.InvariantCulture) & 0xE000u);
        }
    }

    // ------------------------------------------------------------------ the contents

    [Fact]
    public void AScriptIsStoredAsTextInAnItemOfItsPartWithFullPermissions()
    {
        (ImportResult result, Dictionary<string, byte[]> oar, LinksetKit.Built built) = Run();
        XElement door = LinksetKit.Parts(oar)[1];
        XElement item = LinksetKit.Item(door, "Door script");
        Assert.Equal("10", item.Element("Type").Value);
        Assert.Equal("10", item.Element("InvType").Value);
        string asset = "assets/" + item.Element("AssetID").Element("UUID").Value + "_script.lsl";
        Assert.True(oar.ContainsKey(asset), "no script asset " + asset);
        Assert.Equal(File.ReadAllBytes(built.DoorScript), oar[asset]);
        string avatar = ImportKit.Avatar.ToString("D");
        Assert.Equal(avatar, item.Element("OwnerID").Element("UUID").Value);
        Assert.Equal(avatar, item.Element("CreatorID").Element("UUID").Value);
        Assert.Equal(ImportKit.Text(door, "UUID/UUID"), item.Element("ParentID").Element("UUID").Value);
        foreach (string permission in new[] { "BasePermissions", "CurrentPermissions", "NextPermissions" })
            Assert.Equal(0xE000u, uint.Parse(item.Element(permission).Value, CultureInfo.InvariantCulture) & 0xE000u);
        // a script with no saved state starts running when the region loads it
        Assert.DoesNotContain(oar.Keys, k => k.Contains("script_state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Parts[1].Contents, c => c.Type == "script" && c.Name == "Door script" && c.AssetId.ToString("D") == item.Element("AssetID").Element("UUID").Value);
    }

    [Fact]
    public void ANotecardIsStoredInTheLindenNotecardFormatWithItsText()
    {
        (_, Dictionary<string, byte[]> oar, _) = Run();
        XElement shell = LinksetKit.Parts(oar)[0];
        XElement item = LinksetKit.Item(shell, "Read me");
        Assert.Equal("7", item.Element("Type").Value);
        Assert.Equal("7", item.Element("InvType").Value);
        string text = Encoding.UTF8.GetString(oar["assets/" + item.Element("AssetID").Element("UUID").Value + "_notecard.txt"]);
        int length = Encoding.UTF8.GetByteCount(LinksetKit.NotecardText);
        Assert.Equal("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + length.ToString(CultureInfo.InvariantCulture) + "\n" + LinksetKit.NotecardText + "}\n", text);
    }

    [Fact]
    public void ATextureIsStoredAsAJpeg2000AssetWithTheSamePixels()
    {
        (_, Dictionary<string, byte[]> oar, LinksetKit.Built built) = Run();
        XElement item = LinksetKit.Item(LinksetKit.Parts(oar)[1], "Badge");
        Assert.Equal("0", item.Element("Type").Value);
        Assert.Equal("0", item.Element("InvType").Value);
        string asset = "assets/" + item.Element("AssetID").Element("UUID").Value + "_texture.jp2";
        Assert.True(oar.ContainsKey(asset), "no texture asset " + asset);
        // the viewer reads a bare codestream only; a JP2 file shows as a grey face
        Assert.True(oar[asset].AsSpan(0, 4).SequenceEqual(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }), "the texture asset is not a bare codestream");
        Assert.Equal(Pixels(built.BadgePng, false), Pixels(oar[asset], true));
    }

    [Fact]
    public void APartWithoutContentsHasNoItemsAndItemIdsAreDistinct()
    {
        (_, Dictionary<string, byte[]> oar, _) = Run();
        List<XElement> parts = LinksetKit.Parts(oar);
        Assert.Empty(LinksetKit.Items(parts[2]));
        List<string> ids = parts.SelectMany(LinksetKit.Items).Select(i => i.Element("ItemID").Element("UUID").Value).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void TheSameManifestGivesTheSameArchive()
    {
        LinksetKit.Built built = LinksetKit.Write();
        string a = ImportKit.OutputPath(), b = ImportKit.OutputPath();
        ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), a, Options());
        ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), b, Options());
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void ANodeThatIsNotInTheFileIsRefusedAndNothingIsWritten()
    {
        LinksetKit.Built built = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"", ""folder"": ""shell"" }, { ""name"": ""B"", ""glb"": ""building.glb"", ""node"": ""Gate"" } ] }");
        string output = ImportKit.OutputPath();
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), output, Options()));
        Assert.Contains("Gate", e.Message);
        Assert.False(File.Exists(output));
    }
}
