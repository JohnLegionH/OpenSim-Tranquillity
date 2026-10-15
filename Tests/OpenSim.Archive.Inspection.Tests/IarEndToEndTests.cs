/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>An IAR written by the repository's own saver, read back by the inspector.</summary>
[Collection(SceneCollection.Name)]
public class IarEndToEndTests : OpenSimTestCase
{
    public static readonly UUID OwnerId = UUID.Parse("0e0e0e00-0000-4000-8000-000000000010");

    internal sealed class IarFixture
    {
        public TestScene Scene;
        public InventoryArchiverModule Module;
        public UUID Tex, ScriptAsset, ObjAsset, BoxedInner;
        public InventoryItemBase ItemAnn, ItemBen, ItemZed, ObjItem, LinkItem, DeepItem;
        public byte[] Iar;
        public ArchiveReport Report;
    }

    internal static IarFixture BuildScene()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        IarFixture f = new IarFixture();
        f.Module = new InventoryArchiverModule();
        f.Scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(f.Scene, new SerialiserModule(), f.Module);
        StoreDefaultTexture(f.Scene.AssetService);

        // Ann and Ben have accounts here; Zed (Carol's id) has none, so the saver cannot name him.
        UserAccountHelpers.CreateUserWithInventory(f.Scene, new UserAccount(Alice) { FirstName = "Ann", LastName = "Builder" }, "pw");
        UserAccountHelpers.CreateUserWithInventory(f.Scene, new UserAccount(Bob) { FirstName = "Ben", LastName = "Maker" }, "pw");
        UserAccountHelpers.CreateUserWithInventory(f.Scene, new UserAccount(OwnerId) { FirstName = "Olive", LastName = "Owner" }, "pw");

        f.Tex = UUID.Parse("44444444-aaaa-4000-8000-000000000001");
        f.ScriptAsset = UUID.Parse("44444444-aaaa-4000-8000-000000000002");
        f.ObjAsset = UUID.Parse("44444444-aaaa-4000-8000-000000000003");
        f.BoxedInner = UUID.Parse("44444444-aaaa-4000-8000-000000000004");
        StoreAsset(f.Scene.AssetService, f.Tex, AssetType.Texture, new byte[128], Alice);
        StoreAsset(f.Scene.AssetService, f.ScriptAsset, AssetType.LSLText, Utf8("default { state_entry() { llSetTexture(\"" + f.Tex + "\", 0); } }"), Bob);

        // a boxed object whose part was made by Ann, holding a second boxed object made by Ben
        SceneObjectGroup inner = SceneHelpers.CreateSceneObject(1, Bob, "Inner-", 0x700);
        inner.Name = "Inner";
        inner.RootPart.CreatorID = Bob;
        f.Scene.AssetService.Store(AssetHelpers.CreateAsset(f.BoxedInner, inner));
        SceneObjectGroup outer = SceneHelpers.CreateSceneObject(2, Alice, "Outer-", 0x600);
        outer.Name = "Outer";
        foreach (SceneObjectPart p in outer.Parts) p.CreatorID = Alice;
        AddPrimItem(outer.RootPart, "Inner item", f.BoxedInner, AssetType.Object, Bob, Alice, 1500000000);
        f.Scene.AssetService.Store(AssetHelpers.CreateAsset(f.ObjAsset, outer));

        InventoryFolderBase stuff = UserInventoryHelpers.CreateInventoryFolder(f.Scene.InventoryService, OwnerId, "Stuff", true);
        InventoryFolderBase deep = UserInventoryHelpers.CreateInventoryFolder(f.Scene.InventoryService, OwnerId, "Stuff/Deep", true);

        f.ItemAnn = AddItem(f.Scene, stuff, "Ann's texture", f.Tex, AssetType.Texture, Alice, 1550000000);
        f.ItemBen = AddItem(f.Scene, stuff, "Ben's script", f.ScriptAsset, AssetType.LSLText, Bob, 1560000000);
        f.ItemZed = AddItem(f.Scene, stuff, "Zed's texture", f.Tex, AssetType.Texture, Carol, 1570000000);
        f.ObjItem = AddItem(f.Scene, stuff, "Outer object", f.ObjAsset, AssetType.Object, Alice, 1580000000);
        f.DeepItem = AddItem(f.Scene, deep, "Deep script", f.ScriptAsset, AssetType.LSLText, Bob, 1590000000);
        f.LinkItem = AddItem(f.Scene, deep, "Link to the script", f.ItemBen.ID, AssetType.Link, Bob, 1600000000);
        return f;
    }

    private static InventoryItemBase AddItem(TestScene scene, InventoryFolderBase folder, string name, UUID assetOrTarget, AssetType type, UUID creator, int date)
    {
        InventoryItemBase item = new InventoryItemBase
        {
            ID = UUID.Random(),
            Name = name,
            Description = "",
            AssetID = assetOrTarget,
            AssetType = (int)type,
            InvType = type == AssetType.Texture ? (int)InventoryType.Texture : type == AssetType.LSLText ? (int)InventoryType.LSL : type == AssetType.Link ? (int)InventoryType.Object : (int)InventoryType.Object,
            Folder = folder.ID,
            Owner = OwnerId,
            CreatorId = creator.ToString(),
            CreationDate = date,
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7fffffff,
            NextPermissions = 0x82000,
            EveryOnePermissions = 0,
            GroupPermissions = 0
        };
        Assert.True(scene.InventoryService.AddItem(item));
        return item;
    }

    internal static byte[] Save(IarFixture f, Dictionary<string, object> options)
    {
        using MemoryStream ms = new MemoryStream();
        using ManualResetEvent done = new ManualResetEvent(false);
        f.Module.OnInventoryArchiveSaved += (id, ok, ui, path, stream, ex, count, filtered) => done.Set();
        Assert.True(f.Module.ArchiveInventory(UUID.Random(), "Olive", "Owner", "/Stuff", "pw", ms, options));
        Assert.True(done.WaitOne(60000));
        return ms.ToArray();
    }

    [Fact]
    public void WithoutHome_OspaCreators_Version03()
    {
        IarFixture f = BuildScene();
        byte[] iar = Save(f, new Dictionary<string, object>());
        ArchiveReport r = Inspect(iar);

        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(ArchiveKind.Iar, r.Archive.Kind);
        Assert.Equal("0.3", r.Archive.Version);
        Assert.True(r.Archive.AssetsIncluded);
        Assert.True(r.Archive.ControlFileFirst);

        Assert.Equal(6, r.IarItemCount);
        ItemRecord ann = r.Items.Single(i => i.Name == "Ann's texture");
        Assert.Equal(ItemSource.InventoryArchive, ann.Source);
        Assert.Equal("Ann Builder", ann.Creator.Name);       // from the OSPA name; no creator id in a 0.x item
        Assert.Equal("ospa:n=Ann Builder", ann.Creator.Raw);
        Assert.Equal(UUID.Zero, ann.Creator.Id);
        Assert.False(ann.Creator.HasData);
        Assert.Equal(OwnerId, ann.OwnerId);
        Assert.Equal(1550000000, ann.CreationDate);
        Assert.Equal((int)AssetType.Texture, ann.AssetType);
        Assert.Equal(f.Tex, ann.AssetId);
        Assert.Equal("Stuff", ann.FolderPath);
        Assert.NotNull(ann.Masks.Base);
        Assert.NotNull(ann.Masks.NextOwner);

        // the saver cannot name a creator with no account here: the field is empty
        ItemRecord zed = r.Items.Single(i => i.Name == "Zed's texture");
        Assert.True(zed.Creator.IsBlank);

        Assert.Equal("Stuff/Deep", r.Items.Single(i => i.Name == "Deep script").FolderPath);
        Assert.Contains(r.Folders, x => x.Path == "Stuff");
        Assert.Contains(r.Folders, x => x.Path == "Stuff/Deep" && x.ItemCount == 2);
    }

    [Fact]
    public void WithHome_CreatorDataAndIds_Version12()
    {
        IarFixture f = BuildScene();
        byte[] iar = Save(f, new Dictionary<string, object> { ["home"] = "http://grid.example.org:8002" });
        ArchiveReport r = Inspect(iar);

        Assert.Equal("1.2", r.Archive.Version);
        ItemRecord ann = r.Items.Single(i => i.Name == "Ann's texture");
        Assert.Equal(Alice, ann.Creator.Id);
        Assert.True(ann.Creator.HasData);
        Assert.Equal("http://grid.example.org:8002", ann.Creator.Home);
        Assert.Equal("Ann Builder", ann.Creator.Name);

        ItemRecord zed = r.Items.Single(i => i.Name == "Zed's texture");
        Assert.Equal(Carol, zed.Creator.Id);             // CreatorID is written even with no account
        Assert.False(zed.Creator.HasData);

        PartyTotals annTotals = r.Creators.Single(c => c.Id == Alice);
        Assert.Equal("Ann Builder", annTotals.Name);
    }

    [Fact]
    public void LinkAndBoxedObjects()
    {
        IarFixture f = BuildScene();
        byte[] iar = Save(f, new Dictionary<string, object> { ["home"] = "http://grid.example.org:8002" });
        ArchiveReport r = Inspect(iar);

        ItemRecord link = r.Items.Single(i => i.Name == "Link to the script");
        Assert.Equal((int)AssetType.Link, link.AssetType);
        Assert.Equal(f.ItemBen.ID, link.AssetId);                 // a link names an item, not an asset
        Assert.DoesNotContain(f.ItemBen.ID, r.ReferencedAssetIds);
        Assert.DoesNotContain(f.ItemBen.ID, r.MissingAssets);

        Assert.True(r.BoxedObjectCount == 2, Dump(r));
        BoxedObjectRecord outer = r.BoxedObjects.Single(b => b.AssetId == f.ObjAsset);
        BoxedObjectRecord inner = r.BoxedObjects.Single(b => b.AssetId == f.BoxedInner);
        Assert.Equal(1, outer.Depth);
        Assert.Equal(2, inner.Depth);
        Assert.Equal(f.ObjItem.ID, r.Items.Single(i => i.Name == "Outer object").ItemId);
        Assert.Equal(Alice, outer.RootCreator.Id);
        Assert.Equal(Bob, inner.RootCreator.Id);

        // contents of boxed objects count toward their creators
        PartyTotals ann = r.Creators.Single(c => c.Id == Alice);
        Assert.True(ann.Parts >= 2);
        PartyTotals ben = r.Creators.Single(c => c.Id == Bob);
        Assert.True(ben.Parts >= 1);

        Assert.Equal(0, r.MissingAssetCount);
        Assert.Contains(f.Tex, r.CertainReferencedAssetIds);
    }

    [Fact]
    public void WithoutAssets_SaysSoAndReportsNothingMissing()
    {
        IarFixture f = BuildScene();
        byte[] iar = Save(f, new Dictionary<string, object> { ["noassets"] = true });
        ArchiveReport r = Inspect(iar);

        Assert.Equal(false, r.Archive.AssetsIncluded);
        Assert.Equal(0, r.AssetCount);
        Assert.Equal(0, r.MissingAssetCount);                     // none are expected when the archive says so
        Assert.Contains(f.Tex, r.ReferencedAssetIds);
        Assert.Equal(0, r.BoxedObjectCount);
        Assert.Equal(InspectionStatus.Complete, r.Status);
    }
}
