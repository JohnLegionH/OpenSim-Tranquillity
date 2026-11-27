/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Restore.Import;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// "attachPoint" on an "object" item of a part's contents: the object asset remembers the attachment point, so choosing Wear puts the object there.
/// Each test writes its own folders and shares nothing, so the class runs in parallel.
/// </summary>
public class AttachPointContentsTests
{
    private const string TwoObjects = @"
      { ""type"": ""object"", ""folder"": ""shirt-mesh"", ""base"": ""shirt"", ""name"": ""Test Right"", ""attachPoint"": 8 },
      { ""type"": ""object"", ""folder"": ""slab"", ""name"": ""Test Plain"" }";

    private static SceneObjectGroup Rez(Dictionary<string, byte[]> oar, string name) =>
        SceneObjectSerializer.FromOriginalXmlFormat(Encoding.UTF8.GetString(ClothingBoxKit.Asset(oar, ClothingBoxKit.Item(oar, name), "_object.xml")));

    [Fact]
    public void TheRegionReadsTheAttachPointOfAnObjectThatNamesOne()
    {
        SceneObjectGroup group = Rez(ClothingBoxKit.Import(ClothingBoxKit.Write(TwoObjects)), "Test Right");
        Assert.Equal((byte)8, group.RootPart.Shape.State);
        Assert.Equal((byte)8, group.RootPart.Shape.LastAttachPoint);
        Assert.Equal(8u, group.AttachmentPoint);
    }

    [Fact]
    public void AnObjectThatNamesNoAttachPointKeepsNone()
    {
        // pins the behaviour from before the key existed
        SceneObjectGroup group = Rez(ClothingBoxKit.Import(ClothingBoxKit.Write(TwoObjects)), "Test Plain");
        Assert.Equal((byte)0, group.RootPart.Shape.State);
        Assert.Equal((byte)0, group.RootPart.Shape.LastAttachPoint);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("56")]
    [InlineData("7.5")]
    [InlineData("\"right foot\"")]
    public void AnAttachPointThatIsNotAWholeNumberFrom1To55IsRefused(string value)
    {
        string contents = @"{ ""type"": ""object"", ""folder"": ""slab"", ""name"": ""Test Plain"", ""attachPoint"": " + value + " }";
        string refusal = ClothingBoxKit.Refusal(ClothingBoxKit.Write(contents));
        Assert.True(refusal != null && refusal.Contains("attachPoint", StringComparison.Ordinal), "refusal: " + (refusal ?? "none"));
    }

    [Fact]
    public void TheHighestPointAcceptedIsTheRegionsHighest() =>
        Assert.Equal((int)OpenMetaverse.AttachmentPoint.LastValid, LinksetManifest.MaxAttachPoint);

    [Fact]
    public void AnAttachPointOnSomethingThatIsNotAnObjectIsRefused()
    {
        string contents = @"{ ""type"": ""wearable"", ""file"": ""alpha.wearable.txt"", ""name"": ""Test Alpha"", ""textures"": { ""22"": ""upper-alpha.png"", ""21"": ""lower-alpha.png"" }, ""attachPoint"": 8 }";
        string refusal = ClothingBoxKit.Refusal(ClothingBoxKit.Write(contents));
        Assert.True(refusal != null && refusal.Contains("is for an object", StringComparison.Ordinal), "refusal: " + (refusal ?? "none"));
    }
}
