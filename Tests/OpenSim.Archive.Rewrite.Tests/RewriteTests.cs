/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework.Serialization;
using Xunit;
using static OpenSim.Archive.Rewrite.Tests.RewriteKit;

namespace OpenSim.Archive.Rewrite.Tests;

/// <summary>The in-place patch on hand-written object XML, and the rewrite of small hand-made archives.</summary>
public class RewriteTests
{
    private static readonly Guid PartA = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid PartB = Guid.Parse("00000000-0000-4000-8000-0000000000b2");

    /// <summary>Object XML laid out the way a person or another tool might: odd spacing, attributes, comments, CRLF.</summary>
    private static string ObjectText(string nameA = "<Name>Alpha</Name>", string nl = "\n") =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + nl +
        "<SceneObjectGroup>" + nl +
        "  <RootPart>" + nl +
        "    <SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" + nl +
        "      <AllowedDrop>false</AllowedDrop>" + nl +
        "      <UUID><UUID>" + PartA + "</UUID></UUID>" + nl +
        "      " + nameA + nl +
        "      <!-- kept as is -->" + nl +
        "      <Description>root &amp; more</Description>" + nl +
        "      <TaskInventory><TaskInventoryItem><Name>Item inside</Name></TaskInventoryItem></TaskInventory>" + nl +
        "      <Unknown a=\"x>y\" b='1'>  keep   me </Unknown>" + nl +
        "    </SceneObjectPart>" + nl +
        "  </RootPart>" + nl +
        "  <OtherParts>" + nl +
        "    <Part><SceneObjectPart>" + nl +
        "      <UUID><Guid>" + PartB + "</Guid></UUID>" + nl +
        "      <Name>Beta</Name>" + nl +
        "      <Shape><ProfileShape>Circle</ProfileShape></Shape>" + nl +
        "    </SceneObjectPart></Part>" + nl +
        "  </OtherParts>" + nl +
        "</SceneObjectGroup>";

    private static byte[] Patch(byte[] xml, params PartPatch[] patches)
    {
        ObjectXml.Decoded d = ObjectXml.Decode(xml);
        return ObjectXml.Apply(d, patches.ToDictionary(p => p.PartId), "objects/t.xml");
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Rename_ChangesOnlyTheNameText_WhateverTheLineEndings(string nl)
    {
        string before = ObjectText(nl: nl);
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "Alpha two"))));
        Assert.Equal(before.Replace("<Name>Alpha</Name>", "<Name>Alpha two</Name>"), after);
    }

    [Fact]
    public void Rename_OfTheOtherPart_FindsItByUuid_AndLeavesTheFirstAlone()
    {
        string before = ObjectText();
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartB, ElementEdit.SetText("Name", "Beta two"))));
        Assert.Equal(before.Replace("<Name>Beta</Name>", "<Name>Beta two</Name>"), after);
    }

    [Fact]
    public void Text_IsEscaped()
    {
        string after = Encoding.UTF8.GetString(Patch(Utf8(ObjectText()), new PartPatch(PartA, ElementEdit.SetText("Name", "a<b>&c"))));
        Assert.Contains("<Name>a&lt;b&gt;&amp;c</Name>", after);
    }

    [Fact]
    public void AnElementInsideTheTaskInventory_IsNotTakenForThePartsName()
    {
        string before = ObjectText();
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "X"))));
        Assert.Contains("<TaskInventoryItem><Name>Item inside</Name></TaskInventoryItem>", after);
        // the nested one is reachable by its own path
        string nested = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("TaskInventory/TaskInventoryItem/Name", "Y"))));
        Assert.Equal(before.Replace("Item inside", "Y"), nested);
    }

    [Fact]
    public void SelfClosingElement_BecomesAnElementWithContent()
    {
        string before = ObjectText("<Name />");
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "Filled"))));
        Assert.Equal(before.Replace("<Name />", "<Name>Filled</Name>"), after);

        before = ObjectText("<Name/>");
        after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "Filled"))));
        Assert.Equal(before.Replace("<Name/>", "<Name>Filled</Name>"), after);
    }

    [Fact]
    public void EmptyNameElement_CanBeFilled_AndAFilledOneEmptied()
    {
        string before = ObjectText("<Name></Name>");
        Assert.Contains("<Name>F</Name>", Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "F")))));
        Assert.Contains("<Name></Name>", Encoding.UTF8.GetString(Patch(Utf8(ObjectText()), new PartPatch(PartA, ElementEdit.SetText("Name", "")))));
    }

    [Fact]
    public void ByteOrderMark_IsKept()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        byte[] source = bom.Concat(Utf8(ObjectText())).ToArray();
        byte[] result = Patch(source, new PartPatch(PartA, ElementEdit.SetText("Name", "B")));
        Assert.Equal(bom, result.Take(3).ToArray());
        Assert.Contains("<Name>B</Name>", Encoding.UTF8.GetString(result, 3, result.Length - 3));
    }

    [Fact]
    public void NonAsciiText_BeforeThePart_DoesNotShiftThePatch()
    {
        string before = ObjectText().Replace("<AllowedDrop>", "<!-- café \U0001F600 -->\n<AllowedDrop>");
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartA, ElementEdit.SetText("Name", "Z"))));
        Assert.Equal(before.Replace("<Name>Alpha</Name>", "<Name>Z</Name>"), after);
    }

    [Fact]
    public void SetInnerXml_ReplacesNestedContent_AndMalformedMarkupIsRefused()
    {
        string before = ObjectText();
        string after = Encoding.UTF8.GetString(Patch(Utf8(before), new PartPatch(PartB, ElementEdit.SetInnerXml("Shape", "<ProfileShape>Square</ProfileShape>"))));
        Assert.Equal(before.Replace("Circle", "Square"), after);
        Assert.Throws<ArgumentException>(() => ElementEdit.SetInnerXml("Shape", "<open>"));
    }

    [Fact]
    public void TwoEditsToOnePart_AreBothApplied_AndOverlappingOnesRefused()
    {
        string before = ObjectText();
        string after = Encoding.UTF8.GetString(Patch(Utf8(before),
            new PartPatch(PartA, ElementEdit.SetText("Name", "N"), ElementEdit.SetText("Description", "D"))));
        Assert.Equal(before.Replace("<Name>Alpha</Name>", "<Name>N</Name>").Replace("root &amp; more", "D"), after);

        Assert.Throws<InvalidDataException>(() => Patch(Utf8(before),
            new PartPatch(PartA, ElementEdit.SetText("TaskInventory", "x"), ElementEdit.SetText("TaskInventory/TaskInventoryItem/Name", "y"))));
    }

    [Fact]
    public void MissingPart_MissingElement_AndInvalidBytes_AreRefused()
    {
        Assert.Throws<InvalidDataException>(() => Patch(Utf8(ObjectText()), new PartPatch(Guid.NewGuid(), ElementEdit.SetText("Name", "x"))));
        Assert.Throws<InvalidDataException>(() => Patch(Utf8(ObjectText()), new PartPatch(PartA, ElementEdit.SetText("NoSuch", "x"))));
        Assert.Throws<InvalidDataException>(() => Patch(Utf8(ObjectText()), new PartPatch(PartA, ElementEdit.SetText("Unknown/Child", "x"))));
        Assert.Throws<ArgumentException>(() => ElementEdit.SetText("Name", "bad\u0001char"));
        Assert.ThrowsAny<Exception>(() => ObjectXml.Decode(new byte[] { 0x3c, 0xff, 0xfe, 0x3e }));
    }

    // ------------------------------------------------------------------ whole-archive rewrite on hand-made archives

    private static byte[] MakeOar(IEnumerable<(string name, byte[] data)> entries)
    {
        using MemoryStream raw = new MemoryStream();
        using (GZipStream gz = new GZipStream(raw, CompressionLevel.Fastest, true))
        using (TarWriter w = new TarWriter(gz, true))
            foreach ((string n, byte[] d) in entries)
                w.WriteFile(n, d);
        return raw.ToArray();
    }

    private static (RewriteSummary summary, byte[] output) Run(byte[] source, Action<ArchiveRewriter> setup = null)
    {
        ArchiveRewriter rw = new ArchiveRewriter();
        setup?.Invoke(rw);
        using MemoryStream dest = new MemoryStream();
        RewriteSummary s = rw.Rewrite(() => new MemoryStream(source), dest);
        return (s, dest.ToArray());
    }

    private static List<(string name, byte[] data)> HandMade() => new List<(string, byte[])>
    {
        ("archive.xml", Utf8("<archive major_version=\"0\" minor_version=\"8\" />")),
        ("assets/", Array.Empty<byte>()),
        ("assets/" + Guid.Parse("11111111-1111-4111-8111-111111111111") + "_texture.jp2", new byte[] { 1, 2, 3, 4, 5 }),
        ("objects/Thing_001-002-003__" + PartA + ".xml", Utf8(ObjectText())),
        ("objects/" + new string('l', 150) + ".xml", Utf8("<x/>")),
        ("terrains/0,0.r32", new byte[2000]),
        ("empty.bin", Array.Empty<byte>()),
    };

    [Fact]
    public void Identity_KeepsOrderNamesAndBodies_AndCountsEverything()
    {
        List<(string name, byte[] data)> src = HandMade();
        (RewriteSummary s, byte[] output) = Run(MakeOar(src));

        List<(string name, byte[] data)> got = ReadEntries(output);
        Assert.Equal(src.Select(e => e.name), got.Select(e => e.name));
        for (int i = 0; i < src.Count; i++)
            Assert.Equal(src[i].data, got[i].data);

        Assert.Equal(src.Count, s.SourceEntries);
        Assert.Equal(src.Count, s.EntriesCopied);
        Assert.Equal(0, s.ObjectsPatched);
        Assert.Equal(0, s.AssetsAdded);
        Assert.Equal(src.Count, s.EntriesWritten);
        Assert.True(s.ControlFileFirst);
        Assert.Equal("archive.xml", got[0].name);
    }

    [Fact]
    public void PlainTarSource_IsAccepted_AndComesOutGzipped()
    {
        using MemoryStream raw = new MemoryStream();
        using (TarWriter w = new TarWriter(raw, true))
            w.WriteFile("archive.xml", Utf8("<archive/>"));
        (RewriteSummary s, byte[] output) = Run(raw.ToArray());
        Assert.Equal(0x1f, output[0]);
        Assert.Equal(0x8b, output[1]);
        Assert.Equal(1, s.EntriesWritten);
    }

    [Fact]
    public void PartPatch_ChangesOnlyItsObjectFile()
    {
        List<(string name, byte[] data)> src = HandMade();
        (RewriteSummary s, byte[] output) = Run(MakeOar(src), rw => rw.AddPatch(new PartPatch(PartB, ElementEdit.SetText("Name", "Beta renamed"))));

        List<(string name, byte[] data)> got = ReadEntries(output);
        Assert.Equal(src.Select(e => e.name), got.Select(e => e.name));
        for (int i = 0; i < src.Count; i++)
        {
            if (src[i].name.StartsWith("objects/Thing"))
                Assert.Equal(Encoding.UTF8.GetString(src[i].data).Replace("<Name>Beta</Name>", "<Name>Beta renamed</Name>"), Encoding.UTF8.GetString(got[i].data));
            else
                Assert.Equal(src[i].data, got[i].data);
        }
        Assert.Equal(1, s.ObjectsPatched);
        Assert.Equal(1, s.ObjectFilesPatched);
        Assert.Equal(src.Count - 1, s.EntriesCopied);
        Assert.Single(s.PatchedEntries);
    }

    [Fact]
    public void AddedAsset_IsAppended_WithTheTypesExtension()
    {
        List<(string name, byte[] data)> src = HandMade();
        Guid id = Guid.Parse("22222222-2222-4222-8222-222222222222");
        byte[] mesh = { 9, 8, 7 };
        (RewriteSummary s, byte[] output) = Run(MakeOar(src), rw => rw.AddAsset(id, AssetType.Mesh, mesh));

        List<(string name, byte[] data)> got = ReadEntries(output);
        Assert.Equal(src.Count + 1, got.Count);
        Assert.Equal("assets/" + id + "_mesh.llmesh", got[^1].name);
        Assert.Equal(mesh, got[^1].data);
        Assert.Equal(1, s.AssetsAdded);
        Assert.Equal(src.Count, s.EntriesCopied);
        Assert.Equal(src.Count + 1, s.EntriesWritten);
        Assert.Contains("\"AssetsAdded\": 1", s.ToJson());
    }

    [Fact]
    public void Refusals_HappenBeforeTheOutputIsWritten()
    {
        byte[] source = MakeOar(HandMade());
        string dest = TempFile(".oar");

        ArchiveRewriter missing = new ArchiveRewriter();
        missing.AddPatch(new PartPatch(Guid.NewGuid(), ElementEdit.SetText("Name", "x")));
        string src = TempFile(".oar");
        File.WriteAllBytes(src, source);
        Assert.Throws<InvalidDataException>(() => missing.Rewrite(src, dest));
        Assert.False(File.Exists(dest));

        ArchiveRewriter collision = new ArchiveRewriter();
        collision.AddAsset(Guid.Parse("11111111-1111-4111-8111-111111111111"), AssetType.Texture, new byte[1]);   // image/jp2 maps to texture.jp2
        Assert.Throws<InvalidOperationException>(() => collision.Rewrite(src, dest));
        Assert.False(File.Exists(dest));

        Assert.Throws<ArgumentException>(() => new ArchiveRewriter().Rewrite(src, src));

        ArchiveRewriter twice = new ArchiveRewriter();
        twice.AddPatch(new PartPatch(PartA, ElementEdit.SetText("Name", "x")));
        Assert.Throws<InvalidOperationException>(() => twice.AddPatch(new PartPatch(PartA, ElementEdit.SetText("Name", "y"))));

        ArchiveRewriter dup = new ArchiveRewriter();
        Guid g = Guid.NewGuid();
        dup.AddAsset(g, AssetType.Mesh, new byte[1]);
        Assert.Throws<InvalidOperationException>(() => dup.AddAsset(g, AssetType.Mesh, new byte[1]));
    }

    [Fact]
    public void ExistingOutputFile_IsNotOverwritten()
    {
        string src = TempFile(".oar");
        string dest = TempFile(".oar");
        File.WriteAllBytes(src, MakeOar(HandMade()));
        File.WriteAllBytes(dest, new byte[] { 1 });
        Assert.Throws<IOException>(() => new ArchiveRewriter().Rewrite(src, dest));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(dest));
    }

    // ------------------------------------------------------------------ real scene archives, loaded by the repository's loader

    [Collection("scene")]
    public class SceneTests : OpenSim.Tests.Common.OpenSimTestCase
    {
        [Fact]
        public void Identity_OfASavedScene_LoadsToTheSameLinksets_AndEveryEntryIsByteIdentical()
        {
            byte[] oar = SaveSampleOar(out List<PartRecord> expected, out _);
            (RewriteSummary s, byte[] output) = Run(oar);

            var a = ReadEntries(oar);
            var b = ReadEntries(output);
            Assert.Equal(a.Select(e => e.name), b.Select(e => e.name));
            for (int i = 0; i < a.Count; i++)
                Assert.True(a[i].data.AsSpan().SequenceEqual(b[i].data), a[i].name);
            Assert.Equal(a.Count, s.EntriesCopied);

            List<PartRecord> original = LoadAndSnapshot(new MemoryStream(oar));
            List<PartRecord> rewritten = LoadAndSnapshot(new MemoryStream(output));
            Assert.Equal(4, original.Count);
            Assert.Equal(original, rewritten);
            Assert.Equal(expected.Count, original.Count);
        }

        [Fact]
        public void Rename_OfOnePart_LoadsWithTheNewName_AndEverythingElseIsUnchanged()
        {
            byte[] oar = SaveSampleOar(out List<PartRecord> saved, out var ids);
            Guid targetId = ids[("Tower", 2)];
            List<PartRecord> original = LoadAndSnapshot(new MemoryStream(oar));
            Assert.Equal(saved.Count, original.Count);
            PartRecord target = original.Single(r => r.GroupName == "Tower" && r.LinkNum == 2);
            Assert.NotEmpty(target.Items);   // the renamed part has contents, which must survive

            (RewriteSummary s, byte[] output) = Run(oar, rw => rw.AddPatch(new PartPatch(targetId, ElementEdit.SetText("Name", "Renamed <part> & co"))));
            Assert.Equal(1, s.ObjectsPatched);

            List<PartRecord> patched = LoadAndSnapshot(new MemoryStream(output));
            List<PartRecord> expected = original.Select(r => r == target ? r with { Name = "Renamed <part> & co" } : r)
                .OrderBy(r => r.GroupName, StringComparer.Ordinal).ThenBy(r => r.Position, StringComparer.Ordinal).ThenBy(r => r.LinkNum)
                .ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, patched);
        }

        [Fact]
        public void AddedMeshAsset_IsLoadedIntoTheAssetService()
        {
            byte[] oar = SaveSampleOar(out _, out _);
            Guid id = Guid.NewGuid();
            byte[] mesh = Encoding.ASCII.GetBytes("fake mesh bytes");
            (_, byte[] output) = Run(oar, rw => rw.AddAsset(id, AssetType.Mesh, mesh));

            OpenSim.Tests.Common.TestScene scene = NewScene("Asset Region");
            SceneManager_Use(scene);
            Api(scene).DearchiveRegion(Plain(new MemoryStream(output)), Guid.NewGuid(), new Dictionary<string, object>());
            OpenSim.Framework.AssetBase loaded = scene.AssetService.Get(new UUID(id).ToString());
            Assert.NotNull(loaded);
            Assert.Equal((sbyte)AssetType.Mesh, loaded.Type);
            Assert.Equal(mesh, loaded.Data);
        }

        private static void SceneManager_Use(OpenSim.Region.Framework.Scenes.Scene scene)
        {
            OpenSim.Region.Framework.Scenes.SceneManager.Instance.Scenes.Clear();
            OpenSim.Region.Framework.Scenes.SceneManager.Instance.Add(scene);
        }
    }
}
