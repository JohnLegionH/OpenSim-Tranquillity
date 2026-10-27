/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The oar-convert --merge command that writes an archive. Each test writes its own archive and folder, and shares nothing, so the
/// class runs in parallel with others.
/// </summary>
[Collection("Console output")]
public class MergeCommandTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "merge-command-tests", Guid.NewGuid().ToString("N"));

    private static string Source()
    {
        List<KitPart> parts = Enumerable.Range(1, 4).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1" }).ToList();
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { parts });
    }

    [Fact]
    public void MergeWithAnOutputWritesTheMergedArchiveAndThePlanFiles()
    {
        string source = Source(), folder = Folder(), output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--out", output, "--merge-plan-dir", folder }));
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(folder, "merge-plan.json")));
        int parts = 0;
        new ArchiveEligibility(new EligibilityOptions { PartObserver = _ => parts++ }).Evaluate(output);
        Assert.Equal(1, parts);
    }

    [Fact]
    public void MergeNeedsBothAnOutputAndAPlanFolderUnlessItOnlyPlans()
    {
        string source = Source(), folder = Folder(), output = Folder() + ".oar";
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--merge-plan-dir", folder }));
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--out", output }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnOutputThatExistsIsRefusedAndNotChanged()
    {
        string source = Source(), folder = Folder(), output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, "keep me");
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--out", output, "--merge-plan-dir", folder }));
        Assert.Equal("keep me", File.ReadAllText(output));
    }

    [Fact]
    public void TheReportNamesTheMergedPartsAndTheWorstDifferencesFromTheOriginals()
    {
        string source = Source(), output = Folder() + ".oar";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeWriteResult r = MergeArchiveWriter.Write(source, output);
        string text = r.ToText();
        Assert.Contains("merged parts 1", text);
        Assert.Contains("worst position", text);
        Assert.Contains("worst texture coordinate", text);
        Assert.Contains("as written", text);
        Assert.Contains(r.Parts[0].Name, text);
    }
}
