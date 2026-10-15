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
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>
/// The loaders' rewrite of creators and owners, pinned, and the real case it produces: an archive loaded
/// where its creators have no account and saved again credits one person with everything.
/// The pins fail when a loader changes, so the explanation text in <see cref="LoaderBehaviour"/> gets reviewed.
/// </summary>
[Collection(SceneCollection.Name)]
public class LoaderTests : OpenSimTestCase
{
    private static readonly UUID EstateOwner = UUID.Parse("e57a7e00-0000-4000-8000-0000000000e0");

    /// <summary>A scene whose objects were made by several creators, none of whom exist in the scene that loads it.</summary>
    private static byte[] ManyCreators(int perCreator, out OarScene source)
    {
        source = NewScene("Source Region");
        int tail = 0x1000;
        foreach (UUID creator in new[] { Alice, Bob, Carol })
            for (int i = 0; i < perCreator; i++)
                AddObject(source.Scene, "Obj" + tail, creator, creator, 1, tail++, creationDate: 1500000000 + tail * 100);
        return SaveOar(source);
    }

    private static void Load(OarScene dest, byte[] oar, Dictionary<string, object> options = null)
    {
        SceneManager.Instance.Scenes.Clear();
        SceneManager.Instance.Add(dest.Scene);
        using MemoryStream ms = new MemoryStream(oar);
        dest.Api.DearchiveRegion(ms, Guid.NewGuid(), options ?? new Dictionary<string, object>());
    }

    // ------------------------------------------------------------------ OAR

    [Fact]
    public void OarLoader_ReplacesCreatorsItCannotResolve_WithTheEstateOwner()
    {
        byte[] oar = ManyCreators(2, out _);
        OarScene dest = NewScene("Destination", EstateOwner);
        Load(dest, oar);

        SceneObjectGroup[] groups = dest.Scene.GetSceneObjectGroups().ToArray();
        Assert.Equal(6, groups.Length);
        foreach (SceneObjectGroup g in groups)
        {
            Assert.Equal(EstateOwner, g.RootPart.CreatorID);
            Assert.Equal(EstateOwner, g.RootPart.OwnerID);
            Assert.Equal(EstateOwner, g.RootPart.LastOwnerID);
            Assert.True(g.RootPart.CreationDate >= 1500000000);          // the date is not touched
        }
    }

    [Fact]
    public void OarLoader_NoDefaultUser_KeepsTheIds()
    {
        byte[] oar = ManyCreators(2, out _);
        OarScene dest = NewScene("Destination", EstateOwner);
        Load(dest, oar, new Dictionary<string, object> { ["no-defaultuser"] = true });

        HashSet<UUID> creators = new HashSet<UUID>(dest.Scene.GetSceneObjectGroups().Select(g => g.RootPart.CreatorID));
        Assert.True(creators.SetEquals(new[] { Alice, Bob, Carol }));
        Assert.DoesNotContain(EstateOwner, dest.Scene.GetSceneObjectGroups().Select(g => g.RootPart.OwnerID));
    }

    [Fact]
    public void OarLoader_KeepsAnAccountThatExists_AndACreatorWithCreatorData()
    {
        OarScene source = NewScene("Source Region");
        AddObject(source.Scene, "FromBob", Bob, Bob, 1, 0x2000);
        AddObject(source.Scene, "FromAlice", Alice, Alice, 1, 0x2100);
        AddObject(source.Scene, "FromDave", Dave, Dave, 1, 0x2200, creatorData: "http://grid.example.org:8002/;Dave Builder");
        byte[] oar = SaveOar(source);

        OarScene dest = NewScene("Destination", EstateOwner);
        UserAccountHelpers.CreateUserWithInventory(dest.Scene, new UserAccount(Bob) { FirstName = "Ben", LastName = "Maker" }, "pw");
        Load(dest, oar);

        SceneObjectGroup fromBob = dest.Scene.GetSceneObjectGroups().Single(g => g.Name == "FromBob");
        Assert.Equal(Bob, fromBob.RootPart.CreatorID);       // has an account here
        Assert.Equal(Bob, fromBob.RootPart.OwnerID);

        SceneObjectGroup fromAlice = dest.Scene.GetSceneObjectGroups().Single(g => g.Name == "FromAlice");
        Assert.Equal(EstateOwner, fromAlice.RootPart.CreatorID);
        Assert.Equal(EstateOwner, fromAlice.RootPart.OwnerID);

        // creator data present: the creator id is kept, but owner and last owner are still replaced
        SceneObjectGroup fromDave = dest.Scene.GetSceneObjectGroups().Single(g => g.Name == "FromDave");
        Assert.Equal(Dave, fromDave.RootPart.CreatorID);
        Assert.Equal("http://grid.example.org:8002/;Dave Builder", fromDave.RootPart.CreatorData);
        Assert.Equal(EstateOwner, fromDave.RootPart.OwnerID);
        Assert.Equal(EstateOwner, fromDave.RootPart.LastOwnerID);
    }

    [Fact]
    public void OarLoader_ReplacesCreatorsOfPrimInventoryItemsToo_AndZeroesUnknownGroups()
    {
        OarScene source = NewScene("Source Region");
        SceneObjectGroup g = AddObject(source.Scene, "Holder", Alice, Alice, 1, 0x3000);
        g.RootPart.GroupID = Group1;
        AddPrimItem(g.RootPart, "Item", UUID.Random(), AssetType.Notecard, Carol, Alice);
        byte[] oar = SaveOar(source);

        OarScene dest = NewScene("Destination", EstateOwner);
        Load(dest, oar);

        SceneObjectGroup loaded = dest.Scene.GetSceneObjectGroups().Single();
        Assert.Equal(UUID.Zero, loaded.RootPart.GroupID);    // no such group here
        TaskInventoryItem item = loaded.RootPart.TaskInventory.GetItems().Single();
        Assert.Equal(EstateOwner, item.CreatorID);
        Assert.Equal(EstateOwner, item.OwnerID);
    }

    [Fact]
    public void OarLoader_FromAStream_IgnoresDefaultUser()
    {
        // the stream constructor never reads the --default-user option; the file constructor does
        byte[] oar = ManyCreators(1, out _);
        OarScene dest = NewScene("Destination", EstateOwner);
        Load(dest, oar, new Dictionary<string, object> { ["default-user"] = Dave });
        Assert.All(dest.Scene.GetSceneObjectGroups(), g => Assert.Equal(EstateOwner, g.RootPart.CreatorID));
    }

    [Fact]
    public void RealCase_OarLoadedWhereTheCreatorsHaveNoAccount_AndSavedAgain_ShowsSingleCreatorWithTheSameAccountMatch()
    {
        byte[] original = ManyCreators(10, out _);
        ArchiveReport before = Inspect(original);
        Assert.Equal(3, before.Creators.Count);
        Assert.False(before.CreatorAnalysis.Fired);
        Assert.Equal(0, before.ExitCode);

        OarScene dest = NewScene("Destination", EstateOwner);
        Load(dest, original);
        byte[] resaved = SaveOar(dest);
        ArchiveReport after = Inspect(resaved);

        Assert.Equal(30, after.PartCount);
        PartyTotals only = Assert.Single(after.Creators.Where(c => c.Parts > 0));
        Assert.Equal(EstateOwner, only.Id);
        Assert.Contains(after.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.CreatorId == EstateOwner);
        AnalysisMatch m = Assert.Single(after.CreatorAnalysis.Matches, x => x.Id == "MATCH_SAME_ACCOUNT");
        Assert.Contains("30 of 30", m.Statement);
        Assert.Equal(3, after.ExitCode);
        Assert.Equal(LoaderBehaviour.Advisory, after.CreatorAnalysis.Advisory);
    }

    // ------------------------------------------------------------------ IAR

    private sealed class IarWorld
    {
        public TestScene Scene;
        public InventoryArchiverModule Module;
    }

    private static IarWorld NewIarWorld()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        IarWorld w = new IarWorld { Module = new InventoryArchiverModule(), Scene = new SceneHelpers().SetupScene() };
        SceneHelpers.SetupSceneModules(w.Scene, new SerialiserModule(), w.Module);
        StoreDefaultTexture(w.Scene.AssetService);
        return w;
    }

    private static List<InventoryItemBase> AllItems(IInventoryService service, UUID user)
    {
        List<InventoryItemBase> all = new List<InventoryItemBase>();
        foreach (InventoryFolderBase folder in service.GetInventorySkeleton(user))
            all.AddRange(service.GetFolderItems(user, folder.ID));
        return all;
    }

    private static byte[] SaveIar(IarWorld w, string first, string last, string path, Dictionary<string, object> options)
    {
        using MemoryStream ms = new MemoryStream();
        using ManualResetEvent done = new ManualResetEvent(false);
        w.Module.OnInventoryArchiveSaved += (id, ok, ui, p, stream, ex, count, filtered) => done.Set();
        Assert.True(w.Module.ArchiveInventory(UUID.Random(), first, last, path, "pw", ms, options));
        Assert.True(done.WaitOne(60000));
        return ms.ToArray();
    }

    private static byte[] IarFromSeveralCreators(out IarWorld world, int perCreator)
    {
        world = NewIarWorld();
        IarWorld w = world;
        UserAccountHelpers.CreateUserWithInventory(w.Scene, new UserAccount(IarEndToEndTests.OwnerId) { FirstName = "Olive", LastName = "Owner" }, "pw");
        foreach ((UUID id, string first, string last) in new[] { (Alice, "Ann", "Builder"), (Bob, "Ben", "Maker"), (Carol, "Cy", "Crafter") })
            UserAccountHelpers.CreateUserWithInventory(w.Scene, new UserAccount(id) { FirstName = first, LastName = last }, "pw");

        InventoryFolderBase stuff = UserInventoryHelpers.CreateInventoryFolder(w.Scene.InventoryService, IarEndToEndTests.OwnerId, "Stuff", true);
        UUID asset = UUID.Random();
        StoreAsset(w.Scene.AssetService, asset, AssetType.Notecard, Utf8("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n"), Alice);
        foreach (UUID creator in new[] { Alice, Bob, Carol })
            for (int i = 0; i < perCreator; i++)
                w.Scene.InventoryService.AddItem(new InventoryItemBase
                {
                    ID = UUID.Random(), Name = creator.ToString().Substring(0, 4) + " note " + i, Description = "", AssetID = asset, AssetType = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
                    Folder = stuff.ID, Owner = IarEndToEndTests.OwnerId, CreatorId = creator.ToString(), CreationDate = 1500000000 + i * 100000,
                    BasePermissions = 0x7fffffff, CurrentPermissions = 0x7fffffff, NextPermissions = 0x82000
                });
        return SaveIar(w, "Olive", "Owner", "/Stuff", new Dictionary<string, object>());
    }

    [Fact]
    public void IarLoader_MakesTheLoadingUserTheCreatorOfWhatItCannotResolve_AndTheOwnerOfEverything()
    {
        byte[] iar = IarFromSeveralCreators(out _, 3);

        IarWorld dest = NewIarWorld();
        UUID loader = UUID.Parse("10ade700-0000-4000-8000-000000000001");
        UserAccountHelpers.CreateUserWithInventory(dest.Scene, new UserAccount(loader) { FirstName = "Lou", LastName = "Loader" }, "pw");
        // Ben has an account here, so his items keep him as creator through the OSPA name
        UserAccountHelpers.CreateUserWithInventory(dest.Scene, new UserAccount(Bob) { FirstName = "Ben", LastName = "Maker" }, "pw");

        Assert.True(dest.Module.DearchiveInventory(UUID.Random(), "Lou", "Loader", "/", "pw", new MemoryStream(iar)));

        List<InventoryItemBase> items = AllItems(dest.Scene.InventoryService, loader).Where(i => i.AssetType == (int)AssetType.Notecard).ToList();
        Assert.Equal(9, items.Count);
        Assert.All(items, i => Assert.Equal(loader, i.Owner));
        Assert.All(items.Where(i => i.Name.StartsWith(Alice.ToString().Substring(0, 4))), i => Assert.Equal(loader, i.CreatorIdAsUuid));
        Assert.All(items.Where(i => i.Name.StartsWith(Carol.ToString().Substring(0, 4))), i => Assert.Equal(loader, i.CreatorIdAsUuid));
        Assert.All(items.Where(i => i.Name.StartsWith(Bob.ToString().Substring(0, 4))), i => Assert.Equal(Bob, i.CreatorIdAsUuid));
        Assert.All(items, i => Assert.InRange(i.CreationDate, 1500000000, 1500000000 + 9 * 100000));   // dates kept, none reset to now
    }

    [Fact]
    public void RealCase_IarLoadedIntoAnotherUsersInventory_AndSavedAgain_ShowsSingleCreatorWithTheSameAccountMatch()
    {
        byte[] original = IarFromSeveralCreators(out IarWorld source, 10);
        ArchiveReport before = Inspect(original);
        Assert.False(before.CreatorAnalysis.Fired);

        IarWorld dest = NewIarWorld();
        UUID loader = UUID.Parse("10ade700-0000-4000-8000-000000000002");
        UserAccountHelpers.CreateUserWithInventory(dest.Scene, new UserAccount(loader) { FirstName = "Lou", LastName = "Loader" }, "pw");
        Assert.True(dest.Module.DearchiveInventory(UUID.Random(), "Lou", "Loader", "/", "pw", new MemoryStream(original)));

        byte[] resaved = SaveIar(dest, "Lou", "Loader", "/Stuff", new Dictionary<string, object> { ["home"] = "http://grid.example.org:8002" });
        ArchiveReport after = Inspect(resaved);

        Assert.Equal(30, after.IarItemCount);
        Assert.Contains(after.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.Grouping == "uuid" && f.CreatorId == loader);
        Assert.Contains(after.CreatorAnalysis.Matches, m => m.Id == "MATCH_SAME_ACCOUNT" && m.Statement.Contains("loading the archive"));
        Assert.Equal(3, after.ExitCode);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class SceneCollection
{
    /// <summary>Tests that build scenes share process-wide state: SceneManager.Instance and Util.FireAndForgetMethod.</summary>
    public const string Name = "scene state";
}
