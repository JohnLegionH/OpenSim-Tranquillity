/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The files of a merge plan folder have the same bytes on every platform: line feeds only, never the line ending of the platform that wrote them.
/// Each test writes its own plan folder and shares no process state, so the class runs in parallel with the others.
/// </summary>
public class MergePlanLineEndingTests
{
    private static string PlanFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "merge-plan-eol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void EveryFileOfAPlanFolderUsesLineFeedsOnly()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions());
        string folder = PlanFolder();
        MergePlanFiles.Write(plan, folder);

        string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => Path.GetFileName(f) == "merge-plan.json");
        Assert.Contains(files, f => Path.GetFileName(f) == "merge-plan.txt");
        Assert.True(plan.Linksets.Count > 0);
        Assert.Equal(plan.Linksets.Count, Directory.GetFiles(Path.Combine(folder, "linksets")).Length);
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length > 0, file + " is empty");
            Assert.False(Array.IndexOf(bytes, (byte)'\r') >= 0, Path.GetFileName(file) + " holds a carriage return");
            Assert.True(Array.IndexOf(bytes, (byte)'\n') >= 0, Path.GetFileName(file) + " has no line break to check");
        }
    }

    [Fact]
    public void TheTextAndJsonOfAPlanHoldNoCarriageReturn()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(MapsFixture.Write(), new MergeOptions());
        Assert.DoesNotContain('\r', MergePlanFiles.ReportText(plan));
        Assert.DoesNotContain('\r', MergePlanFiles.ArchiveJson(plan));
        Assert.Contains('\n', MergePlanFiles.ReportText(plan));
    }
}
