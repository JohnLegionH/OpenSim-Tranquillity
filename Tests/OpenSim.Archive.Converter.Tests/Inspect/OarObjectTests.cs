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

/// <summary>oar-inspect on an OAR: link order, shape type, and the masks of every part and every item in its contents.</summary>
public class OarObjectTests
{
    private static ObjectReport ReadOar(byte[] oar, ReadOptions options = null)
    {
        using MemoryStream ms = new MemoryStream(oar);
        return ObjectReader.Read(ms, options);
    }

    private static KPart Part(string name, int link, float x = 0, float y = 0, float z = 0) => new KPart { Name = name, Link = link, X = x, Y = y, Z = z };

    // ------------------------------------------------------------------ the archive is recognised

    [Fact]
    public void AnOarIsDetectedFromTheArchiveItself()
    {
        ObjectReport r = ReadOar(Oar(new[] { Part("Chair", 1) }));
        Assert.Equal(ArchiveKind.Oar, r.Kind);
        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal("0.8", r.Version);
        Assert.Single(r.Objects);
        Assert.Equal("Chair", r.Objects[0].Name);
    }

    [Fact]
    public void ANonArchiveIsReportedAsOneAndNothingIsListed()
    {
        ObjectReport r = ReadOar(Utf8("this is not an archive at all, it is just text that is long enough to fill a header block or two of nothing"));
        Assert.Equal(InspectionStatus.NotAnArchive, r.Status);
        Assert.Empty(r.Objects);
        Assert.False(string.IsNullOrEmpty(r.StopReason));
    }

    // ------------------------------------------------------------------ link order

    [Fact]
    public void EveryPartIsListedInFileOrderWithItsStoredLinkNumberNameDescriptionPositionAndShapeType()
    {
        KPart root = Part("Table", 1);
        root.Desc = "the top";
        KPart leg = Part("Leg", 3, 1.5f, -2.25f, 0.5f);
        leg.Desc = "front leg";
        leg.Shape = 2;
        KPart ghost = Part("Ghost", 2, 0, 0, 4);
        ghost.Shape = 1;
        ghost.Phantom = true;

        ObjectEntry o = ReadOar(Oar(new[] { root, leg, ghost })).Objects.One();

        Assert.Equal(new[] { "Table", "Leg", "Ghost" }, o.Parts.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, o.Parts.Select(p => p.FileOrder).ToArray());
        Assert.Equal(new[] { 1, 3, 2 }, o.Parts.Select(p => p.LinkNum).ToArray());
        Assert.Equal(new[] { "the top", "front leg", "" }, o.Parts.Select(p => p.Description).ToArray());
        Assert.Equal(new Vector3(1.5f, -2.25f, 0.5f), o.Parts[1].Position);
        Assert.Equal(new[] { "Prim", "Convex", "None" }, o.Parts.Select(p => p.ShapeTypeName).ToArray());
        Assert.Equal(new[] { -1, 2, 1 }, o.Parts.Select(p => p.ShapeType).ToArray());
        Assert.False(o.Parts[0].Phantom);
        Assert.True(o.Parts[2].Phantom);
    }

    [Fact]
    public void LinkNumbersThatFollowTheFileOrderAreNotFlaggedAndOnesThatDoNotAre()
    {
        ObjectEntry tidy = ReadOar(Oar(new[] { Part("A", 1), Part("B", 2), Part("C", 3) })).Objects.One();
        Assert.True(tidy.LinkOrderOk, tidy.LinkOrderNote);

        ObjectEntry scrambled = ReadOar(Oar(new[] { Part("A", 1), Part("B", 3), Part("C", 2) })).Objects.One();
        Assert.False(scrambled.LinkOrderOk);
        Assert.Contains("file order", scrambled.LinkOrderNote);
    }

    [Fact]
    public void DuplicateAndMissingLinkNumbersAreNamed()
    {
        ObjectEntry dup = ReadOar(Oar(new[] { Part("A", 1), Part("B", 2), Part("C", 2) })).Objects.One();
        Assert.False(dup.LinkOrderOk);
        Assert.Contains("twice", dup.LinkOrderNote);

        ObjectEntry gap = ReadOar(Oar(new[] { Part("A", 1), Part("B", 2), Part("C", 5) })).Objects.One();
        Assert.False(gap.LinkOrderOk);
        Assert.Contains("gap", gap.LinkOrderNote);

        ObjectEntry zero = ReadOar(Oar(new[] { Part("A", 1), Part("B", 0), Part("C", 0) })).Objects.One();
        Assert.False(zero.LinkOrderOk);
        Assert.Contains("no link number", zero.LinkOrderNote);
    }

    [Fact]
    public void AloneAPartMayStoreLinkNumberZeroOrOne()
    {
        Assert.True(ReadOar(Oar(new[] { Part("Solo", 0) })).Objects.One().LinkOrderOk);
        Assert.True(ReadOar(Oar(new[] { Part("Solo", 1) })).Objects.One().LinkOrderOk);
    }

    [Fact]
    public void ShapeKeysDifferForPartsOfDifferentShape()
    {
        KPart box = Part("Box", 1);
        KPart cyl = Part("Cyl", 2);
        cyl.ProfileCurve = 0;
        cyl.PathCurve = 16;
        ObjectEntry o = ReadOar(Oar(new[] { box, cyl })).Objects.One();
        Assert.False(string.IsNullOrEmpty(o.Parts[0].ShapeKey));
        Assert.NotEqual(o.Parts[0].ShapeKey, o.Parts[1].ShapeKey);
    }

    // ------------------------------------------------------------------ mask letters

    [Theory]
    [InlineData(All, "MCT")]
    [InlineData(M | C | T, "MCT")]
    [InlineData(M | C, "MC-")]
    [InlineData(C | T, "-CT")]
    [InlineData(M | T, "M-T")]
    [InlineData(M, "M--")]
    [InlineData(C, "-C-")]
    [InlineData(T, "--T")]
    [InlineData(0u, "---")]
    [InlineData(0x80000u, "---")]
    public void MaskLettersShowModifyCopyAndTransfer(uint mask, string expected)
    {
        Assert.Equal(expected, MaskSet.Letters(mask));
    }

    [Fact]
    public void AnAbsentMaskIsShownAsQuestionMarks()
    {
        Assert.Equal("???", MaskSet.Letters(null));
    }

    [Fact]
    public void ANegativeStoredMaskIsReadAsTheUnsignedNumber()
    {
        KPart p = Part("Neg", 1);
        string xml = Xml2Group(p).Replace("<NextOwnerMask>" + All + "</NextOwnerMask>", "<NextOwnerMask>-1</NextOwnerMask>");
        ObjectEntry o = ReadOar(Oar(new[] { xml }, new[] { "Neg" })).Objects.One();
        Assert.Equal(0xFFFFFFFFu, o.Parts[0].Masks.NextOwner);
        Assert.Equal("MCT", MaskSet.Letters(o.Parts[0].Masks.NextOwner));
    }

    // ------------------------------------------------------------------ the masks of a part

    [Fact]
    public void AllFiveMasksOfAPartAreReportedAsStored()
    {
        KPart p = Part("Masks", 1);
        p.BaseMask = All;
        p.OwnerMask = M | C | T;
        p.GroupMask = C;
        p.EveryoneMask = M;
        p.NextMask = M | T;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();

        Assert.Equal(All, e.Masks.Base);
        Assert.Equal(M | C | T, e.Masks.Owner);
        Assert.Equal(C, e.Masks.Group);
        Assert.Equal(M, e.Masks.Everyone);
        Assert.Equal(M | T, e.Masks.NextOwner);
        Assert.Equal(M | C | T, e.Masks.Get("owner"));
        Assert.Equal(M | T, e.Masks.Get("next"));
    }

    [Fact]
    public void AMaskThatLacksModifyCopyOrTransferIsFlaggedAndNamed()
    {
        KPart p = Part("Flagged", 1);
        p.GroupMask = All;
        p.EveryoneMask = All;
        p.NextMask = M | C;
        p.OwnerMask = M | T;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();

        Assert.True(e.Flagged);
        Assert.Equal(new[] { "owner:C", "next:T" }, e.Lacks.ToArray());
    }

    [Fact]
    public void ZeroGroupAndEveryoneMasksLackAllThree()
    {
        KPart p = Part("Locked", 1);
        p.GroupMask = 0;
        p.EveryoneMask = 0;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();
        Assert.Equal(new[] { "group:MCT", "everyone:MCT" }, e.Lacks.ToArray());
    }

    [Fact]
    public void APartWithFullMasksIsNotFlagged()
    {
        PartEntry e = ReadOar(Oar(new[] { Part("Open", 1) })).Objects.One().Parts.One();
        Assert.False(e.Flagged);
        Assert.Empty(e.Lacks);
    }

    [Fact]
    public void AMaskTheFileDoesNotHoldIsFlaggedAsAbsent()
    {
        string xml = Xml2Group(Part("NoNext", 1)).Replace("<NextOwnerMask>" + All + "</NextOwnerMask>", "");
        PartEntry e = ReadOar(Oar(new[] { xml }, new[] { "NoNext" })).Objects.One().Parts.One();
        Assert.Null(e.Masks.NextOwner);
        Assert.Contains("next:absent", e.Lacks);
    }

    // ------------------------------------------------------------------ who

    [Fact]
    public void CreatorOwnerLastOwnerAndGroupOfEveryPartAreReported()
    {
        KPart p = Part("Who", 1);
        p.Creator = Alice;
        p.Owner = Bob;
        p.LastOwner = Carol;
        p.Group = Group1;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();
        Assert.Equal(Alice, e.CreatorId);
        Assert.Equal(Bob, e.OwnerId);
        Assert.Equal(Carol, e.LastOwnerId);
        Assert.Equal(Group1, e.GroupId);
    }

    [Fact]
    public void ACreatorNameComesFromCreatorDataAndAnOwnerWhoIsAlsoACreatorGetsThatName()
    {
        KPart p = Part("Named", 1);
        p.Creator = Alice;
        p.CreatorData = "http://grid.example.org:8002;Alice Builder";
        p.Owner = Alice;
        p.LastOwner = Bob;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();
        Assert.Equal("Alice Builder", e.CreatorName);
        Assert.Equal("Alice Builder", e.OwnerName);
        Assert.True(string.IsNullOrEmpty(e.LastOwnerName), "Bob is named nowhere in the archive");
    }

    [Fact]
    public void ANameFoundOnAnotherPartIsUsedForTheSameIdEverywhere()
    {
        KPart a = Part("A", 1);
        a.Creator = Alice;
        a.CreatorData = "http://grid.example.org:8002;Alice Builder";
        KPart b = Part("B", 2);
        b.Creator = Bob;
        b.Owner = Alice;
        ObjectEntry o = ReadOar(Oar(new[] { a, b })).Objects.One();
        Assert.Equal("Alice Builder", o.Parts[1].OwnerName);
        Assert.True(string.IsNullOrEmpty(o.Parts[1].CreatorName));
    }

    [Fact]
    public void EveryIdIsCountedByTheRolesItPlays()
    {
        KPart a = Part("A", 1);
        a.Creator = Alice; a.Owner = Bob; a.LastOwner = Bob; a.Group = Group1;
        a.Items.Add(new KItem { Name = "Script", Creator = Alice, Owner = Bob, LastOwner = Carol });
        KPart b = Part("B", 2);
        b.Creator = Alice; b.Owner = Bob; b.LastOwner = Carol;
        ObjectReport r = ReadOar(Oar(new[] { a, b }));

        PartyCount alice = Assert.Single(r.Parties, x => x.Id == Alice);
        Assert.Equal(3, alice.AsCreator);
        Assert.Equal(0, alice.AsOwner);
        PartyCount bob = Assert.Single(r.Parties, x => x.Id == Bob);
        Assert.Equal(3, bob.AsOwner);
        Assert.Equal(1, bob.AsLastOwner);
        PartyCount carol = Assert.Single(r.Parties, x => x.Id == Carol);
        Assert.Equal(2, carol.AsLastOwner);
        Assert.Equal(1, Assert.Single(r.Parties, x => x.Id == Group1).AsGroup);
    }

    // ------------------------------------------------------------------ the contents of a part

    [Fact]
    public void EveryItemInAPartIsListedWithItsMasksWhoAndFlags()
    {
        KPart p = Part("Holder", 1);
        p.Items.Add(new KItem { Name = "Open Script", AssetType = 10, OwnerMask = All, NextMask = All, GroupMask = All, EveryoneMask = All });
        p.Items.Add(new KItem
        {
            Name = "Locked Note", AssetType = 7, Creator = Bob, Owner = Carol, LastOwner = Alice, Group = Group1,
            BaseMask = M | C, OwnerMask = M | C, GroupMask = M, EveryoneMask = 0, NextMask = C | T,
            CreatorData = "http://grid.example.org:8002;Bob Maker"
        });
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();

        Assert.Equal(new[] { "Open Script", "Locked Note" }, e.Items.Select(i => i.Name).ToArray());
        ItemEntry open = e.Items[0], locked = e.Items[1];
        Assert.False(open.Flagged);
        Assert.Equal(10, open.AssetType);
        Assert.Equal("LSLText", open.AssetTypeName);

        Assert.True(locked.Flagged);
        Assert.Equal(new[] { "base:T", "owner:T", "group:CT", "everyone:MCT", "next:M" }, locked.Lacks.ToArray());
        Assert.Equal(M | C, locked.Masks.Base);
        Assert.Equal(C | T, locked.Masks.NextOwner);
        Assert.Equal(Bob, locked.CreatorId);
        Assert.Equal("Bob Maker", locked.CreatorName);
        Assert.Equal(Carol, locked.OwnerId);
        Assert.Equal(Alice, locked.LastOwnerId);
        Assert.Equal(Group1, locked.GroupId);
    }

    [Fact]
    public void APartIsNotFlaggedBecauseOfItsItemsButTheObjectIs()
    {
        KPart p = Part("Holder", 1);
        p.Items.Add(new KItem { Name = "NoCopy", OwnerMask = M | T });
        ObjectEntry o = ReadOar(Oar(new[] { p })).Objects.One();
        Assert.False(o.Parts[0].Flagged);
        Assert.True(o.Parts[0].Items[0].Flagged);
        Assert.True(o.Flagged);
    }

    // ------------------------------------------------------------------ the summary

    [Fact]
    public void TheSummaryCountsObjectsPartsItemsAndWhatIsFlagged()
    {
        KPart a1 = Part("A1", 1);
        KPart a2 = Part("A2", 2);
        a2.NextMask = M | C;
        a2.Items.Add(new KItem { Name = "ok" });
        a2.Items.Add(new KItem { Name = "bad", OwnerMask = M });
        KPart b1 = Part("B1", 1);
        ObjectReport r = ReadOar(Oar(new[] { a1, a2 }, new[] { b1 }));

        ReportSummary s = r.Summary;
        Assert.Equal(2, s.Objects);
        Assert.Equal(3, s.Parts);
        Assert.Equal(2, s.PartItems);
        Assert.Equal(1, s.FlaggedObjects);
        Assert.Equal(1, s.FlaggedParts);
        Assert.Equal(1, s.FlaggedPartItems);
        Assert.Equal(1, s.LackingByMask.GetValueOrDefault("next"));
        Assert.Equal(1, s.LackingByMask.GetValueOrDefault("owner"));
        Assert.Equal(0, s.InventoryItems);
    }

    [Fact]
    public void TheSummaryCountsObjectsWhoseLinkOrderIsOff()
    {
        ObjectReport r = ReadOar(Oar(new[] { Part("A", 1), Part("B", 3), Part("C", 2) }, new[] { Part("D", 1), Part("E", 2) }));
        Assert.Equal(1, r.Summary.LinkOrderProblems);
    }

    // ------------------------------------------------------------------ the filter

    [Fact]
    public void TheNameFilterKeepsMatchingObjectsInAnyCaseAndTheSummaryFollows()
    {
        KPart chairRoot = Part("Red Chair", 1);
        chairRoot.NextMask = M;
        KPart table = Part("Table", 1);
        KPart chair2 = Part("blue CHAIR", 1);
        ObjectReport r = ReadOar(Oar(new[] { chairRoot }, new[] { table }, new[] { chair2 }), new ReadOptions { NameFilter = "chair" });

        Assert.Equal(new[] { "Red Chair", "blue CHAIR" }, r.Objects.Select(o => o.Name).OrderByDescending(n => n).ToArray());
        Assert.Equal(2, r.Summary.Objects);
        Assert.Equal(1, r.Summary.FlaggedObjects);
        Assert.Equal(3, r.ObjectsBeforeFilter);
        Assert.Equal("chair", r.Filter);
    }

    [Fact]
    public void AFilterThatMatchesNothingListsNothing()
    {
        ObjectReport r = ReadOar(Oar(new[] { Part("Table", 1) }), new ReadOptions { NameFilter = "zebra" });
        Assert.Empty(r.Objects);
        Assert.Equal(0, r.Summary.Objects);
        Assert.Equal(1, r.ObjectsBeforeFilter);
    }

    // ------------------------------------------------------------------ read only

    [Fact]
    public void ReadingAnArchiveFileLeavesItAndItsFolderAsTheyWere()
    {
        string folder = NewFolder();
        string path = WriteFile(folder, "a.oar", Oar(new[] { Part("Chair", 1) }));
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);

        ObjectReport r = ObjectReader.Read(path);

        Assert.Single(r.Objects);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void AMissingFileIsReportedAsNotAnArchive()
    {
        ObjectReport r = ObjectReader.Read(Path.Combine(NewFolder(), "nothing.oar"));
        Assert.Equal(InspectionStatus.NotAnArchive, r.Status);
    }
}
