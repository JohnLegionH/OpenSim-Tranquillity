/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// The optional memory log and the heap governor. Parallel with the other classes: the governor's heap size and collection are
/// replaced by the test, the log goes to a private folder, and no static hook is used.
/// </summary>
public class MemoryLogAndHeapGovernorTests
{
    private const long Mb = 1024L * 1024;

    private static string Build(string name) => ObjectXml(new PartSpec { Name = name, Id = new UUID(new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)))), Shape = Box() });

    private static string Archive(string dir, int builds)
    {
        string path = Path.Combine(dir, "log.oar");
        File.WriteAllBytes(path, Oar(Enumerable.Range(0, builds).Select(i => ("o" + i, Build("Build" + i))).ToArray()));
        return path;
    }

    // ------------------------------------------------------------ the governor

    [Fact]
    public void TheGovernorLeavesTheHeapAloneBelowAQuarterOfTheCap()
    {
        long heap = 100 * Mb;
        int collected = 0;
        HeapGovernor g = new HeapGovernor(2048 * Mb, () => heap, () => collected++);
        Assert.Equal(512 * Mb, g.Trigger);
        g.AfterBuild();
        heap = 511 * Mb;
        g.AfterBuild();
        Assert.Equal(0, collected);
        Assert.Equal(0, g.Collections);
    }

    [Fact]
    public void TheGovernorCollectsOncePastTheTriggerAndThenWaitsForTheHeapToFillAgain()
    {
        long heap = 600 * Mb;
        int collected = 0;
        HeapGovernor g = new HeapGovernor(2048 * Mb, () => heap, () => { collected++; heap = 120 * Mb; });
        g.AfterBuild();
        Assert.Equal(1, collected);
        g.AfterBuild();   // 120 MB is below the trigger: nothing
        Assert.Equal(1, collected);
        heap = 700 * Mb;
        g.AfterBuild();
        Assert.Equal(2, collected);
        Assert.Equal(2, g.Collections);
    }

    [Fact]
    public void ARunWithALargeLiveSetCollectsLessOftenAndNeverAfterEveryBuild()
    {
        long heap = 900 * Mb;
        int collected = 0;
        // each collection leaves 800 MB: the trigger moves to twice that, capped at three quarters of the cap
        HeapGovernor g = new HeapGovernor(2048 * Mb, () => heap, () => { collected++; heap = 800 * Mb; });
        g.AfterBuild();
        Assert.Equal(1, collected);
        Assert.Equal(1536 * Mb, g.Trigger);
        heap = 810 * Mb;
        for (int i = 0; i < 10; i++)
            g.AfterBuild();
        Assert.Equal(1, collected);
    }

    [Fact]
    public void ATinyCapStillHasAFloorSoSmallRunsNeverCollect()
    {
        long heap = 63 * Mb;
        int collected = 0;
        HeapGovernor g = new HeapGovernor(8 * Mb, () => heap, () => collected++);
        Assert.Equal(64 * Mb, g.Trigger);
        g.AfterBuild();
        Assert.Equal(0, collected);
    }

    // ------------------------------------------------------------ the memory log

    [Fact]
    public void WithoutAPathNoLogIsWritten()
    {
        string dir = NewFolder();
        ArchiveExporter.Export(Archive(dir, 3), new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        Assert.All(Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories), f => Assert.EndsWith("export-progress.jsonl", f));
    }

    [Fact]
    public void TheLogHasOneLineEveryNBuildsAndOneAtTheEndWithTheSizeOfEveryStructureKeptAcrossBuilds()
    {
        string dir = NewFolder();
        string log = Path.Combine(dir, "memory.jsonl");
        ArchiveExporter.Export(Archive(dir, 10), new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), DiagnosticsPath = log, DiagnosticsEvery = 4 });
        JsonElement[] lines = File.ReadAllLines(log).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        JsonElement[] builds = lines.Where(l => l.GetProperty("phase").GetString() == "build").ToArray();
        Assert.Equal(new[] { 1, 5, 9, 10 }, builds.Select(l => l.GetProperty("count").GetInt32()).ToArray());
        Assert.Contains(lines, l => l.GetProperty("phase").GetString() == "read");
        JsonElement last = builds[^1];
        foreach (string name in new[] { "managedHeapBytes", "heapSizeAfterLastGcBytes", "gen0Bytes", "gen2Bytes", "lohBytes", "workingSetBytes",
                     "buildsHeld", "partsHeld", "summaryRowsHeld", "occurrenceEntries", "journalEntriesHeld", "usedFolderNames", "sharedFileNames",
                     "assetIndexEntries", "assetLoadedBytes", "decodedImageBytes", "sculptMapEntries", "governorCollections" })
            Assert.True(last.TryGetProperty(name, out _), name);
        Assert.Equal(10, last.GetProperty("buildsHeld").GetInt32());
        Assert.Equal(10, last.GetProperty("summaryRowsHeld").GetInt32());
        Assert.False(last.TryGetProperty("liveHeapBytes", out _));
    }

    [Fact]
    public void TheLiveOptionAddsTheHeapSizeAfterACollection()
    {
        string dir = NewFolder();
        string log = Path.Combine(dir, "memory.jsonl");
        ArchiveExporter.Export(Archive(dir, 3), new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), DiagnosticsPath = log, DiagnosticsEvery = 100, DiagnosticsCollect = true });
        JsonElement last = File.ReadAllLines(log).Select(l => JsonDocument.Parse(l).RootElement).Last();
        Assert.True(last.GetProperty("liveHeapBytes").GetInt64() > 0);
    }

    [Fact]
    public void TheLogDoesNotChangeWhatIsWritten()
    {
        string dir = NewFolder();
        string archive = Archive(dir, 6);
        ExportSummary plain = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = Path.Combine(dir, "plain") });
        ExportSummary logged = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = Path.Combine(dir, "logged"), DiagnosticsPath = Path.Combine(dir, "m.jsonl"), DiagnosticsEvery = 2 });
        Assert.Equal(plain.OutputBytes, logged.OutputBytes);
        Assert.Equal(plain.Parts, logged.Parts);
        foreach (SummaryBuild b in plain.BuildList)
        {
            SummaryBuild other = logged.BuildList.Single(x => x.RootPartUuid == b.RootPartUuid);
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "plain", b.Folder, b.File)), File.ReadAllBytes(Path.Combine(dir, "logged", other.Folder, other.File)));
        }
    }
}
