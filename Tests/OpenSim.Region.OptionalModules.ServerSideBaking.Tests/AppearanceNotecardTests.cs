/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// An appearance as a notecard in an object's contents, in the format osNpcSaveAppearance writes: the notecard
/// wrapper around the appearance's LLSD XML, with the wearables added so a bot can be baked from it.
/// </summary>
public class AppearanceNotecardTests
{
    private const int Marker = 25;

    private sealed record Rig(Scene Scene, SceneObjectPart Host, IAssetService Assets);

    private static Rig Setup()
    {
        var scene = new SceneHelpers().SetupScene();
        var host = SceneHelpers.AddSceneObject(scene, "outfit box", UUID.Random()).RootPart;
        return new Rig(scene, host, scene.AssetService);
    }

    private static AvatarAppearance Look(byte marker = 111)
    {
        var look = new AvatarAppearance();
        look.VisualParams[Marker] = marker;
        look.Wearables[(int)WearableType.Shape].Add(UUID.Random(), UUID.Random());
        look.Wearables[(int)WearableType.Skin].Add(UUID.Random(), UUID.Random());
        return look;
    }

    private static string TextOf(Rig rig, TaskInventoryItem item) => Encoding.UTF8.GetString(rig.Assets.Get(item.AssetID.ToString()).Data);

    [Fact]
    public void SavingWritesANotecardNamedAsAskedIntoTheObjectsContents()
    {
        var rig = Setup();

        var item = AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", Look(), includeHuds: false);

        Assert.NotNull(item);
        Assert.Equal("Priest", item.Name);
        Assert.Equal((int)AssetType.Notecard, item.Type);
        Assert.Equal(rig.Host.OwnerID, item.OwnerID);
        Assert.Same(item.Name, rig.Host.Inventory.GetInventoryItem(item.ItemID).Name);
        Assert.NotNull(rig.Assets.Get(item.AssetID.ToString()));
    }

    [Fact]
    public void TheNotecardTextHasTheLindenNotecardWrapperAroundLlsdXml()
    {
        var rig = Setup();
        var item = AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", Look(), includeHuds: false);
        Assert.NotNull(item);

        var text = TextOf(rig, item);

        Assert.StartsWith("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length ", text);
        Assert.EndsWith("}", text);
        var lengthLine = text.Split('\n')[6];
        var body = text[(text.IndexOf('\n', text.IndexOf("Text length", StringComparison.Ordinal)) + 1)..^1];
        Assert.Equal("Text length " + Encoding.UTF8.GetByteCount(body), lengthLine);
        Assert.IsType<OSDMap>(OSDParser.DeserializeLLSDXml(body));
    }

    [Fact]
    public void LoadingGivesBackTheLookThatWasSaved()
    {
        var rig = Setup();
        var look = Look();
        AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", look, includeHuds: false);

        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Priest", out var loaded, out var reason), reason);

        Assert.Equal(111, loaded.VisualParams[Marker]);
        Assert.Equal(look.VisualParams, loaded.VisualParams);
        Assert.Equal(look.Wearables[(int)WearableType.Shape][0], loaded.Wearables[(int)WearableType.Shape][0]);
        Assert.Equal(look.Wearables[(int)WearableType.Skin][0], loaded.Wearables[(int)WearableType.Skin][0]);
        Assert.Equal(look.AvatarHeight, loaded.AvatarHeight);
    }

    [Fact]
    public void TheNotecardCarriesTheWearablesOfEverySlot()
    {
        var rig = Setup();
        var look = Look();
        look.Wearables[(int)WearableType.Hair].Add(UUID.Random(), UUID.Random());
        look.Wearables[(int)WearableType.Tattoo].Add(UUID.Random(), UUID.Random());
        AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", look, includeHuds: false);

        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Priest", out var loaded, out var reason), reason);

        foreach (var slot in new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Tattoo })
            Assert.Equal(look.Wearables[(int)slot][0], loaded.Wearables[(int)slot][0]);
    }

    [Fact]
    public void SavingAgainUnderTheSameNameReplacesTheNotecard()
    {
        var rig = Setup();
        AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", Look(111), includeHuds: false);
        AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", Look(22), includeHuds: false);

        Assert.Single(rig.Host.Inventory.GetInventoryItems().Where(i => i.Name == "Priest"));
        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Priest", out var loaded, out _));
        Assert.Equal(22, loaded.VisualParams[Marker]);
    }

    [Fact]
    public void AHudIsLeftOutUnlessAsked()
    {
        var rig = Setup();
        var look = Look();
        look.SetAttachment((int)AttachmentPoint.HUDCenter, UUID.Random(), UUID.Random());
        look.SetAttachment((int)AttachmentPoint.Chest, UUID.Random(), UUID.Random());
        AppearanceNotecard.Save(rig.Host, rig.Assets, "NoHud", look, includeHuds: false);
        AppearanceNotecard.Save(rig.Host, rig.Assets, "WithHud", look, includeHuds: true);

        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "NoHud", out var without, out _));
        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "WithHud", out var with, out _));

        Assert.Single(without.GetAttachments());
        Assert.Equal(2, with.GetAttachments().Count);
    }

    [Fact]
    public void ANotecardTheStandardWriterMadeLoads()
    {
        var rig = Setup();
        var look = Look();
        var body = Util.GetFormattedXml((OSD)look.PackForNotecard(true));
        var text = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + Encoding.UTF8.GetByteCount(body) + "\n" + body + "}";
        var asset = new AssetBase(UUID.Random(), "Standard", (sbyte)AssetType.Notecard, rig.Host.OwnerID.ToString()) { Data = Encoding.UTF8.GetBytes(text) };
        rig.Assets.Store(asset);
        rig.Host.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "Standard", Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard, AssetID = UUID.Parse(asset.ID),
            OwnerID = rig.Host.OwnerID, CreatorID = rig.Host.OwnerID, ParentID = rig.Host.UUID,
        }, false);

        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Standard", out var loaded, out var reason), reason);

        Assert.Equal(111, loaded.VisualParams[Marker]);
    }

    [Fact]
    public void ANotecardCanBeLoadedByItsAssetId()
    {
        var rig = Setup();
        var item = AppearanceNotecard.Save(rig.Host, rig.Assets, "Priest", Look(), includeHuds: false);
        Assert.NotNull(item);

        Assert.True(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, item.AssetID.ToString(), out var loaded, out var reason), reason);
        Assert.Equal(111, loaded.VisualParams[Marker]);
    }

    [Fact]
    public void ANotecardThatIsNotAnAppearanceFailsWithAReason()
    {
        var rig = Setup();
        var asset = new AssetBase(UUID.Random(), "Junk", (sbyte)AssetType.Notecard, rig.Host.OwnerID.ToString())
            { Data = Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 6\nhello\n}") };
        rig.Assets.Store(asset);
        rig.Host.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "Junk", Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard, AssetID = UUID.Parse(asset.ID),
            OwnerID = rig.Host.OwnerID, CreatorID = rig.Host.OwnerID, ParentID = rig.Host.UUID,
        }, false);

        Assert.False(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Junk", out var loaded, out var reason));

        Assert.Null(loaded);
        Assert.Contains("Junk", reason);
    }

    [Fact]
    public void ANotecardThatIsNotThereFailsWithAReasonNamingIt()
    {
        var rig = Setup();

        Assert.False(AppearanceNotecard.TryLoad(rig.Host, rig.Assets, "Nothing here", out var loaded, out var reason));

        Assert.Null(loaded);
        Assert.Contains("Nothing here", reason);
    }

    [Fact]
    public void ANameOfNothingSavesNothing()
    {
        var rig = Setup();

        Assert.Null(AppearanceNotecard.Save(rig.Host, rig.Assets, "  ", Look(), includeHuds: false));
        Assert.Empty(rig.Host.Inventory.GetInventoryItems());
    }
}
