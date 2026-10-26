/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using OpenSim.ArchiveInspector;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The pixel limit the levels of detail of a merged part are checked against is a setting of the command, and the report states whether merging made
/// the build heavier for viewers than the prims it replaced. Each test writes its own archives and shares no process state, so the class runs in parallel.
/// </summary>
public class MergeLodSettingTests
{
    private static string Output() => Path.Combine(Path.GetTempPath(), "merge-lod-setting-tests", Guid.NewGuid().ToString("N") + ".oar");

    private static string Folder() => Path.Combine(Path.GetTempPath(), "merge-lod-setting-tests", Guid.NewGuid().ToString("N"));

    private static long LowestTriangles(string archive)
    {
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        long total = 0;
        while ((h = tar.ReadNext()) != null)
            if (h.IsFile && h.Name.EndsWith("_mesh.llmesh"))
                total += MeshCodec.Decode(tar.ReadBody()).Lowest.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        return total;
    }

    [Fact]
    public void TheDefaultPixelLimitIsOnePixelTheStrictestOfTheSettingsMeasured()
    {
        Assert.Equal(1.0, new MergeOptions().Lod.PixelLimit);
    }

    [Fact]
    public void ThePixelLimitOptionIsTheLimitTheLevelsAreCheckedAgainst()
    {
        string source = MapsFixture.Write(), strict = Output(), loose = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(strict));
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--out", strict, "--merge-plan-dir", Folder() }));
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--lod-pixel-limit", "64", "--out", loose, "--merge-plan-dir", Folder() }));
        Assert.True(LowestTriangles(loose) < LowestTriangles(strict));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void APixelLimitThatIsNotAPositiveNumberIsRefusedAndNothingIsWritten(string value)
    {
        string source = MapsFixture.Write(), output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--lod-pixel-limit", value, "--out", output, "--merge-plan-dir", Folder() }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ThePixelLimitOptionNeedsAValue()
    {
        string source = MapsFixture.Write(), output = Output();
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--out", output, "--merge-plan-dir", Folder(), "--lod-pixel-limit" }));
    }

    [Fact]
    public void TheComparisonHoldsTheCostsOfTheSourceAndOfTheMergedArchive()
    {
        string source = MapsFixture.Write(), output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeArchiveWriter.Write(source, output);
        CostComparison c = CostComparison.Measure(source, output);
        Assert.Equal(LandImpact.Measure(source).ViewerStreamingCostTotal, c.Before, 9);
        Assert.Equal(LandImpact.Measure(output).ViewerStreamingCostTotal, c.After, 9);
        Assert.Equal(c.After > c.Before, c.Heavier);
    }

    [Fact]
    public void AHeavierBuildIsSaidToBeHeavierWithTheBothCosts()
    {
        string text = new CostComparison(29.18, 251.02).ToText();
        Assert.Contains("29.18", text);
        Assert.Contains("251.02", text);
        Assert.Contains("heavier", text);
        Assert.DoesNotContain("not heavier", text);
    }

    [Fact]
    public void ALighterBuildIsSaidNotToBeHeavier()
    {
        CostComparison c = new CostComparison(30, 12.5);
        Assert.False(c.Heavier);
        Assert.Contains("not heavier", c.ToText());
    }

    [Fact]
    public void TheMergeReportCarriesTheComparisonWhenTheCallerMeasuredIt()
    {
        string source = MapsFixture.Write(), output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeWriteResult r = MergeArchiveWriter.Write(source, output);
        Assert.DoesNotContain("viewer streaming cost", r.ToText());
        r.Cost = new CostComparison(29.18, 251.02);
        Assert.Contains("viewer streaming cost", r.ToText());
        Assert.Contains("251.02", r.ToText());
    }
}
