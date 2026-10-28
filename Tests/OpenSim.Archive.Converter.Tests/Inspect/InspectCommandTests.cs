/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenSim.ArchiveInspector;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;

namespace OpenSim.Archive.ObjectInspect.Tests;

/// <summary>The oar-inspect command line.</summary>
// A command writes to Console.Out, which is process-wide: every class that runs a command in process is in this one collection.
[Collection("Console output")]
public class InspectCommandTests
{
    private static KPart Part(string name, int link) => new KPart { Name = name, Link = link };

    private static (int code, string output, string error) Run(params string[] args)
    {
        TextWriter savedOut = Console.Out, savedErr = Console.Error;
        StringWriter output = new StringWriter(), error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            int code = InspectCommand.Run(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    private static string TableOar(string folder, string name = "table.oar", int legNext = (int)All)
    {
        KPart table = Part("Table", 1), leg = Part("Leg", 3), top = Part("Top", 2);
        leg.NextMask = (uint)legNext;
        return WriteFile(folder, name, Oar(new[] { table, leg, top }, new[] { Part("Lamp", 1) }));
    }

    [Fact]
    public void ByDefaultTheReportIsReadableTextOnStandardOutputAndTheExitCodeIsZero()
    {
        string path = TableOar(NewFolder());
        var (code, output, error) = Run(path);
        Assert.Equal(0, code);
        Assert.Contains("SUMMARY", output);
        Assert.Contains("OBJECT 1 \"Table\"", output);
        Assert.Contains("archive: " + path, output);
        Assert.Equal("", error);
    }

    [Fact]
    public void CsvAndJsonAreChosenByFlag()
    {
        string path = TableOar(NewFolder());
        var csv = Run(path, "--csv");
        Assert.Equal(0, csv.code);
        Assert.StartsWith("kind,object,entry,", csv.output);

        var json = Run(path, "--json");
        Assert.Equal(0, json.code);
        using JsonDocument doc = Json(json.output);
        Assert.Equal(2, doc.RootElement.GetProperty("summary").GetProperty("objects").GetInt32());
    }

    [Fact]
    public void BothCsvAndJsonAtOnceIsAUsageError()
    {
        string path = TableOar(NewFolder());
        var r = Run(path, "--csv", "--json");
        Assert.Equal(64, r.code);
        Assert.Contains("only one of --csv and --json", r.error);
    }

    [Fact]
    public void FilterKeepsTheObjectsWhoseNameContainsTheText()
    {
        string path = TableOar(NewFolder());
        var r = Run(path, "--filter", "lamp");
        Assert.Equal(0, r.code);
        Assert.Contains("OBJECT 1 \"Lamp\"", r.output);
        Assert.DoesNotContain("\"Table\"", r.output);
        Assert.Contains("filter: \"lamp\" (1 of 2 objects)", r.output);
    }

    [Fact]
    public void OutWritesTheReportToAFileAndPrintsNothing()
    {
        string folder = NewFolder();
        string path = TableOar(folder);
        string outPath = Path.Combine(folder, "report.csv");
        var r = Run(path, "--csv", "--out", outPath);
        Assert.Equal(0, r.code);
        Assert.Equal("", r.output);
        Assert.True(File.Exists(outPath), "the report file was not written");
        Assert.StartsWith("kind,object,entry,", File.ReadAllText(outPath));
    }

    [Fact]
    public void OutNamingTheArchiveItselfIsRefusedAndTheArchiveIsLeftAlone()
    {
        string path = TableOar(NewFolder());
        byte[] before = File.ReadAllBytes(path);
        var r = Run(path, "--out", path);
        Assert.Equal(64, r.code);
        Assert.Contains("--out is the archive itself", r.error);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CompareWithAnIdenticalArchiveSaysSoAndExitsZero()
    {
        string folder = NewFolder();
        string a = TableOar(folder, "a.oar"), b = TableOar(folder, "b.oar");
        // two files written from different part ids but the same names, shapes and masks
        var r = Run(b, "--compare", a);
        Assert.Equal(0, r.code);
        Assert.Contains("no differences", r.output);
        Assert.Contains("reference: " + a, r.output);
        Assert.Contains("archive: " + b, r.output);
    }

    [Fact]
    public void CompareWithADifferentArchiveListsTheDifferencesAndExitsThree()
    {
        string folder = NewFolder();
        string before = TableOar(folder, "before.oar");
        string after = TableOar(folder, "after.oar", legNext: (int)(M | T));
        var r = Run(after, "--compare", before);
        Assert.Equal(3, r.code);
        Assert.Contains("\"Leg\": next owner mask MCT -> M-T", r.output);
    }

    [Fact]
    public void CompareHonoursTheOutputFormat()
    {
        string folder = NewFolder();
        string before = TableOar(folder, "before.oar");
        string after = TableOar(folder, "after.oar", legNext: (int)(M | T));
        var csv = Run(after, "--compare", before, "--csv");
        Assert.Equal(3, csv.code);
        Assert.StartsWith("object,area,part,field,reference,archive", csv.output);
        var json = Run(after, "--compare", before, "--json");
        using JsonDocument doc = Json(json.output);
        Assert.False(doc.RootElement.GetProperty("identical").GetBoolean());
    }

    [Fact]
    public void FilterAlsoNarrowsACompare()
    {
        string folder = NewFolder();
        string before = TableOar(folder, "before.oar");
        string after = TableOar(folder, "after.oar", legNext: (int)(M | T));
        var r = Run(after, "--compare", before, "--filter", "lamp");
        Assert.Equal(0, r.code);
        Assert.Contains("no differences", r.output);
    }

    [Fact]
    public void AnIarIsReadTheSameWay()
    {
        string folder = NewFolder();
        IarItem item = new IarItem { Name = "Chair box", Folder = "Builds", AssetType = 6, ObjectXml = OriginalGroup(Part("Chair", 1)) };
        string path = WriteFile(folder, "inv.iar", Iar(item));
        var r = Run(path);
        Assert.Equal(0, r.code);
        Assert.Contains("kind: IAR", r.output);
        Assert.Contains("Builds / Chair box [Object]", r.output);
    }

    [Fact]
    public void AMissingOrNonArchiveFileExitsTwoAndSaysWhy()
    {
        string folder = NewFolder();
        var missing = Run(Path.Combine(folder, "nothing.oar"));
        Assert.Equal(2, missing.code);
        Assert.Contains("not an archive", missing.output + missing.error);

        string text = WriteFile(folder, "text.oar", Utf8(new string('x', 2000)));
        var r = Run(text);
        Assert.Equal(2, r.code);
    }

    [Fact]
    public void AMissingReferenceExitsTwo()
    {
        string path = TableOar(NewFolder());
        var r = Run(path, "--compare", Path.Combine(Path.GetDirectoryName(path), "nothing.oar"));
        Assert.Equal(2, r.code);
        Assert.Contains("reference", r.error);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--filter")]
    [InlineData("--compare")]
    [InlineData("--out")]
    public void BadArgumentsAreAUsageError(string flag)
    {
        string path = TableOar(NewFolder());
        var r = Run(path, flag);
        Assert.Equal(64, r.code);
        Assert.Contains("Usage:", r.error);
    }

    [Fact]
    public void NoArchiveGivenIsAUsageError()
    {
        var r = Run();
        Assert.Equal(64, r.code);
        Assert.Contains("no archive file given", r.error);
    }

    [Fact]
    public void HelpPrintsTheUsageAndExitsZero()
    {
        var r = Run("--help");
        Assert.Equal(0, r.code);
        Assert.Contains("Usage: oar-inspect", r.output);
        Assert.Contains("--compare", r.output);
    }

    [Fact]
    public void VersionPrintsTheProgramAndItsRelease()
    {
        var r = Run("--version");
        Assert.Equal(0, r.code);
        Assert.Equal("oar-inspect " + ToolRelease.Version, r.output.Trim());
    }

    [Fact]
    public void TheArchiveIsOnlyReadNothingElseIsWrittenBesideIt()
    {
        string folder = NewFolder();
        string path = TableOar(folder);
        byte[] before = File.ReadAllBytes(path);
        Assert.Contains("SUMMARY", Run(path).output);
        Assert.StartsWith("kind,", Run(path, "--csv").output);
        Assert.Contains("\"identical\": true", Run(path, "--json", "--compare", path).output);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(folder));
    }
}
