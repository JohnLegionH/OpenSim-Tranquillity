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
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// System clothing and body parts in a part's contents: a "wearable" item made from an LLWearable text file, with its pictures stored as texture assets and
/// written into the wearable's textures block by texture entry number. Asset type, inventory type and item flags follow the viewer: clothing (asset type 5)
/// or a body part (asset type 13, wearable types 0 to 3), inventory type 18, the wearable type in the flags. Each test writes its own folders and shares
/// nothing, so the class runs in parallel.
/// </summary>
public class WearableContentsImportTests
{
    private const string FullPermissions = "2147483647";

    [Fact]
    public void TheManifestReadsAWearableWithItsPicturesByTextureEntryNumber()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        LinksetManifest manifest = null;
        try { manifest = LinksetManifest.Load(b.ManifestPath); }
        catch (ImportException e) { Assert.Fail("the manifest was refused: " + e.Message); }
        LinksetContent shirt = manifest.Parts[0].Contents.Single(c => c.Name == "Test Shirt");
        Assert.Equal("wearable", shirt.Type);
        Assert.Equal(Path.GetFullPath(b.Shirt), shirt.File);
        Assert.Equal("a system shirt", shirt.Description);
        Assert.Equal(new[] { ClothingBoxKit.UpperShirt }, shirt.Textures.Keys.ToArray());
        Assert.Equal(Path.GetFullPath(b.ShirtPicture), shirt.Textures[ClothingBoxKit.UpperShirt]);
        LinksetContent alpha = manifest.Parts[0].Contents.Single(c => c.Name == "Test Alpha");
        Assert.Equal(new[] { ClothingBoxKit.LowerAlpha, ClothingBoxKit.UpperAlpha }, alpha.Textures.Keys.ToArray());
        Assert.Equal(Path.GetFullPath(b.LowerAlphaPicture), alpha.Textures[ClothingBoxKit.LowerAlpha]);
        Assert.Equal(Path.GetFullPath(b.UpperAlphaPicture), alpha.Textures[ClothingBoxKit.UpperAlpha]);
    }

    [Fact]
    public void AShirtIsAClothingItemOfInventoryTypeWearableWithItsWearableTypeInTheFlags()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        XElement shirt = ClothingBoxKit.Item(oar, "Test Shirt");
        Assert.Equal(((int)AssetType.Clothing).ToString(), shirt.Element("Type").Value);
        Assert.Equal(((int)InventoryType.Wearable).ToString(), shirt.Element("InvType").Value);
        Assert.Equal("4", shirt.Element("Flags").Value);
        Assert.Equal("a system shirt", shirt.Element("Description").Value);
        ClothingBoxKit.Asset(oar, shirt, "_clothing.txt");
    }

    [Fact]
    public void AnAlphaIsClothingWithWearableType13InTheFlags()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        XElement alpha = ClothingBoxKit.Item(oar, "Test Alpha");
        Assert.Equal(((int)AssetType.Clothing).ToString(), alpha.Element("Type").Value);
        Assert.Equal(((int)InventoryType.Wearable).ToString(), alpha.Element("InvType").Value);
        Assert.Equal("13", alpha.Element("Flags").Value);
    }

    [Fact]
    public void AShapeIsABodyPartAssetWithWearableType0()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""wearable"", ""file"": ""shape.wearable.txt"", ""name"": ""Test Shape"" }");
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        XElement shape = ClothingBoxKit.Item(oar, "Test Shape");
        Assert.Equal(((int)AssetType.Bodypart).ToString(), shape.Element("Type").Value);
        Assert.Equal(((int)InventoryType.Wearable).ToString(), shape.Element("InvType").Value);
        Assert.Equal("0", shape.Element("Flags").Value);
        ClothingBoxKit.Asset(oar, shape, "_bodypart.txt");
    }

    [Fact]
    public void TheShirtPictureTakesTheLineOfItsTextureEntryAndTheCountStaysRight()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        XElement shirt = ClothingBoxKit.Item(oar, "Test Shirt");
        string text = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, shirt, "_clothing.txt"));
        Dictionary<int, string> lines = ClothingBoxKit.TextureLines(text);
        Assert.Equal(new[] { ClothingBoxKit.UpperShirt }, lines.Keys.ToArray());
        Assert.NotEqual(ClothingBoxKit.ShirtOldTexture.ToString("D"), lines[ClothingBoxKit.UpperShirt]);
        Assert.DoesNotContain(ClothingBoxKit.ShirtOldTexture.ToString("D"), text);
        byte[] stored = oar["assets/" + lines[ClothingBoxKit.UpperShirt] + "_texture.jp2"];
        Assert.Equal(ClothingBoxKit.Pixels(b.ShirtPng, false), ClothingBoxKit.Pixels(stored, true));
    }

    [Fact]
    public void TheAlphaHoldsTheLowerAndTheUpperAlphaPicturesInOneWearable()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        XElement alpha = ClothingBoxKit.Item(oar, "Test Alpha");
        string text = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, alpha, "_clothing.txt"));
        Assert.Contains("\ntextures 2\n", text);
        Dictionary<int, string> lines = ClothingBoxKit.TextureLines(text);
        Assert.Equal(new[] { ClothingBoxKit.LowerAlpha, ClothingBoxKit.UpperAlpha }, lines.Keys.OrderBy(k => k).ToArray());
        Assert.True(oar.ContainsKey("assets/" + lines[ClothingBoxKit.LowerAlpha] + "_texture.jp2"), "no lower alpha texture asset");
        Assert.True(oar.ContainsKey("assets/" + lines[ClothingBoxKit.UpperAlpha] + "_texture.jp2"), "no upper alpha texture asset");
        Assert.Equal(ClothingBoxKit.Pixels(b.LowerAlphaPng, false), ClothingBoxKit.Pixels(oar["assets/" + lines[ClothingBoxKit.LowerAlpha] + "_texture.jp2"], true));
        Assert.Equal(ClothingBoxKit.Pixels(b.UpperAlphaPng, false), ClothingBoxKit.Pixels(oar["assets/" + lines[ClothingBoxKit.UpperAlpha] + "_texture.jp2"], true));
    }

    [Fact]
    public void TheCreatorAndTheOwnerInTheWearableTextAreTheAvatar()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        string avatar = ImportKit.Avatar.ToString("D");
        foreach (string name in new[] { "Test Shirt", "Test Alpha" })
        {
            string text = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, ClothingBoxKit.Item(oar, name), "_clothing.txt"));
            Assert.Contains("\t\tcreator_id\t" + avatar + "\n", text);
            Assert.Contains("\t\towner_id\t" + avatar + "\n", text);
            Assert.Contains("\t\tlast_owner_id\t" + avatar + "\n", text);
            Assert.DoesNotContain("@CREATOR@", text);
            Assert.DoesNotContain("@OWNER@", text);
            Assert.DoesNotContain(ClothingBoxKit.ShirtCreator.ToString("D"), text);
            Assert.DoesNotContain(ClothingBoxKit.ShirtOwner.ToString("D"), text);
        }
    }

    [Fact]
    public void TheRestOfTheWearableTextIsTheSourceTextWithLineFeeds()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(b);
        string avatar = ImportKit.Avatar.ToString("D");
        string shirt = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, ClothingBoxKit.Item(oar, "Test Shirt"), "_clothing.txt"));
        string newId = ClothingBoxKit.TextureLines(shirt)[ClothingBoxKit.UpperShirt];
        string expectedShirt = ClothingBoxKit.ShirtText.Replace(ClothingBoxKit.ShirtCreator.ToString("D"), avatar).Replace(ClothingBoxKit.ShirtOwner.ToString("D"), avatar)
            .Replace(ClothingBoxKit.ShirtOldTexture.ToString("D"), newId);
        Assert.Equal(expectedShirt, shirt);

        string alpha = Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, ClothingBoxKit.Item(oar, "Test Alpha"), "_clothing.txt"));
        Assert.DoesNotContain("\r", alpha);
        Dictionary<int, string> lines = ClothingBoxKit.TextureLines(alpha);
        string expectedAlpha = ClothingBoxKit.AlphaText.Replace("@CREATOR@", avatar).Replace("@OWNER@", avatar)
            .Replace("textures 0\n", "textures 2\n" + ClothingBoxKit.LowerAlpha + " " + lines[ClothingBoxKit.LowerAlpha] + "\n" + ClothingBoxKit.UpperAlpha + " " + lines[ClothingBoxKit.UpperAlpha] + "\n");
        Assert.Equal(expectedAlpha, alpha);
    }

    [Fact]
    public void AWearableItemIsCreatedAndOwnedByTheAvatarWithFullPermissions()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write());
        XElement shirt = ClothingBoxKit.Item(oar, "Test Shirt");
        string avatar = ImportKit.Avatar.ToString("D");
        Assert.Equal(avatar, shirt.Element("CreatorID").Element("UUID").Value);
        Assert.Equal(avatar, shirt.Element("OwnerID").Element("UUID").Value);
        foreach (string mask in new[] { "BasePermissions", "CurrentPermissions", "NextPermissions" })
            Assert.Equal(FullPermissions, shirt.Element(mask).Value);
    }

    [Fact]
    public void ThePicturesOfAWearableAreNotItemsOfThePart()
    {
        Dictionary<string, byte[]> oar = ClothingBoxKit.Import(ClothingBoxKit.Write(@"
          { ""type"": ""wearable"", ""file"": ""shirt.wearable.txt"", ""name"": ""Test Shirt"", ""textures"": { ""1"": ""shirt.png"" } },
          { ""type"": ""wearable"", ""file"": ""alpha.wearable.txt"", ""name"": ""Test Alpha"", ""textures"": { ""22"": ""upper-alpha.png"", ""21"": ""lower-alpha.png"" } }"));
        ClothingBoxKit.Item(oar, "Test Shirt");
        Assert.Equal(new[] { "Test Shirt", "Test Alpha" }, LinksetKit.Items(LinksetKit.Parts(oar)[0]).Select(i => i.Element("Name").Value).ToArray());
    }

    [Fact]
    public void AFileWithoutTheWearableHeaderIsRefused()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""wearable"", ""file"": ""shirt.wearable.txt"", ""name"": ""Test Shirt"" }");
        File.WriteAllText(b.Shirt, ClothingBoxKit.ShirtText.Replace("LLWearable version 22\n", string.Empty));
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("LLWearable version", refusal);
    }

    [Fact]
    public void AFileWithoutATypeLineIsRefused()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""wearable"", ""file"": ""shirt.wearable.txt"", ""name"": ""Test Shirt"" }");
        File.WriteAllText(b.Shirt, ClothingBoxKit.ShirtText.Replace("type 4\n", string.Empty));
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("\"type N\"", refusal);
    }

    [Fact]
    public void ATextureEntryNumberTheViewerDoesNotHaveIsRefused()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""wearable"", ""file"": ""shirt.wearable.txt"", ""name"": ""Test Shirt"", ""textures"": { ""45"": ""shirt.png"" } }");
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("0 to 44", refusal);
    }

    [Fact]
    public void TexturesAreRefusedOnAnItemThatIsNotAWearable()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(@"{ ""type"": ""texture"", ""file"": ""shirt.png"", ""name"": ""Picture"", ""textures"": { ""1"": ""shirt.png"" } }");
        string refusal = ClothingBoxKit.Refusal(b);
        Assert.NotNull(refusal);
        Assert.Contains("\"textures\" is for a wearable", refusal);
    }
}
