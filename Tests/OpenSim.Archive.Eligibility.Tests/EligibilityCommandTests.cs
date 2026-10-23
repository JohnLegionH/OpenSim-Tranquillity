/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Compare;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The oar-eligibility command. These tests redirect the console, which is process-wide state, so this class is the only one
/// that touches it and its tests do not run beside each other.
/// </summary>
[Collection("console")]
public class EligibilityCommandTests
{
    private static (int exit, string output, string error) Run(params string[] args)
    {
        TextWriter oldOut = Console.Out, oldErr = Console.Error;
        StringWriter o = new StringWriter(), e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try
        {
            int exit = OpenSim.ArchiveInspector.Program.Main(args);
            return (exit, o.ToString(), e.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void ItWritesTheThreeFilesAndExitsThreeWhenAPartIsKept()
    {
        string archive = EligibilityKit.WriteArchive(new KitPart("Box"), new KitPart("Phys") { Flags = "Physics" });
        string outDir = Path.Combine(Path.GetDirectoryName(archive), Guid.NewGuid().ToString("N"));

        (int exit, string output, _) = Run("oar-eligibility", archive, "--out", outDir);

        Assert.Equal(3, exit);
        Assert.Contains("SUMMARY", output);
        Assert.Contains("KEEP-PRIM (PHYSICAL)", output);
        Assert.Equal(output, File.ReadAllText(Path.Combine(outDir, "eligibility-report.txt")));
        Assert.Contains("\"schema\": \"oar-eligibility\"", File.ReadAllText(Path.Combine(outDir, "eligibility-report.json")));
        ComparePlan plan = ComparePlan.Load(Path.Combine(outDir, "eligibility-plan.json"));
        Assert.Single(plan.PartChanges);
    }

    [Fact]
    public void ItExitsZeroWhenNothingIsKept()
    {
        string archive = EligibilityKit.WriteArchive(new KitPart("Box"));
        Assert.Equal(0, Run("oar-eligibility", archive).exit);
    }

    [Fact]
    public void ItExitsTwoForABadCommandLineOrAnUnreadableFile()
    {
        Assert.Equal(2, Run("oar-eligibility").exit);
        Assert.Equal(2, Run("oar-eligibility", "--surprise", "x").exit);
        string notAnArchive = Path.Combine(Path.GetTempPath(), "archive-eligibility-tests", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notAnArchive));
        File.WriteAllText(notAnArchive, "this is not an archive");
        Assert.Equal(2, Run("oar-eligibility", notAnArchive).exit);
    }

    [Fact]
    public void TheAssumeSafeFlagIsReportedLoudly()
    {
        string archive = EligibilityKit.WriteArchive(new KitPart("Box"));
        (_, string output, _) = Run("oar-eligibility", archive, "--assume-safe-unscanned");
        Assert.Contains("WARNING: --assume-safe-unscanned is ON", output);
    }
}
