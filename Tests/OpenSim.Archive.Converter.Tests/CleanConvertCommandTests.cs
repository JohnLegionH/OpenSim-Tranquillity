/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>The oar-convert command line: cleaning is on unless --no-clean is given, and --version names the version.</summary>
// A command writes to Console.Out, which is process-wide, and CleanConvertCommandTests redirects it to read the version a tool prints:
// every class that runs a command in process is in this one collection, so they never run together.
[Collection("Console output")]
public class CleanConvertCommandTests
{
    private static long ConvertedSize(params string[] extra)
    {
        KitPart part = new KitPart("Ring") { Shape = FaceMatrix.Shape("ring", "hollow"), Scale = "2,3,4" };
        string source = EligibilityKit.WriteArchive(part);
        string output = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--out", output }.Concat(extra).ToArray()));
        return new FileInfo(output).Length;
    }

    [Fact]
    public void NoCleanWritesTheLargerUncleanedMeshAndTheDefaultWritesTheCleanedOne()
    {
        long cleaned = ConvertedSize();
        long uncleaned = ConvertedSize("--no-clean");
        Assert.True(cleaned < uncleaned, cleaned + " bytes cleaned, " + uncleaned + " uncleaned");
    }

    [Fact]
    public void VersionPrintsTheProgramAndItsVersion()
    {
        TextWriter saved = Console.Out;
        StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        try { Assert.Equal(0, ConvertCommand.Run(new[] { "--version" })); }
        finally { Console.SetOut(saved); }
        Assert.Contains("oar-convert 0.4.1", capture.ToString());
    }

    private static string VersionLine(Func<string[], int> run)
    {
        TextWriter saved = Console.Out;
        StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        try { Assert.Equal(0, run(new[] { "--version" })); }
        finally { Console.SetOut(saved); }
        return capture.ToString().Trim();
    }

    [Fact]
    public void AllFourToolsReportTheSameRelease()
    {
        Assert.Equal("0.4.1", ToolRelease.Version);
        Assert.Equal("oar-convert 0.4.1", VersionLine(ConvertCommand.Run));
        Assert.Equal("oar-export 0.4.1", VersionLine(ExportCommand.Run));
        Assert.Equal("oar-restore 0.4.1", VersionLine(RestoreCommand.Run));
        Assert.Equal("oar-import 0.4.1", VersionLine(ImportCommand.Run));
    }

    [Fact]
    public void TheStampWrittenIntoAGlbStaysAtTheFileFormatVersion()
    {
        Assert.Equal("0.2", OpenSim.Archive.GltfExport.ExportStamp.CurrentVersion);
    }
}
