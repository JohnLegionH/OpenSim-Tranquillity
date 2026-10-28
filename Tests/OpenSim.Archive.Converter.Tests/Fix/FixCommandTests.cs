/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.ArchiveInspector;
using OpenSim.Archive.ObjectInspect;
using Xunit;
using static OpenSim.Archive.ObjectInspect.Tests.InspectKit;
using static OpenSim.Archive.Converter.Tests.Fix.FixKit;

namespace OpenSim.Archive.Converter.Tests.Fix;

/// <summary>The oar-convert command line for --fix-perms and --relink-from.</summary>
// A command writes to Console.Out, which is process-wide: every class that runs a command in process is in this one collection.
[Collection("Console output")]
public class FixCommandTests
{
    private static (int code, string output, string error) Run(params string[] args)
    {
        TextWriter savedOut = Console.Out, savedErr = Console.Error;
        StringWriter output = new StringWriter(), error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            int code = ConvertCommand.Run(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    private static string AlicesChair(string folder)
    {
        KPart p = new KPart { Name = "Chair", Link = 1, Creator = Alice, BaseMask = M, OwnerMask = C, NextMask = 0 };
        return WriteInput(folder, "build.oar", Oar(new[] { p }));
    }

    [Fact]
    public void FixPermsWithACreatorWritesTheArchiveAndPrintsTheChangeReport()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var r = Run(source, "--fix-perms", "--creator", Alice.ToString(), "--out", dest);

        Assert.Equal(0, r.code);
        Assert.True(File.Exists(dest));
        Assert.Contains("Chair", r.output);
        Assert.Contains("M-- -> MCT", r.output);
        Assert.Equal("MCT", MaskSet.Letters(ObjectReader.Read(dest).Objects.One().Parts.One().Masks.Base));
    }

    [Fact]
    public void FixPermsTakesSeveralCreatorsSeparatedByCommas()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var r = Run(source, "--fix-perms", "--creator", Bob + "," + Alice, "--out", dest);

        Assert.Equal(0, r.code);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void FixPermsWithoutACreatorIsRefusedAndNothingIsWritten()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var r = Run(source, "--fix-perms", "--out", dest);

        Assert.Equal(2, r.code);
        Assert.Contains("--creator", r.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void ACreatorThatIsNotAnIdIsRefused()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var r = Run(source, "--fix-perms", "--creator", "Alice", "--out", dest);

        Assert.Equal(2, r.code);
        Assert.Contains("Alice", r.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void CreatorWithoutFixPermsIsRefused()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var r = Run(source, "--creator", Alice.ToString(), "--out", dest);

        Assert.Equal(2, r.code);
        Assert.Contains("--fix-perms", r.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void FixPermsNeedsAnOutputAndNeverOverwritesTheInput()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder);
        byte[] original = File.ReadAllBytes(source);

        var noOut = Run(source, "--fix-perms", "--creator", Alice.ToString());
        Assert.Equal(2, noOut.code);
        Assert.Contains("--out is required with --fix-perms", noOut.error);

        var same = Run(source, "--fix-perms", "--creator", Alice.ToString(), "--out", source);
        Assert.Equal(2, same.code);
        Assert.Contains("the output is the source archive", same.error);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void FixPermsCannotBeCombinedWithMergeOrRelink()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar");

        var merge = Run(source, "--fix-perms", "--creator", Alice.ToString(), "--merge", "--out", dest, "--merge-plan-dir", Path.Combine(folder, "plan"));
        Assert.Equal(2, merge.code);
        Assert.Contains("--fix-perms cannot be combined with --merge or --relink-from", merge.error);

        var relink = Run(source, "--fix-perms", "--creator", Alice.ToString(), "--relink-from", source, "--out", dest);
        Assert.Equal(2, relink.code);
        Assert.Contains("--fix-perms cannot be combined with --merge or --relink-from", relink.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void TheReportFolderGetsATextAndAJsonReport()
    {
        string folder = NewFolder();
        string source = AlicesChair(folder), dest = Path.Combine(folder, "fixed.oar"), report = Path.Combine(folder, "report");

        var r = Run(source, "--fix-perms", "--creator", Alice.ToString(), "--out", dest, "--report", report);

        Assert.Equal(0, r.code);
        Assert.True(File.Exists(Path.Combine(report, "fix-perms-report.txt")));
        using System.Text.Json.JsonDocument json = Json(File.ReadAllText(Path.Combine(report, "fix-perms-report.json")));
        Assert.True(json.RootElement.GetProperty("changes").GetArrayLength() >= 1);
    }

    // ------------------------------------------------------------------ --relink-from

    private static KPart P(string name, int link, float x = 0, float y = 0, float z = 0) => new KPart { Name = name, Link = link, X = x, Y = y, Z = z };

    private static (string reference, string source, string dest, string folder) Table()
    {
        string folder = NewFolder();
        KPart[] reference = { P("Table", 1), P("Top", 2, 0, 0, 1), P("Leg", 3, 1, 1, -1) };
        KPart[] scrambled = { P("Table", 1), P("Leg", 2, 1, 1, -1), P("Top", 3, 0, 0, 1) };
        return (WriteInput(folder, "reference.oar", Oar(reference)), WriteInput(folder, "build.oar", Oar(scrambled)), Path.Combine(folder, "relinked.oar"), folder);
    }

    [Fact]
    public void RelinkFromWritesTheArchiveAndPrintsTheReport()
    {
        var (reference, source, dest, _) = Table();

        var r = Run(source, "--relink-from", reference, "--out", dest);

        Assert.Equal(0, r.code);
        Assert.True(File.Exists(dest));
        Assert.Contains("OBJECT \"Table\"", r.output);
        Assert.Contains("\"Leg\": link 2 -> 3", r.output);
        Assert.Equal(new[] { "Table", "Top", "Leg" }, ObjectReader.Read(dest).Objects.One().Parts.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void RelinkFromNeedsAnOutputAndNeverOverwritesTheInput()
    {
        var (reference, source, dest, _) = Table();
        byte[] original = File.ReadAllBytes(source);

        var noOut = Run(source, "--relink-from", reference);
        Assert.Equal(2, noOut.code);
        Assert.Contains("--out is required with --relink-from", noOut.error);

        var same = Run(source, "--relink-from", reference, "--out", source);
        Assert.Equal(2, same.code);
        Assert.Contains("the output is the source archive", same.error);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void ARelinkReferenceThatIsNotThereIsRefused()
    {
        var (_, source, dest, folder) = Table();

        var r = Run(source, "--relink-from", Path.Combine(folder, "missing.oar"), "--out", dest);

        Assert.Equal(2, r.code);
        Assert.Contains("missing.oar", r.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void RelinkFromCannotBeCombinedWithMerge()
    {
        var (reference, source, dest, folder) = Table();

        var r = Run(source, "--relink-from", reference, "--merge", "--out", dest, "--merge-plan-dir", Path.Combine(folder, "plan"));

        Assert.Equal(2, r.code);
        Assert.Contains("--relink-from cannot be combined with --merge", r.error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void TheRelinkReportFolderGetsATextAndAJsonReport()
    {
        var (reference, source, dest, folder) = Table();
        string report = Path.Combine(folder, "report");

        var r = Run(source, "--relink-from", reference, "--out", dest, "--report", report);

        Assert.Equal(0, r.code);
        Assert.True(File.Exists(Path.Combine(report, "relink-report.txt")));
        using System.Text.Json.JsonDocument json = Json(File.ReadAllText(Path.Combine(report, "relink-report.json")));
        Assert.Equal(1, json.RootElement.GetProperty("objectsChanged").GetInt32());
    }

    [Fact]
    public void TheUsageTextDescribesTheRelinkMode()
    {
        var r = Run("--help");
        Assert.Contains("--relink-from", r.output);
        Assert.Contains("link order", r.output);
    }

    [Fact]
    public void TheUsageTextDescribesTheFixPermsMode()
    {
        var r = Run("--help");
        Assert.Contains("--fix-perms", r.output);
        Assert.Contains("--creator", r.output);
    }
}
