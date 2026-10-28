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

/// <summary>The three outputs: readable text, CSV and JSON.</summary>
public class ReportOutputTests
{
    private static ObjectReport ReadBytes(byte[] archive, ReadOptions options = null)
    {
        using MemoryStream ms = new MemoryStream(archive);
        return ObjectReader.Read(ms, options);
    }

    private static KPart Part(string name, int link, float x = 0, float y = 0, float z = 0) => new KPart { Name = name, Link = link, X = x, Y = y, Z = z };

    /// <summary>A small OAR: a table with a scrambled link order, a locked leg, a phantom ghost and a script with a missing copy right.</summary>
    private static ObjectReport TableReport()
    {
        KPart table = Part("Table", 1);
        table.Desc = "the top";
        table.Creator = Alice;
        table.CreatorData = "http://grid.example.org:8002;Alice Builder";
        table.Owner = Alice;
        table.Items.Add(new KItem { Name = "Script", OwnerMask = M | T, Creator = Alice });
        KPart leg = Part("Leg", 3, 1.5f, -2.25f, 0.5f);
        leg.Shape = 2;
        leg.NextMask = M | T;
        leg.Owner = Bob;
        KPart ghost = Part("Ghost", 2, 0, 0, 4);
        ghost.Shape = 1;
        ghost.Phantom = true;
        return ReadBytes(Oar(new[] { table, leg, ghost }, new[] { Part("Lamp", 1) }));
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ------------------------------------------------------------------ text

    [Fact]
    public void TheTextReportStartsWithASummaryOfObjectsPartsItemsAndWhatIsFlagged()
    {
        string text = ReportText.Build(TableReport());
        Assert.Contains("SUMMARY", text);
        Assert.Contains("objects: 2", text);
        Assert.Contains("parts: 4", text);
        Assert.Contains("items in part contents: 1", text);
        Assert.Contains("flagged objects: 1", text);
        Assert.Contains("flagged parts: 1", text);
        Assert.Contains("flagged part items: 1", text);
        Assert.Contains("objects with link order problems: 1", text);
        Assert.Contains("lacking by mask: base 0, owner 1, group 0, everyone 0, next 1", text);
        Assert.True(text.IndexOf("SUMMARY", StringComparison.Ordinal) < text.IndexOf("OBJECT 1", StringComparison.Ordinal), "the summary comes first");
    }

    [Fact]
    public void EveryPartIsOneLineWithItsFileOrderLinkNumberNamePositionShapeAndDescription()
    {
        string[] lines = Lines(ReportText.Build(TableReport()));
        Assert.Contains(lines, l => l.Contains("[1] link 1") && l.Contains("\"Table\"") && l.Contains("shape Prim") && l.Contains("desc \"the top\""));
        Assert.Contains(lines, l => l.Contains("[2] link 3") && l.Contains("\"Leg\"") && l.Contains("pos <1.5, -2.25, 0.5>") && l.Contains("shape Convex"));
        Assert.Contains(lines, l => l.Contains("[3] link 2") && l.Contains("\"Ghost\"") && l.Contains("shape None, phantom"));
    }

    [Fact]
    public void ThePartsOfOneObjectAreListedInFileOrderUnderTheObjectAndItsLinkOrderVerdict()
    {
        string text = ReportText.Build(TableReport());
        int obj = text.IndexOf("OBJECT 1 \"Table\"", StringComparison.Ordinal);
        Assert.True(obj >= 0, text);
        Assert.True(text.IndexOf("link order: PROBLEM - link numbers are not in file order: 1, 3, 2", obj, StringComparison.Ordinal) > obj);
        int a = text.IndexOf("\"Table\"  pos", obj, StringComparison.Ordinal), b = text.IndexOf("\"Leg\"  pos", obj, StringComparison.Ordinal), c = text.IndexOf("\"Ghost\"  pos", obj, StringComparison.Ordinal);
        Assert.True(a > obj && b > a && c > b);
        Assert.Contains("OBJECT 2 \"Lamp\"", text);
        Assert.Contains("link order: ok", text);
    }

    [Fact]
    public void EveryMaskIsShownAsLettersAndWhatALackingMaskLacksIsNamed()
    {
        string text = ReportText.Build(TableReport());
        Assert.Contains("masks   base MCT  owner MCT  group MCT  everyone MCT  next M-T", text);
        Assert.Contains("LACKS   next owner: copy", text);
        Assert.Contains("LACKS   owner: copy", text);
    }

    [Fact]
    public void ItemsInAPartAreListedUnderItWithTheirMasks()
    {
        string[] lines = Lines(ReportText.Build(TableReport()));
        int item = Array.FindIndex(lines, l => l.Contains("item \"Script\" [LSLText]"));
        Assert.True(item >= 0);
        string[] block = lines.Skip(item + 1).Take(3).ToArray();
        Assert.Contains(block, l => l.Contains("who     creator Alice Builder"));
        Assert.Contains(block, l => l.Contains("masks   base MCT  owner M-T  group MCT  everyone MCT  next MCT"));
        Assert.Contains(block, l => l.Contains("LACKS   owner: copy"));
    }

    [Fact]
    public void ANamedAccountIsShownWithItsNameAndAnUnnamedOneByItsIdAlone()
    {
        string text = ReportText.Build(TableReport());
        Assert.Contains("creator Alice Builder (" + Alice + ")", text);
        Assert.Contains("owner " + Bob + ";", text);
        Assert.DoesNotContain("()", text);
    }

    [Fact]
    public void EveryAccountIdIsCountedByItsRoles()
    {
        string[] lines = Lines(ReportText.Build(TableReport()));
        int accounts = Array.IndexOf(lines, "ACCOUNTS");
        Assert.True(accounts >= 0);
        string alice = lines.Skip(accounts).First(l => l.Contains(Alice.ToString()));
        Assert.Contains("Alice Builder", alice);
        Assert.Contains("creator 5", alice);
        string bob = lines.Skip(accounts).First(l => l.Contains(Bob.ToString()));
        Assert.Contains("owner 1", bob);
    }

    [Fact]
    public void AFilteredReportSaysSo()
    {
        ObjectReport r = ReadBytes(Oar(new[] { Part("Red Chair", 1) }, new[] { Part("Table", 1) }), new ReadOptions { NameFilter = "chair" });
        string text = ReportText.Build(r);
        Assert.Contains("filter: \"chair\" (1 of 2 objects)", text);
        Assert.DoesNotContain("\"Table\"", text);
    }

    [Fact]
    public void AnIarReportListsItsInventoryItemsAndNamesTheItemFolderAndCoalescedPlaceOfEachObject()
    {
        string xml = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { Part("Wall", 1) }), (4f, -2f, 1.5f, new[] { Part("Door", 1) }));
        IarItem pieces = new IarItem { Name = "House pieces", Folder = "Builds", AssetType = 6, ObjectXml = xml, Next = M | C };
        IarItem tex = new IarItem { Name = "Wood", Folder = "Textures", AssetType = 0 };
        string text = ReportText.Build(ReadBytes(Iar(pieces, tex)));

        Assert.Contains("kind: IAR", text);
        Assert.Contains("inventory items: 2", text);
        Assert.Contains("INVENTORY ITEMS", text);
        Assert.Contains("Builds / House pieces [Object]", text);
        Assert.Contains("masks   base MCT  owner MCT  group MCT  everyone MCT  next MC-", text);
        Assert.Contains("OBJECT 1 \"Wall\"", text);
        Assert.Contains("item \"House pieces\" in Builds, linkset 1 of 2, offset <0, 0, 0>", text);
        Assert.Contains("item \"House pieces\" in Builds, linkset 2 of 2, offset <4, -2, 1.5>", text);
    }

    [Fact]
    public void AnObjectItemWhoseAssetIsMissingIsListedWithItsNote()
    {
        string text = ReportText.Build(ReadBytes(Iar(new IarItem { Name = "Lost", AssetType = 6 })));
        Assert.Contains("Lost", text);
        Assert.Contains("the object asset is not in the archive", text);
    }

    [Fact]
    public void ANonArchiveGivesAStatusLineAndNoSummary()
    {
        string text = ReportText.Build(ReadBytes(Utf8("this is not an archive at all, it is just text that is long enough to fill a header block or two of nothing")));
        Assert.Contains("not an archive", text);
        Assert.DoesNotContain("SUMMARY", text);
    }

    // ------------------------------------------------------------------ CSV

    private static List<string[]> ParseCsv(string csv)
    {
        Assert.False(string.IsNullOrEmpty(csv), "the CSV is empty");
        List<string[]> rows = new List<string[]>();
        List<string> fields = new List<string>();
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                rows.Add(fields.ToArray()); fields.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0 || fields.Count > 0) { fields.Add(sb.ToString()); rows.Add(fields.ToArray()); }
        return rows;
    }

    private static string Cell(List<string[]> rows, int row, string column) => rows[row][Array.IndexOf(rows[0], column)];

    [Fact]
    public void TheCsvHasOneHeaderRowAndOneRowPerPartAndPerItem()
    {
        List<string[]> rows = ParseCsv(ReportCsv.Build(TableReport()));
        Assert.Equal("kind,object,entry,item_name,folder,coalesced,offset,link_num,file_order,name,description,x,y,z,shape_type,phantom,link_order,creator_id,creator_name,owner_id,owner_name,last_owner_id,last_owner_name,group_id,base,owner,group,everyone,next,base_hex,owner_hex,group_hex,everyone_hex,next_hex,lacks,flagged",
            string.Join(",", rows[0]));
        Assert.Equal(1 + 4 + 1, rows.Count);
        Assert.All(rows, r => Assert.Equal(rows[0].Length, r.Length));
        Assert.Equal(new[] { "part", "item", "part", "part", "part" }, rows.Skip(1).Select(r => r[0]).ToArray());
    }

    [Fact]
    public void ACsvRowCarriesLinkOrderMasksAsLettersAndNumbersAndWhatIsLacking()
    {
        List<string[]> rows = ParseCsv(ReportCsv.Build(TableReport()));
        int leg = Enumerable.Range(1, rows.Count - 1).First(i => Cell(rows, i, "name") == "Leg");
        Assert.Equal("part", Cell(rows, leg, "kind"));
        Assert.Equal("Table", Cell(rows, leg, "object"));
        Assert.Equal("3", Cell(rows, leg, "link_num"));
        Assert.Equal("2", Cell(rows, leg, "file_order"));
        Assert.Equal("1.5", Cell(rows, leg, "x"));
        Assert.Equal("Convex", Cell(rows, leg, "shape_type"));
        Assert.Equal("MCT", Cell(rows, leg, "owner"));
        Assert.Equal("M-T", Cell(rows, leg, "next"));
        Assert.Equal((M | T).ToString("x"), Cell(rows, leg, "next_hex"));
        Assert.Equal("next:C", Cell(rows, leg, "lacks"));
        Assert.Equal("true", Cell(rows, leg, "flagged"));
        Assert.Equal(Bob.ToString(), Cell(rows, leg, "owner_id"));
        Assert.Equal("PROBLEM", Cell(rows, leg, "link_order"));
    }

    [Fact]
    public void ACsvItemRowNamesTheItemAndItsPart()
    {
        List<string[]> rows = ParseCsv(ReportCsv.Build(TableReport()));
        int item = Enumerable.Range(1, rows.Count - 1).First(i => Cell(rows, i, "kind") == "item");
        Assert.Equal("Script", Cell(rows, item, "name"));
        Assert.Equal("Table", Cell(rows, item, "object"));
        Assert.Equal("M-T", Cell(rows, item, "owner"));
        Assert.Equal("owner:C", Cell(rows, item, "lacks"));
    }

    [Fact]
    public void CommasAndQuotesInTextAreQuotedSoTheRowsStayWhole()
    {
        KPart p = Part("Chair, \"big\"", 1);
        p.Desc = "red, green and \"blue\"";
        List<string[]> rows = ParseCsv(ReportCsv.Build(ReadBytes(Oar(new[] { p }))));
        Assert.Equal(2, rows.Count);
        Assert.Equal("Chair, \"big\"", Cell(rows, 1, "name"));
        Assert.Equal("red, green and \"blue\"", Cell(rows, 1, "description"));
        Assert.Equal(rows[0].Length, rows[1].Length);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-5")]
    [InlineData("@SUM(A1)")]
    public void TextThatASpreadsheetWouldTakeForAFormulaIsMadeIntoText(string name)
    {
        KPart p = Part(name, 1);
        List<string[]> rows = ParseCsv(ReportCsv.Build(ReadBytes(Oar(new[] { p }))));
        Assert.Equal("'" + name, Cell(rows, 1, "name"));
        Assert.Equal("MCT", Cell(rows, 1, "base"));
    }

    [Fact]
    public void AnIarCsvHasInventoryRowsAndObjectRowsWithTheirItemAndOffset()
    {
        string xml = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { Part("Wall", 1) }), (4f, -2f, 1.5f, new[] { Part("Door", 1) }));
        IarItem pieces = new IarItem { Name = "House pieces", Folder = "Builds", AssetType = 6, ObjectXml = xml };
        List<string[]> rows = ParseCsv(ReportCsv.Build(ReadBytes(Iar(pieces))));
        Assert.Equal(1 + 1 + 2, rows.Count);
        Assert.Equal("inventory", Cell(rows, 1, "kind"));
        Assert.Equal("House pieces", Cell(rows, 1, "name"));
        Assert.Equal("Builds", Cell(rows, 1, "folder"));
        Assert.Equal("part", Cell(rows, 3, "kind"));
        Assert.Equal("House pieces", Cell(rows, 3, "item_name"));
        Assert.Equal("2 of 2", Cell(rows, 3, "coalesced"));
        Assert.Equal("4 -2 1.5", Cell(rows, 3, "offset"));
    }

    // ------------------------------------------------------------------ JSON

    [Fact]
    public void TheJsonCarriesTheSummaryTheAccountsAndEveryPartWithItsMasks()
    {
        using JsonDocument doc = Json(ReportJson.Build(TableReport(), "table.oar"));
        JsonElement root = doc.RootElement;
        Assert.Equal("oar-inspect", root.GetProperty("tool").GetString());
        Assert.Equal("table.oar", root.GetProperty("archive").GetProperty("file").GetString());
        Assert.Equal("Oar", root.GetProperty("archive").GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("objects").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("flaggedParts").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("lackingByMask").GetProperty("next").GetInt32());

        JsonElement table = root.GetProperty("objects")[0];
        Assert.Equal("Table", table.GetProperty("name").GetString());
        Assert.False(table.GetProperty("linkOrderOk").GetBoolean());
        JsonElement leg = table.GetProperty("parts")[1];
        Assert.Equal("Leg", leg.GetProperty("name").GetString());
        Assert.Equal(3, leg.GetProperty("linkNum").GetInt32());
        Assert.Equal(2, leg.GetProperty("fileOrder").GetInt32());
        Assert.Equal("Convex", leg.GetProperty("shapeType").GetString());
        Assert.Equal("M-T", leg.GetProperty("masks").GetProperty("next").GetProperty("letters").GetString());
        Assert.Equal(M | T, leg.GetProperty("masks").GetProperty("next").GetProperty("value").GetUInt32());
        Assert.Equal("next:C", leg.GetProperty("lacks")[0].GetString());
        Assert.Equal(Bob.ToString(), leg.GetProperty("owner").GetProperty("id").GetString());

        JsonElement script = table.GetProperty("parts")[0].GetProperty("items")[0];
        Assert.Equal("Script", script.GetProperty("name").GetString());
        Assert.Equal("owner:C", script.GetProperty("lacks")[0].GetString());

        JsonElement alice = root.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("id").GetString() == Alice.ToString());
        Assert.Equal("Alice Builder", alice.GetProperty("name").GetString());
        Assert.Equal(5, alice.GetProperty("asCreator").GetInt32());
    }

    [Fact]
    public void TheJsonOfAnIarListsInventoryItemsAndCoalescedPlace()
    {
        string xml = Coalesced(8, 4, 3, (0f, 0f, 0f, new[] { Part("Wall", 1) }), (4f, -2f, 1.5f, new[] { Part("Door", 1) }));
        IarItem pieces = new IarItem { Name = "House pieces", Folder = "Builds", AssetType = 6, ObjectXml = xml };
        using JsonDocument doc = Json(ReportJson.Build(ReadBytes(Iar(pieces))));
        JsonElement item = doc.RootElement.GetProperty("inventoryItems")[0];
        Assert.Equal("House pieces", item.GetProperty("name").GetString());
        Assert.Equal("Builds", item.GetProperty("folder").GetString());
        Assert.Equal(2, item.GetProperty("linksets").GetInt32());
        JsonElement second = doc.RootElement.GetProperty("objects")[1];
        Assert.Equal(2, second.GetProperty("coalescedIndex").GetInt32());
        Assert.Equal(2, second.GetProperty("coalescedCount").GetInt32());
        Assert.Equal(4.0, second.GetProperty("offset").GetProperty("x").GetDouble());
        Assert.Equal(-2.0, second.GetProperty("offset").GetProperty("y").GetDouble());
    }
}
