/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Eligibility.Tests;
using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The files oar-convert --report writes use LF on every platform, as the merge plan folder does, so the same run gives the same
/// bytes on Windows and on Linux. Each test writes its own archive and folders; the class shares the console collection because a
/// command writes to Console.Out.
/// </summary>
[Collection("Console output")]
public class ReportFileLineEndingTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "report-line-ending-tests", Guid.NewGuid().ToString("N"));

    private static string Source()
    {
        List<KitPart> parts = Enumerable.Range(1, 4).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1" }).ToList();
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { parts });
    }

    private static void AssertLf(string path)
    {
        string text = File.ReadAllText(path);
        Assert.True(text.Split('\n').Length > 3, path + " holds more than a few lines");
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void TheConvertReportFilesHaveNoCarriageReturns()
    {
        string source = Source(), report = Folder(), output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--out", output, "--report", report }));
        AssertLf(Path.Combine(report, "convert-report.txt"));
        AssertLf(Path.Combine(report, "convert-report.json"));
    }

    [Fact]
    public void TheMergeReportFileHasNoCarriageReturns()
    {
        string source = Source(), report = Folder(), output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--out", output, "--merge-plan-dir", Folder(), "--report", report }));
        AssertLf(Path.Combine(report, "merge-report.txt"));
    }

    [Fact]
    public void ThePlanOnlyReportFileHasNoCarriageReturns()
    {
        string source = Source(), report = Folder();
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--plan-only", "--merge-plan-dir", Folder(), "--report", report }));
        AssertLf(Path.Combine(report, "merge-report.txt"));
    }
}
