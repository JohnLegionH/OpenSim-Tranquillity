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

/// <summary>oar-convert --relink-from: each object found in a reference archive gets the reference's link order back.</summary>
public class LinkRestoreTests
{
    private static KPart P(string name, int link, float x = 0, float y = 0, float z = 0) => new KPart { Name = name, Link = link, X = x, Y = y, Z = z };

    private static ObjectReport Read(string path) => ObjectReader.Read(path, new ReadOptions { Strict = true });

    /// <summary>The reference table: root, top, two legs, numbered 1 to 4 in file order.</summary>
    private static KPart[] ReferenceTable() => new[] { P("Table", 1), P("Top", 2, 0, 0, 1), P("Leg1", 3, 1, 1, -1), P("Leg2", 4, -1, 1, -1) };

    /// <summary>The same table built in another order: root, leg 2, top, leg 1.</summary>
    private static KPart[] ScrambledTable() => new[] { P("Table", 1), P("Leg2", 2, -1, 1, -1), P("Top", 3, 0, 0, 1), P("Leg1", 4, 1, 1, -1) };

    private static string[] Names(ObjectEntry o) => o.Parts.Select(p => p.Name).ToArray();
    private static int[] Links(ObjectEntry o) => o.Parts.Select(p => p.LinkNum).ToArray();

    private sealed class Setup
    {
        public string Folder, Reference, Source, Dest;
    }

    private static Setup Prepare(byte[] reference, byte[] source, string sourceName = "build.oar", string referenceName = "reference.oar")
    {
        string folder = NewFolder();
        return new Setup
        {
            Folder = folder,
            Reference = WriteInput(folder, referenceName, reference),
            Source = WriteInput(folder, sourceName, source),
            Dest = Path.Combine(folder, "relinked-" + sourceName)
        };
    }

    private static RelinkResult Relink(Setup s)
    {
        RelinkResult result = LinkRestore.Run(s.Source, s.Reference, s.Dest);
        Assert.True(File.Exists(s.Dest), "the relinked archive was not written");
        return result;
    }

    // ------------------------------------------------------------------ the order comes back

    [Fact]
    public void AScrambledLinksetGetsTheReferencesPartOrderAndNumbersBack()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable()));
        RelinkResult result = Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(o));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Links(o));
        Assert.True(o.LinkOrderOk, o.LinkOrderNote);
        Assert.Equal(1, result.ObjectsChanged);
        Assert.Equal(1, result.EntriesChanged);
    }

    [Fact]
    public void TheOutputAndTheReferenceThenCompareAsIdentical()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable()));
        Relink(s);

        CompareResult compare = ReportCompare.Compare(Read(s.Reference), Read(s.Dest));
        Assert.Empty(compare.OnlyInReference);
        Assert.Empty(compare.OnlyInArchive);
        Assert.Equal(1, compare.Matched);
        Assert.True(compare.Identical, string.Join("; ", compare.Objects.SelectMany(o => o.Differences).Select(d => d.Part + " " + d.Field)));
    }

    [Fact]
    public void LinkNumbersThatDisagreeWithTheFileOrderAreMadeCoherent()
    {
        KPart[] tangled = { P("Table", 1), P("Leg2", 3, -1, 1, -1), P("Top", 2, 0, 0, 1), P("Leg1", 4, 1, 1, -1) };
        Setup s = Prepare(Oar(ReferenceTable()), Oar(tangled));
        Assert.False(Read(s.Source).Objects.One().LinkOrderOk);
        Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(o));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Links(o));
        Assert.True(o.LinkOrderOk, o.LinkOrderNote);
    }

    [Fact]
    public void TheReferencesOrderIsItsLinkNumbersNotItsFilePositions()
    {
        // the reference lists Leg2 before Top in the file but numbers Top 2 and Leg2 4
        KPart[] reference = { P("Table", 1), P("Leg2", 4, -1, 1, -1), P("Top", 2, 0, 0, 1), P("Leg1", 3, 1, 1, -1) };
        Setup s = Prepare(Oar(reference), Oar(ScrambledTable()));
        Relink(s);

        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(Read(s.Dest).Objects.One()));
    }

    [Fact]
    public void TheRootStaysTheRootWhateverTheReferenceSays()
    {
        // the reference stores the root's link number as 3, so by number alone the root would come last
        KPart[] reference = { P("Table", 3), P("Top", 1, 0, 0, 1), P("Leg1", 2, 1, 1, -1) };
        KPart[] source = { P("Table", 1), P("Leg1", 2, 1, 1, -1), P("Top", 3, 0, 0, 1) };
        Setup s = Prepare(Oar(reference), Oar(source));
        Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.True(o.Parts[0].IsRoot);
        Assert.Equal("Table", o.Parts[0].Name);
        Assert.Equal(new[] { "Table", "Top", "Leg1" }, Names(o));
        Assert.Equal(new[] { 1, 2, 3 }, Links(o));
    }

    [Fact]
    public void PartsAreMatchedByNameShapeAndPositionSoPartsSharingANameAreToldApart()
    {
        KPart[] reference = { P("Chair", 1), P("Leg", 2, 1, 0, 0), P("Leg", 3, -1, 0, 0), P("Leg", 4, 0, 1, 0) };
        KPart[] source = { P("Chair", 1), P("Leg", 2, 0, 1, 0), P("Leg", 3, 1, 0, 0), P("Leg", 4, -1, 0, 0) };
        Setup s = Prepare(Oar(reference), Oar(source));
        RelinkResult result = Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.Equal(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0) }, o.Parts.Select(p => p.Position).ToArray());
        Assert.Empty(result.UnmatchedParts);
    }

    [Fact]
    public void ADifferentShapeIsNotTheSamePart()
    {
        KPart sphere = P("Ball", 3, 1, 0, 0);
        sphere.ProfileCurve = 3;
        KPart[] reference = { P("Chair", 1), P("Ball", 2, 1, 0, 0), P("Seat", 3, 0, 0, 1) };
        KPart[] source = { P("Chair", 1), P("Seat", 2, 0, 0, 1), sphere };
        Setup s = Prepare(Oar(reference), Oar(source));
        RelinkResult result = Relink(s);

        // the ball in the source has another shape than the ball in the reference: it has no match and stays after the matched seat
        Assert.Equal(new[] { "Chair", "Seat", "Ball" }, Names(Read(s.Dest).Objects.One()));
        Assert.Contains(result.UnmatchedParts, u => u.Contains("Ball"));
    }

    [Fact]
    public void APartTheReferenceDoesNotHoldKeepsItsOrderAfterTheMatchedPartsAndIsListed()
    {
        KPart[] source =
        {
            P("Table", 1), P("Handle", 2, 0, 2, 0), P("Leg2", 3, -1, 1, -1), P("Knob", 4, 0, 3, 0), P("Top", 5, 0, 0, 1), P("Leg1", 6, 1, 1, -1)
        };
        Setup s = Prepare(Oar(ReferenceTable()), Oar(source));
        RelinkResult result = Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2", "Handle", "Knob" }, Names(o));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, Links(o));
        Assert.Equal(2, result.UnmatchedParts.Count);
        Assert.Contains(result.UnmatchedParts, u => u.Contains("Handle") && u.Contains("Table"));
        Assert.Contains(result.UnmatchedParts, u => u.Contains("Knob"));
    }

    [Fact]
    public void AnObjectTheReferenceDoesNotHoldIsLeftUnchangedAndListed()
    {
        KPart[] lamp = { P("Lamp", 1), P("Shade", 3, 0, 0, 1), P("Base", 2, 0, 0, -1) };
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable(), lamp));
        RelinkResult result = Relink(s);

        var before = Entries(s.Source);
        var after = Entries(s.Dest);
        int lampAt = before.FindIndex(e => e.name.Contains("Lamp"));
        Assert.True(lampAt >= 0);
        Assert.Equal(before[lampAt].data, after[lampAt].data);
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(Read(s.Dest).Objects.Single(o => o.Name == "Table")));
        Assert.Contains(result.UnmatchedObjects, u => u.Contains("Lamp"));
        Assert.DoesNotContain(result.UnmatchedObjects, u => u.Contains("Table"));
    }

    [Fact]
    public void AnObjectAlreadyInTheReferencesOrderIsNotTouched()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ReferenceTable()));
        RelinkResult result = Relink(s);

        Assert.Equal(0, result.ObjectsChanged);
        Assert.Equal(0, result.EntriesChanged);
        Assert.Equal(Entries(s.Source).Select(e => e.data).ToArray(), Entries(s.Dest).Select(e => e.data).ToArray());
    }

    [Fact]
    public void AnObjectOfOnePartIsNotTouched()
    {
        Setup s = Prepare(Oar(new[] { P("Rock", 0) }), Oar(new[] { P("Rock", 1) }));
        RelinkResult result = Relink(s);

        Assert.Equal(0, result.ObjectsChanged);
        Assert.Equal(Entries(s.Source).Select(e => e.data).ToArray(), Entries(s.Dest).Select(e => e.data).ToArray());
    }

    [Fact]
    public void ObjectsAreMatchedAsCompareMatchesThemSoTwoObjectsOfOneNameAreToldApartByTheirParts()
    {
        KPart[] big = { P("Crate", 1), P("Lid", 2, 0, 0, 1), P("Side", 3, 1, 0, 0), P("Back", 4, 0, 1, 0) };
        KPart[] bigScrambled = { P("Crate", 1), P("Back", 2, 0, 1, 0), P("Lid", 3, 0, 0, 1), P("Side", 4, 1, 0, 0) };
        KPart[] small = { P("Crate", 1), P("Lid", 2, 0, 0, 1) };
        Setup s = Prepare(Oar(big, small), Oar(small, bigScrambled));
        Relink(s);

        ObjectReport r = Read(s.Dest);
        Assert.Equal(2, r.Objects.Count);
        ObjectEntry fixedBig = r.Objects.Single(o => o.Parts.Count == 4);
        Assert.Equal(new[] { "Crate", "Lid", "Side", "Back" }, Names(fixedBig));
    }

    // ------------------------------------------------------------------ nothing else changes

    [Fact]
    public void ComparingTheInputWithTheOutputShowsOnlyLinkOrderChanges()
    {
        KPart[] source = ScrambledTable();
        source[2].Items.Add(new KItem { Name = "Notes", Creator = Bob, OwnerMask = M | C });
        source[1].OwnerMask = C;
        Setup s = Prepare(Oar(ReferenceTable()), Oar(source));
        Relink(s);

        CompareResult compare = ReportCompare.Compare(Read(s.Source), Read(s.Dest));

        Assert.False(compare.Identical);
        Assert.Empty(compare.OnlyInReference);
        Assert.Empty(compare.OnlyInArchive);
        Assert.All(compare.Objects.SelectMany(o => o.Differences), d => Assert.Equal("link order", d.Area));
        Assert.Contains(compare.Objects.SelectMany(o => o.Differences), d => d.Part == "Leg2" && d.Field == "link number");
    }

    [Fact]
    public void WhitespaceBetweenPartsIsKeptWhenTheyAreMoved()
    {
        KPart[] parts = ScrambledTable();
        string xml = Xml2Group(parts).Replace("</SceneObjectPart><SceneObjectPart", "</SceneObjectPart>\n      <SceneObjectPart");
        Setup s = Prepare(Oar(ReferenceTable()), Oar(new[] { xml }, new[] { "Table" }));
        Relink(s);

        string after = Text(Entries(s.Dest), "objects/");
        Assert.Equal(2, after.Split("</SceneObjectPart>\n      <SceneObjectPart").Length - 1);
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(Read(s.Dest).Objects.One()));
    }

    [Fact]
    public void ThePartsOfAnObjectAssetInTheOriginalFormWithPartWrappersAreMovedWithTheirWrappers()
    {
        string asset = OriginalGroup(ScrambledTable());
        byte[] iar = Iar(new IarItem { Name = "Table box", AssetType = 6, ObjectXml = asset });
        Setup s = Prepare(Oar(ReferenceTable()), iar, "gift.iar");
        RelinkResult result = Relink(s);

        ObjectEntry o = Read(s.Dest).Objects.One();
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(o));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Links(o));
        Assert.Equal(1, result.ObjectsChanged);
        // the inventory item files are copied as they are
        var before = Entries(s.Source);
        var after = Entries(s.Dest);
        Assert.Equal(before.Where(e => e.name.StartsWith("inventory/")).Select(e => e.data).ToArray(), after.Where(e => e.name.StartsWith("inventory/")).Select(e => e.data).ToArray());
    }

    [Fact]
    public void EveryLinksetOfACoalescedObjectIsRestoredFromItsOwnReference()
    {
        KPart[] otherRef = { P("Shelf", 1), P("Board", 2, 0, 0, 1), P("Bracket", 3, 1, 0, 0) };
        KPart[] otherScrambled = { P("Shelf", 1), P("Bracket", 2, 1, 0, 0), P("Board", 3, 0, 0, 1) };
        string coalesced = Coalesced(0, 0, 0, (0f, 0f, 0f, ScrambledTable()), (2f, 0f, 0f, otherScrambled));
        Setup s = Prepare(Oar(ReferenceTable(), otherRef), Iar(new IarItem { Name = "Both", AssetType = 6, ObjectXml = coalesced }), "gift.iar");
        RelinkResult result = Relink(s);

        ObjectReport r = Read(s.Dest);
        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(r.Objects.Single(o => o.Name == "Table")));
        Assert.Equal(new[] { "Shelf", "Board", "Bracket" }, Names(r.Objects.Single(o => o.Name == "Shelf")));
        Assert.Equal(2, result.ObjectsChanged);
        Assert.Equal(1, result.EntriesChanged);
    }

    // ------------------------------------------------------------------ scripts that use link numbers

    private static byte[] OarWithScripts(KPart[] parts, params (UUID asset, string text)[] scripts)
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", OarControl()) };
        entries.Add(("objects/" + parts[0].Name + "_10-20-30__" + UUID.Random() + ".xml", Utf8(Xml2Group(parts))));
        foreach ((UUID asset, string text) in scripts)
            entries.Add(("assets/" + asset + "_script.lsl", Utf8(text)));
        return Tar(entries);
    }

    private static KItem Script(string name, UUID asset) => new KItem { Name = name, AssetType = 10, AssetId = asset };

    [Fact]
    public void AScriptThatAddressesALinkNumberWhoseHolderChangedIsListedAndOneThatDoesNotIsNot()
    {
        UUID aById = UUID.Random(), cById = UUID.Random(), dById = UUID.Random(), eById = UUID.Random(), bById = UUID.Random();
        KPart[] source = ScrambledTable();
        source[0].Items.Add(Script("paints link three", aById));            // link 3 was Top and is now Leg1
        source[0].Items.Add(Script("counts prims", cById));                 // depends on the numbering as a whole
        source[0].Items.Add(Script("paints the root", dById));              // link 1 is still the root
        source[0].Items.Add(Script("uses LINK_ROOT", eById));
        source[2].Items.Add(Script("paints everything", bById));            // LINK_SET
        byte[] archive = OarWithScripts(source,
            (aById, "default { touch_start(integer n) { llSetLinkColor(3, <1,0,0>, ALL_SIDES); } }"),
            (cById, "default { touch_start(integer n) { llOwnerSay((string)llGetNumberOfPrims()); } }"),
            (dById, "default { touch_start(integer n) { llSetLinkColor(1, <1,0,0>, ALL_SIDES); } }"),
            (eById, "default { touch_start(integer n) { llSetLinkColor(LINK_ROOT, <1,0,0>, ALL_SIDES); } }"),
            (bById, "default { touch_start(integer n) { llSetLinkColor(LINK_SET, <1,0,0>, ALL_SIDES); } }"));
        Setup s = Prepare(Oar(ReferenceTable()), archive);
        RelinkResult result = Relink(s);

        string[] listed = result.Scripts.Select(x => x.Script).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "counts prims", "paints link three" }, listed);
        ScriptLinkChange three = result.Scripts.Single(x => x.Script == "paints link three");
        Assert.Equal("Table", three.Part);
        Assert.Equal("Table", three.Object);
        Assert.Contains(three.Uses, u => u.Contains("llSetLinkColor") && u.Contains("3"));

        string text = result.ToText();
        Assert.Contains("paints link three", text);
        Assert.DoesNotContain("paints the root", text);
    }

    // One script per way of addressing a part by link number. Each holds its call on line 4, so the report's line can be checked.
    // In the scrambled table link 2 was Leg2 and is now Top, link 3 was Top and is now Leg1, link 4 was Leg1 and is now Leg2.
    private static string OnLine4(string statement) => "default\n{\n    touch_start(integer n)\n    {\n        " + statement + "\n    }\n}\n";

    public static readonly object[][] AddressedByNumber =
    {
        new object[] { "llSetLinkPrimitiveParamsFast(3, [PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0]);", "llSetLinkPrimitiveParamsFast", "link 3 was part \"Top\" and is now part \"Leg1\"" },
        new object[] { "llSetLinkPrimitiveParams(4, [PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0]);", "llSetLinkPrimitiveParams", "link 4 was part \"Leg1\" and is now part \"Leg2\"" },
        new object[] { "llMessageLinked(2, 7, \"open\", NULL_KEY);", "llMessageLinked", "link 2 was part \"Leg2\" and is now part \"Top\"" },
        new object[] { "llOwnerSay(llGetLinkName(3));", "llGetLinkName", "link 3 was part \"Top\" and is now part \"Leg1\"" },
        new object[] { "llOwnerSay((string)llGetLinkKey(4));", "llGetLinkKey", "link 4 was part \"Leg1\" and is now part \"Leg2\"" },
        new object[] { "llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_LINK_TARGET, 3, PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0]);", "llSetLinkPrimitiveParamsFast", "link 3 was part \"Top\" and is now part \"Leg1\"" },
        new object[] { "llOwnerSay((string)llGetLinkNumber());", "llGetLinkNumber", "depends on how the parts are numbered" },
        new object[] { "llOwnerSay((string)llGetNumberOfPrims());", "llGetNumberOfPrims", "depends on how the parts are numbered" },
        new object[] { "llOwnerSay(llGetLinkName(n));", "llGetLinkName", "a link number the scan cannot work out" },
    };

    [Theory]
    [MemberData(nameof(AddressedByNumber))]
    public void EveryWayOfAddressingAPartByLinkNumberIsListedWithItsLineAndWhatChanged(string statement, string function, string what)
    {
        UUID id = UUID.Random();
        KPart[] source = ScrambledTable();
        source[0].Items.Add(Script("by number", id));
        Setup s = Prepare(Oar(ReferenceTable()), OarWithScripts(source, (id, OnLine4(statement))));

        RelinkResult result = Relink(s);

        ScriptLinkChange change = Assert.Single(result.Scripts);
        Assert.Equal("by number", change.Script);
        Assert.Equal("Table", change.Part);
        Assert.Contains(change.Uses, u => u == "line 4: " + function + " - " + what);
        Assert.Contains("      line 4: " + function + " - " + what, result.ToText());
        Assert.Contains("line 4: " + function + " - " + what, result.ToJson());
    }

    [Fact]
    public void ALinkMessageHandlerThatReadsTheSendersLinkNumberIsListedAtTheHandlersLine()
    {
        UUID id = UUID.Random();
        KPart[] source = ScrambledTable();
        source[0].Items.Add(Script("listens", id));
        string text = "default\n{\n    touch_start(integer n) { }\n    link_message(integer sender, integer num, string str, key k)\n    {\n        llOwnerSay((string)sender);\n    }\n}\n";
        Setup s = Prepare(Oar(ReferenceTable()), OarWithScripts(source, (id, text)));

        ScriptLinkChange change = Assert.Single(Relink(s).Scripts);
        Assert.Contains(change.Uses, u => u == "line 4: link_message - reads the sender's link number");
    }

    [Fact]
    public void AScriptInAChildPartIsListedWithTheNameOfThePartThatHoldsIt()
    {
        UUID id = UUID.Random();
        KPart[] source = ScrambledTable();
        source[2].Items.Add(Script("in the top", id));                         // "Top"
        Setup s = Prepare(Oar(ReferenceTable()), OarWithScripts(source, (id, OnLine4("llSetLinkColor(3, <1,0,0>, ALL_SIDES);"))));

        ScriptLinkChange change = Assert.Single(Relink(s).Scripts);
        Assert.Equal("Top", change.Part);
        Assert.Equal("in the top", change.Script);
    }

    [Theory]
    [InlineData("llSetLinkColor(LINK_SET, <1,0,0>, ALL_SIDES);")]
    [InlineData("llSetLinkColor(LINK_ALL_OTHERS, <1,0,0>, ALL_SIDES);")]
    [InlineData("llSetLinkColor(LINK_ALL_CHILDREN, <1,0,0>, ALL_SIDES);")]
    [InlineData("llSetLinkColor(LINK_THIS, <1,0,0>, ALL_SIDES);")]
    [InlineData("llSetLinkColor(1, <1,0,0>, ALL_SIDES);")]
    [InlineData("llMessageLinked(LINK_ALL_OTHERS, 1, \"ping\", NULL_KEY);")]
    [InlineData("llMessageLinked(LINK_SET, 1, \"ping\", NULL_KEY);")]
    [InlineData("llOwnerSay(\"no link at all\");")]
    public void AScriptThatNamesNoPartByNumberIsNotListedWhateverIsRenumbered(string statement)
    {
        UUID id = UUID.Random();
        KPart[] source = ScrambledTable();
        source[0].Items.Add(Script("harmless", id));
        Setup s = Prepare(Oar(ReferenceTable()), OarWithScripts(source, (id, OnLine4(statement))));

        RelinkResult result = Relink(s);

        Assert.Equal(3, result.Objects.Single().Parts.Count(p => p.Changed));
        Assert.Empty(result.Scripts);
    }

    [Fact]
    public void AnLuaScriptIsListedAsOneTheScanCannotReadWhenPartsAreRenumberedAndNotOtherwise()
    {
        UUID lua = UUID.Random(), lua2 = UUID.Random(), lsl = UUID.Random();
        KPart[] source = ScrambledTable();
        source[0].Items.Add(Script("lua with marker", lua));
        source[1].Items.Add(Script("lua with dashes", lua2));
        source[0].Items.Add(Script("lsl with marker", lsl));
        byte[] archive = OarWithScripts(source,
            (lua, "//SLua:slua\nll.SetLinkColor(3, vector(1, 0, 0), ALL_SIDES)\n"),
            (lua2, "--!slua\nll.MessageLinked(2, 1, 'x', '')\n"),
            (lsl, "//SLua:LSL\ndefault\n{\n    touch_start(integer n) { llOwnerSay(\"hello\"); }\n}\n"));
        Setup s = Prepare(Oar(ReferenceTable()), archive);

        RelinkResult result = Relink(s);

        Assert.Equal(new[] { "lua with dashes", "lua with marker" }, result.Scripts.Select(x => x.Script).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(result.Scripts, x => Assert.Contains(x.Uses, u => u.StartsWith("not read: ") && u.Contains("Lua")));
        Assert.Equal("Leg2", result.Scripts.Single(x => x.Script == "lua with dashes").Part);
        Assert.Contains("not read: ", result.ToText());

        // the same scripts in an object whose parts were already in the reference's order: nothing to list
        UUID again = UUID.Random();
        KPart[] same = ReferenceTable();
        same[0].Items.Add(Script("lua with marker", again));
        Setup unchanged = Prepare(Oar(ReferenceTable()), OarWithScripts(same, (again, "//SLua:slua\nll.SetLinkColor(3, vector(1, 0, 0), ALL_SIDES)\n")));
        Assert.Empty(Relink(unchanged).Scripts);
    }

    [Fact]
    public void NoScriptIsListedForAnObjectWhosePartsWereNotRenumbered()
    {
        UUID id = UUID.Random();
        KPart[] source = ReferenceTable();
        source[0].Items.Add(Script("paints link three", id));
        byte[] archive = OarWithScripts(source, (id, "default { touch_start(integer n) { llSetLinkColor(3, <1,0,0>, ALL_SIDES); } }"));
        Setup s = Prepare(Oar(ReferenceTable()), archive);

        Assert.Empty(Relink(s).Scripts);
    }

    // ------------------------------------------------------------------ the report

    [Fact]
    public void TheReportShowsEveryPartsLinkNumberAndFilePositionBeforeAndAfter()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable()));
        RelinkResult result = Relink(s);

        ObjectRelink o = result.Objects.One();
        Assert.Equal("Table", o.Name);
        Assert.StartsWith("objects/", o.Entry);
        PartRelink leg2 = o.Parts.Single(p => p.Name == "Leg2");
        Assert.Equal(2, leg2.LinkBefore);
        Assert.Equal(4, leg2.LinkAfter);
        Assert.Equal(2, leg2.FilePositionBefore);
        Assert.Equal(4, leg2.FilePositionAfter);
        Assert.True(leg2.Matched);

        string text = result.ToText();
        Assert.Contains("OBJECT \"Table\"", text);
        Assert.Contains("\"Leg2\": link 2 -> 4", text);
        Assert.Contains("file position 2 -> 4", text);
        using System.Text.Json.JsonDocument json = Json(result.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("objectsChanged").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("objects")[0].GetProperty("parts").GetArrayLength());
    }

    [Fact]
    public void TheReportListsWhatCouldNotBeMatched()
    {
        KPart[] lamp = { P("Lamp", 1), P("Shade", 2, 0, 0, 1) };
        KPart[] source = ScrambledTable().Concat(new[] { P("Handle", 5, 0, 2, 0) }).ToArray();
        Setup s = Prepare(Oar(ReferenceTable()), Oar(source, lamp));
        string text = Relink(s).ToText();

        Assert.Contains("NOT MATCHED", text);
        Assert.Contains("\"Lamp\"", text);
        Assert.Contains("\"Handle\"", text);
    }

    // ------------------------------------------------------------------ the files

    [Fact]
    public void TheSameRunTwiceGivesTheSameBytesAndRunningItOnItsOutputChangesNothing()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable()));
        Relink(s);
        string second = Path.Combine(s.Folder, "second.oar");
        LinkRestore.Run(s.Source, s.Reference, second);
        Assert.Equal(File.ReadAllBytes(s.Dest), File.ReadAllBytes(second));

        string third = Path.Combine(s.Folder, "third.oar");
        RelinkResult again = LinkRestore.Run(s.Dest, s.Reference, third);
        Assert.True(File.Exists(third));
        Assert.Equal(0, again.ObjectsChanged);
        Assert.Equal(File.ReadAllBytes(s.Dest), File.ReadAllBytes(third));
    }

    [Fact]
    public void NeitherTheInputNorTheReferenceIsEverChangedOrOverwritten()
    {
        Setup s = Prepare(Oar(ReferenceTable()), Oar(ScrambledTable()));
        byte[] input = File.ReadAllBytes(s.Source), reference = File.ReadAllBytes(s.Reference);

        Assert.ThrowsAny<Exception>(() => LinkRestore.Run(s.Source, s.Reference, s.Source));
        Assert.Equal(input, File.ReadAllBytes(s.Source));

        Relink(s);
        Assert.Equal(input, File.ReadAllBytes(s.Source));
        Assert.Equal(reference, File.ReadAllBytes(s.Reference));

        byte[] first = File.ReadAllBytes(s.Dest);
        Assert.Throws<IOException>(() => LinkRestore.Run(s.Source, s.Reference, s.Dest));
        Assert.Equal(first, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void AReferenceThatIsNotAnArchiveIsRefusedAndNothingIsWritten()
    {
        string folder = NewFolder();
        string source = WriteInput(folder, "build.oar", Oar(ScrambledTable()));
        string notes = WriteInput(folder, "notes.oar", Utf8("this is not an archive at all, it is just text that is long enough to fill a header block or two of nothing"));
        string dest = Path.Combine(folder, "out.oar");

        Assert.Throws<InvalidDataException>(() => LinkRestore.Run(source, notes, dest));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void ObjectsOfAnotherKindOfArchiveAreMatchedToo()
    {
        // the reference is an IAR holding the table; the archive to fix is an OAR
        byte[] referenceIar = Iar(new IarItem { Name = "Table box", AssetType = 6, ObjectXml = OriginalGroup(ReferenceTable()) });
        Setup s = Prepare(referenceIar, Oar(ScrambledTable()), "build.oar", "reference.iar");
        Relink(s);

        Assert.Equal(new[] { "Table", "Top", "Leg1", "Leg2" }, Names(Read(s.Dest).Objects.One()));
    }
}
