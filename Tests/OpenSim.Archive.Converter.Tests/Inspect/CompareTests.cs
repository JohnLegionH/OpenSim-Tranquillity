/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;

namespace OpenSim.Archive.ObjectInspect.Tests;

/// <summary>The before and after compare: objects paired by name, then link order and permissions part by part.</summary>
public class CompareTests
{
    private static ObjectReport ReadBytes(byte[] archive)
    {
        using MemoryStream ms = new MemoryStream(archive);
        return ObjectReader.Read(ms);
    }

    private static KPart Part(string name, int link) => new KPart { Name = name, Link = link };

    private static CompareResult Compare(byte[] reference, byte[] archive) => ReportCompare.Compare(ReadBytes(reference), ReadBytes(archive));

    // ------------------------------------------------------------------ nothing differs

    [Fact]
    public void TwoReadsOfTheSameArchiveAreIdentical()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        byte[] oar = Oar(new[] { table, leg });
        CompareResult r = Compare(oar, oar);
        Assert.True(r.Identical);
        Assert.Equal(1, r.Matched);
        Assert.Empty(r.Objects.SelectMany(o => o.Differences));
        Assert.Empty(r.OnlyInReference);
        Assert.Empty(r.OnlyInArchive);
        Assert.Contains("no differences", r.ToText());
    }

    // ------------------------------------------------------------------ link order

    [Fact]
    public void ReorderedPartsAreReportedOneByOneWithTheirOldAndNewLinkNumber()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2), top = Part("Top", 3);
        byte[] before = Oar(new[] { table, leg, top });
        KPart legAfter = Part("Leg", 3), topAfter = Part("Top", 2);
        legAfter.Id = leg.Id;
        topAfter.Id = top.Id;
        byte[] after = Oar(new[] { table, topAfter, legAfter });

        CompareResult r = Compare(before, after);

        Assert.False(r.Identical);
        Assert.Equal(1, r.ObjectsWithLinkOrderDifferences);
        Assert.Equal(0, r.ObjectsWithPermissionDifferences);
        ObjectComparison o = r.Objects.One();
        Assert.Equal("Table", o.Name);
        Assert.Equal(new[] { "Table", "Leg", "Top" }, o.Parts.Select(p => p.Name).OrderBy(n => n == "Table" ? 0 : n == "Leg" ? 1 : 2).ToArray());
        PartLink legRow = o.Parts.Single(p => p.Name == "Leg");
        Assert.Equal((2, 3), (legRow.ReferenceLink.Value, legRow.ArchiveLink.Value));
        Assert.Equal((2, 3), (legRow.ReferenceFileOrder.Value, legRow.ArchiveFileOrder.Value));
        Assert.True(legRow.Changed);
        PartLink topRow = o.Parts.Single(p => p.Name == "Top");
        Assert.Equal((3, 2), (topRow.ReferenceLink.Value, topRow.ArchiveLink.Value));
        Assert.False(o.Parts.Single(p => p.Name == "Table").Changed);

        Difference d = o.Differences.Single(x => x.Part == "Leg" && x.Area == "link order" && x.Field == "link number");
        Assert.Equal("2", d.Reference);
        Assert.Equal("3", d.Archive);
        Assert.Contains("\"Leg\": link 2 -> 3", r.ToText());
    }

    [Fact]
    public void AChangedFilePositionWithTheSameLinkNumberIsAlsoALinkOrderDifference()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2), top = Part("Top", 3);
        byte[] before = Oar(new[] { table, leg, top });
        byte[] after = Oar(new[] { table, top, leg });

        ObjectComparison o = Compare(before, after).Objects.One();
        Assert.True(o.LinkOrderDiffers);
        Difference d = o.Differences.Single(x => x.Part == "Leg" && x.Field == "file position");
        Assert.Equal("2", d.Reference);
        Assert.Equal("3", d.Archive);
    }

    [Fact]
    public void PartsAreMatchedByNameAndShapeWhenTheirIdsChanged()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2), top = Part("Top", 3);
        byte[] before = Oar(new[] { table, leg, top });
        KPart tableB = Part("Table", 1), legB = Part("Leg", 3), topB = Part("Top", 2);
        byte[] after = Oar(new[] { tableB, topB, legB });

        CompareResult r = Compare(before, after);
        Assert.Equal(1, r.ObjectsWithLinkOrderDifferences);
        Assert.Equal("3", r.Objects.One().Differences.Single(x => x.Part == "Leg" && x.Field == "link number").Archive);
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public void AChangedMaskIsReportedWithTheLettersBeforeAndAfter()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        byte[] before = Oar(new[] { table, leg });
        KPart legAfter = Part("Leg", 2);
        legAfter.Id = leg.Id;
        legAfter.NextMask = M | T;
        byte[] after = Oar(new[] { table, legAfter });

        CompareResult r = Compare(before, after);
        Assert.False(r.Identical);
        Assert.Equal(0, r.ObjectsWithLinkOrderDifferences);
        Assert.Equal(1, r.ObjectsWithPermissionDifferences);
        Difference d = r.Objects.One().Differences.Single();
        Assert.Equal("permissions", d.Area);
        Assert.Equal("Leg", d.Part);
        Assert.Equal("next owner mask", d.Field);
        Assert.Equal("MCT", d.Reference);
        Assert.Equal("M-T", d.Archive);
        Assert.Contains("\"Leg\": next owner mask MCT -> M-T", r.ToText());
    }

    [Fact]
    public void AMaskThatChangedBetweenValuesWithTheSameLettersIsStillReported()
    {
        KPart table = Part("Table", 1);
        byte[] before = Oar(new[] { table });
        KPart after1 = Part("Table", 1);
        after1.Id = table.Id;
        after1.OwnerMask = M | C | T;
        byte[] after = Oar(new[] { after1 });
        Difference d = Compare(before, after).Objects.One().Differences.One();
        Assert.Equal("owner mask", d.Field);
        Assert.Equal("MCT (7fffffff)", d.Reference);
        Assert.Equal("MCT (e000)", d.Archive);
    }

    [Fact]
    public void AChangedOwnerCreatorLastOwnerOrGroupIsReportedAsWho()
    {
        KPart table = Part("Table", 1);
        byte[] before = Oar(new[] { table });
        KPart t2 = Part("Table", 1);
        t2.Id = table.Id;
        t2.Owner = Bob;
        t2.Group = Group1;
        Difference[] diffs = Compare(before, Oar(new[] { t2 })).Objects.One().Differences.ToArray();
        Assert.Equal(new[] { "owner", "group" }, diffs.Select(d => d.Field).ToArray());
        Assert.All(diffs, d => Assert.Equal("who", d.Area));
        Assert.Equal(Alice.ToString(), diffs[0].Reference);
        Assert.Equal(Bob.ToString(), diffs[0].Archive);
    }

    [Fact]
    public void ItemsInAPartAreMatchedByNameAndTypeAndTheirMasksCompared()
    {
        KPart table = Part("Table", 1);
        table.Items.Add(new KItem { Name = "Script" });
        table.Items.Add(new KItem { Name = "Note", AssetType = 7 });
        byte[] before = Oar(new[] { table });
        KPart t2 = Part("Table", 1);
        t2.Id = table.Id;
        t2.Items.Add(new KItem { Name = "Script", OwnerMask = M | T });
        t2.Items.Add(new KItem { Name = "Note", AssetType = 7 });
        t2.Items.Add(new KItem { Name = "Extra" });

        ObjectComparison o = Compare(before, Oar(new[] { t2 })).Objects.One();
        Difference mask = o.Differences.Single(d => d.Field == "item \"Script\" owner mask");
        Assert.Equal("permissions", mask.Area);
        Assert.Equal("Table", mask.Part);
        Assert.Equal("MCT", mask.Reference);
        Assert.Equal("M-T", mask.Archive);
        Difference extra = o.Differences.Single(d => d.Field == "item \"Extra\" only in archive");
        Assert.Equal("items", extra.Area);
        Assert.Equal("absent", extra.Reference);
        Assert.Equal("present", extra.Archive);
    }

    // ------------------------------------------------------------------ pairing

    [Fact]
    public void ObjectsOnlyOnOneSideAreListed()
    {
        byte[] before = Oar(new[] { Part("Table", 1) }, new[] { Part("Lamp", 1) });
        byte[] after = Oar(new[] { Part("Table", 1) }, new[] { Part("Chair", 1) });
        CompareResult r = Compare(before, after);
        Assert.Equal(new[] { "Lamp" }, r.OnlyInReference.ToArray());
        Assert.Equal(new[] { "Chair" }, r.OnlyInArchive.ToArray());
        Assert.Equal(1, r.Matched);
        Assert.False(r.Identical);
    }

    [Fact]
    public void ObjectsWithTheSameNameAreToldApartByPartCount()
    {
        KPart small1 = Part("Wall", 1), small2 = Part("Panel", 2);
        KPart big1 = Part("Wall", 1), big2 = Part("Panel", 2), big3 = Part("Frame", 3), big4 = Part("Door", 4);
        byte[] before = Oar(new[] { small1, small2 }, new[] { big1, big2, big3, big4 });
        KPart bigB1 = Part("Wall", 1), bigB2 = Part("Panel", 2), bigB3 = Part("Frame", 3), bigB4 = Part("Door", 4);
        bigB1.Id = big1.Id; bigB2.Id = big2.Id; bigB3.Id = big3.Id; bigB4.Id = big4.Id;
        KPart smallB1 = Part("Wall", 1), smallB2 = Part("Panel", 2);
        smallB1.Id = small1.Id; smallB2.Id = small2.Id;
        bigB4.NextMask = M;
        byte[] after = Oar(new[] { bigB1, bigB2, bigB3, bigB4 }, new[] { smallB1, smallB2 });

        CompareResult r = Compare(before, after);
        Assert.Equal(2, r.Matched);
        Assert.Empty(r.OnlyInArchive);
        Assert.Empty(r.OnlyInReference);
        ObjectComparison changed = r.Objects.Single(o => o.Differences.Count > 0);
        Assert.Equal(4, changed.Parts.Count);
        Assert.Contains("part count", changed.Matched);
        Assert.Equal("Door", changed.Differences.One().Part);
    }

    [Fact]
    public void ObjectsWithTheSameNameAndPartCountAreToldApartByShape()
    {
        KPart box = Part("Block", 1);
        KPart cyl = Part("Block", 1);
        cyl.ProfileCurve = 0;
        byte[] before = Oar(new[] { box }, new[] { cyl });
        KPart boxB = Part("Block", 1), cylB = Part("Block", 1);
        cylB.ProfileCurve = 0;
        cylB.NextMask = M;
        byte[] after = Oar(new[] { cylB }, new[] { boxB });

        CompareResult r = Compare(before, after);
        Assert.Equal(2, r.Matched);
        ObjectComparison changed = r.Objects.Single(o => o.Differences.Count > 0);
        Assert.Contains("shape", changed.Matched);
        Assert.Equal(1, changed.ReferenceIndex == 2 ? 1 : 0);
        Assert.Equal("next owner mask", changed.Differences.One().Field);
    }

    [Fact]
    public void APartOnlyOnOneSideIsReported()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2), top = Part("Top", 3);
        byte[] before = Oar(new[] { table, leg, top });
        byte[] after = Oar(new[] { table, leg });
        ObjectComparison o = Compare(before, after).Objects.One();
        Difference d = o.Differences.One();
        Assert.Equal("parts", d.Area);
        Assert.Equal("Top", d.Part);
        Assert.Equal("only in reference", d.Field);
        Assert.Equal("present", d.Reference);
        Assert.Equal("absent", d.Archive);
        PartLink row = o.Parts.Single(p => p.Name == "Top");
        Assert.Null(row.ArchiveLink);
        Assert.Equal(3, row.ReferenceLink);
    }

    [Fact]
    public void PartsThatShareANameAreMatchedInFileOrder()
    {
        KPart root = Part("Table", 1), a = Part("Leg", 2), b = Part("Leg", 3);
        b.X = 5;
        byte[] before = Oar(new[] { root, a, b });
        KPart rootB = Part("Table", 1), aB = Part("Leg", 2), bB = Part("Leg", 3);
        bB.X = 5;
        bB.NextMask = M;
        CompareResult r = Compare(before, Oar(new[] { rootB, aB, bB }));
        Difference d = r.Objects.One().Differences.One();
        Assert.Equal("Leg", d.Part);
        Assert.Equal("next owner mask", d.Field);
    }

    // ------------------------------------------------------------------ IARs

    [Fact]
    public void TheLinksetsOfACoalescedItemAreComparedOneByOne()
    {
        KPart w1 = Part("Wall", 1), w2 = Part("Pane", 2);
        KPart d1 = Part("Door", 1), d2 = Part("Knob", 2), d3 = Part("Hinge", 3);
        string before = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { w1, w2 }), (4f, 0f, 0f, new[] { d1, d2, d3 }));
        KPart d2b = Part("Knob", 3), d3b = Part("Hinge", 2);
        d2b.Id = d2.Id; d3b.Id = d3.Id;
        string after = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { w1, w2 }), (4f, 0f, 0f, new[] { d1, d3b, d2b }));
        IarItem item = new IarItem { Name = "Pieces", AssetType = 6, ObjectXml = before };
        IarItem itemAfter = new IarItem { Name = "Pieces", AssetType = 6, ObjectXml = after };

        CompareResult r = Compare(Iar(item), Iar(itemAfter));
        Assert.Equal(2, r.Matched);
        Assert.Equal(1, r.ObjectsWithLinkOrderDifferences);
        ObjectComparison door = r.Objects.Single(o => o.Name == "Door");
        Assert.Equal("3", door.Differences.Single(x => x.Part == "Knob" && x.Field == "link number").Archive);
    }

    [Fact]
    public void AnOarAndAnIarCanBeComparedByObjectName()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2);
        byte[] oar = Oar(new[] { table, leg });
        KPart legB = Part("Leg", 2);
        legB.NextMask = M;
        IarItem item = new IarItem { Name = "Table box", AssetType = 6, ObjectXml = OriginalGroup(table, legB) };
        CompareResult r = Compare(oar, Iar(item));
        Assert.Equal(1, r.Matched);
        Assert.Equal("next owner mask", r.Objects.One().Differences.One().Field);
    }

    // ------------------------------------------------------------------ outputs

    private static CompareResult Reordered()
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 2), top = Part("Top", 3);
        byte[] before = Oar(new[] { table, leg, top }, new[] { Part("Lamp", 1) });
        KPart legAfter = Part("Leg", 3), topAfter = Part("Top", 2);
        legAfter.Id = leg.Id; topAfter.Id = top.Id;
        legAfter.NextMask = M;
        return Compare(before, Oar(new[] { table, topAfter, legAfter }));
    }

    [Fact]
    public void TheTextNamesTheTwoArchivesCountsAndEachDifferenceGroupedByObject()
    {
        string text = Reordered().ToText("before.oar", "after.oar");
        Assert.Contains("reference: before.oar", text);
        Assert.Contains("archive: after.oar", text);
        Assert.Contains("objects matched: 1", text);
        Assert.Contains("only in reference: 1", text);
        Assert.Contains("only in archive: 0", text);
        Assert.Contains("objects with link order differences: 1", text);
        Assert.Contains("objects with permission differences: 1", text);
        int obj = text.IndexOf("OBJECT \"Table\" (matched by name)", StringComparison.Ordinal);
        Assert.True(obj >= 0, text);
        Assert.True(text.IndexOf("LINK ORDER", obj, StringComparison.Ordinal) > obj);
        Assert.True(text.IndexOf("PERMISSIONS", obj, StringComparison.Ordinal) > obj);
        Assert.Contains("\"Top\": link 3 -> 2", text);
        Assert.Contains("\"Leg\": next owner mask MCT -> M--", text);
    }

    [Fact]
    public void TheCsvHasOneRowPerDifference()
    {
        string[] lines = Reordered().ToCsv().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal("object,area,part,field,reference,archive", lines[0]);
        Assert.Contains("Table,link order,Leg,link number,2,3", lines);
        Assert.Contains("Table,permissions,Leg,next owner mask,MCT,M--", lines);
        Assert.Contains("Lamp,objects,,only in reference,present,absent", lines);
    }

    [Fact]
    public void TheJsonListsMatchedObjectsDifferencesAndTheUnmatched()
    {
        using JsonDocument doc = Json(Reordered().ToJson("before.oar", "after.oar"));
        JsonElement root = doc.RootElement;
        Assert.Equal("before.oar", root.GetProperty("reference").GetString());
        Assert.False(root.GetProperty("identical").GetBoolean());
        Assert.Equal(1, root.GetProperty("matched").GetInt32());
        Assert.Equal("Lamp", root.GetProperty("onlyInReference")[0].GetString());
        JsonElement table = root.GetProperty("objects")[0];
        Assert.Equal("Table", table.GetProperty("name").GetString());
        JsonElement d = table.GetProperty("differences").EnumerateArray().First(x => x.GetProperty("part").GetString() == "Leg" && x.GetProperty("area").GetString() == "link order");
        Assert.Equal("2", d.GetProperty("reference").GetString());
        Assert.Equal("3", d.GetProperty("archive").GetString());
        Assert.Equal(3, table.GetProperty("parts").GetArrayLength());
    }
}
