/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;
using static OpenSim.Archive.Inspection.Tests.HandMade;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>The tar reader and the limits that protect against hostile archives.</summary>
public class ReaderTests
{
    private static (string, byte[]) SimpleObject(string file, string name = "Thing") =>
        ObjectFile(file, Xml2Object(PartXml(name, UUID.Random(), Alice, Alice, Alice)));

    [Fact]
    public void NamesOverOneHundredCharacters_AreReadWholeFromTheRepoWriter()
    {
        string longFolder = string.Join("/", Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 40) + "__" + UUID.Random()));
        UUID id = UUID.Random();
        string path = "inventory/" + longFolder + "/Long item name__" + id + ".xml";
        Assert.True(path.Length > 200);

        byte[] iar = Tar(new[]
        {
            ("archive.xml", IarControl("0.3")),
            (path, Utf8(IarItemXml("Long item name", id, Alice, Bob, UUID.Random(), 0, 1600000000, creatorUuidText: "ospa:n=Ann Builder")))
        });

        ArchiveReport r = Inspect(iar);
        Assert.Equal(InspectionStatus.Complete, r.Status);
        ItemRecord item = Assert.Single(r.Items);
        Assert.Equal(path, item.Entry);
        Assert.Equal("Long item name", item.Name);
        Assert.Equal(4, r.FolderCount);
    }

    [Fact]
    public void ControlFileNotFirst_IsFoundAndNoted()
    {
        byte[] oar = Tar(new[]
        {
            SimpleObject("A_1-1-1__x"),
            ("archive.xml", OarControl("0.8"))
        });
        ArchiveReport r = Inspect(oar);
        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.True(r.Archive.ControlFileFound);
        Assert.False(r.Archive.ControlFileFirst);
        Assert.Equal(ArchiveKind.Oar, r.Archive.Kind);
        Assert.Equal(1, r.PartCount);
    }

    [Fact]
    public void PlainTar_IsAcceptedWithoutGzip()
    {
        byte[] plain = Tar(new[] { ("archive.xml", OarControl()), SimpleObject("A_1-1-1__x") }, gzip: false);
        Assert.NotEqual(0x1f, plain[0]);
        ArchiveReport r = Inspect(plain);
        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.False(r.Archive.Compressed);
        Assert.Equal(1, r.PartCount);

        ArchiveReport gz = Inspect(Tar(new[] { ("archive.xml", OarControl()), SimpleObject("A_1-1-1__x") }, gzip: true));
        Assert.True(gz.Archive.Compressed);
        Assert.Equal(1, gz.PartCount);
    }

    [Fact]
    public void TruncatedFile_GivesPartialReportAndTheEntryWhereItStopped()
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", OarControl()) };
        for (int i = 0; i < 40; i++)
            entries.Add(SimpleObject("Obj" + i + "_1-1-1__" + i, "Thing " + i));
        byte[] plain = Tar(entries, gzip: false);

        // cut inside the data of an entry
        byte[] cutPlain = plain.Take(512 * 30 + 100).ToArray();
        ArchiveReport r = Inspect(cutPlain);
        Assert.Equal(InspectionStatus.Partial, r.Status);
        Assert.NotNull(r.StoppedAt);
        Assert.StartsWith("objects/", r.StoppedAt);
        Assert.True(r.PartCount > 0 && r.PartCount < 40);
        Assert.Equal(1, r.ExitCode);

        // and a gzip file cut short
        byte[] gz = Tar(entries, gzip: true);
        ArchiveReport cutGz = Inspect(gz.Take(gz.Length / 2).ToArray());
        Assert.Equal(InspectionStatus.Partial, cutGz.Status);
        Assert.NotNull(cutGz.StopReason);
        Assert.True(cutGz.PartCount > 0 && cutGz.PartCount < 40);
    }

    [Fact]
    public void CorruptXmlEntry_IsReportedAndTheRestIsStillRead()
    {
        byte[] oar = Oar(new[]
        {
            SimpleObject("Good1_1-1-1__a", "Good 1"),
            ObjectFile("Bad_1-1-1__b", "<SceneObjectGroup><SceneObjectPart><Name>oops"),
            SimpleObject("Good2_1-1-1__c", "Good 2")
        });
        ArchiveReport r = Inspect(oar);
        Assert.Equal(InspectionStatus.Partial, r.Status);
        Assert.Equal(2, r.PartCount);
        Assert.Contains(r.Problems, p => p.Entry == "objects/Bad_1-1-1__b.xml" && p.Message.Contains("not valid"));
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void XmlEntryWithADtd_IsRefusedAndNothingIsResolved()
    {
        string xml = "<?xml version=\"1.0\"?><!DOCTYPE g [<!ENTITY secret SYSTEM \"file:///does/not/exist\">]><SceneObjectGroup>" +
                     PartXml("&secret;", UUID.Random(), Alice, Alice, Alice) + "</SceneObjectGroup>";
        ArchiveReport r = Inspect(Oar(new[] { ObjectFile("Dtd_1-1-1__a", xml), SimpleObject("Fine_1-1-1__b") }));
        Assert.Equal(InspectionStatus.Partial, r.Status);
        Assert.Equal(1, r.PartCount);                       // only the fine object
        Assert.Contains(r.Problems, p => p.Entry == "objects/Dtd_1-1-1__a.xml");
        Assert.DoesNotContain(r.Parts, p => p.Name != null && p.Name.Contains("secret"));
    }

    [Fact]
    public void OversizeEntry_IsSkippedWithAMessageNamingIt()
    {
        InspectorOptions o = new InspectorOptions { MaxObjectBodyBytes = 2000 };
        string big = Xml2Object(PartXml(new string('n', 5000), UUID.Random(), Alice, Alice, Alice));
        ArchiveReport r = Inspect(Oar(new[] { ObjectFile("Big_1-1-1__a", big), SimpleObject("Small_1-1-1__b") }), o);
        Assert.Equal(InspectionStatus.Partial, r.Status);
        Assert.Equal(1, r.PartCount);
        ProblemRecord p = Assert.Single(r.Problems, x => x.Entry == "objects/Big_1-1-1__a.xml");
        Assert.Contains("limit", p.Message);
    }

    [Fact]
    public void BodyThatExpandsPastTheRatioCap_StopsWithTheEntryNamed()
    {
        UUID tex = UUID.Random();
        byte[] zeros = new byte[24 * 1024 * 1024];
        byte[] oar = Oar(new[] { ("assets/" + tex + "_texture.jp2", zeros) });
        Assert.True(zeros.Length / oar.Length > 100, "ratio of the test archive is only " + zeros.Length / oar.Length);

        InspectorOptions o = new InspectorOptions { MaxCompressionRatio = 50, RatioCheckMinBytes = 1024 * 1024 };
        ArchiveReport r = Inspect(oar, o);
        Assert.Equal(InspectionStatus.Partial, r.Status);
        Assert.Contains("ratio", r.StopReason);
        Assert.Contains(tex.ToString(), r.StopReason);
        Assert.Equal("assets/" + tex + "_texture.jp2", r.StoppedAt);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void TotalSizeAndEntryCountCaps_StopWithTheEntryNamed()
    {
        byte[] oar = Oar(Enumerable.Range(0, 6).Select(i => ("assets/" + UUID.Random() + "_texture.jp2", new byte[2 * 1024 * 1024])));
        ArchiveReport size = Inspect(oar, new InspectorOptions { MaxTotalUncompressedBytes = 5 * 1024 * 1024 });
        Assert.Equal(InspectionStatus.Partial, size.Status);
        Assert.Contains("uncompressed size", size.StopReason);
        Assert.StartsWith("assets/", size.StoppedAt);

        byte[] many = Oar(Enumerable.Range(0, 20).Select(i => SimpleObject("O" + i + "_1-1-1__" + i)));
        ArchiveReport count = Inspect(many, new InspectorOptions { MaxEntries = 5 });
        Assert.Equal(InspectionStatus.Partial, count.Status);
        Assert.Contains("entries", count.StopReason);
        Assert.NotNull(count.StoppedAt);
    }

    [Fact]
    public void ControlCharactersInNamesAreRemoved()
    {
        string evil = "Door\u0007\u001b[31m\u202etxt\u0000x";
        string part = PartXml(evil, UUID.Random(), Alice, Alice, Alice, creatorData: "http://grid.example.org/;Ev\u001bil Name");
        ArchiveReport r = Inspect(Oar(new[] { ObjectFile("Evil\u0001_1-1-1__a", Xml2Object(part)) }));

        Assert.True(r.PartCount == 1, Dump(r));
        PartRecord p = Assert.Single(r.Parts);
        Assert.Equal("Door[31mtxtx", p.Name);
        Assert.Equal("Evil Name", p.Creator.Name);
        Assert.DoesNotContain(r.Parts, x => x.Entry.Any(char.IsControl));

        string text = TextReport.Build(r, true);
        Assert.DoesNotContain(text, c => char.IsControl(c) && c != '\n' && c != '\r');
        int bidi = text.IndexOf((char)0x202E);
        Assert.True(bidi < 0, bidi < 0 ? "" : text.Substring(Math.Max(0, bidi - 100), Math.Min(200, text.Length - Math.Max(0, bidi - 100))));
        string json = ReportJson.Serialize(r);
        Assert.DoesNotContain("\\u001", json, StringComparison.OrdinalIgnoreCase);   // no escaped control characters either
        Assert.DoesNotContain("\\u000", json, StringComparison.OrdinalIgnoreCase);
        int esc = json.IndexOf((char)0x1b);
        Assert.True(esc < 0, esc < 0 ? "" : json.Substring(Math.Max(0, esc - 100), Math.Min(200, json.Length - Math.Max(0, esc - 100))));
    }

    [Fact]
    public void BinaryObjectPayloads_AreCountedAndNeverDecoded()
    {
        UUID group = UUID.Random(), coalesced = UUID.Random(), other = UUID.Random();
        byte[] g = new byte[] { 0x49, 0x54, 0x47, 0x01 }.Concat(Enumerable.Range(0, 300).Select(i => (byte)(i * 7))).ToArray();
        byte[] c = new byte[] { 0x49, 0x54, 0x43, 0x01 }.Concat(Enumerable.Range(0, 100).Select(i => (byte)i)).ToArray();
        byte[] o = Enumerable.Range(0, 64).Select(i => (byte)(200 + i % 50)).ToArray();

        ArchiveReport r = Inspect(Oar(new[]
        {
            ("assets/" + group + "_object.xml", g),
            ("assets/" + coalesced + "_object.xml", c),
            ("assets/" + other + "_object.xml", o)
        }));

        Assert.Equal(0, r.BoxedObjectCount);
        Assert.Equal(0, r.BoxedPartCount);
        Assert.Equal(1, r.LegacyPayloads.Single(l => l.Kind == "binary-object-thoosa-group").Count);
        Assert.Equal(1, r.LegacyPayloads.Single(l => l.Kind == "binary-object-thoosa-coalesced").Count);
        Assert.Equal(1, r.LegacyPayloads.Single(l => l.Kind == "binary-object-unknown").Count);
        Assert.Equal(g.Length, r.LegacyPayloads.Single(l => l.Kind == "binary-object-thoosa-group").Bytes);
        Assert.Equal(InspectionStatus.Complete, r.Status);
    }

    [Fact]
    public void ScriptStateFromAnotherEngine_IsCountedAndNotRead()
    {
        string xml = "<SceneObjectGroup>" + PartXml("Scripted", UUID.Random(), Alice, Alice, Alice) +
                     "<OtherParts /><PhloxGroupScriptStates><SavedScriptState Item=\"" + UUID.Random() + "\"><ScriptState>AAECAwQ=</ScriptState></SavedScriptState></PhloxGroupScriptStates>" +
                     "<GroupScriptStates><SavedScriptState Item=\"" + UUID.Random() + "\"><State>x</State></SavedScriptState></GroupScriptStates></SceneObjectGroup>";
        ArchiveReport r = Inspect(Oar(new[] { ObjectFile("S_1-1-1__a", xml) }));
        Assert.Equal(1, r.LegacyPayloads.Single(l => l.Kind == "script-state-phlox").Count);
        Assert.Equal(1, r.ScriptStateBlobs);
        Assert.Equal(1, r.PartCount);
    }

    [Fact]
    public void NotAnArchive_ExitsTwo()
    {
        Random rnd = new Random(7);
        byte[] noise = new byte[5000];
        rnd.NextBytes(noise);
        Assert.Equal(InspectionStatus.NotAnArchive, Inspect(noise).Status);
        Assert.Equal(2, Inspect(noise).ExitCode);
        Assert.Equal(InspectionStatus.NotAnArchive, Inspect(new byte[0]).Status);
        Assert.Equal(InspectionStatus.NotAnArchive, Inspect(new byte[] { 0x1f, 0x8b, 8, 0, 1, 2, 3 }).Status);

        // a valid tar with nothing that belongs to an OAR or IAR
        ArchiveReport other = Inspect(Tar(new[] { ("readme.txt", Utf8("hello")), ("data/x.bin", new byte[100]) }));
        Assert.Equal(InspectionStatus.NotAnArchive, other.Status);
    }

    [Fact]
    public void ReadsGnuLongNamesThatTheRepoWriterProduces_ViaTheReaderDirectly()
    {
        string name = "objects/" + new string('x', 150) + ".xml";
        byte[] tar = Tar(new[] { (name, Utf8("<a/>")), ("short.txt", Utf8("1")) }, gzip: false);
        TarStreamReader reader = new TarStreamReader(new MemoryStream(tar));
        TarEntryHeader h = reader.ReadNext();
        Assert.Equal(name, h.Name);
        Assert.Equal(4, h.Size);
        Assert.Equal("<a/>", Encoding.UTF8.GetString(reader.ReadBody()));
        Assert.Equal("short.txt", reader.ReadNext().Name);
        Assert.Null(reader.ReadNext());
    }

    // ------------------------------------------------------------------ bounded memory

    [Fact]
    public void SkippingAHugeBody_DoesNotHoldIt()
    {
        const long size = 160L * 1024 * 1024;
        UUID tex = UUID.Random();
        byte[] control = Tar(new[] { ("archive.xml", OarControl()) }, gzip: false);
        // control entry without its two end blocks, then the header of the big entry
        byte[] head = control.Take(control.Length - 1024).Concat(RawHeader("assets/" + tex + "_texture.jp2", size)).ToArray();
        long pad = (512 - size % 512) % 512;
        byte[] tail = new byte[pad + 1024];
        LazyTar lazy = new LazyTar(head, size, tail);

        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        ArchiveReport r = new ArchiveInspector().Inspect(lazy);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(1, r.AssetCount);
        Assert.Equal(size, r.AssetsByType.Single().Bytes);
        Assert.False(r.Assets.Single().BodyRead);
        Assert.True(allocated < 8 * 1024 * 1024, "allocated " + allocated + " bytes to skip " + size);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "took " + watch.Elapsed);
    }
}
