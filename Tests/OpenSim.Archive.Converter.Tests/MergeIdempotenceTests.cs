/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Running the merge over its own output must do no harm: at the same pixel limit it gives the same bytes, and the archive says which limit
/// its merged parts were made at, so a second pass at another limit can say that it leaves their levels of detail as they are.
/// Each test writes its own archives and shares no process state, so the class runs in parallel with the others.
/// </summary>
public class MergeIdempotenceTests
{
    private static string Output() => Path.Combine(Path.GetTempPath(), "merge-idempotence-tests", Guid.NewGuid().ToString("N") + ".oar");

    private static MergeOptions Limit(double pixels)
    {
        MergeOptions o = new MergeOptions();
        o.Lod.PixelLimit = pixels;
        return o;
    }

    private static string MergeTo(string source, double pixels = 1)
    {
        string output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeArchiveWriter.Write(source, output, Limit(pixels));
        return output;
    }

    /// <summary>Boxes in a row longer than the soft cap, in two materials, so the planner cuts the row into pieces and leaves some parts alone.</summary>
    private static string Row()
    {
        List<KitPart> parts = Enumerable.Range(1, 14).Select(i => new KitPart("Row " + i, i)
        {
            Offset = (i * (i < 13 ? 3 : 20)) + ",0,0", Scale = "1,1,1", Description = "row " + i
        }).ToList();
        parts[3].Creator = "a11ce000-0000-4000-8000-000000000002";
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { parts, parts.Select(p => new KitPart(p.Name + "b", p.LinkNum) { Offset = p.Offset, Scale = "1,2,1" }).ToList() });
    }

    private static string Scripted()
    {
        var linksets = MergeLinkSafetyTests.ScriptedArchive(out Dictionary<string, byte[]> assets);
        return EligibilityKit.WriteArchive(linksets.Select(l => l.parts).ToList(), assets);
    }

    public static IEnumerable<object[]> Sources()
    {
        yield return new object[] { "maps fixture", (Func<string>)MapsFixture.Write };
        yield return new object[] { "row", (Func<string>)Row };
        yield return new object[] { "scripted linksets", (Func<string>)Scripted };
    }

    private static List<string> Notecards(string archive)
    {
        List<string> texts = new List<string>();
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            if (h.IsFile && h.Name.StartsWith("assets/", StringComparison.Ordinal) && h.Name.EndsWith("_notecard.txt", StringComparison.Ordinal))
                texts.Add(Encoding.UTF8.GetString(tar.ReadBody()));
        return texts;
    }

    // ---------------------------------------------------------------- the limit is recorded

    [Fact]
    public void TheMergePlanRecordsTheLimitNextToTheToolVersion()
    {
        string output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeWriteResult r = MergeArchiveWriter.Write(MapsFixture.Write(), output, Limit(4));
        string json = MergePlanFiles.ArchiveJson(r.Plan);
        Assert.Contains("\"lodPixelLimit\": 4", json);
        Assert.True(json.IndexOf("\"tool\"", StringComparison.Ordinal) < json.IndexOf("\"lodPixelLimit\"", StringComparison.Ordinal));
    }

    [Fact]
    public void APlanThatOnlyPlansHasNoLimitToRecord()
    {
        string json = MergePlanFiles.ArchiveJson(ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions()));
        Assert.DoesNotContain("lodPixelLimit", json);
    }

    [Fact]
    public void EveryManifestNotecardOfTheArchiveSaysWhichLimitTheLevelsOfDetailWereMadeAt()
    {
        string merged = MergeTo(MapsFixture.Write(), 4);
        List<string> manifests = Notecards(merged).Where(t => t.Contains("Merged part ")).ToList();
        Assert.NotEmpty(manifests);
        Assert.All(manifests, t => Assert.Contains("Levels of detail made at a limit of 4 pixels.", t));
    }

    // ---------------------------------------------------------------- a second pass

    [Fact]
    public void ASecondPassAtAnotherLimitSaysItKeepsTheLevelsOfDetailItFindsAndMergesNothingMore()
    {
        string merged = MergeTo(MapsFixture.Write(), 4);
        MergePlan again = ArchiveMergePlanner.Plan(merged, Limit(1));
        Assert.Equal(0, again.MergedParts);
        Assert.Equal(again.PartsBefore, again.PartsAfter);
        string warning = Assert.Single(again.Warnings, w => w.Contains("limit of 4 pixels"));
        Assert.Contains("1 pixel", warning);
    }

    [Fact]
    public void ASecondPassAtTheSameLimitHasNothingToSayAboutIt()
    {
        string merged = MergeTo(MapsFixture.Write(), 4);
        MergePlan again = ArchiveMergePlanner.Plan(merged, Limit(4));
        Assert.DoesNotContain(again.Warnings, w => w.Contains("limit of"));
    }

    [Fact]
    public void AMergedPartIsKeptByAPlanCodeOfItsOwn()
    {
        string merged = MergeTo(MapsFixture.Write());
        MergePlan again = ArchiveMergePlanner.Plan(merged, new MergeOptions());
        int mergedBefore = ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions()).MergedParts;
        Assert.True(mergedBefore > 0);
        Assert.Equal(mergedBefore, again.Linksets.SelectMany(l => l.Kept).Count(k => k.Rules.Any(r => r.Code == "ALREADY_MERGED")));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void MergingTheMergedArchiveAtTheSameLimitGivesTheSameBytes(string name, Func<string> make)
    {
        string source = make();
        Assert.True(ArchiveMergePlanner.Plan(source, new MergeOptions()).MergedParts > 0, name + ": nothing merges, so the comparison would mean nothing");
        string first = MergeTo(source), second = MergeTo(first);
        Assert.True(File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)), name + ": the second pass differs from the first");
        MergePlan plan = ArchiveMergePlanner.Plan(first, new MergeOptions());
        Assert.Equal(0, plan.MergedParts);
    }

    [Fact]
    public void ThreePassesGiveTheBytesOfTheSecond()
    {
        string row = Row();
        Assert.True(ArchiveMergePlanner.Plan(row, new MergeOptions()).MergedParts > 0);
        string first = MergeTo(row), second = MergeTo(first), third = MergeTo(second);
        Assert.True(File.ReadAllBytes(second).AsSpan().SequenceEqual(File.ReadAllBytes(third)));
    }
}
