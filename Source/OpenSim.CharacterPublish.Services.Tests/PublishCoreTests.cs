/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSim.CharacterPublish.Objects;
using OpenSim.Framework;
using Nini.Config;
using OpenSim.Services.InventoryService;

namespace OpenSim.CharacterPublish.Services.Tests;

/// <summary>The checks every successful publish must pass, shared by the synthetic and the Max proofs.</summary>
internal static class Verify
{
    /// <summary>The outfit, its items, the assets and the previous look are in the resident's inventory exactly as the bundle says, and nothing else changed.</summary>
    public static List<string> Published(ScratchGrid g, ScratchGrid.Resident r, Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content,
        PublishResult res, Dictionary<string, string> before, bool previousLook = true)
    {
        Assert.True(res.Complete, res.Summary + " " + string.Join("; ", res.Failures));

        var mo = g.Plain.GetFolderContent(r.Id, r.Root).Folders.Where(f => f.Type == BundleTypes.FolderMyOutfits).ToList();
        Assert.Single(mo);
        Assert.Equal(mo[0].ID, res.MyOutfitsFolderId);

        // the outfit folder: type 47, under My Outfits, named by the bundle
        var outfit = g.Plain.GetFolder(r.Id, bundle.OutfitFolder.Id)!;
        Assert.NotNull(outfit);
        Assert.Equal(BundleTypes.FolderOutfit, outfit.Type);
        Assert.Equal(mo[0].ID, outfit.ParentID);
        Assert.Equal(bundle.OutfitFolder.Name, outfit.Name);
        Assert.Equal(r.Id, outfit.Owner);

        // exactly the bundle's items, with the right types, flags, permissions, creator and owner
        var inFolder = g.Plain.GetFolderContent(r.Id, outfit.ID);
        Assert.Empty(inFolder.Folders);
        Assert.Equal(bundle.Items.Select(i => i.Id).Order(), inFolder.Items.Select(i => i.ID).Order());
        foreach (var want in bundle.Items)
        {
            var have = inFolder.Items.Single(i => i.ID == want.Id);
            Assert.Equal(r.Id, have.Owner);
            Assert.Equal(outfit.ID, have.Folder);
            Assert.Equal(want.Name, have.Name);
            Assert.Equal(want.Description, have.Description);
            Assert.Equal(want.AssetId, have.AssetID);
            Assert.Equal(want.AssetType, have.AssetType);
            Assert.Equal(want.InvType, have.InvType);
            Assert.Equal(want.Flags, have.Flags);
            Assert.Equal(want.BasePermissions, have.BasePermissions);
            Assert.Equal(want.CurrentPermissions, have.CurrentPermissions);
            Assert.Equal(want.NextPermissions, have.NextPermissions);
            Assert.Equal(want.GroupPermissions, have.GroupPermissions);
            Assert.Equal(want.EveryonePermissions, have.EveryOnePermissions);
            Assert.Equal(want.CreatorId.ToString(), have.CreatorIdentification, ignoreCase: true);
            Assert.Equal(want.CreationDate, have.CreationDate);
            Assert.Equal(want.OwnerId, have.Owner);
            Assert.Equal(bundle.CreatorId, UUID.Parse(have.CreatorId));
        }

        // every asset is retrievable and equal to the bundle's
        foreach (var a in bundle.Assets)
        {
            var asset = g.PlainAssets.Get(a.Id.ToString());
            Assert.NotNull(asset);
            Assert.Equal(content[a.Id], asset.Data);
            Assert.Equal((sbyte)a.AssetType, asset.Type);
        }

        // the previous look: links to the Current Outfit's four targets, never to links
        var expectedAdded = (before.ContainsKey("F:" + mo[0].ID) ? 0 : 1) + 1 + bundle.Items.Count; // My Outfits (when new), the outfit folder, its items
        if (previousLook)
        {
            var look = bundle.CurrentLook!;
            var lf = g.Plain.GetFolder(r.Id, look.FolderId)!;
            Assert.NotNull(lf);
            Assert.Equal(BundleTypes.FolderOutfit, lf.Type);
            Assert.Equal(mo[0].ID, lf.ParentID);
            Assert.Equal(look.FolderName, lf.Name);
            Assert.Matches(@"^Previous look \d{4}-\d{2}-\d{2} before outfit \d+$", lf.Name);
            var links = g.Plain.GetFolderContent(r.Id, lf.ID).Items;
            Assert.Equal(r.Targets.Select(t => t.ID).Order(), links.Select(l => l.AssetID).Order());
            foreach (var l in links)
            {
                Assert.Equal(BundleTypes.AssetLink, l.AssetType);
                Assert.Equal(r.Id, l.Owner);
                var target = r.Targets.Single(t => t.ID == l.AssetID);
                Assert.Equal(target.InvType, l.InvType);
                Assert.Equal(target.Name, l.Name);
                Assert.Equal(target.Flags, l.Flags);
                Assert.NotEqual(BundleTypes.AssetLink, g.Plain.GetItem(r.Id, l.AssetID)!.AssetType); // the target is an item, not another link
            }
            expectedAdded += 1 + links.Count;
        }

        // nothing that was there changed: the Current Outfit and every older folder and item are identical; only the parents gained a child
        var added = ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(r.Id), r.Root, mo[0].ID);
        Assert.Equal(expectedAdded, added.Count);
        return added;
    }

    public static void NothingWritten(ScratchGrid g, ScratchGrid.Resident r, Dictionary<string, string> before, RecordingInventory inv, RecordingAssets assets)
    {
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(r.Id))); // not even a folder version moved
        Assert.Empty(inv.ItemsAdded);
        Assert.Empty(inv.FoldersAdded);
        Assert.Empty(assets.Stored);
        Assert.Empty(inv.Forbidden);
        Assert.Empty(assets.Forbidden);
    }
}

[Collection(ScratchGridCollection.Name)]
public class PublishCoreTests
{
    readonly ScratchGrid g;
    public PublishCoreTests(ScratchGrid grid) => g = grid;

    PublishService Service(RecordingInventory inv, RecordingAssets assets) => new(assets, inv);

    // ---- the happy path -------------------------------------------------------------------------------------------

    [Fact]
    public void PublishWritesTheOutfitAndThePreviousLookAndChangesNothingElse()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id);

        Verify.Published(g, r, a.Bundle, a.Content, res, before);
        Assert.True(res.MyOutfitsCreated);
        Assert.True(res.OutfitFolderCreated);
        Assert.True(res.PreviousLookFolderCreated);
        Assert.Equal(a.Bundle.Assets.Count, res.AssetsStored.Count);
        Assert.Equal(a.Bundle.Items.Count, res.ItemsCreated.Count);
        Assert.Equal(4, res.PreviousLookLinksCreated.Count);
        Assert.False(res.AlreadyPublished);
        Assert.Empty(inv.Forbidden);
        Assert.Empty(ast.Forbidden);
        // an outfit with the order's parts: shape, skin, eyes, one attachment, the credits notecard
        Assert.Equal(new[] { "attachment-1", "eyes", "notecard", "shape", "skin" }, a.Bundle.Items.Select(i => i.Role).Order().ToArray());
        // the blank texture the wearables use is not on this scratch grid, which the result says
        Assert.Contains(res.Warnings, w => w.Contains("not on this grid"));
    }

    [Fact]
    public void ThePublishIdIsDerivedFromTheBundleAndTheResident()
    {
        var r1 = g.NewResident(); var r2 = g.NewResident();
        var a1 = BundleFixtures.Assemble(r1.Id);
        Assert.Equal(PublishService.DerivePublishId(a1.Bundle, r1.Id), PublishService.DerivePublishId(BundleFixtures.Assemble(r1.Id).Bundle, r1.Id));
        Assert.NotEqual(PublishService.DerivePublishId(a1.Bundle, r1.Id), PublishService.DerivePublishId(BundleFixtures.Assemble(r2.Id).Bundle, r2.Id));
        Assert.NotEqual(PublishService.DerivePublishId(a1.Bundle, r1.Id), PublishService.DerivePublishId(BundleFixtures.Assemble(r1.Id, folderNumber: 2).Bundle, r1.Id));
        Assert.NotEqual(PublishService.DerivePublishId(a1.Bundle, r1.Id), PublishService.DerivePublishId(BundleFixtures.Assemble(r1.Id, name: "Other").Bundle, r1.Id));
    }

    // ---- publishing again ------------------------------------------------------------------------------------------

    [Fact]
    public void PublishingTheSameBundleAgainWritesNothingAndSaysAlreadyPublished()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var first = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);
        Assert.True(first.Complete, first.Summary);
        var afterFirst = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var again = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(again.Complete, again.Summary);
        Assert.True(again.AlreadyPublished);
        Assert.Equal(first.PublishId, again.PublishId);
        Assert.Equal(first.OutfitFolderId, again.OutfitFolderId);
        Assert.Equal(first.PreviousLookFolderId, again.PreviousLookFolderId);
        Assert.Equal(a.Bundle.Items.Count, again.ItemsAlreadyPresent.Count);
        Assert.Equal(a.Bundle.Assets.Count, again.AssetsAlreadyPresent.Count);
        Assert.Equal(first.PreviousLookLinks.Order(), again.PreviousLookLinks.Order());
        Assert.Contains("Already published", again.Summary);
        // not one write call, and the inventory is identical to the byte, versions included
        Assert.Empty(inv.ItemsAdded); Assert.Empty(inv.FoldersAdded); Assert.Empty(ast.Stored); Assert.Empty(inv.Forbidden);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(afterFirst, g.Snapshot(r.Id)));
        Assert.DoesNotContain(inv.Calls, c => c.StartsWith("Add")); // not even an attempt
        // still one My Outfits, one outfit folder and one previous-look folder
        var mo = g.MyOutfitsOf(r.Id)!;
        Assert.Equal(2, g.Plain.GetFolderContent(r.Id, mo.ID).Folders.Count);
    }

    [Fact]
    public void ARepublishAfterTheResidentChangedWhatTheyWearAddsNothingToThePreviousLook()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        Assert.True(Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id).Complete);
        // the resident now wears something else: the Current Outfit changes, and the old link is gone
        var gone = r.Links[3];
        gone.Folder = g.Plain.GetFolderForType(r.Id, FolderType.Trash).ID; // taken off, moved to the Trash (a test never deletes a row)
        Assert.True(g.Plain.MoveItems(r.Id, new List<InventoryItemBase> { gone }));
        var extra = ScratchGrid.Item(r.Id, g.Plain.GetFolderForType(r.Id, FolderType.Clothing).ID, "New Shirt", AssetType.Clothing, InventoryType.Wearable, 4);
        Assert.True(g.Plain.AddItem(extra));
        Assert.True(g.Plain.AddItem(ScratchGrid.Link(r.Id, r.Cof, extra, "@400")));
        var snap = g.Snapshot(r.Id);

        var again = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(again.Complete, again.Summary);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(snap, g.Snapshot(r.Id)));
        Assert.Contains(again.Warnings, w => w.Contains("no longer holds"));
    }

    // ---- failure injection -----------------------------------------------------------------------------------------

    [Fact]
    public void AnItemWriteThatFailsMidwayLeavesAPartialOutfitAndARetryCompletesItWithoutDuplicates()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv1 = g.NewInventory(); var ast1 = g.NewAssets();
        inv1.FailAddItem = (n, _) => n == 3; // the first two items are written, the third is refused by the database

        var broken = Service(inv1, ast1).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(broken.Partial);
        Assert.False(broken.Complete);
        Assert.Equal("item", broken.Failures[0].Step);
        Assert.Equal(2, broken.ItemsCreated.Count);
        Assert.Contains("completes it", broken.Summary);
        Assert.Empty(inv1.Forbidden);
        // what is in the inventory now: only additions, the outfit folder and two items, no previous look yet
        var mid = g.Snapshot(r.Id);
        var added = ScratchGrid.AssertOnlyAdditions(before, mid, r.Root, g.MyOutfitsOf(r.Id)!.ID);
        Assert.Equal(1 + 1 + 2, added.Count);
        Assert.Null(g.Plain.GetFolder(r.Id, a.Bundle.CurrentLook!.FolderId));

        var inv2 = g.NewInventory(); var ast2 = g.NewAssets();
        var fixedUp = Service(inv2, ast2).Publish(a.Bundle, a.Content, r.Id);

        Verify.Published(g, r, a.Bundle, a.Content, fixedUp, before);
        Assert.False(fixedUp.MyOutfitsCreated); // made by the first attempt
        Assert.False(fixedUp.OutfitFolderCreated);
        Assert.Equal(2, fixedUp.ItemsAlreadyPresent.Count);
        Assert.Equal(a.Bundle.Items.Count - 2, fixedUp.ItemsCreated.Count);
        Assert.Empty(fixedUp.ItemsCreated.Intersect(broken.ItemsCreated)); // nothing written twice
        Assert.Empty(ast2.Stored); // the assets were already there
        Assert.Empty(inv2.Forbidden);
        Assert.Single(g.Plain.GetFolderContent(r.Id, r.Root).Folders, f => f.Type == BundleTypes.FolderMyOutfits);
    }

    [Fact]
    public void ADatabaseThatThrowsIsTreatedTheSameAsOneThatRefuses()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv1 = g.NewInventory();
        inv1.FailAddItem = (n, _) => n >= 2; inv1.FailByException = true;

        var broken = Service(inv1, g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(broken.Partial);
        Assert.Single(broken.ItemsCreated);
        Assert.Contains("injected: the database went away", broken.Failures[0].Message); // the reason is kept for the operator
        var fixedUp = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);
        Verify.Published(g, r, a.Bundle, a.Content, fixedUp, before);
    }

    [Fact]
    public void AFailureWhileWritingThePreviousLookIsReportedAndARetryCompletesTheLinks()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv1 = g.NewInventory();
        inv1.FailAddItem = (n, _) => n == a.Bundle.Items.Count + 3; // the outfit is whole, two links are written, the third fails

        var broken = Service(inv1, g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(broken.Partial);
        Assert.Equal("link", broken.Failures[0].Step);
        Assert.Equal(a.Bundle.Items.Count, broken.ItemsCreated.Count);
        Assert.Equal(2, broken.PreviousLookLinksCreated.Count);
        Assert.Equal(2, g.Plain.GetFolderContent(r.Id, a.Bundle.CurrentLook!.FolderId).Items.Count);

        var inv2 = g.NewInventory();
        var fixedUp = Service(inv2, g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Verify.Published(g, r, a.Bundle, a.Content, fixedUp, before);
        Assert.Equal(2, fixedUp.PreviousLookLinksCreated.Count);
        Assert.DoesNotContain(inv2.ItemsAdded, i => broken.PreviousLookLinksCreated.Contains(i.ID));
        Assert.False(fixedUp.PreviousLookFolderCreated);
    }

    [Fact]
    public void AnAssetThatCannotBeStoredStopsBeforeAnyInventoryWriteAndARetryCompletes()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id, name: "AssetFail");
        var before = g.Snapshot(r.Id);
        var ast1 = g.NewAssets();
        ast1.FailStore = (n, _) => n == 2;
        var inv1 = g.NewInventory();

        var broken = Service(inv1, ast1).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(broken.Partial);
        Assert.Equal("asset", broken.Failures[0].Step);
        Assert.Single(broken.AssetsStored);
        Assert.Empty(inv1.ItemsAdded); Assert.Empty(inv1.FoldersAdded);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(r.Id)));

        var ast2 = g.NewAssets();
        var fixedUp = Service(g.NewInventory(), ast2).Publish(a.Bundle, a.Content, r.Id);
        Verify.Published(g, r, a.Bundle, a.Content, fixedUp, before);
        Assert.Equal(a.Bundle.Assets.Count - 1, ast2.Stored.Count);
        Assert.Single(fixedUp.AssetsAlreadyPresent);
    }

    [Fact]
    public void AFolderThatCannotBeCreatedLeavesMyOutfitsAndARetryReusesIt()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv1 = g.NewInventory();
        inv1.FailAddFolder = (n, _) => n == 2; // My Outfits is made, the outfit folder is refused

        var broken = Service(inv1, g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(broken.Partial);
        Assert.Equal("folder", broken.Failures[0].Step);
        Assert.True(broken.MyOutfitsCreated);
        Assert.Empty(inv1.ItemsAdded);

        var fixedUp = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);
        Verify.Published(g, r, a.Bundle, a.Content, fixedUp, before);
        Assert.False(fixedUp.MyOutfitsCreated);
        Assert.Single(g.Plain.GetFolderContent(r.Id, r.Root).Folders, f => f.Type == BundleTypes.FolderMyOutfits);
    }

    // ---- limits ----------------------------------------------------------------------------------------------------

    [Fact]
    public void PublishingPastTheCapIsRefusedWithAMessageAndWritesNothing()
    {
        var r = g.NewResident();
        var opt = new PublishOptions { MaxOutfitsPerResident = 2 };
        var firstAssets = new HashSet<UUID>();
        foreach (var n in new[] { 1, 2 })
        {
            var ok = BundleFixtures.Assemble(r.Id, n);
            Assert.True(Service(g.NewInventory(), g.NewAssets()).Publish(ok.Bundle, ok.Content, r.Id, opt).Complete);
            firstAssets.UnionWith(ok.Bundle.Assets.Select(x => x.Id));
        }
        var third = BundleFixtures.Assemble(r.Id, 3, name: "Cap Check"); // different wearables, so its assets are new
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(third.Bundle, third.Content, r.Id, opt);

        Assert.True(res.Refused);
        Assert.False(res.Complete);
        var refusal = Assert.Single(res.Refusals);
        Assert.Equal("cap-reached", refusal.Code);
        Assert.Contains("You already have 2 published outfits", refusal.Message);
        Assert.Contains("the limit on this grid is 2", refusal.Message);
        Assert.Contains("Nothing was written", refusal.Message);
        Verify.NothingWritten(g, r, before, inv, ast);
        var newAssets = third.Bundle.Assets.Where(x => !firstAssets.Contains(x.Id)).ToList();
        Assert.NotEmpty(newAssets); // the renamed character's wearables and object
        Assert.All(newAssets, x => Assert.Null(g.PlainAssets.Get(x.Id.ToString())));
        // the first two are still there, and still the retry of one of them is not "a new outfit"
        var retry = Service(g.NewInventory(), g.NewAssets()).Publish(BundleFixtures.Assemble(r.Id, 2).Bundle, BundleFixtures.Assemble(r.Id, 2).Content, r.Id, opt);
        Assert.True(retry.AlreadyPublished);
    }

    [Fact]
    public void TheCapCountsPublishedOutfitsOnlyNotTheResidentsOwnOrPreviousLooks()
    {
        var r = g.NewResident();
        var mo = g.AddFolder(r.Id, r.Root, "My Outfits", (FolderType)BundleTypes.FolderMyOutfits);
        for (var i = 0; i < 5; i++) // five outfits the resident saved themselves, each with an item of their own
        {
            var own = g.AddFolder(r.Id, mo.ID, "Casual " + i, (FolderType)BundleTypes.FolderOutfit);
            Assert.True(g.Plain.AddItem(ScratchGrid.Item(r.Id, own.ID, "own", AssetType.Clothing, InventoryType.Wearable, 4)));
        }
        var opt = new PublishOptions { MaxOutfitsPerResident = 1 };
        var first = BundleFixtures.Assemble(r.Id, 1);
        var res = Service(g.NewInventory(), g.NewAssets()).Publish(first.Bundle, first.Content, r.Id, opt);
        Assert.True(res.Complete, res.Summary);
        Assert.False(res.MyOutfitsCreated); // the resident's own My Outfits was used
        Assert.Equal(mo.ID, res.MyOutfitsFolderId);
        // the previous-look folder is not counted either: a second publish is refused only because one published outfit is already held
        var second = BundleFixtures.Assemble(r.Id, 2);
        var refused = Service(g.NewInventory(), g.NewAssets()).Publish(second.Bundle, second.Content, r.Id, opt);
        Assert.Equal("cap-reached", Assert.Single(refused.Refusals).Code);
        Assert.Contains("You already have 1 published outfits", refused.Refusals[0].Message);
    }

    [Fact]
    public void ABundleOverTheSizeLimitIsRefusedWithAClearMessage()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id, new PublishOptions { Limits = new BundleLimits(MaxAssets: 64, MaxAssetBytes: 1000) });

        var refusal = Assert.Single(res.Refusals, x => x.Code == "bundle-too-large");
        Assert.Contains("larger than this grid allows", refusal.Message);
        Assert.Contains("Nothing was written", refusal.Message);
        Verify.NothingWritten(g, r, before, inv, ast);

        var tooMany = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id, new PublishOptions { MaxItems = 3 });
        Assert.Equal("bundle-too-large", Assert.Single(tooMany.Refusals).Code);
        Verify.NothingWritten(g, r, before, inv, ast);
    }

    // ---- two residents ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoResidentsPublishingTheSameCharacterShareTheMeshButNotTheirObjectsWearablesOrFolders()
    {
        var ra = g.NewResident(); var rb = g.NewResident();
        var a = BundleFixtures.Assemble(ra.Id);
        var b = BundleFixtures.Assemble(rb.Id);
        var beforeA = g.Snapshot(ra.Id); var beforeB = g.Snapshot(rb.Id);

        var resA = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, ra.Id);
        var astB = g.NewAssets();
        var resB = Service(g.NewInventory(), astB).Publish(b.Bundle, b.Content, rb.Id);

        Verify.Published(g, ra, a.Bundle, a.Content, resA, beforeA);
        Verify.Published(g, rb, b.Bundle, b.Content, resB, beforeB);

        var meshA = a.Bundle.Assets.Single(x => x.AssetType == BundleTypes.AssetMesh).Id;
        var meshB = b.Bundle.Assets.Single(x => x.AssetType == BundleTypes.AssetMesh).Id;
        Assert.Equal(meshA, meshB);                       // content-derived: one mesh asset
        Assert.Contains(meshA, resA.AssetsStored);
        Assert.Contains(meshB, resB.AssetsAlreadyPresent); // the second resident stored nothing for it
        Assert.DoesNotContain(meshB, astB.Stored);

        // everything that names an owner is separate: wearables and the object differ, and so do all ids
        foreach (var role in new[] { "shape", "skin", "eyes", "attachment-1" })
            Assert.NotEqual(a.Bundle.Items.Single(i => i.Role == role).AssetId, b.Bundle.Items.Single(i => i.Role == role).AssetId);
        Assert.Empty(a.Bundle.Items.Select(i => i.Id).Intersect(b.Bundle.Items.Select(i => i.Id)));
        Assert.NotEqual(resA.OutfitFolderId, resB.OutfitFolderId);
        Assert.NotEqual(resA.MyOutfitsFolderId, resB.MyOutfitsFolderId);
        Assert.NotEqual(resA.PublishId, resB.PublishId);

        // neither inventory holds anything of the other's
        Assert.DoesNotContain(g.Snapshot(ra.Id).Keys, k => b.Bundle.Items.Any(i => k == "I:" + i.Id) || k == "F:" + b.Bundle.OutfitFolder.Id);
        Assert.DoesNotContain(g.Snapshot(rb.Id).Keys, k => a.Bundle.Items.Any(i => k == "I:" + i.Id) || k == "F:" + a.Bundle.OutfitFolder.Id);
    }

    // ---- who may be written to -------------------------------------------------------------------------------------

    [Fact]
    public void ABundleBuiltForAnotherResidentIsRefusedAndWritesNothing()
    {
        var owner = g.NewResident(); var other = g.NewResident();
        var a = BundleFixtures.Assemble(owner.Id);
        var before = g.Snapshot(other.Id); var beforeOwner = g.Snapshot(owner.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, other.Id);

        Assert.Equal("wrong-owner", Assert.Single(res.Refusals).Code);
        Verify.NothingWritten(g, other, before, inv, ast);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(beforeOwner, g.Snapshot(owner.Id)));
    }

    [Fact]
    public void ACreatorThatIsNotTheConfiguredAccountIsRefused()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id, new PublishOptions { RequiredCreator = UUID.Random() });
        Assert.Equal("wrong-creator", Assert.Single(res.Refusals).Code);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(r.Id)));
        Assert.Null(g.Plain.GetFolder(r.Id, a.Bundle.OutfitFolder.Id));
    }

    [Fact]
    public void AResidentWithNoInventoryIsRefused()
    {
        var nobody = UUID.Random();
        var a = BundleFixtures.Assemble(nobody);
        var ast = g.NewAssets();
        var res = Service(g.NewInventory(), ast).Publish(a.Bundle, a.Content, nobody);
        Assert.Equal("no-inventory", Assert.Single(res.Refusals).Code);
        Assert.Empty(ast.Stored);
    }

    [Fact]
    public void ABundleThatFailsValidationWritesNothing()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();
        var tampered = new Dictionary<UUID, byte[]>(a.Content);
        var wearable = a.Bundle.Items.Single(i => i.Role == "skin").AssetId;
        tampered[wearable] = tampered[wearable].Concat(new byte[] { 1 }).ToArray(); // the id is a hash of the bytes

        var res = Service(inv, ast).Publish(a.Bundle, tampered, r.Id);

        var refusal = Assert.Single(res.Refusals);
        Assert.Equal("invalid-bundle", refusal.Code);
        Assert.Contains("failed its checks", refusal.Message);
        Verify.NothingWritten(g, r, before, inv, ast);
    }

    [Fact]
    public void AFileInTheBundleFolderThatTheManifestDoesNotListIsRefused()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(new BundleFiles.Loaded(a.Bundle, a.Content, new[] { "stray.script" }), r.Id);

        Assert.Equal("invalid-bundle", Assert.Single(res.Refusals).Code);
        Assert.Contains("stray.script", res.Refusals[0].Message);
        Verify.NothingWritten(g, r, before, inv, ast);
    }

    [Fact]
    public void AnAssetIdAlreadyHoldingOtherBytesIsRefusedAndNothingIsWritten()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id, name: "Parked");
        var id = a.Bundle.Items.Single(i => i.Role == "shape").AssetId;
        var parked = new AssetBase(id, "parked", (sbyte)BundleTypes.AssetBodypart, UUID.Random().ToString()) { Data = new byte[] { 9, 9, 9 } };
        Assert.NotEqual(UUID.Zero.ToString(), g.PlainAssets.Store(parked));
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id);

        Assert.Equal("asset-conflict", Assert.Single(res.Refusals).Code);
        Verify.NothingWritten(g, r, before, inv, ast);
        Assert.Equal(new byte[] { 9, 9, 9 }, g.PlainAssets.Get(id.ToString())!.Data); // the parked asset was not touched
    }

    [Fact]
    public void AnItemIdThatExistsAndDiffersIsNeverOverwritten_AndAnotherResidentsItemIsSafe()
    {
        var victim = g.NewResident(); var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        // the victim somehow holds an item with the id one of this outfit's items will have (the data layer replaces on a repeated id)
        var clash = a.Bundle.Items.Single(i => i.Role == "notecard").Id;
        var theirs = ScratchGrid.Item(victim.Id, g.Plain.GetFolderForType(victim.Id, FolderType.Notecard).ID, "Theirs", AssetType.Notecard, InventoryType.Notecard, 0);
        theirs.ID = clash;
        Assert.True(g.Plain.AddItem(theirs));
        var beforeVictim = g.Snapshot(victim.Id); var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id);

        Assert.Equal("item-conflict", Assert.Single(res.Refusals).Code);
        Verify.NothingWritten(g, r, before, inv, ast);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(beforeVictim, g.Snapshot(victim.Id)));
        Assert.Equal("Theirs", g.Plain.GetItem(victim.Id, clash)!.Name);
    }

    [Fact]
    public void AFolderIdHeldByAnotherResidentIsRefused()
    {
        var victim = g.NewResident(); var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var f = new InventoryFolderBase(a.Bundle.OutfitFolder.Id, "Theirs", victim.Id, victim.Root) { Type = (short)FolderType.None, Version = 1 };
        Assert.True(g.Plain.AddFolder(f));
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = Service(inv, ast).Publish(a.Bundle, a.Content, r.Id);

        Assert.Equal("folder-conflict", Assert.Single(res.Refusals).Code);
        Verify.NothingWritten(g, r, before, inv, ast);
        Assert.Equal("Theirs", g.Plain.GetFolder(victim.Id, f.ID)!.Name);
    }

    // ---- My Outfits ------------------------------------------------------------------------------------------------

    [Fact]
    public void AnExistingMyOutfitsIsReusedAndNotCreatedAgain()
    {
        var r = g.NewResident();
        var mo = g.AddFolder(r.Id, r.Root, "My Outfits", (FolderType)BundleTypes.FolderMyOutfits);
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);

        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Verify.Published(g, r, a.Bundle, a.Content, res, before);
        Assert.Equal(mo.ID, res.MyOutfitsFolderId);
        Assert.False(res.MyOutfitsCreated);
    }

    [Fact]
    public void TwoMyOutfitsFoldersAreAWarningAndTheRetryUsesTheSameOne()
    {
        var r = g.NewResident();
        var one = g.AddFolder(r.Id, r.Root, "My Outfits", (FolderType)BundleTypes.FolderMyOutfits);
        var two = new InventoryFolderBase(UUID.Random(), "My Outfits", r.Id, r.Root) { Type = BundleTypes.FolderMyOutfits, Version = 1 };
        Assert.False(g.Plain.AddFolder(two)); // the service itself refuses a second system folder under the root...
        g.ForceFolder(two);                   // ...so the data fault is made below it, as an older bug did
        var expectedId = new[] { one.ID, two.ID }.OrderBy(i => i.ToString(), StringComparer.Ordinal).First();
        var a = BundleFixtures.Assemble(r.Id);

        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(res.Complete, res.Summary);
        Assert.Equal(expectedId, res.MyOutfitsFolderId);
        Assert.Contains(res.Warnings, w => w.Contains("2 My Outfits folders"));
        Assert.Equal(expectedId, g.Plain.GetFolder(r.Id, a.Bundle.OutfitFolder.Id)!.ParentID);

        // a later publish for the same resident, and a retry of this one, find the outfit where it is
        var again = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);
        Assert.True(again.AlreadyPublished);
        Assert.Equal(expectedId, again.MyOutfitsFolderId);
    }

    // ---- the previous look -----------------------------------------------------------------------------------------

    [Fact]
    public void BrokenAndChainedLinksInTheCurrentOutfitAreSkippedOrFollowedAndReported()
    {
        var r = g.NewResident();
        var clothing = g.Plain.GetFolderForType(r.Id, FolderType.Clothing);
        // a fifth worn item reached through a link to a link: the previous look must link the item, not the link
        var shirt = ScratchGrid.Item(r.Id, clothing.ID, "Shirt", AssetType.Clothing, InventoryType.Wearable, 4);
        Assert.True(g.Plain.AddItem(shirt));
        var inner = ScratchGrid.Link(r.Id, clothing.ID, shirt, "@400");
        Assert.True(g.Plain.AddItem(inner));
        var outer = ScratchGrid.Link(r.Id, r.Cof, inner, "@400"); outer.Name = "Shirt via link";
        Assert.True(g.Plain.AddItem(outer));
        // a link whose item is gone
        var ghost = ScratchGrid.Item(r.Id, clothing.ID, "Deleted pants", AssetType.Clothing, InventoryType.Wearable, 5);
        var dead = ScratchGrid.Link(r.Id, r.Cof, ghost, "@500");
        Assert.True(g.Plain.AddItem(dead));
        // a link to someone else's item
        var stranger = g.NewResident(wornLook: false);
        var theirs = ScratchGrid.Item(stranger.Id, g.Plain.GetFolderForType(stranger.Id, FolderType.Clothing).ID, "Not mine", AssetType.Clothing, InventoryType.Wearable, 6);
        Assert.True(g.Plain.AddItem(theirs));
        var foreign = ScratchGrid.Link(r.Id, r.Cof, theirs, "@600");
        Assert.True(g.Plain.AddItem(foreign));
        // the link to the base outfit folder the viewer keeps in the Current Outfit: not an item, skipped without a complaint
        var baseOutfit = ScratchGrid.Link(r.Id, r.Cof, shirt, ""); baseOutfit.AssetType = (int)AssetType.LinkFolder; baseOutfit.AssetID = clothing.ID; baseOutfit.Name = "Base outfit";
        Assert.True(g.Plain.AddItem(baseOutfit));
        // the same item linked twice: one link
        Assert.True(g.Plain.AddItem(ScratchGrid.Link(r.Id, r.Cof, r.Targets[0], "@0")));

        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);

        Assert.True(res.Complete, res.Summary);
        var links = g.Plain.GetFolderContent(r.Id, a.Bundle.CurrentLook!.FolderId).Items;
        Assert.Equal(r.Targets.Select(t => t.ID).Append(shirt.ID).Order(), links.Select(l => l.AssetID).Order());
        Assert.All(links, l => Assert.Equal(BundleTypes.AssetLink, l.AssetType));
        Assert.DoesNotContain(links, l => l.AssetID == inner.ID || l.AssetID == outer.ID); // never a link to a link
        Assert.Equal(2, res.BrokenLinks.Count);
        Assert.Contains(res.BrokenLinks, b => b.CurrentOutfitItemId == dead.ID && b.Reason.Contains("gone"));
        Assert.Contains(res.BrokenLinks, b => b.CurrentOutfitItemId == foreign.ID && b.Reason.Contains("someone else"));
        Assert.Contains(res.Warnings, w => w.Contains("Deleted pants"));
        // the Current Outfit itself still holds every one of its entries, broken ones included, and nothing older changed
        ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(r.Id), r.Root, g.MyOutfitsOf(r.Id)!.ID);
        Assert.Equal(r.Links.Count + 5, g.Plain.GetFolderContent(r.Id, r.Cof).Items.Count);
    }

    [Fact]
    public void AnEmptyCurrentOutfitMakesNoPreviousLookFolder()
    {
        var r = g.NewResident(wornLook: false);
        var a = BundleFixtures.Assemble(r.Id);
        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id);
        Assert.True(res.Complete, res.Summary);
        Assert.Contains(res.Warnings, w => w.Contains("Current Outfit holds nothing"));
        Assert.Null(g.Plain.GetFolder(r.Id, a.Bundle.CurrentLook!.FolderId));
    }

    [Fact]
    public void TurningThePreviousLookOffWritesNoPreviousLookFolder()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var before = g.Snapshot(r.Id);
        var res = Service(g.NewInventory(), g.NewAssets()).Publish(a.Bundle, a.Content, r.Id, new PublishOptions { SaveCurrentLook = false });
        Verify.Published(g, r, a.Bundle, a.Content, res, before, previousLook: false);
        Assert.Null(g.Plain.GetFolder(r.Id, a.Bundle.CurrentLook!.FolderId));

        var noPlan = BundleFixtures.Assemble(r.Id, 2, saveLook: false);
        var res2 = Service(g.NewInventory(), g.NewAssets()).Publish(noPlan.Bundle, noPlan.Content, r.Id);
        Assert.True(res2.Complete);
        Assert.Equal(Guid.Empty.ToString(), res2.PreviousLookFolderId.ToString());
    }

    // ---- what the service never does -------------------------------------------------------------------------------

    [Fact]
    public void ThePublishServiceNeverCallsAnUpdateMoveDeleteOrPurge()
    {
        var r = g.NewResident();
        var a = BundleFixtures.Assemble(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();
        var svc = Service(inv, ast);
        Assert.True(svc.Publish(a.Bundle, a.Content, r.Id).Complete);
        Assert.True(svc.Publish(a.Bundle, a.Content, r.Id).AlreadyPublished);
        inv.FailAddItem = (n, _) => true;
        var b = BundleFixtures.Assemble(r.Id, 2);
        Assert.True(svc.Publish(b.Bundle, b.Content, r.Id).Partial);
        Assert.Empty(inv.Forbidden);
        Assert.Empty(ast.Forbidden);
        Assert.DoesNotContain(inv.Calls, c => c.StartsWith("Delete") || c.StartsWith("Update") || c.StartsWith("Move") || c.StartsWith("Purge") || c.StartsWith("CreateUserInventory"));
        // and it added nothing to the Current Outfit folder
        Assert.DoesNotContain(inv.ItemsAdded, i => i.Folder == r.Cof);
        Assert.DoesNotContain(inv.FoldersAdded, f => f.Type == BundleTypes.FolderCurrentOutfit);
    }
}
