/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;
using static OpenSim.Archive.Inspection.Tests.CheckKit;

namespace OpenSim.Archive.Inspection.Tests;

// ---------------------------------------------------------------------------------------- Part A: calibration

public class CalibrationTests
{
    private const string NamedData = "http://grid.example.org:8002/;Ann Builder";

    [Fact]
    public void BuiltInPlants_AreCountedApartAndLeftOutOfTheShares()
    {
        // 30 built parts by Alice; 40 trees and grass (three plant PCodes) placed by Bob: Bob is not a creator share
        List<(string, byte[], char)> parts = new List<(string, byte[], char)>();
        for (int i = 0; i < 30; i++)
            parts.Add(Part(i + 1, Alice, Alice, "<Shape><PCode>9</PCode></Shape>"));
        int[] plant = { 255, 111, 95 };
        for (int i = 0; i < 40; i++)
            parts.Add(Part(100 + i, Bob, Bob, "<Shape><PCode>" + plant[i % 3] + "</PCode></Shape>"));

        ArchiveReport r = Run(Oar(parts));
        Assert.Equal(40, r.CreatorAnalysis.PlantRecords);
        Assert.Equal(30, r.CreatorAnalysis.CountedRecords);
        Assert.Equal(70, r.CreatorAnalysis.TotalRecords);
        Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.CreatorId == Alice);
        Assert.Contains("40", TextReport.Build(r, false));
        Assert.Contains("built-in trees and grass", TextReport.Build(r, false));
    }

    [Theory]
    [InlineData(9, false)]     // an ordinary prim
    [InlineData(47, false)]    // an avatar PCode is not a plant
    [InlineData(143, false)]   // a particle system is not a plant
    [InlineData(95, true)]     // grass
    [InlineData(111, true)]    // new tree
    [InlineData(255, true)]    // tree
    public void OnlyTreeNewTreeAndGrassAreBuiltInPlants(int pcode, bool plant)
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 25).Select(i => Part(i, Alice, Alice, "<Shape><PCode>" + pcode + "</PCode></Shape>"))));
        Assert.Equal(plant ? 25 : 0, r.CreatorAnalysis.PlantRecords);
        Assert.Equal(plant ? 0 : 25, r.CreatorAnalysis.CountedRecords);
        Assert.Equal(!plant, r.CreatorAnalysis.Fired);
    }

    [Fact]
    public void APartWithNoShape_IsNotAPlant()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 25).Select(i => Part(i, Alice, Alice))));
        Assert.Equal(0, r.CreatorAnalysis.PlantRecords);
        Assert.Equal(25, r.CreatorAnalysis.CountedRecords);
    }

    [Fact]
    public void SameAccountMatch_GivesBothReadingsAndSaysOnlyAGridCheckTellsThemApart()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 30).Select(i => Part(i, Dave, Dave, date: 1500000000 + i * 5_000_000))));
        AnalysisMatch m = Assert.Single(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_SAME_ACCOUNT");
        Assert.Contains("replaces creators it cannot resolve", m.Statement);
        Assert.Contains("one person's own work that was never sold or given away", m.Statement);
        Assert.Contains("only a check against a grid", m.Statement);
        Assert.Contains("content dated before the credited account existed", m.Statement);
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_LOADER_REWRITE");
    }

    [Fact]
    public void BulkDatesMatch_NamesRezzingAndPlantingScriptsToo()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 30).Select(i => Part(i, Alice, Alice, date: 1650000000 + i * 60))));
        AnalysisMatch m = Assert.Single(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_BULK_DATES");
        Assert.Contains("import tool", m.Statement);
        Assert.Contains("mass re-creation", m.Statement);
        Assert.Contains("rezzing or planting script", m.Statement);
        Assert.Contains("run by the owner", m.Statement);
    }

    private static ArchiveReport Mixed(int named, int unnamed)
    {
        // every part has its own creator id, so the uuid grouping cannot fire; the named ones all say "Ann Builder"
        List<(string, byte[], char)> parts = new List<(string, byte[], char)>();
        for (int i = 0; i < named + unnamed; i++)
            parts.Add(Part(i + 1, Id(0x9000 + i), Bob, creatorData: i < named ? NamedData : null));
        return Run(Oar(parts));
    }

    [Fact]
    public void NameGrouping_FlagsAtExactlyHalfAndStatesItsCoverage()
    {
        ArchiveReport r = Mixed(40, 40);   // 50.0% carry a name
        CreatorFlag f = Assert.Single(r.CreatorAnalysis.Flags, x => x.Grouping == "name");
        Assert.Equal("SINGLE_CREATOR", f.Code);
        Assert.Equal("40 of the 40 records that carry a name; 40 of 80 counted records carry one", f.Coverage);
        Assert.Contains(f.Coverage, r.Findings.Single(x => x.Code == "SINGLE_CREATOR").Detail);
        Assert.Contains(f.Coverage, TextReport.Build(r, false));
        Assert.Empty(r.CreatorAnalysis.Notes);
        Assert.DoesNotContain(r.Findings, x => x.Code == "NAME_COVERAGE_LOW");
    }

    [Fact]
    public void NameGrouping_BelowHalfAddsAnInfoNoteInsteadOfAFlag()
    {
        ArchiveReport r = Mixed(39, 41);   // 48.75%
        Assert.DoesNotContain(r.CreatorAnalysis.Flags, x => x.Grouping == "name");
        Assert.False(r.CreatorAnalysis.Fired);
        Finding note = Assert.Single(r.Findings, x => x.Code == "NAME_COVERAGE_LOW");
        Assert.Equal(FindingSeverity.Info, note.Severity);
        Assert.Contains("39 of the 39 records that carry a name", note.Detail);
        Assert.Contains("39 of 80 counted records", note.Detail);
        Assert.Equal(0, r.ExitCode);                       // an Info note does not change the exit code
        Assert.Contains("Note:", TextReport.Build(r, false));
    }

    [Fact]
    public void NameGrouping_BelowHalfAndNothingToFlag_AddsNoNote()
    {
        ArchiveReport r = Mixed(10, 70);   // only ten named records, under the 20 needed
        Assert.Empty(r.CreatorAnalysis.Notes);
        Assert.DoesNotContain(r.Findings, x => x.Code == "NAME_COVERAGE_LOW");
    }

    [Fact]
    public void NameCoverageShareIsASetting()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 80).Select(i => Part(i, Id(0x9000 + i), Bob, creatorData: i <= 39 ? NamedData : null))),
            new InspectorOptions { Analysis = new AnalysisSettings { NameCoverageShare = 0.4 } });
        Assert.Contains(r.CreatorAnalysis.Flags, x => x.Grouping == "name");
    }

    [Fact]
    public void PlantCount_IsInTheArchiveSectionOfTheTextEvenWhenNothingFlags()
    {
        List<(string, byte[], char)> parts = new List<(string, byte[], char)>();
        for (int i = 0; i < 30; i++)
            parts.Add(Part(i + 1, Alice, Alice, "<Shape><PCode>111</PCode></Shape>"));
        ArchiveReport r = Run(Oar(parts));
        Assert.False(r.CreatorAnalysis.Fired);
        Assert.Contains("built-in trees and grass: 30 parts", TextReport.Build(r, false));
    }

    [Fact]
    public void UuidFlag_StatesItsCoverageToo()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 25).Select(i => Part(i, Alice, Alice))));
        CreatorFlag f = Assert.Single(r.CreatorAnalysis.Flags, x => x.Grouping == "uuid");
        Assert.Equal("25 of the 25 records that carry a creator id; 25 of 25 counted records carry one", f.Coverage);
    }

    [Fact]
    public void CoverageNumbersUseThousandsSeparators()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 1200).Select(i => Part(i, Alice, Alice))));
        Assert.Contains("1,200 of the 1,200 records that carry a creator id; 1,200 of 1,200 counted records carry one", r.CreatorAnalysis.Flags[0].Coverage);
    }
}

// ---------------------------------------------------------------------------------------- Part C.1: IAR sanity

public class IarSanityTests
{
    private static ArchiveReport Iar(params (string, byte[], char)[] entries) => Run(IarArchive(entries));

    [Fact]
    public void AWellFormedIar_HasNoSanityFindings()
    {
        UUID a = Id(0x500);
        ArchiveReport r = Iar(IarItem(1, "Stuff", i => { i.Asset = a; i.AssetType = 7; i.InvType = 7; }), Asset(a, "_notecard.txt", "Linden text version 2"));
        Assert.DoesNotContain(r.Findings, f => f.Code.StartsWith("IAR_"));
    }

    [Fact]
    public void ControlFileNotFirst_Fires()
    {
        ArchiveReport r = Run(IarArchive(new[] { IarItem(1) }, controlFirst: false));
        Finding f = Find(r, "IAR_ORDER");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains(f.Examples, e => e.Note.Contains("not the first entry"));
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public void NoControlFile_Fires()
    {
        ArchiveReport r = Run(RawTar(new[] { IarItem(1) }));
        Assert.Contains(Find(r, "IAR_ORDER").Examples, e => e.Note.Contains("no control file"));
    }

    [Fact]
    public void InventoryEntryAfterTheFirstAsset_FiresOnce()
    {
        UUID a = Id(0x500);
        ArchiveReport r = Iar(IarItem(1), Asset(a, "_notecard.txt"), IarItem(2));
        Finding f = Find(r, "IAR_ORDER");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.Contains("comes after the first asset entry", f.Examples[0].Note);
    }

    [Fact]
    public void AssetsBeforeAnyInventory_Fires()
    {
        ArchiveReport r = Iar(Asset(Id(0x500), "_notecard.txt"));
        Assert.Contains(Find(r, "IAR_ORDER").Examples, e => e.Note.Contains("before any inventory entry"));
    }

    [Fact]
    public void AnIarWithNoAssetsAtAll_IsInOrder()
    {
        Assert.False(Has(Iar(IarItem(1)), "IAR_ORDER"));
    }

    [Fact]
    public void UnparsableItemAndFolderEntries_Fire()
    {
        ArchiveReport r = Iar(IarItem(1), E("inventory/" + FolderPath("Stuff") + "/Broken__" + Id(2) + ".xml", "<InventoryItem><Name>x"), E("inventory/NoIdFolder/Item__" + Id(3) + ".xml", IarItem(3).data));
        Finding f = Find(r, "IAR_UNPARSED");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains(f.Examples, e => e.Note.Contains("not valid XML"));
        Assert.Contains(f.Examples, e => e.Note.Contains("no parsable folder id"));
        Assert.Equal(1, r.ExitCode);   // an entry that does not parse also makes the read partial
    }

    [Theory]
    [InlineData(0, 0, false)]     // Texture with Texture
    [InlineData(0, 15, false)]    // Texture with Snapshot is a pair in the table
    [InlineData(0, 1, true)]      // Texture with Sound is not
    [InlineData(7, 7, false)]
    [InlineData(7, 10, true)]     // Notecard with LSL
    [InlineData(10, 10, false)]
    [InlineData(6, 17, false)]    // Object with Attachment
    public void ItemAndInventoryType_MustBeAPairInTheTable(int assetType, int invType, bool fires)
    {
        ArchiveReport r = Iar(IarItem(1, "Stuff", i => { i.AssetType = assetType; i.InvType = invType; }));
        Assert.Equal(fires, Has(r, "IAR_TYPE_DISAGREE"));
    }

    [Fact]
    public void ItemAssetTypeAgainstTheAssetFile_FiresOnADifferentFamilyOnly()
    {
        UUID tex = Id(0x501), sound = Id(0x502), tga = Id(0x503);
        ArchiveReport r = Iar(
            IarItem(1, "Stuff", i => { i.Asset = tex; i.AssetType = 0; i.InvType = 0; }),
            IarItem(2, "Stuff", i => { i.Asset = tga; i.AssetType = 0; i.InvType = 0; }),
            Asset(tex, "_sound.ogg"), Asset(tga, "_texture.tga"));
        Finding f = Find(r, "IAR_ASSET_TYPE_MISMATCH");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);                      // the texture stored as TGA is the same family
        Assert.Equal(Id(1).ToString(), f.Examples[0].Id);
        Assert.Contains("Sound", f.Examples[0].Note);
    }

    [Fact]
    public void LinkItems_MustPointAtAnItemOrFolderInTheArchive()
    {
        UUID target = Id(1);
        ArchiveReport ok = Iar(IarItem(1), IarItem(2, "Stuff", i => { i.AssetType = 24; i.InvType = -1; i.Asset = target; }));
        Assert.False(Has(ok, "IAR_LINK_DANGLING"));

        ArchiveReport bad = Iar(IarItem(1), IarItem(2, "Stuff", i => { i.AssetType = 24; i.InvType = -1; i.Asset = Id(999); }));
        Finding f = Find(bad, "IAR_LINK_DANGLING");
        Assert.True(f != null, Dump(bad) + " kind=" + bad.Archive.Kind + " codes=" + Codes(bad) + " items=" + bad.IarItemCount + " folders=" + bad.FolderCount + " first=" + bad.Archive.ControlFileFirst + " found=" + bad.Archive.ControlFileFound);
        Assert.Equal(1, f.Count);
        Assert.Contains(Id(999).ToString(), f.Examples[0].Note);
    }

    [Fact]
    public void FolderLinks_AreCheckedAgainstTheArchivesFolders()
    {
        UUID folder = FolderIdOf("Stuff");
        ArchiveReport ok = Iar(IarItem(1), IarItem(2, "Stuff", i => { i.AssetType = 25; i.InvType = -1; i.Asset = folder; }));
        Assert.False(Has(ok, "IAR_LINK_DANGLING"));
        ArchiveReport bad = Iar(IarItem(1), IarItem(2, "Stuff", i => { i.AssetType = 25; i.InvType = -1; i.Asset = Id(998); }));
        Assert.True(Has(bad, "IAR_LINK_DANGLING"));
    }

    [Theory]
    [InlineData("Trash", true)]
    [InlineData("trash", true)]
    [InlineData("Current Outfit", true)]
    [InlineData("Lost And Found", true)]
    [InlineData("My Suitcase", true)]
    [InlineData("My Inventory", true)]
    [InlineData("Trash 2", false)]
    [InlineData("Objects", false)]        // a per-type folder: the loader merges it by name
    [InlineData("Textures", false)]
    public void SystemFolders_AreFlaggedByName(string folder, bool fires)
    {
        ArchiveReport r = Iar(IarItem(1, folder));
        Finding f = Find(r, "IAR_SYSTEM_FOLDER");
        Assert.Equal(fires, f != null);
        if (fires)
        {
            Assert.Equal(FindingSeverity.Notice, f.Severity);
            Assert.Equal(1, f.Count);
            Assert.Contains("name", f.Detail);
        }
    }

    [Fact]
    public void SystemFolderMatchesAtAnyDepth()
    {
        Assert.True(Has(Iar(IarItem(1, "Stuff/Trash")), "IAR_SYSTEM_FOLDER"));
    }

    [Theory]
    [InlineData("A/B", false)]
    [InlineData("A/B/C", true)]
    public void FolderDepth_IsBoundedBySettings(string folder, bool fires)
    {
        ArchiveReport r = Run(IarArchive(new[] { IarItem(1, folder) }), new InspectorOptions { Structure = new StructureSettings { MaxFolderDepth = 2 } });
        Assert.Equal(fires, Has(r, "IAR_LIMITS"));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void ItemsPerFolder_AreBoundedBySettings(int items, bool fires)
    {
        ArchiveReport r = Run(IarArchive(Enumerable.Range(1, items).Select(i => IarItem(i))), new InspectorOptions { Structure = new StructureSettings { MaxItemsPerFolder = 3 } });
        Assert.Equal(fires, Has(r, "IAR_LIMITS"));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void TotalItems_AreBoundedBySettings(int items, bool fires)
    {
        ArchiveReport r = Run(IarArchive(Enumerable.Range(1, items).Select(i => IarItem(i, "F" + i))), new InspectorOptions { Structure = new StructureSettings { MaxTotalItems = 5 } });
        Assert.Equal(fires, Has(r, "IAR_LIMITS"));
    }

    [Fact]
    public void BoxedObjects_MustParse()
    {
        UUID good = Id(0x600), bad = Id(0x601);
        string obj = HandMade.OriginalObject(HandMade.PartXml("Box", Id(0x700), Alice, Bob, Bob));
        ArchiveReport r = Iar(IarItem(1, "Stuff", i => { i.Asset = good; i.AssetType = 6; i.InvType = 6; }), IarItem(2, "Stuff", i => { i.Asset = bad; i.AssetType = 6; i.InvType = 6; }),
            Asset(good, "_object.xml", obj), Asset(bad, "_object.xml", "<SceneObjectGroup><RootPart>"));
        Finding f = Find(r, "IAR_BOXED_UNPARSED");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.Equal(bad.ToString(), f.Examples[0].Id);

        ArchiveReport clean = Iar(IarItem(1, "Stuff", i => { i.Asset = good; i.AssetType = 6; i.InvType = 6; }), Asset(good, "_object.xml", obj));
        Assert.False(Has(clean, "IAR_BOXED_UNPARSED"));
    }

    [Fact]
    public void AnOar_GetsNoIarFindings()
    {
        ArchiveReport r = Run(Oar(new[] { Part(1, Alice, Alice), E("inventory/Trash__" + Id(5) + "/x.xml", "x") }));
        Assert.DoesNotContain(r.Findings, f => f.Code.StartsWith("IAR_"));
    }
}

// ---------------------------------------------------------------------------------------- Part C.2: permissions

public class PermissionsTests
{
    private static ArchiveReport OneRecord(uint b, uint o, uint g, uint e, uint n, UUID? creator = null, UUID? owner = null) =>
        Run(Oar(new[] { Part(1, creator ?? Alice, owner ?? Alice, Masks(b, o, g, e, n)) }));

    [Fact]
    public void MaskWiderThanBase_FiresPerMaskAndNotWhenEqual()
    {
        uint c = 0x4000 | 0x8000 | 0x2000;   // modify, copy, transfer
        Assert.False(Has(OneRecord(c, c, c, 0, c), "PERM_WIDER_THAN_BASE"));
        Assert.False(Has(OneRecord(c, c & ~0x4000u, 0, 0, c & ~0x2000u), "PERM_WIDER_THAN_BASE"));

        Finding owner = Find(OneRecord(c & ~0x4000u, c, 0, 0, 0), "PERM_WIDER_THAN_BASE");
        Assert.NotNull(owner);
        Assert.Equal("1", owner.Numbers["owner"]);
        Assert.Contains("owner mask has modify that base lacks", owner.Examples[0].Note);

        Assert.Equal("1", Find(OneRecord(c & ~0x8000u, 0, c, 0, 0), "PERM_WIDER_THAN_BASE").Numbers["group"]);
        Assert.Equal("1", Find(OneRecord(c & ~0x2000u, 0, 0, c, 0), "PERM_WIDER_THAN_BASE").Numbers["everyone"]);
        Assert.Equal("1", Find(OneRecord(c & ~0x2000u, 0, 0, 0, c), "PERM_WIDER_THAN_BASE").Numbers["nextOwner"]);
    }

    [Fact]
    public void MovePastBase_IsNotCompared()
    {
        uint c = 0x4000 | 0x8000 | 0x2000;
        Assert.False(Has(OneRecord(c, c | 0x80000, 0, 0, 0), "PERM_WIDER_THAN_BASE"));
    }

    [Fact]
    public void ExportOnAMaskButNotInBase_CountsAsWider()
    {
        uint c = 0x4000 | 0x8000 | 0x2000;
        Assert.True(Has(OneRecord(c, c | Export, 0, 0, 0), "PERM_WIDER_THAN_BASE"));
    }

    [Fact]
    public void FullyOpenRecords_AreCountedAsInfo()
    {
        uint open = 0x4000 | 0x8000 | 0x2000;
        ArchiveReport r = Run(Oar(new[]
        {
            Part(1, Alice, Alice, Masks(open, open, open, open, open)),
            Part(2, Alice, Alice, Masks(open, open, open, open, open)),
            Part(3, Alice, Alice, Masks(open, open, open, open, open & ~0x2000u)),   // next owner cannot transfer
            Part(4, Alice, Alice, Masks(open, open, 0, open, open))                  // group closed
        }));
        Finding f = Find(r, "PERM_ALL_OPEN");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Equal(2, f.Count);
        Assert.Equal("4", f.Numbers["records"]);
        Assert.False(Has(OneRecord(open, open, 0, 0, open), "PERM_ALL_OPEN"));
    }

    [Fact]
    public void HeldItemWhoseCreatorDidNotAllowExport_IsCountedPerCreator()
    {
        ArchiveReport r = Run(Oar(new[]
        {
            Part(1, Alice, Bob, Masks(All, NoExport, 0, 0, Next)),
            Part(2, Alice, Bob, Masks(All, NoExport, 0, 0, Next)),
            Part(3, Carol, Bob, Masks(All, NoExport, 0, 0, Next))
        }));
        Finding f = Find(r, "PERM_NO_EXPORT_HELD");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Equal(3, f.Count);
        Assert.Contains("the creators of these items did not allow them to leave their grid", f.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2", f.Numbers.Single(kv => kv.Key.Contains(Alice.ToString())).Value);
        Assert.Equal("1", f.Numbers.Single(kv => kv.Key.Contains(Carol.ToString())).Value);
        Assert.Equal(3, r.ExitCode);
    }

    [Theory]
    [InlineData(All, 0u, false)]                 // the owner mask carries export: the grid would let it go
    [InlineData(NoExport, Export, false)]        // the everyone mask carries the creator's choice
    [InlineData(NoExport, 0u, true)]
    public void ExportRule_ReadsTheOwnerAndEveryoneMasksOfAHeldRecord(uint owner, uint everyone, bool fires)
    {
        ArchiveReport r = OneRecord(All, owner, 0, everyone, Next, Alice, Bob);
        Assert.Equal(fires, Has(r, "PERM_NO_EXPORT_HELD"));
    }

    [Fact]
    public void ARecordHeldByItsCreator_IsNeverCounted()
    {
        Assert.False(Has(OneRecord(All, NoExport, 0, 0, Next, Alice, Alice), "PERM_NO_EXPORT_HELD"));
    }

    [Fact]
    public void AnArchiveFromBeforeTheExportBitExisted_IsNotCountedAsRefused()
    {
        // no mask anywhere has bit 16: base without export means the right never existed
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 5).Select(i => Part(i, Alice, Bob, Masks(NoExport, NoExport, 0, 0, Next)))));
        Assert.False(Has(r, "PERM_NO_EXPORT_HELD"));
        Finding info = Find(r, "PERM_NO_EXPORT_INFO");
        Assert.NotNull(info);
        Assert.Equal(FindingSeverity.Info, info.Severity);
        Assert.Equal(5, info.Count);
        Assert.Contains("before the export bit existed", info.Detail);
    }

    [Fact]
    public void IarItemsAreCheckedLikeParts()
    {
        ArchiveReport r = Run(IarArchive(new[] { IarItem(1, "Stuff", i => { i.Creator = Alice; i.Owner = Bob; i.Owner_ = NoExport; i.Base = All; i.Everyone = 0; }) }));
        Assert.True(Has(r, "PERM_NO_EXPORT_HELD"));
        ArchiveReport own = Run(IarArchive(new[] { IarItem(1, "Stuff", i => { i.Creator = Bob; i.Owner = Bob; i.Owner_ = NoExport; }) }));
        Assert.False(Has(own, "PERM_NO_EXPORT_HELD"));
    }

    [Fact]
    public void HeldLibraryContent_IsNotCounted()
    {
        UUID library = UUID.Parse(Constants.m_MrOpenSimIDString);
        Assert.False(Has(OneRecord(All, NoExport, 0, 0, Next, library, Bob), "PERM_NO_EXPORT_HELD"));
    }
}

// ---------------------------------------------------------------------------------------- Part C.3: references and types

public class ReferenceTests
{
    private static string Inv(params string[] items) => "<TaskInventory>" + string.Concat(items) + "</TaskInventory>";

    [Fact]
    public void UnknownExtension_Fires()
    {
        ArchiveReport r = Run(Oar(new[] { Asset(Id(0x800), "_texture.jp2"), Asset(Id(0x801), "_widget.xyz"), E("assets/" + Id(0x802), "x") }));
        Finding f = Find(r, "REF_UNKNOWN_EXTENSION");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Notice, f.Severity);
        Assert.Equal(2, f.Count);
        Assert.DoesNotContain(f.Examples, e => e.Id == Id(0x800).ToString());

        Assert.False(Has(Run(Oar(new[] { Asset(Id(0x800), "_texture.jp2") })), "REF_UNKNOWN_EXTENSION"));
    }

    private static ArchiveReport ItemAsset(int itemType, string ext)
    {
        UUID asset = Id(0x810);
        return Run(Oar(new[]
        {
            Part(1, Alice, Alice, Inv(HandMade.TaskItem("Thing", Id(0x811), asset, itemType, Alice, Alice))),
            Asset(asset, ext)
        }));
    }

    [Theory]
    [InlineData(7, "_notecard.txt", false)]
    [InlineData(7, "_script.lsl", true)]          // a notecard item whose asset is stored as a script
    [InlineData(10, "_script.lsl", false)]
    [InlineData(10, "_bytecode.lso", false)]      // script text and bytecode are one family
    [InlineData(0, "_texture.jp2", false)]
    [InlineData(0, "_texture.tga", false)]        // texture encodings are one family
    [InlineData(0, "_sound.ogg", true)]
    [InlineData(1, "_sound.wav", false)]
    public void ItemAssetType_MustMatchTheAssetFile(int itemType, string ext, bool fires)
    {
        ArchiveReport r = ItemAsset(itemType, ext);
        Finding f = Find(r, "REF_TYPE_MISMATCH");
        Assert.Equal(fires, f != null);
        if (fires)
        {
            Assert.Equal(FindingSeverity.Warning, f.Severity);
            Assert.Equal(Id(0x811).ToString(), f.Examples[0].Id);
        }
    }

    [Fact]
    public void SculptField_AcceptsATextureOrAMesh_ButNotASound()
    {
        UUID asset = Id(0x820);
        string sculpt = "<Shape><SculptTexture><UUID>" + asset + "</UUID></SculptTexture></Shape>";
        Assert.False(Has(Run(Oar(new[] { Part(1, Alice, Alice, sculpt), Asset(asset, "_mesh.llmesh") })), "REF_TYPE_MISMATCH"));
        Assert.False(Has(Run(Oar(new[] { Part(1, Alice, Alice, sculpt), Asset(asset, "_texture.jp2") })), "REF_TYPE_MISMATCH"));
        Assert.True(Has(Run(Oar(new[] { Part(1, Alice, Alice, sculpt), Asset(asset, "_sound.ogg") })), "REF_TYPE_MISMATCH"));
    }

    [Fact]
    public void DuplicateAssetIds_Fire()
    {
        UUID a = Id(0x830);
        ArchiveReport r = Run(Oar(new[] { Asset(a, "_texture.jp2"), Asset(a, "_texture.jp2", "y"), Asset(Id(0x831), "_texture.jp2") }));
        Finding f = Find(r, "REF_DUPLICATE_ASSET");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.Equal(a.ToString(), f.Examples[0].Id);
        Assert.False(Has(Run(Oar(new[] { Asset(a, "_texture.jp2"), Asset(Id(0x831), "_texture.jp2") })), "REF_DUPLICATE_ASSET"));
    }

    [Fact]
    public void ObjectAssetsThatFailToParse_Fire()
    {
        UUID bad = Id(0x840);
        ArchiveReport r = Run(Oar(new[] { Asset(bad, "_object.xml", "<SceneObjectGroup>"), Asset(Id(0x841), "_object.xml", HandMade.OriginalObject(HandMade.PartXml("ok", Id(0x842), Alice, Alice, Alice))) }));
        Finding f = Find(r, "REF_OBJECT_UNPARSED");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.Equal(bad.ToString(), f.Examples[0].Id);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
    }

    [Fact]
    public void ReferencedButMissing_AndUnreferenced_AreReported()
    {
        UUID present = Id(0x850), missing = Id(0x851), stray = Id(0x852);
        ArchiveReport r = Run(Oar(new[]
        {
            Part(1, Alice, Alice, Inv(HandMade.TaskItem("A", Id(0x860), present, 7, Alice, Alice), HandMade.TaskItem("B", Id(0x861), missing, 7, Alice, Alice))),
            Asset(present, "_notecard.txt"), Asset(stray, "_texture.jp2")
        }));
        Finding m = Find(r, "REF_MISSING");
        Assert.NotNull(m);
        Assert.Equal(FindingSeverity.Notice, m.Severity);
        Assert.Equal(missing.ToString(), m.Examples.Single().Id);
        Finding u = Find(r, "REF_UNREFERENCED");
        Assert.NotNull(u);
        Assert.Equal(FindingSeverity.Info, u.Severity);
        Assert.Equal(stray.ToString(), u.Examples.Single().Id);

        ArchiveReport clean = Run(Oar(new[] { Part(1, Alice, Alice, Inv(HandMade.TaskItem("A", Id(0x860), present, 7, Alice, Alice))), Asset(present, "_notecard.txt") }));
        Assert.False(Has(clean, "REF_MISSING"));
        Assert.False(Has(clean, "REF_UNREFERENCED"));
    }

    [Fact]
    public void ArchiveSavedWithoutAssets_SaysSoInsteadOfListingMissingAssets()
    {
        ArchiveReport r = Run(Oar(new[] { Part(1, Alice, Alice, Inv(HandMade.TaskItem("A", Id(0x860), Id(0x851), 7, Alice, Alice))) }, control: OarControlWithoutAssets()));
        Assert.False(Has(r, "REF_MISSING"));
        Assert.Equal(FindingSeverity.Info, Find(r, "REF_NO_ASSETS").Severity);
    }
}

// ---------------------------------------------------------------------------------------- Part C.4: author lines

public class AuthorLineTests
{
    private static string Inv(params string[] items) => "<TaskInventory>" + string.Concat(items) + "</TaskInventory>";

    private static ArchiveReport Scripts(params (UUID creator, string body)[] scripts)
    {
        List<(string, byte[], char)> entries = new List<(string, byte[], char)>();
        for (int i = 0; i < scripts.Length; i++)
        {
            UUID asset = Id(0x900 + i);
            entries.Add(Part(i + 1, scripts[i].creator, Bob, Inv(HandMade.TaskItem("Script " + i, Id(0x950 + i), asset, 10, scripts[i].creator, Bob))));
            entries.Add(Asset(asset, "_script.lsl", scripts[i].body));
        }
        return Run(Oar(entries));
    }

    [Fact]
    public void NamesAreListedWithTheCreatorOfTheItemsThatHoldThem()
    {
        ArchiveReport r = Scripts((Alice, "// Copyright 2015 Jane Maker. All rights reserved.\ndefault { state_entry() {} }"),
                                  (Carol, "/* (c) 2020 jane maker */\n// Script by: Tom Coder"));
        Finding f = Find(r, "AUTHOR_LINES");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Info, f.Severity);
        FindingExample jane = f.Examples.Single(e => e.Name.Equals("Jane Maker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("in 2 assets", jane.Note);
        Assert.Contains(Alice.ToString(), jane.Note);
        Assert.Contains(Carol.ToString(), jane.Note);
        FindingExample tom = f.Examples.Single(e => e.Name == "Tom Coder");
        Assert.Contains("in 1 asset", tom.Note);
        Assert.Contains(Carol.ToString(), tom.Note);
        Assert.Equal("2", f.Numbers["assetsWithLines"]);
        Assert.Equal("2", f.Numbers["distinctNames"]);
    }

    [Theory]
    [InlineData("// Created by Ann Writer", "Ann Writer")]
    [InlineData("// Made by: Ann Writer", "Ann Writer")]
    [InlineData("// Author: Ann Writer", "Ann Writer")]
    [InlineData("// Licensed to Ann Writer", "Ann Writer")]
    [InlineData("// © 2019 Ann Writer", "Ann Writer")]
    [InlineData("// Copyright (c) 2010-2014 Ann Writer", "Ann Writer")]
    [InlineData("// Written by Ann Writer under the MIT licence", "Ann Writer")]
    public void EachAuthorForm_GivesTheName(string line, string name)
    {
        Finding f = Find(Scripts((Alice, line + "\ndefault{}")), "AUTHOR_LINES");
        Assert.NotNull(f);
        Assert.Contains(f.Examples, e => e.Name == name);
    }

    [Theory]
    [InlineData("// All rights reserved", "(all rights reserved)")]
    [InlineData("// Do not resell", "(do not resell)")]
    public void PhrasesThatNameNobody_AreListedAsThePhrase(string line, string key)
    {
        Finding f = Find(Scripts((Alice, line)), "AUTHOR_LINES");
        Assert.Contains(f.Examples, e => e.Name.StartsWith(key.Substring(0, key.Length - 1)));
    }

    [Theory]
    [InlineData("default { state_entry() { if (c) return; } }")]
    [InlineData("integer author_count = 3;")]
    [InlineData("// no names here")]
    public void CodeThatOnlyLooksSimilar_IsNotListed(string body)
    {
        Assert.False(Has(Scripts((Alice, body)), "AUTHOR_LINES"));
    }

    [Theory]
    [InlineData("THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS")]
    [InlineData("The above copyright notice and this permission notice shall be included")]
    [InlineData("IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE")]
    [InlineData("Copyrights are by the original owners")]
    public void LicenceBoilerplate_IsNotAnAuthorLine(string line)
    {
        Assert.False(Has(Scripts((Alice, line)), "AUTHOR_LINES"));
    }

    [Fact]
    public void NamesAreCutAtTheFirstClause_AndUrlsAndProseAreDropped()
    {
        Finding f = Find(Scripts(
            (Alice, "// Roth 2.0 Copyright 2018 by Shin Ingen, who can be found at https://example.org/shin"),
            (Alice, "// Original by Ann Writer: https://example.org/ann"),
            (Alice, "// Copyright 2009 MIT License"),
            (Alice, "// Created by me when I felt like it and then put your own designs on them or whatever")), "AUTHOR_LINES");
        Assert.Contains(f.Examples, e => e.Name == "Shin Ingen");
        Assert.Contains(f.Examples, e => e.Name == "Ann Writer");
        Assert.DoesNotContain(f.Examples, e => e.Name.Contains("http"));
        Assert.DoesNotContain(f.Examples, e => e.Name == "MIT");
        Assert.Contains(f.Examples, e => e.Name == "(copyright, no name on the line)");
        Assert.Contains(f.Examples, e => e.Name == "(created by, no name on the line)");
    }

    [Fact]
    public void Notecards_AreReadToo()
    {
        UUID asset = Id(0x990);
        ArchiveReport r = Run(Oar(new[]
        {
            Part(1, Alice, Bob, Inv(HandMade.TaskItem("Card", Id(0x991), asset, 7, Alice, Bob))),
            Asset(asset, "_notecard.txt", "Linden text version 2\n{\nLLEmbeddedItems version 1\n}\nText length 30\nCreated by Pat Notecard\n")
        }));
        Assert.Contains(Find(r, "AUTHOR_LINES").Examples, e => e.Name == "Pat Notecard");
    }
}

// ---------------------------------------------------------------------------------------- Part C.5: creator data

public class CreatorDataTests
{
    private const string Home = "http://grid.example.org:8002/";

    private static ArchiveReport Parts(params (UUID creator, string data)[] p) =>
        Run(Oar(p.Select((x, i) => Part(i + 1, x.creator, Bob, creatorData: x.data))));

    [Fact]
    public void OneIdWithDifferentNames_Fires()
    {
        ArchiveReport r = Parts((Alice, Home + ";Ann Builder"), (Alice, Home + ";Anna Builder"));
        Finding f = Find(r, "CREATOR_ID_MANY_NAMES");
        Assert.NotNull(f);
        Assert.Equal(Alice.ToString(), f.Examples.Single().Id);
        Assert.False(Has(Parts((Alice, Home + ";Ann Builder"), (Alice, Home + ";Ann Builder")), "CREATOR_ID_MANY_NAMES"));
    }

    [Fact]
    public void OneIdWithDifferentHomes_Fires()
    {
        Assert.True(Has(Parts((Alice, Home + ";Ann Builder"), (Alice, "http://other.example.org:9000/;Ann Builder")), "CREATOR_ID_MANY_NAMES"));
    }

    [Fact]
    public void OneNameAndHomeWithDifferentIds_Fires()
    {
        ArchiveReport r = Parts((Alice, Home + ";Ann Builder"), (Bob, Home + ";Ann Builder"));
        Finding f = Find(r, "CREATOR_NAME_MANY_IDS");
        Assert.NotNull(f);
        Assert.Contains(Alice.ToString(), f.Examples.Single().Id);
        Assert.Contains(Bob.ToString(), f.Examples.Single().Id);
        // the same name on a different home is a different person
        Assert.False(Has(Parts((Alice, Home + ";Ann Builder"), (Bob, "http://other.example.org:9000/;Ann Builder")), "CREATOR_NAME_MANY_IDS"));
    }

    [Theory]
    [InlineData(";", true)]                                     // no name
    [InlineData("not a url;Ann Builder", true)]                 // home is not a URL
    [InlineData("http://grid.example.org:8002/;Ann Builder", false)]
    [InlineData("Ann Builder", false)]                          // a bare name parses as a name
    public void CreatorDataThatDoesNotParse_Fires(string data, bool fires)
    {
        ArchiveReport r = Parts((Alice, data));
        Assert.Equal(fires, Has(r, "CREATOR_DATA_UNPARSED"));
    }

    [Fact]
    public void CreatorFieldThatIsNeitherUuidNorOspa_Fires()
    {
        ArchiveReport r = Run(IarArchive(new[] { IarItem(1, "Stuff", i => i.CreatorText = "someone") }));
        Assert.True(Has(r, "CREATOR_DATA_UNPARSED"));
        ArchiveReport ospa = Run(IarArchive(new[] { IarItem(1, "Stuff", i => i.CreatorText = "ospa:n=Ann Builder") }));
        Assert.False(Has(ospa, "CREATOR_DATA_UNPARSED"));
    }
}

// ---------------------------------------------------------------------------------------- Part C.6: archive integrity

public class IntegrityTests
{
    [Fact]
    public void EntriesUnderNoKnownPath_AreListedWithSizes()
    {
        ArchiveReport r = Run(Oar(new[] { Part(1, Alice, Alice), E("extras/readme.txt", new string('x', 1234)), E("misc/a.bin", "abc") }));
        Finding f = Find(r, "ARCH_UNKNOWN_PATH");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Notice, f.Severity);
        Assert.Equal(2, f.Count);
        Assert.Equal(1237, long.Parse(f.Numbers["bytes"]));
        Assert.Contains(f.Examples, e => e.Name == "extras/readme.txt" && e.Note.Contains("1,234 bytes"));
        Assert.False(Has(Run(Oar(new[] { Part(1, Alice, Alice) })), "ARCH_UNKNOWN_PATH"));
    }

    [Fact]
    public void APrefixTheOtherLoaderReads_IsUnknownToThisOne()
    {
        // objects/ in an IAR and inventory/ in an OAR are read by neither loader of that kind
        ArchiveReport iar = Run(IarArchive(new[] { IarItem(1), E("objects/x.xml", "<a/>") }));
        Assert.Contains(Find(iar, "ARCH_UNKNOWN_PATH").Examples, e => e.Name == "objects/x.xml");
        ArchiveReport oar = Run(Oar(new[] { Part(1, Alice, Alice), E("inventory/F__" + Id(5) + "/i.xml", "<a/>") }));
        Assert.Contains(Find(oar, "ARCH_UNKNOWN_PATH").Examples, e => e.Name.StartsWith("inventory/"));
    }

    [Fact]
    public void DuplicateEntryNames_Fire()
    {
        (string, byte[], char) p = Part(1, Alice, Alice);
        ArchiveReport r = Run(Oar(new[] { p, p, Part(2, Alice, Alice) }));
        Finding f = Find(r, "ARCH_DUPLICATE_ENTRY");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.Equal(p.Item1, f.Examples[0].Name);
        Assert.False(Has(Run(Oar(new[] { p, Part(2, Alice, Alice) })), "ARCH_DUPLICATE_ENTRY"));
    }

    [Fact]
    public void DirectoryEntriesHoldingData_Fire()
    {
        ArchiveReport r = Run(Oar(new[] { Part(1, Alice, Alice), E("objects/dir/", "hello", '5') }));
        Finding f = Find(r, "ARCH_DIRECTORY_DATA");
        Assert.NotNull(f);
        Assert.Equal(1, f.Count);
        Assert.False(Has(Run(Oar(new[] { Part(1, Alice, Alice), E("objects/dir/", "", '5') })), "ARCH_DIRECTORY_DATA"));
    }

    [Fact]
    public void LegacyPayloads_AreReportedAsFindings()
    {
        UUID a = Id(0xA00);
        byte[] thoosa = { 0x49, 0x54, 0x47, 0x01, 1, 2, 3, 4, 5, 6 };
        ArchiveReport r = Run(Oar(new[] { Part(1, Alice, Alice), E("assets/" + a + "_object.xml", thoosa) }));
        Finding f = Find(r, "ARCH_LEGACY_PAYLOAD");
        Assert.NotNull(f);
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Equal(1, f.Count);
        Assert.Contains("binary-object-thoosa-group", f.Title);
        Assert.Equal(a.ToString(), f.Examples[0].Id);
        Assert.False(Has(Run(Oar(new[] { Part(1, Alice, Alice) })), "ARCH_LEGACY_PAYLOAD"));
    }

    [Fact]
    public void PhloxScriptState_IsAFinding()
    {
        string xml = "<SceneObjectGroup>" + HandMade.PartXml("P", Id(1), Alice, Alice, Alice) + "<OtherParts /><PhloxGroupScriptStates><SavedScriptState /></PhloxGroupScriptStates></SceneObjectGroup>";
        ArchiveReport r = Run(Oar(new[] { E("objects/o__" + Id(1) + ".xml", xml) }));
        Assert.True(Has(r, "ARCH_LEGACY_PAYLOAD"));
    }
}

// ---------------------------------------------------------------------------------------- report, exit codes, tables

public class CheckReportTests
{
    [Fact]
    public void ChecksSection_FollowsTheCreatorAnalysis_WarningsFirst()
    {
        // a single creator (Warning from the analysis), an IAR-style order problem cannot be in an OAR, so use a permission Warning and a Notice
        List<(string, byte[], char)> e = new List<(string, byte[], char)>();
        for (int i = 1; i <= 25; i++)
            e.Add(Part(i, Alice, Bob, Masks(All, NoExport, 0, 0, Next)));
        e.Add(Asset(Id(0xB00), "_widget.xyz"));
        ArchiveReport r = Run(Oar(e));
        string text = TextReport.Build(r, false);

        int analysis = text.IndexOf("CREATOR ANALYSIS", StringComparison.Ordinal);
        int checks = text.IndexOf("CHECKS", StringComparison.Ordinal);
        int warning = text.IndexOf("  WARNING (", StringComparison.Ordinal);
        int notice = text.IndexOf("  NOTICE (", StringComparison.Ordinal);
        int info = text.IndexOf("  INFO (", StringComparison.Ordinal);
        Assert.True(analysis >= 0 && analysis < checks);
        Assert.True(checks < warning && warning < notice && notice < info, text);
        Assert.True(text.IndexOf("ARCHIVE\n", checks, StringComparison.Ordinal) > info || text.IndexOf("ARCHIVE\r\n", checks, StringComparison.Ordinal) > info);
    }

    [Fact]
    public void TheTextListsTwentyExamples_TheJsonCarriesAll()
    {
        List<(string, byte[], char)> e = new List<(string, byte[], char)> { IarItem(1) };
        for (int i = 0; i < 25; i++)
            e.Add(IarItem(100 + i, "Stuff", x => { x.AssetType = 24; x.InvType = -1; x.Asset = Id(0x7000); }));
        ArchiveReport r = Run(IarArchive(e));
        Finding f = Find(r, "IAR_LINK_DANGLING");
        Assert.Equal(25, f.Count);
        Assert.Equal(25, f.Examples.Count);

        string text = TextReport.Build(r, false);
        int shown = text.Split('\n').Count(l => l.Contains("points at") && l.TrimStart().StartsWith("- "));
        Assert.Equal(20, shown);
        Assert.Contains("... 5 more in the JSON", text);

        using JsonDocument doc = JsonDocument.Parse(ReportJson.Serialize(r));
        JsonElement finding = doc.RootElement.GetProperty("findings").EnumerateArray().Single(x => x.GetProperty("code").GetString() == "IAR_LINK_DANGLING");
        Assert.Equal(25, finding.GetProperty("examples").GetArrayLength());
        Assert.Equal("Warning", finding.GetProperty("severity").GetString());
        Assert.Equal(25, finding.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ExitCode_IsThreeOnlyForAWarning()
    {
        ArchiveReport clean = Run(Oar(new[] { Part(1, Alice, Alice), Part(2, Bob, Bob) }));
        Assert.Equal(0, clean.ExitCode);

        ArchiveReport notice = Run(Oar(new[] { Part(1, Alice, Alice), Asset(Id(0xB01), "_widget.xyz") }));
        Assert.True(Has(notice, "REF_UNKNOWN_EXTENSION"));
        Assert.DoesNotContain(notice.Findings, f => f.Severity == FindingSeverity.Warning);
        Assert.Equal(0, notice.ExitCode);

        ArchiveReport warning = Run(IarArchive(new[] { IarItem(1) }, controlFirst: false));
        Assert.Equal(3, warning.ExitCode);

        Assert.Equal(2, Run(new byte[] { 1, 2, 3 }).ExitCode);
    }

    [Fact]
    public void TheJsonCarriesEveryFinding_IncludingTheCreatorOnes()
    {
        ArchiveReport r = Run(Oar(Enumerable.Range(1, 25).Select(i => Part(i, Alice, Alice))));
        using JsonDocument doc = JsonDocument.Parse(ReportJson.Serialize(r));
        string[] codes = doc.RootElement.GetProperty("findings").EnumerateArray().Select(x => x.GetProperty("code").GetString()).ToArray();
        Assert.Contains("SINGLE_CREATOR", codes);
        Assert.Contains("MATCH_SAME_ACCOUNT", codes);
        Assert.Equal(r.Findings.Count, codes.Length);
        // the creator analysis keeps its own section in the text, so the Checks section does not repeat it
        Assert.DoesNotContain("SINGLE_CREATOR:", TextReport.Build(r, false).Split("CHECKS")[1]);
    }

    [Fact]
    public void StandardChecksCanBeSwitchedOff()
    {
        ArchiveReport r = Run(IarArchive(new[] { IarItem(1) }, controlFirst: false), new InspectorOptions { StandardChecks = false });
        Assert.Empty(r.Findings.Where(f => f.Source != "creator-analysis"));
    }

    [Fact]
    public void AFindingWithManyRecords_KeepsOnlyTheConfiguredNumberOfExamples()
    {
        List<(string, byte[], char)> e = new List<(string, byte[], char)> { IarItem(1) };
        for (int i = 0; i < 12; i++)
            e.Add(IarItem(100 + i, "Stuff", x => { x.AssetType = 24; x.InvType = -1; x.Asset = Id(0x7000); }));
        ArchiveReport r = Run(IarArchive(e), new InspectorOptions { MaxFindingExamples = 5 });
        Finding f = Find(r, "IAR_LINK_DANGLING");
        Assert.Equal(12, f.Count);
        Assert.Equal(5, f.Examples.Count);
        Assert.True(f.ExamplesTruncated);
    }
}

public class TypeTableTests
{
    [Fact]
    public void TheInventoryTypeTable_MatchesTheGridsPrivateTable()
    {
        FieldInfo field = typeof(SLUtil).GetField("MAPPINGS", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Array mappings = (Array)field.GetValue(null);

        Dictionary<int, HashSet<int>> grid = new Dictionary<int, HashSet<int>>();
        foreach (object m in mappings)
        {
            Type t = m.GetType();
            int asset = Convert.ToInt32(t.GetField("AssetType").GetValue(m));
            int inv = Convert.ToInt32(t.GetField("InventoryType").GetValue(m));
            if (!grid.TryGetValue(asset, out HashSet<int> set))
                grid[asset] = set = new HashSet<int>();
            set.Add(inv);
        }

        foreach (KeyValuePair<int, int[]> kv in TypeAgreement.Table)
        {
            Assert.True(grid.ContainsKey(kv.Key), "asset type " + kv.Key + " is not in SLUtil.MAPPINGS");
            if (kv.Key == (int)AssetType.Folder)
                continue;       // the grid lists every folder type under Folder; the check does not judge folders
            Assert.True(grid[kv.Key].SetEquals(kv.Value), "asset type " + kv.Key + ": grid " + string.Join(",", grid[kv.Key].OrderBy(x => x)) + " copy " + string.Join(",", kv.Value.OrderBy(x => x)));
        }

        // asset types the grid lists that the copy leaves out are links, folders and the unknown / OpenSim material entries
        int[] unjudged = { (int)AssetType.Unknown, (int)AssetType.Link, (int)AssetType.LinkFolder, (int)AssetType.OSMaterial, (int)AssetType.Folder };
        foreach (int asset in grid.Keys.Where(a => !TypeAgreement.Table.ContainsKey(a)))
            Assert.Contains(asset, unjudged);
    }

    [Fact]
    public void TheSystemFolderNames_MatchTheGridsDefaults()
    {
        // names the grid creates for its single-instance folders (XInventoryService.cs 162-211, InventoryFolderBase.ROOT_FOLDER_NAME)
        Assert.Equal(InventoryFolderBase.ROOT_FOLDER_NAME, TypeAgreement.SystemFolderNames.Single(kv => kv.Value == "Root").Key);
        foreach (string name in new[] { "Current Outfit", "Trash", "Lost And Found", "Favorites", "Photo Album" })
            Assert.True(TypeAgreement.SystemFolderNames.ContainsKey(name), name);
    }

    [Fact]
    public void LinksAreNotJudgedByTheTable()
    {
        Assert.True(TypeAgreement.InventoryTypeAgrees((int)AssetType.Link, 12345));
        Assert.True(TypeAgreement.InventoryTypeAgrees(9999, 1));
    }
}
