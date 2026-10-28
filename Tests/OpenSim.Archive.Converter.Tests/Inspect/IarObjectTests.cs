/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Inspection;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;

namespace OpenSim.Archive.ObjectInspect.Tests;

/// <summary>oar-inspect on an IAR: every inventory item with its own masks and folder, object items opened, coalesced items split.</summary>
public class IarObjectTests
{
    private static ObjectReport ReadIar(byte[] iar, ReadOptions options = null)
    {
        using MemoryStream ms = new MemoryStream(iar);
        return ObjectReader.Read(ms, options);
    }

    private static KPart Part(string name, int link, float x = 0, float y = 0, float z = 0) => new KPart { Name = name, Link = link, X = x, Y = y, Z = z };

    private static IarItem ObjectItem(string name, string xml, string folder = "Objects") => new IarItem { Name = name, Folder = folder, AssetType = 6, ObjectXml = xml };

    // ------------------------------------------------------------------ the archive and its items

    [Fact]
    public void AnIarIsDetectedFromTheArchiveItself()
    {
        ObjectReport r = ReadIar(Iar(new IarItem { Name = "Picture", AssetType = 0 }));
        Assert.Equal(ArchiveKind.Iar, r.Kind);
        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal("0.3", r.Version);
    }

    [Fact]
    public void EveryInventoryItemIsListedInArchiveOrderWithItsFolderAndItsOwnMasks()
    {
        IarItem texture = new IarItem
        {
            Name = "Wood", Folder = "Builds/Textures", AssetType = 0, Creator = Bob, Owner = Carol,
            Base = All, Current = M | C | T, Group = C, Everyone = 0, Next = M | T
        };
        IarItem note = new IarItem { Name = "Read me", Folder = "Notes", AssetType = 7 };
        ObjectReport r = ReadIar(Iar(texture, note));

        Assert.Equal(new[] { "Wood", "Read me" }, r.InventoryItems.Select(i => i.Name).ToArray());
        InventoryItemEntry wood = r.InventoryItems[0];
        Assert.Equal("Builds/Textures", wood.FolderPath);
        Assert.Equal(texture.Id, wood.ItemId);
        Assert.Equal(texture.Asset, wood.AssetId);
        Assert.Equal("Texture", wood.AssetTypeName);
        Assert.Equal(All, wood.Masks.Base);
        Assert.Equal(M | C | T, wood.Masks.Owner);
        Assert.Null(wood.Masks.Group);
        Assert.Equal(0u, wood.Masks.Everyone);
        Assert.Equal(M | T, wood.Masks.NextOwner);
        Assert.Equal(Bob, wood.CreatorId);
        Assert.Equal(Carol, wood.OwnerId);
        Assert.Equal(new[] { "everyone:MCT", "next:C" }, wood.Lacks.ToArray());
        Assert.True(wood.Flagged);
        Assert.Equal("Notes", r.InventoryItems[1].FolderPath);
        Assert.False(r.InventoryItems[1].Flagged);
    }

    [Fact]
    public void AnIarItemNeverHoldsAGroupMaskAndThatIsNotAFlag()
    {
        ObjectReport r = ReadIar(Iar(new IarItem { Name = "Note", AssetType = 7 }));
        InventoryItemEntry note = Assert.Single(r.InventoryItems);
        Assert.Null(note.Masks.Group);
        Assert.Empty(note.Lacks);
        Assert.False(note.Flagged);
        Assert.Equal(0, r.Summary.FlaggedInventoryItems);
        Assert.Equal(0, r.Summary.LackingByMask.GetValueOrDefault("group"));
        Assert.Contains("group n/a", ReportText.Build(r));
    }

    [Fact]
    public void ItemsThatAreNotObjectsListNoObjects()
    {
        ObjectReport r = ReadIar(Iar(new IarItem { Name = "Picture", AssetType = 0 }));
        Assert.Single(r.InventoryItems);
        Assert.Empty(r.Objects);
        Assert.Equal(0, r.InventoryItems[0].LinksetCount);
    }

    // ------------------------------------------------------------------ object items

    [Fact]
    public void AnObjectItemIsOpenedAndItsPartsAreListedInLinkOrderWithTheirOwnMasks()
    {
        KPart root = Part("Chair", 1);
        KPart leg = Part("Leg", 3, 1, 2, 3);
        leg.NextMask = M | C;
        KPart seat = Part("Seat", 2);
        seat.Shape = 1;
        IarItem item = ObjectItem("My Chair", OriginalGroup(root, leg, seat), "Builds");
        item.Current = M | C;
        item.Next = M | C | T;
        ObjectReport r = ReadIar(Iar(item));

        InventoryItemEntry inv = Assert.Single(r.InventoryItems);
        Assert.Equal(1, inv.LinksetCount);
        Assert.Equal(M | C, inv.Masks.Owner);

        ObjectEntry o = r.Objects.One();
        Assert.Equal("Chair", o.Name);
        Assert.Equal("My Chair", o.ItemName);
        Assert.Equal(item.Id, o.ItemId);
        Assert.Equal(item.Asset, o.AssetId);
        Assert.Equal("Builds", o.FolderPath);
        Assert.Equal(0, o.CoalescedIndex);
        Assert.False(o.HasOffset);
        Assert.Equal(new[] { "Chair", "Leg", "Seat" }, o.Parts.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { 1, 3, 2 }, o.Parts.Select(p => p.LinkNum).ToArray());
        Assert.False(o.LinkOrderOk);
        Assert.Equal(new[] { "Prim", "Prim", "None" }, o.Parts.Select(p => p.ShapeTypeName).ToArray());
        Assert.Equal(new[] { "next:T" }, o.Parts[1].Lacks.ToArray());
        Assert.Empty(o.Parts[0].Lacks);
    }

    [Fact]
    public void ThePrimInventoryOfAPartInsideAnObjectItemIsListedAndFlagged()
    {
        KPart root = Part("Box", 1);
        root.Items.Add(new KItem { Name = "Script", OwnerMask = M | T });
        ObjectReport r = ReadIar(Iar(ObjectItem("Box", OriginalGroup(root))));
        ItemEntry script = r.Objects.One().Parts.One().Items.One();
        Assert.Equal("Script", script.Name);
        Assert.Equal(new[] { "owner:C" }, script.Lacks.ToArray());
        Assert.Equal(1, r.Summary.PartItems);
        Assert.Equal(1, r.Summary.FlaggedPartItems);
    }

    [Fact]
    public void AnObjectItemWhoseAssetIsNotInTheArchiveIsListedWithANote()
    {
        IarItem item = new IarItem { Name = "Lost", AssetType = 6, ObjectXml = null };
        ObjectReport r = ReadIar(Iar(item));
        InventoryItemEntry inv = Assert.Single(r.InventoryItems);
        Assert.Equal(0, inv.LinksetCount);
        Assert.Contains("not in the archive", inv.Note);
        Assert.Empty(r.Objects);
    }

    [Fact]
    public void TwoItemsThatHoldTheSameAssetEachGetTheirOwnObjectEntry()
    {
        KPart root = Part("Lamp", 1);
        string xml = OriginalGroup(root);
        UUID asset = UUID.Random();
        IarItem a = ObjectItem("Lamp A", xml); a.Asset = asset;
        IarItem b = ObjectItem("Lamp B", null); b.Asset = asset;
        ObjectReport r = ReadIar(Iar(a, b));

        Assert.Equal(new[] { "Lamp A", "Lamp B" }, r.Objects.Select(o => o.ItemName).ToArray());
        Assert.Equal(new[] { a.Id, b.Id }, r.Objects.Select(o => o.ItemId).ToArray());
        Assert.Equal(new[] { 1, 1 }, r.InventoryItems.Select(i => i.LinksetCount).ToArray());
    }

    // ------------------------------------------------------------------ coalesced items

    [Fact]
    public void ACoalescedItemListsEachLinksetSeparatelyWithItsOwnLinkOrderAndOffset()
    {
        KPart a1 = Part("Wall", 1), a2 = Part("Pane", 3), a3 = Part("Frame", 2);
        KPart b1 = Part("Door", 1), b2 = Part("Knob", 2, 0.5f, 0, 1);
        string xml = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { a1, a2, a3 }), (4f, -2f, 1.5f, new[] { b1, b2 }));
        ObjectReport r = ReadIar(Iar(ObjectItem("House pieces", xml)));

        InventoryItemEntry inv = Assert.Single(r.InventoryItems);
        Assert.Equal(2, inv.LinksetCount);
        Assert.Equal(2, r.Objects.Count);

        ObjectEntry first = r.Objects[0], second = r.Objects[1];
        Assert.Equal("Wall", first.Name);
        Assert.Equal("Door", second.Name);
        Assert.Equal(new[] { 1, 2 }, new[] { first.CoalescedIndex, second.CoalescedIndex });
        Assert.Equal(new[] { 2, 2 }, new[] { first.CoalescedCount, second.CoalescedCount });
        Assert.True(first.HasOffset);
        Assert.Equal(new Vector3(0, 0, 0), first.Offset);
        Assert.Equal(new Vector3(4, -2, 1.5f), second.Offset);
        Assert.Equal(new[] { "Wall", "Pane", "Frame" }, first.Parts.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { 1, 3, 2 }, first.Parts.Select(p => p.LinkNum).ToArray());
        Assert.False(first.LinkOrderOk);
        Assert.Equal(new[] { 1, 2 }, second.Parts.Select(p => p.LinkNum).ToArray());
        Assert.True(second.LinkOrderOk);
        Assert.Equal(new Vector3(0.5f, 0, 1), second.Parts[1].Position);
        Assert.Equal("House pieces", second.ItemName);
        Assert.Equal(inv.ItemId, second.ItemId);
    }

    [Fact]
    public void TheMasksOfEachLinksetOfACoalescedItemAreReportedApart()
    {
        KPart a = Part("Open", 1);
        KPart b = Part("Closed", 1);
        b.NextMask = M;
        string xml = Coalesced(2, 2, 2, (0f, 0f, 0f, new[] { a }), (1f, 1f, 1f, new[] { b }));
        ObjectReport r = ReadIar(Iar(ObjectItem("Pair", xml)));
        Assert.Equal(2, r.Objects.Count);
        Assert.False(r.Objects[0].Flagged);
        Assert.True(r.Objects[1].Flagged);
        Assert.Equal(new[] { "next:CT" }, r.Objects[1].Parts[0].Lacks.ToArray());
        Assert.Equal(1, r.Summary.FlaggedObjects);
        Assert.Equal(2, r.Summary.Objects);
    }

    // ------------------------------------------------------------------ names, summary, filter

    [Fact]
    public void NamesComeFromTheCreatorDataOfTheIarsOwnRecords()
    {
        IarItem a = new IarItem { Name = "Made by Alice", AssetType = 0, Creator = Alice, Owner = Bob, CreatorData = "http://grid.example.org:8002/" + Alice + ";Alice Builder" };
        IarItem b = new IarItem { Name = "Made by Carol", AssetType = 0, Creator = Carol, Owner = Alice };
        ObjectReport r = ReadIar(Iar(a, b));

        Assert.Equal(2, r.InventoryItems.Count);
        Assert.Equal("Alice Builder", r.InventoryItems[0].CreatorName);
        Assert.Equal("Alice Builder", r.InventoryItems[1].OwnerName);
        Assert.True(string.IsNullOrEmpty(r.InventoryItems[1].CreatorName));
        Assert.True(string.IsNullOrEmpty(r.InventoryItems[0].OwnerName));
        PartyCount alice = Assert.Single(r.Parties, x => x.Id == Alice);
        Assert.Equal("Alice Builder", alice.Name);
        Assert.Equal(1, alice.AsCreator);
        Assert.Equal(1, alice.AsOwner);
    }

    [Fact]
    public void TheSummaryCountsInventoryItemsObjectsPartsAndWhatIsFlagged()
    {
        KPart p1 = Part("A", 1), p2 = Part("B", 2);
        p2.GroupMask = 0;
        IarItem obj = ObjectItem("Thing", OriginalGroup(p1, p2));
        IarItem tex = new IarItem { Name = "Tex", AssetType = 0, Next = M };
        IarItem ok = new IarItem { Name = "Fine", AssetType = 7 };
        ObjectReport r = ReadIar(Iar(obj, tex, ok));

        ReportSummary s = r.Summary;
        Assert.Equal(3, s.InventoryItems);
        Assert.Equal(1, s.FlaggedInventoryItems);
        Assert.Equal(1, s.Objects);
        Assert.Equal(2, s.Parts);
        Assert.Equal(1, s.FlaggedParts);
        Assert.Equal(1, s.FlaggedObjects);
        Assert.Equal(1, s.LackingByMask.GetValueOrDefault("next"));
        Assert.Equal(1, s.LackingByMask.GetValueOrDefault("group"));
    }

    [Fact]
    public void TheNameFilterKeepsItemsAndObjectsWhoseItemOrObjectNameMatches()
    {
        IarItem chairItem = ObjectItem("Chair box", OriginalGroup(Part("Seat", 1)));
        IarItem lampItem = ObjectItem("Lamp", OriginalGroup(Part("Reading Chair", 1)));
        IarItem plain = ObjectItem("Table", OriginalGroup(Part("Top", 1)));
        IarItem texture = new IarItem { Name = "chair texture", AssetType = 0 };
        ObjectReport r = ReadIar(Iar(chairItem, lampItem, plain, texture), new ReadOptions { NameFilter = "CHAIR" });

        Assert.Equal(new[] { "Chair box", "Lamp", "chair texture" }, r.InventoryItems.Select(i => i.Name).ToArray());
        Assert.Equal(new[] { "Seat", "Reading Chair" }, r.Objects.Select(o => o.Name).ToArray());
        Assert.Equal(3, r.Summary.InventoryItems);
        Assert.Equal(2, r.Summary.Objects);
    }

    [Fact]
    public void AnOarHasNoInventoryItems()
    {
        KPart chair = Part("Chair", 1);
        chair.Items.Add(new KItem { Name = "Script" });
        using MemoryStream ms = new MemoryStream(Oar(new[] { chair }));
        ObjectReport r = ObjectReader.Read(ms);
        Assert.Single(r.Objects.One().Parts.One().Items);
        Assert.Empty(r.InventoryItems);
        Assert.Equal(0, r.Summary.InventoryItems);
    }
}
