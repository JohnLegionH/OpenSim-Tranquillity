/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The comparison of shape data across a region's save: blocks are compared by what they mean, so a region that writes the same blocks in
/// another order, or an empty place where a default media entry was, shows no difference, and a changed id, text or field does.
/// </summary>
public class ShapeBlockCompareTests
{
    private static readonly Guid MeshId = Guid.Parse("61111111-2222-4333-8444-555555555555");
    private static readonly Guid MaterialOne = Guid.Parse("62111111-2222-4333-8444-555555555555");
    private static readonly Guid MaterialTwo = Guid.Parse("63111111-2222-4333-8444-555555555555");

    private static byte[] Extra(Guid mesh, params (int Face, Guid Material)[] materials)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape
        {
            SculptEntry = true, SculptTexture = new UUID(mesh), SculptType = (byte)SculptType.Mesh,
            RenderMaterials = new Primitive.RenderMaterials
            {
                entries = materials.Select(m => new Primitive.RenderMaterials.RenderMaterialEntry { te_index = (byte)m.Face, id = new UUID(m.Material) }).ToArray()
            }
        };
        return shape.ExtraParamsToBytes();
    }

    private static byte[] Overrides(params (int Face, string Data)[] overrides)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape
        {
            RenderMaterials = new Primitive.RenderMaterials { overrides = overrides.Select(o => new Primitive.RenderMaterials.RenderMaterialOverrideEntry { te_index = (byte)o.Face, data = o.Data }).ToArray() }
        };
        return shape.RenderMaterialsOvrToRawBin();
    }

    /// <summary>The same blocks with the first two swapped (each block is a type, a length and its bytes).</summary>
    private static byte[] Swapped(byte[] extra)
    {
        int count = extra[0], at = 1;
        List<byte[]> blocks = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            int length = (int)BitConverter.ToUInt32(extra, at + 2);
            blocks.Add(extra.AsSpan(at, 6 + length).ToArray());
            at += 6 + length;
        }
        (blocks[0], blocks[1]) = (blocks[1], blocks[0]);
        return new[] { (byte)count }.Concat(blocks.SelectMany(b => b)).ToArray();
    }

    private static string Media(params MediaEntry[] entries) => MediaFixture.MediaXml(entries);

    private static ParsedPart Part(byte[] extra, byte[] overrides, string media) => new ParsedPart { ExtraParams = extra, MaterialOverrides = overrides, MediaXml = media };

    private static ShapeBlockReport Compare(ParsedPart a, ParsedPart b)
    {
        ShapeBlockReport report = new ShapeBlockReport();
        ShapeBlockCompare.ComparePart("part", a, b, report);
        return report;
    }

    private static ParsedPart Full() => Part(Extra(MeshId, (0, MaterialOne), (2, MaterialTwo)), Overrides((0, "{\"ti\":[{\"o\":[0.1,0.2]}]}"), (2, "{\"am\":1}")),
        Media(MediaFixture.Entry("http://example.org/a", 400), null, MediaFixture.Entry("http://example.org/b", 640)));

    [Fact]
    public void AnUnchangedPartShowsNoDifference()
    {
        ShapeBlockReport r = Compare(Full(), Full());
        Assert.Empty(r.Differences);
        Assert.Empty(r.Notes);
        Assert.Equal(2, r.Blocks);
        Assert.Equal(2, r.PbrFaces);
        Assert.Equal(2, r.Overrides);
        Assert.Equal(2, r.MediaEntries);
    }

    [Fact]
    public void TheSameBlocksInAnotherOrderAreTheSameInMeaning()
    {
        ParsedPart a = Full(), b = Full();
        b.ExtraParams = Swapped(a.ExtraParams);
        Assert.NotEqual(a.ExtraParams, b.ExtraParams);
        ShapeBlockReport r = Compare(a, b);
        Assert.Empty(r.Differences);
        Assert.Single(r.Notes);
    }

    [Fact]
    public void AChangedMaterialIdIsADifferenceNamingTheFace()
    {
        ParsedPart b = Full();
        b.ExtraParams = Extra(MeshId, (0, MaterialOne), (2, MaterialOne));
        ShapeBlockReport r = Compare(Full(), b);
        Assert.Contains(r.Differences, d => d.Contains("face 2") && d.Contains("material"));
        Assert.DoesNotContain(r.Differences, d => d.Contains("face 0"));
    }

    [Fact]
    public void AChangedSculptBlockFieldIsADifferenceNamingTheField()
    {
        ParsedPart b = Full();
        b.ExtraParams = Extra(Guid.Parse("64111111-2222-4333-8444-555555555555"), (0, MaterialOne), (2, MaterialTwo));
        Assert.Contains(Compare(Full(), b).Differences, d => d.Contains("sculpt") && d.Contains("texture"));
    }

    [Fact]
    public void ABlockThatIsGoneIsADifference()
    {
        ParsedPart b = Full();
        b.ExtraParams = new PrimitiveBaseShape { SculptEntry = true, SculptTexture = new UUID(MeshId), SculptType = (byte)SculptType.Mesh }.ExtraParamsToBytes();
        Assert.Contains(Compare(Full(), b).Differences, d => d.Contains("0x80") && d.Contains("missing"));
    }

    [Fact]
    public void AChangedOverrideTextIsADifferenceNamingTheFace()
    {
        ParsedPart b = Full();
        b.MaterialOverrides = Overrides((0, "{\"ti\":[{\"o\":[0.1,0.3]}]}"), (2, "{\"am\":1}"));
        ShapeBlockReport r = Compare(Full(), b);
        Assert.Contains(r.Differences, d => d.Contains("override") && d.Contains("face 0"));
        Assert.DoesNotContain(r.Differences, d => d.Contains("face 2"));
    }

    [Fact]
    public void AChangedMediaFieldIsADifferenceNamingTheField()
    {
        MediaEntry changed = MediaFixture.Entry("http://example.org/b", 640);
        changed.AutoLoop = false;
        ParsedPart b = Full();
        b.MediaXml = Media(MediaFixture.Entry("http://example.org/a", 400), null, changed);
        ShapeBlockReport r = Compare(Full(), b);
        Assert.Single(r.Differences);
        Assert.Contains("face 2", r.Differences[0]);
        Assert.Contains("auto_loop", r.Differences[0]);
    }

    [Fact]
    public void AMediaEntryThatIsGoneIsADifferenceAndAnEmptyPlaceIsTheSameAsADefaultEntry()
    {
        ParsedPart gone = Full();
        gone.MediaXml = Media(MediaFixture.Entry("http://example.org/a", 400), null, null);
        Assert.Contains(Compare(Full(), gone).Differences, d => d.Contains("face 2") && d.Contains("media"));

        ParsedPart withPlace = Part(Full().ExtraParams, Full().MaterialOverrides, Media(MediaFixture.Entry("http://example.org/a", 400), null, MediaFixture.Entry("http://example.org/b", 640)));
        ParsedPart withDefault = Part(Full().ExtraParams, Full().MaterialOverrides, Media(MediaFixture.Entry("http://example.org/a", 400), new MediaEntry(), MediaFixture.Entry("http://example.org/b", 640)));
        Assert.Empty(Compare(withPlace, withDefault).Differences);
        Assert.Empty(Compare(withDefault, withPlace).Differences);
    }

    [Fact]
    public void TheArchivesOfTheFixturesCompareEqualToThemselvesAndSayHowMuchWasCompared()
    {
        string pbr = PbrFixture.Write(), media = MediaFixture.Write();
        ShapeBlockReport p = ShapeBlockCompare.CompareArchives(pbr, pbr), m = ShapeBlockCompare.CompareArchives(media, media);
        Assert.Empty(p.Differences);
        Assert.Empty(m.Differences);
        Assert.True(p.Parts > 0 && p.PbrFaces > 0 && p.Overrides > 0, p.ToText());
        Assert.True(m.Parts > 0 && m.MediaEntries > 0, m.ToText());
    }

    [Fact]
    public void ThePartsOfTwoArchivesAreMatchedByIdAndAPartInOneOnlyIsADifference()
    {
        ShapeBlockReport r = ShapeBlockCompare.CompareArchives(PbrFixture.Write(), MediaFixture.Write());
        Assert.NotEmpty(r.Differences);
    }

    [Fact]
    public void TheArchivesNamedInTheEnvironmentAreComparedAndTheReportIsWritten()
    {
        string before = Environment.GetEnvironmentVariable("OARX_BLOCKS_BEFORE"), after = Environment.GetEnvironmentVariable("OARX_BLOCKS_AFTER"), report = Environment.GetEnvironmentVariable("OARX_BLOCKS_REPORT");
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after))
            return;
        ShapeBlockReport r = ShapeBlockCompare.CompareArchives(before, after);
        if (!string.IsNullOrEmpty(report))
            File.WriteAllText(report, r.ToText());
        Assert.Empty(r.Differences);
    }

    private static string Mutated(Action<List<List<KitPart>>> change)
    {
        List<List<KitPart>> sets = PbrFixture.Parts();
        change(sets);
        return EligibilityKit.WriteArchive(sets, PbrFixture.Assets(), PlanarFixture.FlatTerrain());
    }

    [Fact]
    public void ARegionGivesPartsNewIdsSoPartsAreMatchedByNameLinkNumberAndPositionWhenAskedTo()
    {
        string a = PbrFixture.Write(), b = PbrFixture.Write();
        Assert.NotEmpty(ShapeBlockCompare.CompareArchives(a, b).Differences);
        ShapeBlockReport r = ShapeBlockCompare.CompareArchives(a, b, true);
        Assert.Empty(r.Differences);
        Assert.True(r.Parts > 0 && r.PbrFaces > 0 && r.Overrides > 0, r.ToText());
    }

    [Fact]
    public void AChangedBlockIsFoundWhenPartsAreMatchedByPosition()
    {
        string a = PbrFixture.Write();
        string b = Mutated(sets => sets[0][0].ExtraParams = PbrFixture.Blocks(new[] { (0, PbrFixture.P2), (1, PbrFixture.P1), (2, PbrFixture.P2) }, new[] { (1, PbrFixture.O1), (2, PbrFixture.O1) }).ExtraParams);
        ShapeBlockReport r = ShapeBlockCompare.CompareArchives(a, b, true);
        Assert.Contains(r.Differences, d => d.Contains("Pbr 0.1"));
    }

    [Fact]
    public void APartThatMovedIsMissingWhereItWasAndAppearsWhereItIsWhenMatchedByPosition()
    {
        string a = PbrFixture.Write();
        string b = Mutated(sets => sets[0][1].Offset = "9,9,9");
        ShapeBlockReport r = ShapeBlockCompare.CompareArchives(a, b, true);
        Assert.Contains(r.Differences, d => d.Contains("missing"));
        Assert.Contains(r.Differences, d => d.Contains("appears"));
    }
}
