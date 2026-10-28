/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Fix;
using OpenSim.Archive.ObjectInspect;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;
using static OpenSim.Archive.Converter.Tests.Fix.FixKit;

namespace OpenSim.Archive.Converter.Tests.Fix;

/// <summary>oar-convert --fix-perms: only the named creators' parts and items get modify, copy and transfer.</summary>
public class PermissionFixTests
{
    private const uint MCT = M | C | T;

    private static KPart Part(string name, int link) => new KPart { Name = name, Link = link };

    private static ObjectReport Read(string path) => ObjectReader.Read(path, new ReadOptions { Strict = true });

    private static (string source, string dest, string folder) Paths(byte[] archive, string name = "build.oar")
    {
        string folder = NewFolder();
        return (WriteInput(folder, name, archive), Path.Combine(folder, "fixed-" + name), folder);
    }

    private static PermissionFixResult Fix(string source, string dest, params UUID[] creators)
    {
        PermissionFixResult result = PermissionFix.Run(source, dest, creators);
        Assert.True(File.Exists(dest), "the fixed archive was not written");
        return result;
    }

    /// <summary>A table by Alice with a leg by Bob; both parts and their contents have masks that lack things.</summary>
    private static byte[] Table()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        table.Creator = Alice;
        table.BaseMask = M; table.OwnerMask = C; table.NextMask = 0; table.GroupMask = 0; table.EveryoneMask = 0;
        table.Items.Add(new KItem { Name = "Alice note", Creator = Alice, BaseMask = M | C, OwnerMask = M | C, NextMask = C });
        table.Items.Add(new KItem { Name = "Bob note", Creator = Bob, BaseMask = M | C, OwnerMask = M | C, NextMask = C });
        leg.Creator = Bob;
        leg.BaseMask = M; leg.OwnerMask = C; leg.NextMask = 0; leg.GroupMask = C; leg.EveryoneMask = M;
        leg.Items.Add(new KItem { Name = "Alice script", Creator = Alice, BaseMask = C, OwnerMask = C, NextMask = C, GroupMask = 0, EveryoneMask = 0 });
        return Oar(new[] { table, leg });
    }

    // ------------------------------------------------------------------ who changes

    [Fact]
    public void ThePartsAndItemsOfTheNamedCreatorGetModifyCopyAndTransferOnBaseOwnerAndNextOwner()
    {
        var (source, dest, _) = Paths(Table());
        Fix(source, dest, Alice);

        ObjectEntry o = Read(dest).Objects.One();
        PartEntry table = o.Parts[0];
        Assert.Equal(M | MCT, table.Masks.Base | M);
        Assert.Equal("MCT", MaskSet.Letters(table.Masks.Base));
        Assert.Equal("MCT", MaskSet.Letters(table.Masks.Owner));
        Assert.Equal("MCT", MaskSet.Letters(table.Masks.NextOwner));
        Assert.Equal("MCT", MaskSet.Letters(table.Items[0].Masks.Base));
        Assert.Equal("MCT", MaskSet.Letters(table.Items[0].Masks.Owner));
        Assert.Equal("MCT", MaskSet.Letters(table.Items[0].Masks.NextOwner));
        // Alice's script inside Bob's leg
        Assert.Equal("MCT", MaskSet.Letters(o.Parts[1].Items[0].Masks.Base));
        Assert.Equal("MCT", MaskSet.Letters(o.Parts[1].Items[0].Masks.Owner));
        Assert.Equal("MCT", MaskSet.Letters(o.Parts[1].Items[0].Masks.NextOwner));
    }

    [Fact]
    public void ThePartsAndItemsOfAnyoneElseAreLeftExactlyAsTheyWere()
    {
        var (source, dest, _) = Paths(Table());
        PermissionFixResult result = Fix(source, dest, Alice);

        ObjectReport before = Read(source), after = Read(dest);
        PartEntry legBefore = before.Objects[0].Parts[1], legAfter = after.Objects[0].Parts[1];
        Assert.Equal(legBefore.Masks.Base, legAfter.Masks.Base);
        Assert.Equal(legBefore.Masks.Owner, legAfter.Masks.Owner);
        Assert.Equal(legBefore.Masks.NextOwner, legAfter.Masks.NextOwner);
        ItemEntry bobBefore = before.Objects[0].Parts[0].Items[1], bobAfter = after.Objects[0].Parts[0].Items[1];
        Assert.Equal("Bob note", bobAfter.Name);
        Assert.Equal(bobBefore.Masks.Base, bobAfter.Masks.Base);
        Assert.Equal(bobBefore.Masks.Owner, bobAfter.Masks.Owner);
        Assert.Equal(bobBefore.Masks.NextOwner, bobAfter.Masks.NextOwner);

        Assert.Contains(result.Left, l => l.Creator == Bob && l.Where.Contains("Leg"));
        Assert.Contains(result.Left, l => l.Creator == Bob && l.Where.Contains("Bob note"));
        Assert.DoesNotContain(result.Left, l => l.Creator == Alice);
    }

    [Fact]
    public void SeveralCreatorsCanBeNamedAndEachOneIsFixed()
    {
        var (source, dest, _) = Paths(Table());
        Fix(source, dest, Alice, Bob);

        ObjectReport after = Read(dest);
        Assert.All(after.Objects[0].Parts, p => Assert.Equal("MCT", MaskSet.Letters(p.Masks.Base)));
        Assert.Equal("MCT", MaskSet.Letters(after.Objects[0].Parts[0].Items[1].Masks.NextOwner));
    }

    [Fact]
    public void GroupAndEveryoneMasksAreNeverChanged()
    {
        var (source, dest, _) = Paths(Table());
        Fix(source, dest, Alice, Bob);

        ObjectReport before = Read(source), after = Read(dest);
        foreach (var (b, a) in before.Objects[0].Parts.Zip(after.Objects[0].Parts))
        {
            Assert.Equal(b.Masks.Group, a.Masks.Group);
            Assert.Equal(b.Masks.Everyone, a.Masks.Everyone);
            foreach (var (bi, ai) in b.Items.Zip(a.Items))
            {
                Assert.Equal(bi.Masks.Group, ai.Masks.Group);
                Assert.Equal(bi.Masks.Everyone, ai.Masks.Everyone);
            }
        }
    }

    [Fact]
    public void CreatorOwnerLastOwnerAndGroupStayAsTheyWere()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice; p.Owner = Bob; p.LastOwner = Carol; p.Group = Group1; p.BaseMask = M;
        p.Items.Add(new KItem { Name = "Note", Creator = Alice, Owner = Carol, LastOwner = Bob, Group = Group1, OwnerMask = C });
        var (source, dest, _) = Paths(Oar(new[] { p }));
        Fix(source, dest, Alice);

        PartEntry a = Read(dest).Objects.One().Parts.One();
        Assert.Equal("MCT", MaskSet.Letters(a.Masks.Base));
        Assert.Equal(Alice, a.CreatorId);
        Assert.Equal(Bob, a.OwnerId);
        Assert.Equal(Carol, a.LastOwnerId);
        Assert.Equal(Group1, a.GroupId);
        ItemEntry i = a.Items.One();
        Assert.Equal("MCT", MaskSet.Letters(i.Masks.Owner));
        Assert.Equal(Alice, i.CreatorId);
        Assert.Equal(Carol, i.OwnerId);
        Assert.Equal(Bob, i.LastOwnerId);
        Assert.Equal(Group1, i.GroupId);
    }

    [Fact]
    public void BitsOtherThanModifyCopyAndTransferAreKept()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice;
        p.BaseMask = 0x80000;   // move only
        p.OwnerMask = 0x80000 | C;
        var (source, dest, _) = Paths(Oar(new[] { p }));
        Fix(source, dest, Alice);

        PartEntry a = Read(dest).Objects.One().Parts.One();
        Assert.Equal(0x80000u | MCT, a.Masks.Base);
        Assert.Equal(0x80000u | MCT, a.Masks.Owner);
    }

    [Fact]
    public void AMaskStoredAsASignedNumberIsWrittenBackAsASignedNumber()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice;
        string xml = Xml2Group(p).Replace("<BaseMask>" + All + "</BaseMask>", "<BaseMask>" + int.MinValue + "</BaseMask>");
        var (source, dest, _) = Paths(Oar(new[] { xml }, new[] { "Chair" }));
        Fix(source, dest, Alice);

        string text = Text(Entries(dest), "objects/");
        Assert.Contains("<BaseMask>" + unchecked((int)(0x80000000u | MCT)) + "</BaseMask>", text);
    }

    // ------------------------------------------------------------------ the rest of the archive

    [Fact]
    public void EveryEntryThatNeedsNoChangeIsCopiedByteForByte()
    {
        KPart bobs = Part("Bobs shed", 1);
        bobs.Creator = Bob;
        KPart alices = Part("Alices hut", 1);
        alices.Creator = Alice;
        alices.BaseMask = M;
        var (source, dest, _) = Paths(Oar(new[] { bobs }, new[] { alices }));
        Fix(source, dest, Alice);

        var before = Entries(source);
        var after = Entries(dest);
        Assert.Equal(before.Select(e => e.name), after.Select(e => e.name));
        Assert.Equal(before[0].data, after[0].data);                       // archive.xml
        Assert.Equal(before[1].data, after[1].data);                       // Bob's object file
        Assert.NotEqual(before[2].data, after[2].data);                    // Alice's
    }

    [Fact]
    public void OnlyTheDigitsOfTheChangedMasksDifferInAnObjectFile()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice;
        p.BaseMask = M; p.OwnerMask = C; p.NextMask = T;
        var (source, dest, _) = Paths(Oar(new[] { p }));
        Fix(source, dest, Alice);

        string before = Text(Entries(source), "objects/"), after = Text(Entries(dest), "objects/");
        string expected = before
            .Replace("<BaseMask>" + M + "</BaseMask>", "<BaseMask>" + MCT + "</BaseMask>")
            .Replace("<OwnerMask>" + C + "</OwnerMask>", "<OwnerMask>" + MCT + "</OwnerMask>")
            .Replace("<NextOwnerMask>" + T + "</NextOwnerMask>", "<NextOwnerMask>" + MCT + "</NextOwnerMask>");
        Assert.NotEqual(before, after);
        Assert.Equal(expected, after);
    }

    [Fact]
    public void TheInputIsNeverChangedAndNeverOverwritten()
    {
        var (source, dest, _) = Paths(Table());
        byte[] original = File.ReadAllBytes(source);

        Assert.ThrowsAny<Exception>(() => PermissionFix.Run(source, source, new[] { Alice }));
        Assert.Equal(original, File.ReadAllBytes(source));

        Fix(source, dest, Alice);
        Assert.Equal(original, File.ReadAllBytes(source));

        byte[] first = File.ReadAllBytes(dest);
        Assert.Throws<IOException>(() => PermissionFix.Run(source, dest, new[] { Alice }));
        Assert.Equal(first, File.ReadAllBytes(dest));
    }

    [Fact]
    public void NoCreatorMeansNothingIsWrittenAndTheProgramRefuses()
    {
        var (source, dest, _) = Paths(Table());

        Assert.Throws<ArgumentException>(() => PermissionFix.Run(source, dest, Array.Empty<UUID>()));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void TheSameRunTwiceGivesTheSameBytes()
    {
        var (source, dest, folder) = Paths(Table());
        string second = Path.Combine(folder, "second.oar");
        Fix(source, dest, Alice);
        Fix(source, second, Alice);

        Assert.Equal(File.ReadAllBytes(dest), File.ReadAllBytes(second));
    }

    [Fact]
    public void RunningItOnItsOwnOutputChangesNothingAndReportsAlreadyComplete()
    {
        var (source, dest, folder) = Paths(Table());
        Fix(source, dest, Alice);
        string again = Path.Combine(folder, "again.oar");
        PermissionFixResult result = Fix(dest, again, Alice);

        Assert.Empty(result.Changes);
        Assert.True(result.AlreadyComplete >= 3);
        Assert.Equal(File.ReadAllBytes(dest), File.ReadAllBytes(again));
    }

    // ------------------------------------------------------------------ the report

    [Fact]
    public void TheReportListsEveryChangedMaskWithItsValueBeforeAndAfter()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice;
        p.BaseMask = M; p.OwnerMask = MCT; p.NextMask = C;
        var (source, dest, _) = Paths(Oar(new[] { p }));
        PermissionFixResult result = Fix(source, dest, Alice);

        Assert.Equal(2, result.Changes.Count);
        MaskChange b = Assert.Single(result.Changes, c => c.Mask == "base");
        Assert.Equal(M, b.Before);
        Assert.Equal(MCT, b.After);
        MaskChange n = Assert.Single(result.Changes, c => c.Mask == "next");
        Assert.Equal(C, n.Before);
        Assert.Equal(MCT, n.After);
        Assert.Contains("Chair", b.Where);
        Assert.StartsWith("objects/", b.Entry);
        Assert.Equal(1, result.EntriesChanged);

        string text = result.ToText();
        Assert.Contains("base", text);
        Assert.Contains("M-- -> MCT", text);
        Assert.Contains("-C- -> MCT", text);
        Assert.Contains("Chair", text);
        using System.Text.Json.JsonDocument json = Json(result.ToJson());
        Assert.Equal(2, json.RootElement.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void TheReportListsWhatWasLeftAloneAndWhoCreatedIt()
    {
        var (source, dest, _) = Paths(Table());
        PermissionFixResult result = Fix(source, dest, Alice);

        string text = result.ToText();
        Assert.Contains("left as it was", text);
        Assert.Contains(Bob.ToString(), text);
        Assert.Contains("Leg", text);
    }

    [Fact]
    public void AMaskTheFileDoesNotHoldIsReportedAndNotInvented()
    {
        KPart p = Part("Chair", 1);
        p.Creator = Alice;
        p.BaseMask = M;
        string xml = Xml2Group(p).Replace("<NextOwnerMask>" + All + "</NextOwnerMask>", "");
        var (source, dest, _) = Paths(Oar(new[] { xml }, new[] { "Chair" }));
        PermissionFixResult result = Fix(source, dest, Alice);

        Assert.Equal("MCT", MaskSet.Letters(Read(dest).Objects.One().Parts.One().Masks.Base));
        Assert.Null(Read(dest).Objects.One().Parts.One().Masks.NextOwner);
        string why = Assert.Single(result.NotChanged);
        Assert.Contains("next", why);
        Assert.Contains("Chair", why);
    }

    // ------------------------------------------------------------------ the difference from the input

    [Fact]
    public void ComparingTheInputWithTheOutputShowsOnlyThePermissionChangesOfTheNamedCreator()
    {
        var (source, dest, _) = Paths(Table());
        Fix(source, dest, Alice);

        CompareResult result = ReportCompare.Compare(Read(source), Read(dest));

        Assert.False(result.Identical);
        Assert.Empty(result.OnlyInReference);
        Assert.Empty(result.OnlyInArchive);
        Assert.All(result.Objects.SelectMany(o => o.Differences), d => Assert.Equal("permissions", d.Area));
        string[] changed = result.Objects.SelectMany(o => o.Differences).Select(d => d.Part + "|" + d.Field).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] expected =
        {
            "Leg|item \"Alice script\" base mask", "Leg|item \"Alice script\" next owner mask", "Leg|item \"Alice script\" owner mask",
            "Table|base mask", "Table|item \"Alice note\" base mask", "Table|item \"Alice note\" next owner mask",
            "Table|item \"Alice note\" owner mask", "Table|next owner mask", "Table|owner mask"
        };
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(), changed);
    }

    // ------------------------------------------------------------------ IARs

    private static IarItem ObjectItem(string name, UUID creator, string objectXml, uint baseMask = M, uint current = C, uint next = 0) =>
        new IarItem { Name = name, AssetType = 6, Creator = creator, Owner = Alice, Base = baseMask, Current = current, Next = next, Group = 0, Everyone = 0, ObjectXml = objectXml };

    [Fact]
    public void InAnIarTheInventoryItemsOfTheCreatorAndTheirObjectsPartsAndContentsAreFixed()
    {
        KPart box = Part("Box", 1);
        box.Creator = Alice; box.BaseMask = M; box.OwnerMask = C; box.NextMask = 0;
        box.Items.Add(new KItem { Name = "Inside", Creator = Alice, OwnerMask = C });
        KPart bobsBox = Part("Bobs box", 1);
        bobsBox.Creator = Bob; bobsBox.BaseMask = M;
        byte[] iar = Iar(
            ObjectItem("Alice object", Alice, OriginalGroup(box)),
            ObjectItem("Bob object", Bob, OriginalGroup(bobsBox)),
            new IarItem { Name = "Alice note", AssetType = 7, Creator = Alice, Base = M, Current = C, Next = 0, Group = 0, Everyone = 0 });
        var (source, dest, _) = Paths(iar, "gift.iar");
        PermissionFixResult result = Fix(source, dest, Alice);

        ObjectReport after = Read(dest);
        InventoryItemEntry aliceObject = after.InventoryItems.Single(i => i.Name == "Alice object");
        Assert.Equal("MCT", MaskSet.Letters(aliceObject.Masks.Base));
        Assert.Equal("MCT", MaskSet.Letters(aliceObject.Masks.Owner));
        Assert.Equal("MCT", MaskSet.Letters(aliceObject.Masks.NextOwner));
        Assert.Equal("MCT", MaskSet.Letters(after.InventoryItems.Single(i => i.Name == "Alice note").Masks.NextOwner));
        InventoryItemEntry bobObject = after.InventoryItems.Single(i => i.Name == "Bob object");
        Assert.Equal("M--", MaskSet.Letters(bobObject.Masks.Base));
        Assert.Equal("-C-", MaskSet.Letters(bobObject.Masks.Owner));

        ObjectEntry aliceParts = after.Objects.Single(o => o.Name == "Box");
        Assert.Equal("MCT", MaskSet.Letters(aliceParts.Parts[0].Masks.Base));
        Assert.Equal("MCT", MaskSet.Letters(aliceParts.Parts[0].Items[0].Masks.Owner));
        ObjectEntry bobParts = after.Objects.Single(o => o.Name == "Bobs box");
        Assert.Equal("M--", MaskSet.Letters(bobParts.Parts[0].Masks.Base));

        // the inventory item's group mask does not exist in the format and nothing is invented for it
        Assert.Null(aliceObject.Masks.Group);
        Assert.Contains(result.Left, l => l.Creator == Bob && l.Where.Contains("Bob object"));
    }

    [Fact]
    public void AnIarItemKeepsItsOwnerCreatorAndEveryoneMask()
    {
        byte[] iar = Iar(new IarItem { Name = "Alice note", AssetType = 7, Creator = Alice, Owner = Carol, Base = M, Current = C, Next = 0, Everyone = M });
        var (source, dest, _) = Paths(iar, "gift.iar");
        Fix(source, dest, Alice);

        InventoryItemEntry i = Read(dest).InventoryItems.One();
        Assert.Equal(Alice, i.CreatorId);
        Assert.Equal(Carol, i.OwnerId);
        Assert.Equal(M, i.Masks.Everyone);
    }

    [Fact]
    public void ACoalescedObjectInAnIarHasEveryLinksetFixed()
    {
        KPart a = Part("First", 1), b = Part("Second", 1);
        a.Creator = Alice; a.BaseMask = M;
        b.Creator = Alice; b.BaseMask = C;
        string coalesced = Coalesced(0, 0, 0, (0f, 0f, 0f, new[] { a }), (1f, 0f, 0f, new[] { b }));
        var (source, dest, _) = Paths(Iar(ObjectItem("Both", Alice, coalesced)), "gift.iar");
        Fix(source, dest, Alice);

        ObjectReport after = Read(dest);
        Assert.Equal(2, after.Objects.Count);
        Assert.All(after.Objects, o => Assert.Equal("MCT", MaskSet.Letters(o.Parts[0].Masks.Base)));
    }
}
