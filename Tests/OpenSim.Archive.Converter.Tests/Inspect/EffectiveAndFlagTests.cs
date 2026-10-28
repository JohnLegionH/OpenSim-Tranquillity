/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.ArchiveInspector;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;

namespace OpenSim.Archive.ObjectInspect.Tests;

/// <summary>oar-inspect: which masks are flagged by default, an object's effective permissions, and names from a list.</summary>
[Collection("Console output")]
public class EffectiveAndFlagTests
{
    private static ObjectReport ReadOar(byte[] oar, ReadOptions options = null)
    {
        using MemoryStream ms = new MemoryStream(oar);
        return ObjectReader.Read(ms, options);
    }

    private static KPart Part(string name, int link) => new KPart { Name = name, Link = link };

    private static ObjectEntry Table(KItem item, Action<KPart> root = null, Action<KPart> child = null)
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        if (item != null)
            leg.Items.Add(item);
        root?.Invoke(table);
        child?.Invoke(leg);
        return ReadOar(Oar(new[] { table, leg })).Objects.One();
    }

    private static string Letters(EffectivePerms e, Func<EffectivePerms, uint> pick)
    {
        Assert.NotNull(e);
        return MaskSet.Letters(pick(e));
    }

    // ------------------------------------------------------------------ what is flagged

    [Fact]
    public void ByDefaultAGroupOrEveryoneMaskThatLacksSomethingIsShownAndNotFlagged()
    {
        KPart p = Part("Locked", 1);
        p.GroupMask = 0;
        p.EveryoneMask = 0;
        ObjectReport r = ReadOar(Oar(new[] { p }));
        PartEntry e = r.Objects.One().Parts.One();

        Assert.Equal("---", MaskSet.Letters(e.Masks.Group));
        Assert.Equal("---", MaskSet.Letters(e.Masks.Everyone));
        Assert.Empty(e.Lacks);
        Assert.False(e.Flagged);
        Assert.False(r.Objects[0].Flagged);
        Assert.Equal(0, r.Summary.FlaggedParts);
    }

    [Fact]
    public void ByDefaultABaseOwnerOrNextOwnerMaskThatLacksSomethingIsFlagged()
    {
        KPart p = Part("Locked", 1);
        p.BaseMask = M | C;
        p.OwnerMask = M | T;
        p.NextMask = C | T;
        p.GroupMask = 0;
        p.EveryoneMask = 0;
        PartEntry e = ReadOar(Oar(new[] { p })).Objects.One().Parts.One();

        Assert.Equal(new[] { "base:T", "owner:C", "next:M" }, e.Lacks.ToArray());
        Assert.True(e.Flagged);
    }

    [Fact]
    public void StrictFlagsAllFiveMasks()
    {
        KPart p = Part("Locked", 1);
        p.GroupMask = 0;
        p.EveryoneMask = M;
        ObjectReport r = ReadOar(Oar(new[] { p }), new ReadOptions { Strict = true });
        PartEntry e = r.Objects.One().Parts.One();

        Assert.Equal(new[] { "group:MCT", "everyone:CT" }, e.Lacks.ToArray());
        Assert.True(r.Objects[0].Flagged);
        Assert.Equal(1, r.Summary.FlaggedParts);
    }

    [Fact]
    public void AnItemInAPartIsFlaggedBySameRule()
    {
        KItem item = new KItem { Name = "Notes", GroupMask = 0, EveryoneMask = 0, NextMask = M };
        ItemEntry e = Table(item).Parts[1].Items.One();

        Assert.Equal(new[] { "next:CT" }, e.Lacks.ToArray());
    }

    [Fact]
    public void TheSummaryStillCountsGroupAndEveryoneMasksThatLackSomethingAlthoughTheyAreNotFlagged()
    {
        KPart p = Part("Locked", 1);
        p.GroupMask = 0;
        p.EveryoneMask = 0;
        ReportSummary s = ReadOar(Oar(new[] { p })).Summary;

        Assert.Equal(1, s.LackingByMask["group"]);
        Assert.Equal(1, s.LackingByMask["everyone"]);
        Assert.Equal(0, s.FlaggedParts);
    }

    // ------------------------------------------------------------------ effective permissions

    [Fact]
    public void AnObjectWhoseContentsRestrictNothingIsEffectivelyFull()
    {
        ObjectEntry o = Table(null);

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
        Assert.Equal("MCT", Letters(o.Effective, e => e.Group));
        // the region never gives everyone modify (SceneObjectGroup.AggregatePerms: everyone &= ~Modify)
        Assert.Equal("-CT", Letters(o.Effective, e => e.Everyone));
        Assert.Equal("MCT", Letters(o.Effective, e => e.NextOwner));
        Assert.Empty(o.Effective.RestrictedBy);
    }

    [Fact]
    public void OneNoCopyItemInsideAChildPartMakesTheWholeObjectNoCopyAndNamesTheItemAndThePart()
    {
        KItem item = new KItem { Name = "Door script", OwnerMask = M | T };
        ObjectEntry o = Table(item);

        Assert.Equal("M-T", Letters(o.Effective, e => e.Owner));
        Assert.Equal("M-T", Letters(o.Effective, e => e.Group));
        string why = Assert.Single(o.Effective.RestrictedBy);
        Assert.Contains("copy", why);
        Assert.Contains("Door script", why);
        Assert.Contains("\"Leg\"", why);
    }

    [Fact]
    public void AnItemThatLacksTransferTakesTransferAway()
    {
        ObjectEntry o = Table(new KItem { Name = "Gift", OwnerMask = M | C });

        Assert.Equal("MC-", Letters(o.Effective, e => e.Owner));
        Assert.Contains("transfer", Assert.Single(o.Effective.RestrictedBy));
    }

    [Fact]
    public void WhenContentsLeaveNeitherCopyNorTransferTheRegionGivesTransferBack()
    {
        // SceneObjectGroup.AggregatePerms: if ((owner & (Copy | Transfer)) == 0) owner |= Transfer
        ObjectEntry o = Table(new KItem { Name = "Locked", OwnerMask = M });

        Assert.Equal("M-T", Letters(o.Effective, e => e.Owner));
    }

    [Fact]
    public void AnItemThatLacksModifyDoesNotTakeModifyAwayFromTheObject()
    {
        ObjectEntry o = Table(new KItem { Name = "Fixed", OwnerMask = C | T });

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
        Assert.Empty(o.Effective.RestrictedBy);
    }

    [Fact]
    public void ALandmarkInsideAPartRestrictsNothing()
    {
        ObjectEntry o = Table(new KItem { Name = "Home", AssetType = 3, InvType = 3, OwnerMask = 0 });

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
    }

    [Fact]
    public void TheOwnerAndNextOwnerMasksOfAChildPartDoNotCountOnlyTheRootPartsAndTheContentsDo()
    {
        ObjectEntry o = Table(null, child: leg => { leg.OwnerMask = 0; leg.NextMask = 0; leg.GroupMask = 0; leg.EveryoneMask = 0; });

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
        Assert.Equal("MCT", Letters(o.Effective, e => e.NextOwner));
        Assert.Empty(o.Effective.RestrictedBy);
    }

    [Fact]
    public void AChildPartsBaseMaskStillLimitsWhatPassesToTheNextOwner()
    {
        // SceneObjectGroup.CurrentAndFoldedNextPermissions: the owner mask is ANDed with every part's base mask
        ObjectEntry o = Table(null, child: leg => leg.BaseMask = M | T);

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
        Assert.Equal("M-T", Letters(o.Effective, e => e.NextOwner));
        string why = Assert.Single(o.Effective.RestrictedBy);
        Assert.Contains("\"Leg\"", why);
        Assert.Contains("base mask", why);
    }

    [Fact]
    public void TheRootPartsBaseMaskLimitsTheOwner()
    {
        ObjectEntry o = Table(null, root: t => t.BaseMask = M | T);

        Assert.Equal("M-T", Letters(o.Effective, e => e.Owner));
        Assert.Contains("base mask", Assert.Single(o.Effective.RestrictedBy));
    }

    [Fact]
    public void TheRootPartsOwnerMaskWithoutModifyLeavesTheObjectWithoutModify()
    {
        ObjectEntry o = Table(null, root: t => t.OwnerMask = C | T);

        Assert.Equal("-CT", Letters(o.Effective, e => e.Owner));
        Assert.Contains("modify", Assert.Single(o.Effective.RestrictedBy));
    }

    [Fact]
    public void ForTheNextOwnerAnItemThatCannotBeCopiedOnPassesNoCopy()
    {
        // MaskEffectivePermissions: the item's current mask ANDed with its next owner mask clears the folded copy bit
        ObjectEntry o = Table(new KItem { Name = "Tool", OwnerMask = All, NextMask = M | T });

        Assert.Equal("MCT", Letters(o.Effective, e => e.Owner));
        Assert.Equal("M-T", Letters(o.Effective, e => e.NextOwner));
        string why = Assert.Single(o.Effective.RestrictedBy);
        Assert.Contains("next owner", why);
        Assert.Contains("Tool", why);
    }

    [Fact]
    public void ForTheNextOwnerTheRootPartsNextOwnerMaskApplies()
    {
        ObjectEntry o = Table(null, root: t => t.NextMask = M | C);

        Assert.Equal("MC-", Letters(o.Effective, e => e.NextOwner));
    }

    [Fact]
    public void EachObjectOfAnArchiveHasItsOwnEffectivePermissions()
    {
        KPart a = Part("Free", 1), b = Part("Bound", 1);
        b.Items.Add(new KItem { Name = "Locked", OwnerMask = M | T });
        ObjectReport r = ReadOar(Oar(new[] { a }, new[] { b }));

        Assert.Equal("MCT", Letters(r.Objects[0].Effective, e => e.Owner));
        Assert.Equal("M-T", Letters(r.Objects[1].Effective, e => e.Owner));
    }

    [Fact]
    public void TheTextCsvAndJsonShowTheEffectivePermissionsAndWhatRestrictsThem()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        leg.Items.Add(new KItem { Name = "Door script", OwnerMask = M | T });
        ObjectReport r = ReadOar(Oar(new[] { table, leg }));

        string text = ReportText.Build(r);
        Assert.Contains("effective", text);
        Assert.Contains("owner M-T", text);
        Assert.Contains("restricted by", text);
        Assert.Contains("Door script", text);

        string csv = ReportCsv.Build(r);
        Assert.StartsWith("kind,", csv);
        Assert.Contains("effective_owner", csv.Split('\r')[0]);
        Assert.Contains("M-T", csv.Split("\r\n")[1]);

        using System.Text.Json.JsonDocument doc = Json(ReportJson.Build(r));
        System.Text.Json.JsonElement eff = doc.RootElement.GetProperty("objects")[0].GetProperty("effective");
        Assert.Equal("M-T", eff.GetProperty("owner").GetProperty("letters").GetString());
        Assert.Contains("Door script", eff.GetProperty("restrictedBy")[0].GetString());
    }

    // ------------------------------------------------------------------ names from a list

    private const string AliceId = "a11ce000-0000-4000-8000-000000000001";
    private const string BobId = "b0b00000-0000-4000-8000-000000000002";
    private const string CarolId = "ca701000-0000-4000-8000-000000000003";

    [Fact]
    public void ANameListTakesAnIdAndANameSeparatedByASpaceCommaTabOrEqualsSign()
    {
        string text = "# who is who\n\n" + AliceId + " Alice Example\n" + BobId + ",Bob Example\n" + CarolId + "\tCarol Example\n" +
                      "9a009000-0000-4000-8000-000000000005 = Example Group\n";
        Dictionary<UUID, string> names = NameList.Parse(text);

        Assert.Equal(4, names.Count);
        Assert.Equal("Alice Example", names[Alice]);
        Assert.Equal("Bob Example", names[Bob]);
        Assert.Equal("Carol Example", names[Carol]);
        Assert.Equal("Example Group", names[Group1]);
    }

    [Fact]
    public void ALineThatIsNotAnIdAndANameIsRefusedWithItsLineNumber()
    {
        FormatException e = Assert.Throws<FormatException>(() => NameList.Parse(AliceId + " Alice\nnot an id here\n"));
        Assert.Contains("line 2", e.Message);
    }

    [Fact]
    public void AnIdNamedTwiceWithDifferentNamesIsRefused()
    {
        FormatException e = Assert.Throws<FormatException>(() => NameList.Parse(AliceId + " Alice\n" + AliceId + " Alicia\n"));
        Assert.Contains("line 2", e.Message);
    }

    [Fact]
    public void NamesFromTheListFillCreatorOwnerAndLastOwnerOfPartsAndItems()
    {
        KPart table = Part("Table", 1);
        table.Owner = Bob;
        table.LastOwner = Carol;
        table.Items.Add(new KItem { Name = "Notes", Creator = Bob, Owner = Bob, LastOwner = Carol });
        ReadOptions options = new ReadOptions { Names = NameList.Parse(AliceId + " Alice Example\n" + BobId + " Bob Example\n" + CarolId + " Carol Example\n") };

        ObjectReport r = ReadOar(Oar(new[] { table }), options);
        PartEntry p = r.Objects.One().Parts.One();
        ItemEntry i = p.Items.One();

        Assert.Equal("Alice Example", p.CreatorName);
        Assert.Equal("Bob Example", p.OwnerName);
        Assert.Equal("Carol Example", p.LastOwnerName);
        Assert.Equal("Bob Example", i.CreatorName);
        Assert.Equal("Carol Example", i.LastOwnerName);
        Assert.Contains(r.Parties, c => c.Id == Bob && c.Name == "Bob Example");
        Assert.Contains("Bob Example", ReportText.Build(r));
    }

    [Fact]
    public void ANameInTheArchiveItselfIsKeptOverOneFromTheList()
    {
        KPart table = Part("Table", 1);
        table.CreatorData = "example.org;Archive Alice";
        ReadOptions options = new ReadOptions { Names = NameList.Parse(AliceId + " Listed Alice\n") };

        PartEntry p = ReadOar(Oar(new[] { table }), options).Objects.One().Parts.One();

        Assert.Equal("Archive Alice", p.CreatorName);
    }

    [Fact]
    public void WithoutAListAnUnnamedIdStaysUnnamed()
    {
        PartEntry p = ReadOar(Oar(new[] { Part("Table", 1) })).Objects.One().Parts.One();
        Assert.True(string.IsNullOrEmpty(p.CreatorName));
    }

    [Fact]
    public void NamesFromTheListAlsoNameTheCreatorAndOwnerOfAnInventoryItem()
    {
        byte[] iar = Iar(new IarItem { Name = "Note", AssetType = 7, Creator = Alice, Owner = Bob });
        using MemoryStream ms = new MemoryStream(iar);
        ObjectReport r = ObjectReader.Read(ms, new ReadOptions { Names = NameList.Parse(AliceId + " Alice Example\n" + BobId + " Bob Example\n") });

        InventoryItemEntry i = r.InventoryItems.One();
        Assert.Equal("Alice Example", i.CreatorName);
        Assert.Equal("Bob Example", i.OwnerName);
    }

    // ------------------------------------------------------------------ the command line

    private static (int code, string output, string error) Run(params string[] args)
    {
        TextWriter savedOut = Console.Out, savedErr = Console.Error;
        StringWriter output = new StringWriter(), error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            int code = InspectCommand.Run(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    private static string LockedGroupOar(string folder)
    {
        KPart p = Part("Locked", 1);
        p.GroupMask = 0;
        return WriteFile(folder, "locked.oar", Oar(new[] { p }));
    }

    [Fact]
    public void TheCommandFlagsGroupAndEveryoneMasksOnlyWithStrict()
    {
        string path = LockedGroupOar(NewFolder());

        var plain = Run(path, "--json");
        Assert.Equal(0, plain.code);
        Assert.Equal(0, Json(plain.output).RootElement.GetProperty("summary").GetProperty("flaggedParts").GetInt32());

        var strict = Run(path, "--json", "--strict");
        Assert.Equal(0, strict.code);
        Assert.Equal(1, Json(strict.output).RootElement.GetProperty("summary").GetProperty("flaggedParts").GetInt32());
    }

    [Fact]
    public void TheCommandNamesAccountsFromAFileGivenWithNames()
    {
        string folder = NewFolder();
        string path = LockedGroupOar(folder);
        string names = WriteFile(folder, "names.txt", Utf8(AliceId + " Alice Example\n"));

        var r = Run(path, "--names", names);

        Assert.Equal(0, r.code);
        Assert.Contains("Alice Example", r.output);
    }

    [Fact]
    public void ANamesFileWithABadLineIsRefusedWithTheLineNumberAndNothingIsReported()
    {
        string folder = NewFolder();
        string path = LockedGroupOar(folder);
        string names = WriteFile(folder, "names.txt", Utf8(AliceId + " Alice Example\nnonsense\n"));

        var r = Run(path, "--names", names);

        Assert.Equal(2, r.code);
        Assert.Contains("line 2", r.error);
        Assert.Equal("", r.output);
    }

    [Fact]
    public void ANamesFileThatIsNotThereIsRefused()
    {
        string path = LockedGroupOar(NewFolder());

        var r = Run(path, "--names", Path.Combine(NewFolder(), "missing.txt"));

        Assert.Equal(2, r.code);
        Assert.Contains("names", r.error);
    }

    [Fact]
    public void TheUsageTextNamesStrictAndNames()
    {
        var r = Run("--help");
        Assert.Contains("--strict", r.output);
        Assert.Contains("--names", r.output);
    }

    // ------------------------------------------------------------------ the next owner column is labelled as derived

    private static ObjectReport RestrictedTable()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        leg.Items.Add(new KItem { Name = "Door script", OwnerMask = M | T, NextMask = M | T });
        return ReadOar(Oar(new[] { table, leg }));
    }

    [Fact]
    public void TheTextLabelsTheNextOwnerColumnAsDerivedAndTheOthersNot()
    {
        string line = ReportText.Build(RestrictedTable()).Split('\n').Single(l => l.StartsWith("  effective"));

        Assert.Contains("next owner (derived) M-T", line);
        Assert.Contains("owner M-T", line);
        Assert.DoesNotContain("owner (derived)", line.Replace("next owner (derived)", ""));
        Assert.DoesNotContain("group (derived)", line);
        Assert.DoesNotContain("everyone (derived)", line);
    }

    [Fact]
    public void TheTextLegendSaysTheNextOwnerColumnIsDerivedAndWhy()
    {
        string text = ReportText.Build(RestrictedTable());

        Assert.Contains("next owner column of the effective permissions is derived", text);
        Assert.Contains("not a single function of the region", text);
    }

    [Fact]
    public void TheCsvNamesTheNextOwnerColumnAsDerived()
    {
        string header = ReportCsv.Build(RestrictedTable()).Split("\r\n")[0];

        Assert.Contains(",effective_next_derived,", header);
        Assert.DoesNotContain("effective_next,", header);
        Assert.Contains("effective_owner,effective_group,effective_everyone,effective_next_derived,restricted_by", header);
    }

    [Fact]
    public void TheJsonNamesTheNextOwnerEntryAsDerivedAndCarriesItsLetters()
    {
        using System.Text.Json.JsonDocument doc = Json(ReportJson.Build(RestrictedTable()));
        System.Text.Json.JsonElement eff = doc.RootElement.GetProperty("objects")[0].GetProperty("effective");

        Assert.True(eff.TryGetProperty("nextOwnerDerived", out System.Text.Json.JsonElement derived), "the effective object has no nextOwnerDerived entry");
        Assert.Equal("M-T", derived.GetProperty("letters").GetString());
        Assert.False(eff.TryGetProperty("nextOwner", out _));
        Assert.Equal("M-T", eff.GetProperty("owner").GetProperty("letters").GetString());
    }

    [Fact]
    public void TheUsageTextSaysTheNextOwnerColumnIsDerived()
    {
        var r = Run("--help");
        Assert.Contains("next owner column is derived", r.output);
    }
}
