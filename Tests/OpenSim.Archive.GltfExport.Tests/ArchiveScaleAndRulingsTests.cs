/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>A sculpt with a profile cut, a hollow or an open path; a rigged mesh; the part-link attribute.</summary>
public class SculptFacesAndRiggedMeshTests
{
    private static PrimParams Round(float hollow = 0f, float profileBegin = 0f, float profileEnd = 1f, float pathBegin = 0f, float pathEnd = 1f, int holeType = 0) => new PrimParams
    {
        ProfileCurve = 0, HoleType = holeType, PathCurve = 32,
        ProfileBegin = profileBegin, ProfileEnd = profileEnd, ProfileHollow = hollow,
        PathBegin = pathBegin, PathEnd = pathEnd, PathScaleX = 1, PathScaleY = 1, PathRevolutions = 1
    };

    // A plane is not stitched, so the flat faces along a profile cut have an area; on a stitched type the last column is the
    // first column's pixel again, and the viewer's flat face over that pair has no area either.
    private static SculptResult Volume(PrimParams p, int type = SculptGeometry.TypePlane) =>
        SculptGeometry.BuildVolume(type == SculptGeometry.TypePlane ? SculptTests.PlaneMap() : SculptTests.TorusMap(), type, p);

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "hollow", 0.5f, 0f, 1f, 0f, 1f, 0 };
        yield return new object[] { "square hole", 0.5f, 0f, 1f, 0f, 1f, 0x20 };
        yield return new object[] { "triangle hole", 0.5f, 0f, 1f, 0f, 1f, 0x30 };
        yield return new object[] { "profile cut", 0f, 0.25f, 0.75f, 0f, 1f, 0 };
        yield return new object[] { "profile cut and hollow", 0.4f, 0.25f, 0.75f, 0f, 1f, 0 };
        yield return new object[] { "path cut", 0f, 0f, 1f, 0f, 0.5f, 0 };
        yield return new object[] { "everything", 0.4f, 0.2f, 0.8f, 0.1f, 0.6f, 0 };
        yield return new object[] { "begin cut only", 0f, 0.3f, 1f, 0f, 1f, 0 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFacesAreTheOnesTheViewerGivesTheProfileEachWithItsOwnNumber(string name, float hollow, float pb, float pe, float tb, float te, int hole)
    {
        PrimParams p = Round(hollow, pb, pe, tb, te, hole);
        Assert.True(SculptGeometry.HasProfileFaces(p), name);
        List<PrimFaceKind> rule = ViewerFaceRule.Faces(p);
        SculptResult r = Volume(p);
        Assert.Null(r.Problem);
        Assert.Equal(rule.Count, r.ViewerFaceCount);
        Assert.True(r.Faces.Count > 0);
        foreach (PrimFace f in r.Faces)
        {
            Assert.Equal(rule[f.Number], f.Kind);
            Assert.True(f.TriangleCount > 0);
            Assert.All(f.Positions, v => Assert.True(float.IsFinite(v)));
            Assert.All(f.Normals, v => Assert.True(float.IsFinite(v)));
            Assert.Equal(f.Positions.Length / 3 * 2, f.Uvs.Length);
        }
        // every face the rule lists has geometry on a map that has no flat spots
        Assert.Equal(Enumerable.Range(0, rule.Count), r.Faces.Select(f => f.Number));
        Assert.Equal(r.Faces.Count, r.Faces.Select(f => f.Number).Distinct().Count());
    }

    [Fact]
    public void OnAStitchedMapTheFlatFacesAlongACutHaveNoAreaButKeepTheirNumbers()
    {
        PrimParams p = Round(0.4f, 0.25f, 0.75f);
        SculptResult r = Volume(p, SculptGeometry.TypeTorus);
        List<PrimFaceKind> rule = ViewerFaceRule.Faces(p);
        Assert.Equal(rule.Count, r.ViewerFaceCount);
        Assert.NotEmpty(r.Faces);
        Assert.All(r.Faces, f => Assert.Equal(rule[f.Number], f.Kind));
        Assert.Contains(r.Faces, f => f.Kind == PrimFaceKind.Outer);
        Assert.Contains(r.Faces, f => f.Kind == PrimFaceKind.Inner);
    }

    [Fact]
    public void AHollowSculptHasAnOuterAndAnInnerWallWithTheExpectedTriangleCounts()
    {
        SculptResult r = Volume(Round(hollow: 0.5f));
        // 64 x 64 map: 32 sides each way; the hole's circle has 24 sides (6 * the highest detail, 4)
        Assert.Equal(new[] { PrimFaceKind.Outer, PrimFaceKind.Inner }, r.Faces.Select(f => f.Kind));
        Assert.Equal(2 * 32 * 32, r.Faces[0].TriangleCount + 0);
        Assert.Equal(2 * 32 * 24, r.Faces[1].TriangleCount);
        Assert.Equal(33, r.Rows);
        Assert.Equal(33 + 25, r.Columns);
    }

    [Fact]
    public void AProfileCutAddsTheTwoFlatFacesAndAPathCutAddsTheCaps()
    {
        SculptResult cut = Volume(Round(profileBegin: 0.25f, profileEnd: 0.75f));
        // the outer wall: 16 quads across (17 outer points, then the centre point, which the wall leaves out)
        Assert.Equal(new[] { PrimFaceKind.Outer, PrimFaceKind.ProfileCutBegin, PrimFaceKind.ProfileCutEnd }, cut.Faces.Select(f => f.Kind));
        Assert.Equal(18, cut.Columns);
        Assert.Equal(2 * 32 * 16, cut.Faces[0].TriangleCount);
        Assert.Equal(2 * 32, cut.Faces[1].TriangleCount);
        Assert.Equal(2 * 32, cut.Faces[2].TriangleCount);

        SculptResult pathCut = Volume(Round(pathEnd: 0.5f));
        Assert.Equal(new[] { PrimFaceKind.Top, PrimFaceKind.Outer, PrimFaceKind.Bottom }, pathCut.Faces.Select(f => f.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, pathCut.Faces.Select(f => f.Number));
        // a whole ring has 33 points: the cap is a fan of 32 triangles round a centre vertex
        Assert.Equal(32, pathCut.Faces[0].TriangleCount);
        Assert.Equal(32, pathCut.Faces[2].TriangleCount);
        // 17 rows over half the path
        Assert.Equal(17, pathCut.Rows);
        // a cap has one flat normal
        PrimFace top = pathCut.Faces[0];
        for (int i = 3; i < top.Normals.Length; i++)
            Assert.Equal(top.Normals[i % 3], top.Normals[i], 4);
        Assert.All(top.Uvs, v => Assert.InRange(v, -0.0001f, 1.0001f));
    }

    [Fact]
    public void ThePathAndProfileOfAPlainSculptHaveOneMoreRowThanSidesForEveryMapSize()
    {
        foreach ((int w, int h) in new[] { (16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (128, 64), (64, 128), (1024, 256) })
        {
            SculptGeometry.Resolution(w, h, out int pathSides, out _);
            List<float> rows = SculptGeometry.PathRows(0f, 1f, pathSides);
            // the square maps step exactly; others can leave one more row, because the viewer adds a float step repeatedly
            Assert.InRange(rows.Count, pathSides + 1, pathSides + 2);
            if (w == h)
                Assert.Equal(pathSides + 1, rows.Count);
            Assert.Equal(0f, rows[0]);
            Assert.Equal(1f, rows[^1]);
            for (int i = 1; i < rows.Count; i++)
                Assert.True(rows[i] >= rows[i - 1]);
        }
    }

    [Fact]
    public void ASculptWithOnlyAStraightPathOrAnotherProfileKeepsTheOneFace()
    {
        Assert.False(SculptGeometry.HasProfileFaces(Round()));
        PrimParams square = Round(hollow: 0.5f);
        square.ProfileCurve = 1;
        Assert.False(SculptGeometry.HasProfileFaces(square));
        PrimParams line = Round(hollow: 0.5f);
        line.PathCurve = 16;
        Assert.False(SculptGeometry.HasProfileFaces(line));
    }

    // ------------------------------------------------------------ in an archive

    private static readonly UUID MapId = UUID.Parse("a0000000-0000-4000-8000-0000000000b9");

    [Fact]
    public void AHollowSculptInAnArchiveExportsItsFacesNumberedAndSaysSo()
    {
        string dir = NewFolder();
        string file = Path.Combine(dir, "hollow.oar");
        PartSpec root = new PartSpec
        {
            Name = "HollowSculpt",
            Shape = Box(profileCurve: 0, pathCurve: 32, hollow: 25000, sculptType: SculptGeometry.TypeTorus, sculptTexture: MapId.ToString(), sculptEntry: true)
        };
        File.WriteAllBytes(file, OarWithAssets(new[] { ("hollow", ObjectXml(root)) }, new[] { (MapId, (sbyte)AssetType.Texture, SculptTests.J2k(SculptTests.TorusMap())) }));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { SelfContained = true, OutputDirectory = Path.Combine(dir, "out") });
        SummaryBuild b = Assert.Single(s.BuildList);
        Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(dir, "out", b.Folder, b.File)));
        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        Assert.Equal(new[] { 0, 1 }, mesh.GetProperty("primitives").EnumerateArray().Select(p => p.GetProperty("extras").GetProperty("primFace").GetInt32()));
        Assert.Equal(2, mesh.GetProperty("extras").GetProperty("primFaceCount").GetInt32());
        Assert.Equal(new[] { "Outer", "Inner" }, mesh.GetProperty("extras").GetProperty("primFaceKinds").EnumerateArray().Select(e => e.GetString()));
        Assert.True(glb.Node(1).GetProperty("extras").GetProperty("sculpt").TryGetProperty("profileFaces", out _));
    }

    // ------------------------------------------------------------ rigged mesh

    [Fact]
    public void ARiggedMeshIsExportedThroughItsBindShapeMatrixWithoutThePartsSize()
    {
        // scale (2, 3, 4) and move 1 along x, in the viewer's layout: rows of a row-vector matrix, translation in the fourth row
        float[] bind = { 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 1, 0, 0, 1 };
        byte[] asset = MeshTests.Asset(new[] { MeshTests.Triangle() }, joints: new[] { "mPelvis" }, bindShape: bind);
        (ExportSummary s, Glb glb, JsonElement manifest, _) = MeshTests.Export(asset, scale: new Vector3(7, 7, 7));
        Assert.Equal(1, s.RiggedMeshes);
        JsonElement prim = glb.Root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        float[] pos = glb.Floats(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
        float[] nor = glb.Floats(prim.GetProperty("attributes").GetProperty("NORMAL").GetInt32());
        // viewer (-0.5, -0.5, 0) -> (-0.5 * 2 + 1, -0.5 * 3, 0) = (0, -1.5, 0); glTF is (x, z, -y)
        Assert.Equal(new[] { 0f, 0f, 1.5f }, pos.Take(3).Select(v => MathF.Round(v, 3)).ToArray());
        // (0.5, -0.5, 0) -> (2, -1.5, 0)
        Assert.Equal(new[] { 2f, 0f, 1.5f }, pos.Skip(3).Take(3).Select(v => MathF.Round(v, 3)).ToArray());
        // the normal (0, 0, 1) through the inverse transpose of a scale stays along z; glTF y
        Assert.Equal(new[] { 0f, 1f, 0f }, nor.Take(3).Select(v => MathF.Round(v, 4)).ToArray());
        JsonElement extras = glb.Node(1).GetProperty("extras");
        Assert.True(extras.GetProperty("bindShapeApplied").GetBoolean());
        JsonElement skin = extras.GetProperty("skin");
        Assert.True(skin.GetProperty("bindShapeApplied").GetBoolean());
        Assert.Equal(2f, skin.GetProperty("bindShapeMatrix")[0].GetSingle());
        Assert.Equal(1f, skin.GetProperty("bindShapeMatrix")[12].GetSingle());
    }

    [Fact]
    public void AMeshThatIsNotRiggedStillGetsThePartsSizeAndNoBindShapeFlag()
    {
        byte[] asset = MeshTests.Asset(new[] { MeshTests.Triangle() });
        (_, Glb glb, _, _) = MeshTests.Export(asset, scale: new Vector3(2, 2, 2));
        JsonElement prim = glb.Root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        float[] pos = glb.Floats(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
        Assert.Equal(-1f, pos[0], 4);
        Assert.False(glb.Node(1).GetProperty("extras").TryGetProperty("bindShapeApplied", out _));
    }

    // ------------------------------------------------------------ part ids

    [Fact]
    public void EveryVertexCarriesItsPartsLinkNumberAndASingleBuildCarriesZero()
    {
        PartSpec root = new PartSpec { Name = "Root", Link = 1 };
        PartSpec child = new PartSpec { Name = "Child", Link = 2, Offset = new Vector3(2, 0, 0) };
        string dir = NewFolder();
        string file = Path.Combine(dir, "two.oar");
        File.WriteAllBytes(file, Oar(("two", ObjectXml(root, child)), ("one", ObjectXml(new PartSpec { Name = "Lone" }))));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { SelfContained = true, OutputDirectory = Path.Combine(dir, "out") });
        Assert.Equal(2, s.Builds);
        foreach (SummaryBuild b in s.BuildList)
        {
            Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(dir, "out", b.Folder, b.File)));
            int index = 0;
            foreach (JsonElement part in glb.PartNodes)
            {
                int link = part.GetProperty("extras").GetProperty("linkNumber").GetInt32();
                JsonElement mesh = glb.Root.GetProperty("meshes")[part.GetProperty("mesh").GetInt32()];
                foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    float[] values = glb.Floats(prim.GetProperty("attributes").GetProperty("_PART_LINK").GetInt32());
                    Assert.NotEmpty(values);
                    Assert.All(values, v => Assert.Equal((float)link, v));
                    // and one value per vertex, as _PRIM_FACE has
                    Assert.Equal(glb.Floats(prim.GetProperty("attributes").GetProperty("_PRIM_FACE").GetInt32()).Length, values.Length);
                }
                index++;
            }
            Assert.True(index >= 1);
            if (b.Name == "Lone")
                Assert.Equal(1, index);
        }
        Assert.Contains(s.BuildList, b => b.Name == "Root" && b.Parts == 2);
    }
}
