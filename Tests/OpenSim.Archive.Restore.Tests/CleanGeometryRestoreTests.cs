/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.GltfExport;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>The restore reads the exporter's stamp: it compares a cleaned set with a cleaned fresh export and an unstamped set with an uncleaned one.</summary>
public class CleanGeometryRestoreTests
{
    private static void AssertEveryEntryIdentical(Lset l)
    {
        Dictionary<string, byte[]> before = l.Before, after = l.After;
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (string k in before.Keys)
            Assert.True(before[k].SequenceEqual(after[k]), "entry differs: " + k);
    }

    [Fact]
    public void ACleanedSetCarriesTheStampAndRestoresUntouchedWithParityAndEveryEntryIdentical()
    {
        Lset l = new Lset();
        ExportStamp stamp = GlbFile.PeekStamp(l.GlbPath);
        Assert.NotNull(stamp);
        Assert.Equal("0.2", stamp.Version);
        Assert.True(stamp.Clean);

        RestoreResult r = l.Restore();

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass);
        Assert.True(r.ComparedAsCleaned);
        AssertEveryEntryIdentical(l);
    }

    [Fact]
    public void AnUnstampedSetFromTheEarlierExporterStillRestoresUntouchedWithParityAndEveryEntryIdentical()
    {
        Lset l = new Lset();
        string old = Path.Combine(l.Dir, "glb-v01");
        ExportSummary export = ArchiveExporter.Export(l.Archive, new ExportOptions { OutputDirectory = old, ImageCheck = () => null, Clean = false });
        Assert.Equal(0, export.ExitCode);
        Assert.Null(GlbFile.PeekStamp(Directory.GetFiles(old, "*.glb", SearchOption.AllDirectories).Single()));

        RestoreResult r = new ArchiveRestorer(new RestoreOptions { WorkFolder = Path.Combine(l.Dir, "work-v01") }).Restore(l.Archive, old, l.Out());

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass);
        Assert.False(r.ComparedAsCleaned);
        AssertEveryEntryIdentical(l);
    }

    [Fact]
    public void AStampNamingAVersionThisBuildDoesNotKnowIsRefusedWithAMessageThatSaysSo()
    {
        Lset l = new Lset();
        byte[] glb = File.ReadAllBytes(l.GlbPath);
        string text = Encoding.Latin1.GetString(glb);
        string from = "\"oarExport\":{\"version\":\"0.2\"";
        Assert.Contains(from, text);
        File.WriteAllBytes(l.GlbPath, Encoding.Latin1.GetBytes(text.Replace(from, "\"oarExport\":{\"version\":\"9.9\"")));

        RestoreException e = Assert.Throws<RestoreException>(() => l.Restore());

        Assert.Contains("9.9", e.Message);
        Assert.Contains("newer", e.Message);
        Assert.Contains("0.2", e.Message);
    }

    /// <summary>A box tapered to a point: its top face and one triangle of each side have no area, so cleaning changes its triangle count.</summary>
    private static string ApexArchive()
    {
        var p = FaceMatrix.Make("box", "plain");
        p.PathTaperX = 1f;
        p.PathTaperY = 1f;
        KitPart apex = new KitPart("Apex", 1) { Scale = "2,2,2", Shape = FaceMatrix.Stored(p) };
        return EligibilityKit.WriteArchive(apex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShapeCleaningChangesIsComparedWithAFreshExportMadeTheSameWayAsTheFilesWereAndStaysUnchanged(bool clean)
    {
        string archive = ApexArchive();
        string run = Path.Combine(Path.GetDirectoryName(archive), Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(run, "glb");
        Assert.Equal(0, ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = dir, ImageCheck = () => null, Clean = clean }).ExitCode);

        RestoreResult r = new ArchiveRestorer(new RestoreOptions { WorkFolder = Path.Combine(run, "work") }).Restore(archive, dir, Path.Combine(run, "out.oar"));

        Assert.Equal(clean, r.ComparedAsCleaned);
        Assert.Equal(1, r.Count(PartOutcome.Unchanged));
        Assert.Equal(0, r.Count(PartOutcome.Rebuilt));
        Assert.Equal(0, r.ExitCode);
    }
}
