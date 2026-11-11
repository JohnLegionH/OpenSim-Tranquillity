/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Restore.Import;
using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The check (oar-import --check) of a part that holds wearables and objects: a wearable's text is the source text apart from the creator, the owner and the
/// texture ids, and each of its pictures has the pixels of its file; an object is checked as the single-model import checks its folder, its lines named
/// after the item. In the Console output collection with the other round trip tests, which share the process-wide Console.Out.
/// </summary>
[Collection("Console output")]
public class GarmentContentsCheckTests
{
    private static (ImportCheckResult Check, ClothingBoxKit.Built Built) Run(Action<ClothingBoxKit.Built> afterImport = null, string contents = ClothingBoxKit.DefaultContents)
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write(contents);
        ClothingBoxKit.Import(b, out string archive);
        afterImport?.Invoke(b);
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        return (ImportCheck.RunLinkset(archive, LinksetManifest.Load(b.ManifestPath), ClothingBoxKit.Options(), work), b);
    }

    private static CheckLine Line(ImportCheckResult check, string what)
    {
        Assert.True(check.Lines.Any(l => l.What == what), "no line '" + what + "' in:\n" + check.ToText());
        return check.Lines.Single(l => l.What == what);
    }

    [Fact]
    public void TheCheckOfABoxOfGarmentsPasses()
    {
        ImportCheckResult check = Run().Check;
        Assert.True(check.Passed, check.ToText());
        CheckLine contents = Line(check, "contents of Box");
        Assert.True(contents.Ok, contents.Detail);
        foreach (string name in new[] { "Test Shirt Mesh", "Test Skirt Mesh", "Test Slab", "Test Shirt", "Test Alpha" })
            Assert.Contains(name, contents.Detail);
    }

    [Fact]
    public void EachObjectIsCheckedAsTheSingleModelCheckChecksItsFolder()
    {
        ImportCheckResult check = Run().Check;
        foreach (string name in new[] { "Test Shirt Mesh", "Test Skirt Mesh", "Test Slab" })
            foreach (string level in new[] { "high level", "medium level", "low level", "lowest level" })
                Assert.True(Line(check, name + ": " + level).Ok, Line(check, name + ": " + level).Detail);
        Assert.True(Line(check, "Test Shirt Mesh: skin and weights").Ok, Line(check, "Test Shirt Mesh: skin and weights").Detail);
        Assert.True(Line(check, "Test Slab: picture of top").Ok, Line(check, "Test Slab: picture of top").Detail);
        Assert.True(Line(check, "Test Slab: physics shape").Ok, Line(check, "Test Slab: physics shape").Detail);
    }

    [Fact]
    public void TheCheckFailsWhenAWearablesTextDiffersFromItsFile()
    {
        ImportCheckResult check = Run(b => File.WriteAllText(b.Shirt, ClothingBoxKit.ShirtText.Replace("800 .25", "800 .75"))).Check;
        CheckLine contents = Line(check, "contents of Box");
        Assert.False(contents.Ok);
        Assert.Contains("Test Shirt", contents.Detail);
        Assert.False(check.Passed);
    }

    [Fact]
    public void TheCheckFailsWhenAWearablesPictureDiffersFromItsFile()
    {
        ImportCheckResult check = Run(b => File.WriteAllBytes(b.UpperAlphaPicture, ImportKit.Png(1, 2, 3, 8))).Check;
        CheckLine contents = Line(check, "contents of Box");
        Assert.False(contents.Ok);
        Assert.Contains("Test Alpha", contents.Detail);
        Assert.False(check.Passed);
    }

    [Fact]
    public void TheCheckFailsWhenAnObjectsWeightsDifferFromItsFolder()
    {
        ImportCheckResult check = Run(b => RigKit.Write(new RigKit.Options { Fixed = new[] { 0.6f, 0.4f } }, name: "shirt", into: b.ShirtMesh)).Check;
        Assert.False(Line(check, "Test Shirt Mesh: skin and weights").Ok);
        Assert.True(Line(check, "Test Skirt Mesh: skin and weights").Ok, Line(check, "Test Skirt Mesh: skin and weights").Detail);
        Assert.False(check.Passed);
    }

    [Fact]
    public void TheCheckFailsWhenAnObjectsTrianglesDifferFromItsFolder()
    {
        // the full mesh of the slab is written again after the import, the same box and materials cut into a finer grid
        ImportCheckResult check = Run(b =>
        {
            ImportMaterial top = new ImportMaterial { Name = "top", Color = new[] { 0.2f, 0.4f, 0.6f, 1f }, Picture = ImportKit.Png(200, 40, 10) };
            ImportMaterial side = new ImportMaterial { Name = "side", Color = new[] { 1f, 1f, 1f, 1f }, Picture = ImportKit.Png(30, 90, 150) };
            ImportKit.Geo topGeo = new ImportKit.Geo(), sideGeo = new ImportKit.Geo();
            ImportKit.AddBox(topGeo, 6, s => s == 0);
            ImportKit.AddBox(sideGeo, 6, s => s != 0);
            File.WriteAllBytes(Path.Combine(b.Slab, "slab.glb"), ImportKit.Glb(new[] { (top, topGeo), (side, sideGeo) }));
        }).Check;
        Assert.False(Line(check, "Test Slab: high level").Ok);
        Assert.False(check.Passed);
    }

    [Fact]
    public void TheCommandWritesTheBoxAndItsCheckPasses()
    {
        ClothingBoxKit.Built b = ClothingBoxKit.Write();
        string output = ImportKit.OutputPath();
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        TextWriter saved = Console.Out;
        StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        int exit;
        try { exit = ImportCommand.Run(new[] { "--manifest", b.ManifestPath, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--check", work }); }
        finally { Console.SetOut(saved); }
        Assert.True(exit == 0, "exit " + exit + "\n" + capture);
        Assert.Contains("wearable 'Test Alpha'", capture.ToString());
        Assert.Contains("object 'Test Shirt Mesh'", capture.ToString());
    }

    [Fact]
    public void TheHelpTextDocumentsWearablesAndObjects()
    {
        string help = ImportCommand.Usage;
        foreach (string word in new[] { "\"wearable\"", "\"object\"", "\"textures\"", "LLWearable", "texture entry", "jointPositions", "Copy to inventory" })
            Assert.Contains(word, help);
    }
}
