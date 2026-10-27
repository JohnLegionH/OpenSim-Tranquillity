/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>The oar-import command line with a manifest. Each test writes its own folder and archive, so the class runs in parallel with others.</summary>
public class LinksetImportCommandTests
{
    [Fact]
    public void TheManifestOptionWritesALinkedObjectAndExitsWithZero()
    {
        LinksetKit.Built built = LinksetKit.Write();
        string output = ImportKit.OutputPath();
        int exit = ImportCommand.Run(new[] { "--manifest", built.ManifestPath, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--position", "11.5,22.5,33" });
        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        List<System.Xml.Linq.XElement> parts = LinksetKit.Parts(ImportKit.ReadOar(output));
        Assert.Equal(3, parts.Count);
        Assert.Equal("11.5", ImportKit.Text(parts[0], "GroupPosition/X"));
    }

    [Fact]
    public void AFolderWithALinksetJsonInItTakesItAsTheManifestAndTheCheckPasses()
    {
        LinksetKit.Built built = LinksetKit.Write();
        string output = ImportKit.OutputPath();
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        int exit = ImportCommand.Run(new[] { built.Folder, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--check", work });
        Assert.Equal(0, exit);
        Assert.Equal(3, LinksetKit.Parts(ImportKit.ReadOar(output)).Count);
    }

    [Fact]
    public void ABadManifestExitsWith2AndWritesNothing()
    {
        LinksetKit.Built built = LinksetKit.Write(manifestOverride: @"{ ""name"": ""X"", ""parts"": [ { ""name"": ""A"" } ] }");
        string output = ImportKit.OutputPath();
        Assert.Equal(2, ImportCommand.Run(new[] { "--manifest", built.ManifestPath, "--out", output, "--avatar", ImportKit.Avatar.ToString("D") }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AManifestAndAFolderTogetherOrAMissingManifestFileExitWith64()
    {
        LinksetKit.Built built = LinksetKit.Write();
        string output = ImportKit.OutputPath();
        string avatar = ImportKit.Avatar.ToString("D");
        Assert.Equal(64, ImportCommand.Run(new[] { built.Folder, "--manifest", built.ManifestPath, "--out", output, "--avatar", avatar }));
        Assert.Equal(64, ImportCommand.Run(new[] { "--manifest", "--out", output, "--avatar", avatar }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TheHelpTextDocumentsTheManifest()
    {
        string help = ImportCommand.Usage;
        foreach (string word in new[] { "--manifest", "linkset.json", "parts", "folder", "glb", "node", "position", "rotation", "contents", "script", "notecard", "texture", "root" })
            Assert.Contains(word, help);
    }
}
