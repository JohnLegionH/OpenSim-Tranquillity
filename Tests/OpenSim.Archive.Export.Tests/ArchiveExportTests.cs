/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text.Json;
using OpenMetaverse;
using Xunit;
using static OpenSim.Archive.Export.Tests.TestKit;

namespace OpenSim.Archive.Export.Tests;

/// <summary>A whole archive in, a folder per build out; the archive itself untouched.</summary>
public class ArchiveExportTests
{
    private static (string archive, string outDir, PartSpec treeRoot, PartSpec houseRoot) MakeArchive()
    {
        PartSpec houseRoot = new PartSpec { Name = "House", Link = 1 };
        PartSpec wall2 = new PartSpec { Name = "Wall", Link = 3, Offset = new Vector3(2, 0, 0) };
        PartSpec wall1 = new PartSpec { Name = "Wall", Link = 2, Offset = new Vector3(-2, 0, 0) };
        PartSpec treeRoot = new PartSpec { Name = "Oak", Link = 0, Shape = Box(pcode: 255) };
        PartSpec held = new PartSpec { Name = "Bought", Link = 0, Creator = Alice, Owner = Bob, BaseMask = (uint)PermissionMask.Export | 0x7fff, OwnerMask = 0x7fff, EveryoneMask = 0 };

        string dir = NewFolder();
        string archive = Path.Combine(dir, "build.oar");
        File.WriteAllBytes(archive, Oar(("House_1", ObjectXml(houseRoot, wall2, wall1)), ("Oak_2", ObjectXml(treeRoot)), ("Bought_3", ObjectXml(held))));
        return (archive, Path.Combine(dir, "out"), treeRoot, houseRoot);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void WritesAFolderPerBuildWithAGlbAndAManifestAndLeavesTheArchiveAlone()
    {
        (string archive, string outDir, PartSpec tree, PartSpec house) = MakeArchive();
        string before = Hash(archive);

        ExportSummary s = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir });

        Assert.Equal(before, Hash(archive));
        Assert.Equal("Complete", s.ReadStatus);
        Assert.Equal(3, s.Builds);
        Assert.Equal(2, s.BuildsWithAFile);
        Assert.Equal(5, s.Parts);
        Assert.Equal(3, s.Exported);   // house x3 (one placeheld tree, one skipped, so: 3 of the house)
        Assert.Equal(1, s.Placeheld);
        Assert.Equal(1, s.Skipped);
        Assert.True(s.OutputBytes > 0);
        Assert.True(File.Exists(Path.Combine(outDir, "export-summary.json")));

        SummaryBuild h = s.BuildList.Single(b => b.Name == "House");
        string folder = Path.Combine(outDir, h.Folder);
        Assert.True(File.Exists(Path.Combine(folder, h.File)));
        Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(folder, h.File)));
        Assert.Equal(new[] { "House", "Wall", "Wall" }, glb.PartNodes.Select(n => n.GetProperty("name").GetString()).ToArray());

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        JsonElement root = manifest.RootElement;
        Assert.Equal("build.oar", root.GetProperty("archive").GetString());
        Assert.Equal(house.Id.ToString(), root.GetProperty("rootPartUuid").GetString());
        Assert.Equal(new[] { 1, 2, 3 }, root.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("linkNumber").GetInt32()).ToArray());
        Assert.DoesNotContain(Path.GetDirectoryName(archive), File.ReadAllText(Path.Combine(folder, "manifest.json")));

        SummaryBuild skipped = s.BuildList.Single(b => b.Name == "Bought");
        Assert.Null(skipped.File);
        Assert.Contains("skipped: creator did not allow export", s.Reasons.Keys.Single(k => k.StartsWith("skipped", StringComparison.Ordinal)));
        Assert.Contains(s.Reasons.Keys, k => k.StartsWith("placeheld: built-in tree", StringComparison.Ordinal));
        _ = tree;
    }

    [Fact]
    public void ObjectFilterTakesARootIdOrAName()
    {
        (string archive, string outDir, PartSpec tree, PartSpec house) = MakeArchive();
        ExportSummary byId = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir + "-id", Object = house.Id.ToString() });
        Assert.Equal(new[] { "House" }, byId.BuildList.Select(b => b.Name).ToArray());
        ExportSummary byName = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir + "-name", Object = "oak" });
        Assert.Equal(new[] { "Oak" }, byName.BuildList.Select(b => b.Name).ToArray());
        Assert.Equal(tree.Id.ToString(), byName.BuildList.Single().RootPartUuid);
        Assert.Empty(ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir + "-none", Object = "nothing by this name" }).BuildList);
    }

    [Fact]
    public void LimitStopsAfterThatManyBuilds()
    {
        (string archive, string outDir, _, _) = MakeArchive();
        ExportSummary s = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir, Limit = 2 });
        Assert.Equal(2, s.Builds);
        Assert.Equal(2, Directory.GetDirectories(outDir).Length);
    }

    [Fact]
    public void AnInventoryArchiveIsRefused()
    {
        string dir = NewFolder();
        string iar = Path.Combine(dir, "items.iar");
        File.WriteAllBytes(iar, Tar(new[] { ("archive.xml", IarControl()) }));
        Assert.Throws<InvalidDataException>(() => ArchiveExporter.Export(iar, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") }));
    }

    [Fact]
    public void TheOutputFolderCannotBeTheArchiveItself()
    {
        (string archive, _, _, _) = MakeArchive();
        Assert.Throws<ArgumentException>(() => ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = archive }));
        Assert.Throws<ArgumentException>(() => ArchiveExporter.Export(archive, new ExportOptions()));
    }

    [Fact]
    public void AFileThatIsNotAnArchiveIsReportedAsSuch()
    {
        string dir = NewFolder();
        string junk = Path.Combine(dir, "junk.oar");
        File.WriteAllBytes(junk, new byte[3000]);
        ExportSummary s = ArchiveExporter.Export(junk, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        Assert.Equal("NotAnArchive", s.ReadStatus);
        Assert.Equal(0, s.Builds);
    }

    [Fact]
    public void ACutOffArchiveExportsWhatWasRead()
    {
        (string archive, string outDir, _, _) = MakeArchive();
        string cut = Path.Combine(Path.GetDirectoryName(archive), "cut.oar");
        // a plain tar cut inside its third entry: the first object is whole, the rest is lost
        PartSpec house = new PartSpec { Name = "House", Link = 0 };
        byte[] whole = Tar(new[] { ("archive.xml", OarControl()), ("objects/House_1.xml", Utf8(ObjectXml(house))), ("objects/Tree_2.xml", Utf8(ObjectXml(new PartSpec { Name = "Tree" }))) }, gzip: false);
        File.WriteAllBytes(cut, whole.Take(whole.Length - 1500).ToArray());
        ExportSummary s = ArchiveExporter.Export(cut, new ExportOptions { OutputDirectory = outDir });
        Assert.Equal("Partial", s.ReadStatus);
        Assert.Equal(1, s.Builds);
        Assert.Equal("House", s.BuildList.Single().Name);
    }

    [Fact]
    public void TheCommandLineWritesTheSameAndSetsExitCodes()
    {
        (string archive, string outDir, _, _) = MakeArchive();
        Assert.Equal(0, RunCli("oar-export", archive, "--out", outDir, "--limit", "1"));
        Assert.Single(Directory.GetDirectories(outDir));
        Assert.Equal(64, RunCli("oar-export", archive));
        Assert.Equal(64, RunCli("oar-export", archive, "--out", outDir, "--limit", "0"));
        Assert.Equal(64, RunCli("oar-export", "--out", outDir));
        Assert.Equal(2, RunCli("oar-export", archive, "--out", archive));
    }

    private static int RunCli(params string[] args)
    {
        // Console.Out and Console.Error are process-wide, so the tests that redirect them run one at a time
        lock (ConsoleLock)
        {
            TextWriter o = Console.Out, e = Console.Error;
            try
            {
                Console.SetOut(new StringWriter());
                Console.SetError(new StringWriter());
                return OpenSim.ArchiveInspector.Program.Main(args);
            }
            finally
            {
                Console.SetOut(o);
                Console.SetError(e);
            }
        }
    }

    private static readonly object ConsoleLock = new object();
}
