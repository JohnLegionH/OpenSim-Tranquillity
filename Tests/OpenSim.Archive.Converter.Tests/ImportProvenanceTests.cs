/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Where an imported archive came from: the archive holds a notes file that names the manifest, each part's source and the SHA-256 of every file the import read,
/// and the run report prints the same lines. Each test writes its own folders and the library is called directly, so no process state is shared and the class
/// runs in parallel with the others.
/// </summary>
public class ImportProvenanceTests
{
    private const string NotesEntry = "oar-import-notes.txt";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static ImportOptions Options() => new ImportOptions { Avatar = ImportKit.Avatar, Created = 1700000000 };

    private static string NotesOf(Dictionary<string, byte[]> oar)
    {
        Assert.True(oar.ContainsKey(NotesEntry), "the archive has no " + NotesEntry + "; its entries: " + string.Join(", ", oar.Keys));
        return Utf8.GetString(oar[NotesEntry]);
    }

    private static (ImportResult Result, Dictionary<string, byte[]> Oar, string Notes) ImportHouse(LinksetKit.Built house)
    {
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.ImportLinkset(LinksetManifest.Load(house.ManifestPath), output, Options());
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        return (result, oar, NotesOf(oar));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from))
            CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    [Fact]
    public void TheNotesNameTheManifestEachPartsSourceAndTheHashOfEveryInputFile()
    {
        LinksetKit.Built house = LinksetKit.Write();
        string notes = ImportHouse(house).Notes;
        List<string> lines = notes.Split('\n').ToList();

        Assert.Contains("manifest: linkset.json sha256 " + Sha(house.ManifestPath), lines);
        Assert.Contains("part 1 'Shell': folder shell", lines);
        Assert.Contains("part 2 'Door': glb building.glb node 'Door'", lines);
        Assert.Contains("part 3 'Window': glb building.glb node 'Window'", lines);
        foreach (string file in new[] { "shell.glb", "shell_lod2.glb", "shell_lod1.glb", "shell_lod0.glb", "shell_physics.glb" })
            Assert.Contains("  shell/" + file + " sha256 " + Sha(Path.Combine(house.Folder, "shell", file)), lines);
        Assert.Contains("  readme.txt sha256 " + Sha(house.Notecard), lines);
        Assert.Contains("  door.lsl sha256 " + Sha(house.DoorScript), lines);
        Assert.Contains("  badge.png sha256 " + Sha(house.Badge), lines);
        // the two node parts read the same file: each part lists it, under its own heading
        string building = "  building.glb sha256 " + Sha(Path.Combine(house.Folder, "building.glb"));
        Assert.Equal(2, lines.Count(l => l == building));
    }

    [Fact]
    public void TheFilesOfAPartAreListedUnderItsOwnHeadingInLinkOrder()
    {
        LinksetKit.Built house = LinksetKit.Write();
        List<string> lines = ImportHouse(house).Notes.Split('\n').ToList();
        int shell = lines.IndexOf("part 1 'Shell': folder shell"), door = lines.IndexOf("part 2 'Door': glb building.glb node 'Door'"), window = lines.IndexOf("part 3 'Window': glb building.glb node 'Window'");
        Assert.True(shell >= 0 && shell < door && door < window, "heading positions " + shell + ", " + door + ", " + window);
        Assert.Contains(lines.GetRange(shell, door - shell), l => l.StartsWith("  readme.txt sha256 ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines.GetRange(shell, door - shell), l => l.StartsWith("  door.lsl ", StringComparison.Ordinal));
        Assert.Contains(lines.GetRange(door, window - door), l => l.StartsWith("  door.lsl sha256 ", StringComparison.Ordinal));
        Assert.Contains(lines.GetRange(door, window - door), l => l.StartsWith("  badge.png sha256 ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRunReportPrintsTheSameLinesAsTheNotes()
    {
        (ImportResult result, _, string notes) = ImportHouse(LinksetKit.Write());
        string report = result.ToText();
        foreach (string line in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line + "\n", report);
    }

    [Fact]
    public void TheNotesUseLineFeedsOnly()
    {
        string notes = ImportHouse(LinksetKit.Write()).Notes;
        Assert.DoesNotContain('\r', notes);
        Assert.EndsWith("\n", notes);
    }

    [Fact]
    public void TheSameInputsInAnotherFolderGiveTheSameArchiveBytes()
    {
        LinksetKit.Built first = LinksetKit.Write();
        string moved = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        CopyFolder(first.Folder, moved);
        string outA = ImportKit.OutputPath(), outB = ImportKit.OutputPath();
        ObjectImporter.ImportLinkset(LinksetManifest.Load(first.ManifestPath), outA, Options());
        ObjectImporter.ImportLinkset(LinksetManifest.Load(Path.Combine(moved, "linkset.json")), outB, Options());
        Assert.Contains(NotesEntry, ImportKit.ReadOar(outA).Keys);
        Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
    }

    [Fact]
    public void ChangingOneInputFileChangesOnlyItsOwnLineInTheNotes()
    {
        LinksetKit.Built house = LinksetKit.Write();
        string before = ImportHouse(house).Notes;
        File.WriteAllText(house.DoorScript, LinksetKit.DoorScriptText.Replace("Open", "Shut"));
        string after = ImportHouse(house).Notes;

        List<string> changed = after.Split('\n').Except(before.Split('\n')).ToList();
        Assert.Equal("  door.lsl sha256 " + Sha(house.DoorScript), Assert.Single(changed));
        Assert.Equal(before.Split('\n').Length, after.Split('\n').Length);
    }

    [Fact]
    public void ASinglePartImportRecordsItsFolderAndTheHashOfEachFileItRead()
    {
        ImportKit.Folder folder = ImportKit.Write();
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.Import(folder.Path, output, Options());
        string notes = NotesOf(ImportKit.ReadOar(output));
        List<string> lines = notes.Split('\n').ToList();

        Assert.Contains("source folder: " + Path.GetFileName(folder.Path), lines);
        foreach (string file in new[] { ".glb", "_lod2.glb", "_lod1.glb", "_lod0.glb", "_physics.glb" })
            Assert.Contains("  " + ImportKit.Name + file + " sha256 " + Sha(Path.Combine(folder.Path, ImportKit.Name + file)), lines);
        Assert.DoesNotContain(folder.Path.Replace('\\', '/'), notes.Replace('\\', '/'));
        Assert.Contains(notes, result.ToText());
    }

    [Fact]
    public void ASinglePartImportWithoutAPhysicsFileListsOnlyTheFourLevels()
    {
        ImportKit.Folder folder = ImportKit.Write(withPhysics: false);
        string output = ImportKit.OutputPath();
        ObjectImporter.Import(folder.Path, output, Options());
        string notes = NotesOf(ImportKit.ReadOar(output));
        Assert.Contains("  " + ImportKit.Name + "_lod0.glb sha256 ", notes);
        Assert.DoesNotContain("_physics.glb", notes);
    }
}
