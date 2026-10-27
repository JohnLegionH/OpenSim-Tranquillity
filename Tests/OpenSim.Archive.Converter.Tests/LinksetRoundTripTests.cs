/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Restore.Import;
using OpenSim.ArchiveInspector;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The round trip of a linked object: the imported archive is written out by the exporter and compared with the manifest it was made from (link order,
/// local places within half a millimetre, names, descriptions, contents). Each test writes its own folders and shares nothing, so the class runs in parallel.
/// </summary>
public class LinksetRoundTripTests
{
    private static readonly string[] Parts = { "Shell", "Door", "Window" };

    private static (ImportCheckResult Check, LinksetKit.Built Built, LinksetManifest Manifest, string Archive, ImportOptions Options) Run(Action<LinksetKit.Built, LinksetManifest> afterImport = null)
    {
        LinksetKit.Built built = LinksetKit.Write();
        LinksetManifest manifest = LinksetManifest.Load(built.ManifestPath);
        string archive = ImportKit.OutputPath();
        ImportOptions options = new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(40f, 50f, 60f), Created = 1767225600 };
        ObjectImporter.ImportLinkset(manifest, archive, options);
        Assert.True(File.Exists(archive), "the archive was not written");
        afterImport?.Invoke(built, manifest);
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        return (ImportCheck.RunLinkset(archive, manifest, options, work), built, manifest, archive, options);
    }

    private static CheckLine Line(ImportCheckResult check, string what)
    {
        Assert.Contains(check.Lines, l => l.What == what);
        return check.Lines.Single(l => l.What == what);
    }

    [Fact]
    public void TheExportedBuildHasTheLinkOrderNamesAndDescriptionsOfTheManifest()
    {
        ImportCheckResult check = Run().Check;
        Assert.True(check.Passed, check.ToText());
        Assert.True(Line(check, "link order").Ok, Line(check, "link order").Detail);
        foreach (string part in Parts)
        {
            Assert.True(Line(check, "name of " + part).Ok, Line(check, "name of " + part).Detail);
            Assert.True(Line(check, "description of " + part).Ok, Line(check, "description of " + part).Detail);
        }
    }

    [Fact]
    public void LocalPlacesAndTurnsOfTheExportedPartsMatchWithinHalfAMillimetre()
    {
        ImportCheckResult check = Run().Check;
        foreach (string part in Parts)
        {
            CheckLine position = Line(check, "position of " + part);
            Assert.True(position.Ok, position.Detail);
            Assert.InRange(position.MaxPosition, 0f, ImportCheck.PositionTolerance);
            Assert.True(Line(check, "rotation of " + part).Ok, Line(check, "rotation of " + part).Detail);
        }
        // the shell is the root part, so the exported places are relative to it and its own offset is nothing
        Assert.Equal(0f, Line(check, "position of Shell").MaxPosition);
    }

    [Fact]
    public void TheContentsOfEveryPartComeBackEqual()
    {
        ImportCheckResult check = Run().Check;
        foreach (string part in Parts)
            Assert.True(Line(check, "contents of " + part).Ok, Line(check, "contents of " + part).Detail);
        Assert.Contains("Door script", Line(check, "contents of Door").Detail);
        Assert.Contains("Badge", Line(check, "contents of Door").Detail);
        Assert.Contains("Read me", Line(check, "contents of Shell").Detail);
    }

    [Fact]
    public void EachPartKeepsTheTriangleCountOfItsModel()
    {
        ImportCheckResult check = Run().Check;
        Assert.Equal(12, Line(check, "triangles of Door").InputTriangles);
        Assert.Equal(12, Line(check, "triangles of Door").OutputTriangles);
        Assert.Equal(ImportKit.SlabTriangles(4, true), Line(check, "triangles of Shell").OutputTriangles);
    }

    [Fact]
    public void ADescriptionChangedAfterTheImportFailsTheCheckOnThatPart()
    {
        ImportCheckResult check = Run((_, m) => m.Parts[1].Description = "back door leaf").Check;
        Assert.False(check.Passed);
        Assert.False(Line(check, "description of Door").Ok);
        Assert.True(Line(check, "description of Shell").Ok);
    }

    [Fact]
    public void APlaceChangedAfterTheImportFailsTheCheckOnThatPartWithTheDifference()
    {
        ImportCheckResult check = Run((_, m) => m.Parts[1].Position = new[] { 0.31f, 0.2f, 0f }).Check;
        Assert.False(check.Passed);
        CheckLine door = Line(check, "position of Door");
        Assert.False(door.Ok);
        Assert.InRange(door.MaxPosition, 0.0099f, 0.0101f);
        Assert.True(Line(check, "position of Window").Ok);
    }

    [Fact]
    public void AScriptChangedAfterTheImportFailsTheCheckOnTheContentsOfThatPart()
    {
        ImportCheckResult check = Run((built, _) => File.WriteAllText(built.DoorScript, "default { state_entry() { } }")).Check;
        Assert.False(check.Passed);
        CheckLine contents = Line(check, "contents of Door");
        Assert.False(contents.Ok);
        Assert.Contains("Door script", contents.Detail);
        Assert.True(Line(check, "contents of Shell").Ok);
    }

    [Fact]
    public void PartsInAnotherOrderFailTheLinkOrderCheck()
    {
        ImportCheckResult check = Run((_, m) => { LinksetPart door = m.Parts[1]; m.Parts[1] = m.Parts[2]; m.Parts[2] = door; }).Check;
        Assert.False(check.Passed);
        Assert.False(Line(check, "link order").Ok);
    }
}
