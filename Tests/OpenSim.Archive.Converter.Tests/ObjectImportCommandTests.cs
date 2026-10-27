/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml.Linq;
using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>The oar-import command line. Each test writes its own folder and archive, so the class runs in parallel with others.</summary>
public class ObjectImportCommandTests
{
    [Fact]
    public void TheCommandWritesTheArchiveAndExitsWithZero()
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap: true);
        string output = ImportKit.OutputPath();
        int exit = ImportCommand.Run(new[] { folder.Path, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--name", "Command Slab", "--description", "made by the command",
            "--position", "11.5,22.5,33" });
        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        XElement part = ImportKit.Part(ImportKit.ReadOar(output));
        Assert.Equal("Command Slab", ImportKit.Text(part, "Name"));
        Assert.Equal("made by the command", ImportKit.Text(part, "Description"));
        Assert.Equal("11.5", ImportKit.Text(part, "GroupPosition/X"));
        Assert.Equal("22.5", ImportKit.Text(part, "GroupPosition/Y"));
    }

    [Fact]
    public void ACommandLineWithoutAnAvatarOrAnOutputOrWithAnUnknownOptionExitsWith64AndWritesNothing()
    {
        ImportKit.Folder folder = ImportKit.Write();
        string output = ImportKit.OutputPath();
        Assert.Equal(64, ImportCommand.Run(new[] { folder.Path, "--out", output }));
        Assert.Equal(64, ImportCommand.Run(new[] { folder.Path, "--avatar", ImportKit.Avatar.ToString("D") }));
        Assert.Equal(64, ImportCommand.Run(new[] { folder.Path, "--out", output, "--avatar", "not-a-uuid" }));
        Assert.Equal(64, ImportCommand.Run(new[] { folder.Path, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--frobnicate" }));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AFolderThatCannotBeImportedExitsWith2()
    {
        string empty = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        string output = ImportKit.OutputPath();
        Assert.Equal(2, ImportCommand.Run(new[] { empty, "--out", output, "--avatar", ImportKit.Avatar.ToString("D") }));
        Assert.False(File.Exists(output));
    }
}
