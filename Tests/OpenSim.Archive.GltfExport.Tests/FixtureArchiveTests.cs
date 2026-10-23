/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// A small archive with sculpts of every kind and mesh parts (several faces, rigged), exported whole. Set OARX_KEEP_FIXTURE to a
/// file path to keep the archive, for checking the output in other tools.
/// </summary>
public class FixtureArchiveTests
{
    private static MeshTests.SubMesh Quad(float z, float x0 = -0.5f) => new MeshTests.SubMesh
    {
        Positions = new[] { x0, -0.5f, z, x0 + 1f, -0.5f, z, x0 + 1f, 0.5f, z, x0, 0.5f, z },
        Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f },
        Uvs = new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f },
        Indices = new[] { 0, 1, 2, 0, 2, 3 }
    };

    private static string Sculpt(UUID map, int type) => Box(profileCurve: 0, pathCurve: 32, sculptType: type, sculptTexture: map.ToString(), sculptEntry: true);
    private static string MeshShape(UUID asset, int type = 5) => Box(profileCurve: 0, pathCurve: 16, sculptType: type, sculptTexture: asset.ToString(), sculptEntry: true);

    [Fact]
    public void AnArchiveOfSculptsAndMeshesExportsEveryPart()
    {
        UUID sphere = UUID.Random(), torus = UUID.Random(), plane = UUID.Random(), cylinder = UUID.Random();
        UUID multi = UUID.Random(), rigged = UUID.Random(), single = UUID.Random(), paint = UUID.Random();

        List<(UUID, sbyte, byte[])> assets = new List<(UUID, sbyte, byte[])>
        {
            (sphere, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.SphereMap())),
            (torus, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.TorusMap())),
            (plane, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.PlaneMap())),
            (cylinder, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.CylinderMap())),
            (multi, (sbyte)AssetType.Mesh, MeshTests.Asset(new[] { Quad(0.5f), Quad(0f), Quad(-0.5f) }, low: new[] { Quad(0.5f) }, physics: true)),
            (rigged, (sbyte)AssetType.Mesh, MeshTests.Asset(new[] { Quad(0f) }, joints: new[] { "mPelvis", "mTorso" })),
            (single, (sbyte)AssetType.Mesh, MeshTests.Asset(new[] { MeshTests.Triangle() })),
            (paint, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.Map(8, 8, (c, r) => (c / 8.0 - 0.5, r / 8.0 - 0.5, 0))))
        };

        Primitive.TextureEntry painted = new Primitive.TextureEntry(paint);
        Primitive.TextureEntryFace paintedFace = painted.CreateFace(0);
        paintedFace.TextureID = paint;
        paintedFace.RepeatU = 3f; paintedFace.RepeatV = 2f; paintedFace.Rotation = 0.4f;
        string paintedXml = Convert.ToBase64String(painted.GetBytes());

        PartSpec sculpts = new PartSpec { Name = "Sculpts", Link = 1, Scale = new Vector3(1, 1, 1), Shape = Sculpt(sphere, 1), TextureEntryBase64 = paintedXml };
        PartSpec torusPart = new PartSpec { Name = "Torus", Link = 2, Offset = new Vector3(1.5f, 0, 0), Shape = Sculpt(torus, 2 | 0x40) };
        PartSpec planePart = new PartSpec { Name = "Plane", Link = 3, Offset = new Vector3(3f, 0, 0), Shape = Sculpt(plane, 3 | 0x80) };
        PartSpec cylinderPart = new PartSpec { Name = "Cylinder", Link = 4, Offset = new Vector3(4.5f, 0, 0), Shape = Sculpt(cylinder, 4 | 0xC0) };

        PartSpec multiPart = new PartSpec { Name = "Three faces", Link = 1, Shape = MeshShape(multi), TextureEntryBase64 = paintedXml };
        PartSpec riggedPart = new PartSpec { Name = "Rigged", Link = 2, Offset = new Vector3(1.5f, 0, 0), Shape = MeshShape(rigged) };
        PartSpec singlePart = new PartSpec { Name = "Single", Link = 3, Offset = new Vector3(3f, 0, 0), Shape = MeshShape(single, 5 | 0x80) };

        PartSpec mixedRoot = new PartSpec { Name = "Mixed", Link = 1, Shape = Box() };
        PartSpec mixedSculpt = new PartSpec { Name = "Mixed sculpt", Link = 2, Offset = new Vector3(0, 1.5f, 0), Shape = Sculpt(sphere, 1) };
        PartSpec mixedMesh = new PartSpec { Name = "Mixed mesh", Link = 3, Offset = new Vector3(0, 3f, 0), Shape = MeshShape(multi) };

        string dir = NewFolder();
        string file = Path.Combine(dir, "fixture.oar");
        File.WriteAllBytes(file, OarWithAssets(new[]
        {
            ("sculpts", ObjectXml(sculpts, torusPart, planePart, cylinderPart)),
            ("meshes", ObjectXml(multiPart, riggedPart, singlePart)),
            ("mixed", ObjectXml(mixedRoot, mixedSculpt, mixedMesh))
        }, assets.ToArray()));
        byte[] before = File.ReadAllBytes(file);

        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal((3, 10, 10, 0), (s.Builds, s.Parts, s.Exported, s.Placeheld));
        Assert.Equal(5, s.SculptsExported);
        Assert.Equal(4, s.MeshesExported);
        Assert.Equal(1, s.RiggedMeshes);
        Assert.Empty(s.Reasons);

        string keep = Environment.GetEnvironmentVariable("OARX_KEEP_FIXTURE");
        if (!string.IsNullOrEmpty(keep))
            File.Copy(file, keep, true);
    }
}
