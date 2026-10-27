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
/// The round trip: an imported archive is written back out to glTF by the exporter (and the other levels read back out of the mesh asset) and compared
/// with the input files. Each test writes its own folders and shares nothing, so the class runs in parallel with others.
/// </summary>
public class ImportRoundTripTests
{
    private static ImportOptions Options() => new ImportOptions
    {
        Name = "Round Trip Slab", Avatar = ImportKit.Avatar, Position = new Vector3(40f, 50f, 60f), Created = 1767225600
    };

    private static (ImportCheckResult Check, ImportKit.Folder Folder, string Archive, ImportOptions Options) Run(bool normalMap = true, ImportKit.PhysicsKind physics = ImportKit.PhysicsKind.Box,
        Action<ImportKit.Folder> afterImport = null)
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap, physics);
        string archive = ImportKit.OutputPath();
        ImportOptions options = Options();
        ObjectImporter.Import(folder.Path, archive, options);
        Assert.True(File.Exists(archive), "the archive was not written");
        afterImport?.Invoke(folder);
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        return (ImportCheck.Run(archive, folder.Path, options, work), folder, archive, options);
    }

    private static CheckLine Line(ImportCheckResult check, string what)
    {
        Assert.Contains(check.Lines, l => l.What == what);
        return check.Lines.Single(l => l.What == what);
    }

    private static readonly string[] LevelLines = { "high level", "medium level", "low level", "lowest level" };

    [Fact]
    public void TheExportedGlbAndTheMeshAssetHaveTheTriangleCountOfTheInputForEveryLevel()
    {
        ImportCheckResult check = Run().Check;
        Assert.True(check.Passed, check.ToText());
        int[] expected = { ImportKit.SlabTriangles(4, true), ImportKit.SlabTriangles(3, true), ImportKit.SlabTriangles(2, true), ImportKit.SlabTriangles(1, false) };
        for (int i = 0; i < 4; i++)
        {
            CheckLine line = Line(check, LevelLines[i]);
            Assert.Equal(expected[i], line.InputTriangles);
            Assert.Equal(expected[i], line.OutputTriangles);
            Assert.True(line.Ok, line.Detail);
        }
    }

    [Fact]
    public void PositionsOfEveryLevelMatchWithinHalfAMillimetre()
    {
        ImportCheckResult check = Run().Check;
        foreach (string what in LevelLines)
        {
            CheckLine line = Line(check, what);
            Assert.True(line.Ok, what + ": " + line.Detail);
            Assert.InRange(line.MaxPosition, 0f, ImportCheck.PositionTolerance);
        }
        Assert.Equal(0.0005f, ImportCheck.PositionTolerance);
    }

    [Fact]
    public void TextureCoordinatesPicturesAndTheNormalMapMatch()
    {
        ImportCheckResult check = Run(normalMap: true).Check;
        foreach (string what in LevelLines)
            Assert.InRange(Line(check, what).MaxUv, 0f, 1e-4f);
        foreach (string what in new[] { "picture of top", "picture of side", "normal map of side" })
        {
            CheckLine line = Line(check, what);
            Assert.True(line.Ok, what + ": " + line.Detail);
        }
        Assert.True(check.Passed, check.ToText());
    }

    [Fact]
    public void AConvexPhysicsShapeComesBackAsTheSamePoints()
    {
        ImportCheckResult check = Run(physics: ImportKit.PhysicsKind.Box).Check;
        CheckLine line = Line(check, "physics shape");
        Assert.True(line.Ok, line.Detail);
        Assert.Equal(8, line.InputTriangles);
        Assert.Equal(8, line.OutputTriangles);
        Assert.Contains("convex hull", line.Detail);
    }

    [Fact]
    public void ATrianglePhysicsMeshComesBackWithTheSameTriangles()
    {
        ImportCheckResult check = Run(physics: ImportKit.PhysicsKind.Ell).Check;
        CheckLine line = Line(check, "physics shape");
        Assert.True(line.Ok, line.Detail);
        Assert.Equal(24, line.InputTriangles);
        Assert.Equal(24, line.OutputTriangles);
        Assert.InRange(line.MaxPosition, 0f, ImportCheck.PositionTolerance);
        Assert.Contains("triangle mesh", line.Detail);
    }

    [Fact]
    public void TheCheckFailsWhenALevelMovedTwoMillimetresAfterTheImport()
    {
        ImportCheckResult check = Run(afterImport: folder =>
        {
            ImportKit.Geo topGeo = new ImportKit.Geo(), sideGeo = new ImportKit.Geo();
            ImportKit.AddBox(topGeo, 2, s => s == 0, hx: 0.202f);
            ImportKit.AddBox(sideGeo, 2, s => s != 0, hx: 0.202f);
            File.WriteAllBytes(Path.Combine(folder.Path, ImportKit.Name + "_lod1.glb"), ImportKit.Glb(new[] { (folder.Top, topGeo), (folder.Side, sideGeo) }));
        }).Check;
        Assert.False(check.Passed);
        CheckLine low = Line(check, "low level");
        Assert.False(low.Ok);
        Assert.Contains("position", low.Detail);
        Assert.True(Line(check, "medium level").Ok);
        Assert.True(Line(check, "lowest level").Ok);
    }

    [Fact]
    public void TheCheckFailsWhenALevelHasADifferentTriangleCountAfterTheImport()
    {
        ImportCheckResult check = Run(afterImport: folder =>
        {
            ImportKit.Geo topGeo = new ImportKit.Geo(), sideGeo = new ImportKit.Geo();
            ImportKit.AddBox(topGeo, 1, s => s == 0);
            ImportKit.AddBox(sideGeo, 1, s => s != 0);
            File.WriteAllBytes(Path.Combine(folder.Path, ImportKit.Name + "_lod2.glb"), ImportKit.Glb(new[] { (folder.Top, topGeo), (folder.Side, sideGeo) }));
        }).Check;
        Assert.False(check.Passed);
        CheckLine medium = Line(check, "medium level");
        Assert.False(medium.Ok);
        Assert.Equal(ImportKit.SlabTriangles(1, true), medium.InputTriangles);
        Assert.Equal(ImportKit.SlabTriangles(3, true), medium.OutputTriangles);
    }

    [Fact]
    public void TheCheckFailsWhenAPictureChangedAfterTheImport()
    {
        ImportCheckResult check = Run(afterImport: folder =>
        {
            ImportMaterial changed = new ImportMaterial { Name = "side", Color = folder.Side.Color, Picture = ImportKit.Png(31, 90, 150), Normal = folder.Side.Normal };
            ImportKit.Geo topGeo = new ImportKit.Geo(), sideGeo = new ImportKit.Geo();
            ImportKit.AddBox(topGeo, 4, s => s == 0);
            ImportKit.AddBox(sideGeo, 4, s => s != 0);
            File.WriteAllBytes(Path.Combine(folder.Path, ImportKit.Name + ".glb"), ImportKit.Glb(new[] { (folder.Top, topGeo), (changed, sideGeo) }));
        }).Check;
        Assert.False(check.Passed);
        Assert.False(Line(check, "picture of side").Ok);
        Assert.True(Line(check, "picture of top").Ok);
        Assert.True(Line(check, "high level").Ok);
    }

    [Fact]
    public void TheCommandWithCheckWritesTheArchiveChecksItAndExitsWithZero()
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap: true);
        string output = ImportKit.OutputPath(), work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        int exit = ImportCommand.Run(new[] { folder.Path, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--check", work });
        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        Assert.True(Directory.Exists(work));
    }
}
