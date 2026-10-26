/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Collision of a merged part. When every member is exactly convex the part carries a hull list (one hull for each member, in its physics_convex
/// block) next to the triangle mesh; when any member is hollow, cut or concave it carries the triangle mesh as before and no hull list, because a
/// hull of such a member would be solid where the member is not. Each test writes its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergePhysicsHullTests
{
    private static PhysicsGeometry Of(string shape, string modifier)
    {
        PhysicsGeometry g = PrimPhysics.Build(FaceMatrix.Shape(shape, modifier), null, null, new OpenMetaverse.Vector3(2, 2, 2), out string problem);
        Assert.True(g != null, problem);
        return g;
    }

    [Theory]
    [InlineData("box", "plain")]
    [InlineData("cylinder", "plain")]
    [InlineData("prism", "plain")]
    [InlineData("box", "taper")]
    public void AShapeThatIsAConvexSolidIsConvex(string shape, string modifier)
    {
        Assert.True(ConvexPhysics.IsConvex(Of(shape, modifier)), shape + " " + modifier);
    }

    [Theory]
    [InlineData("box", "hollow")]
    [InlineData("cylinder", "hollow")]
    [InlineData("cylinder", "hollow-circle-hole")]
    [InlineData("box", "twist")]
    [InlineData("torus", "plain")]
    [InlineData("tube", "plain")]
    [InlineData("ring", "plain")]
    [InlineData("box", "hollow+path-cut")]
    public void AHollowTwistedOrRoundedShapeIsNotConvex(string shape, string modifier)
    {
        Assert.False(ConvexPhysics.IsConvex(Of(shape, modifier)), shape + " " + modifier);
    }

    [Fact]
    public void ATriangleSoupThatIsOpenOrHasNoAreaIsNotConvex()
    {
        // a box with one side missing, and a single triangle
        PhysicsGeometry box = Of("box", "plain");
        PhysicsGeometry open = new PhysicsGeometry { Positions = box.Positions, Indices = box.Indices.Take(box.Indices.Length - 6).ToArray() };
        Assert.False(ConvexPhysics.IsConvex(open));
        Assert.False(ConvexPhysics.IsConvex(new PhysicsGeometry { Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Indices = new[] { 0, 1, 2 } }));
        Assert.False(ConvexPhysics.IsConvex(new PhysicsGeometry()));
    }

    [Fact]
    public void TheHullOfABoxHasItsEightCornersAndTheHullOfACylinderStaysUnderTheLimit()
    {
        Assert.Equal(8, ConvexPhysics.Hull(Of("box", "plain")).Count);
        List<Vector3> cylinder = ConvexPhysics.Hull(Of("cylinder", "plain"));
        Assert.InRange(cylinder.Count, 8, ConvexPhysics.MaxHullVertices);
    }

    [Fact]
    public void AHullListWrittenIntoAMeshAssetReadsBackWithinTheStorageStep()
    {
        List<List<Vector3>> hulls = new List<List<Vector3>>
        {
            new List<Vector3> { new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, -0.5f, 0.5f) },
            Enumerable.Range(0, 256).Select(i => new Vector3((float)Math.Cos(i * 0.1) * 0.3f + 0.1f, (float)Math.Sin(i * 0.1) * 0.3f, (i % 2) * 0.4f - 0.2f)).ToList()
        };
        MeshAssetModel model = new MeshAssetModel();
        PhysicsBlock.AddHullList(model, hulls, hulls.SelectMany(h => h).Take(8).ToList());
        List<List<Vector3>> back = PhysicsBlock.ReadHullList(model.PhysicsConvex);
        Assert.Equal(2, back.Count);
        Assert.Equal(new[] { 4, 256 }, back.Select(h => h.Count));
        float step = 1.0f / 65535f * 1.01f;
        for (int h = 0; h < 2; h++)
            for (int i = 0; i < hulls[h].Count; i++)
                Assert.True((hulls[h][i] - back[h][i]).Length() < step * 3, "hull " + h + " point " + i);
    }

    // ---------------------------------------------------------------- merged parts

    private static byte[] Entry(string archive, string name)
    {
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            if (h.Name == name)
                return tar.ReadBody();
        return null;
    }

    /// <summary>Five parts in a row, boxes and cylinders; <paramref name="change"/> may replace a shape.</summary>
    private static (MergeWriteResult Result, string Output, List<KitPart> Parts) Write(Action<List<KitPart>> change = null)
    {
        List<KitPart> parts = Enumerable.Range(1, 6).Select(i => new KitPart("Wall " + i, i)
        {
            Offset = (i * 2) + "," + (i % 2) + ",0.5", Scale = (1 + i * 0.25f).ToString(CultureInfo.InvariantCulture) + ",1,2", Shape = FaceMatrix.Shape(i % 3 == 0 ? "cylinder" : "box", "plain")
        }).ToList();
        parts[0].Offset = "0,0,0";
        change?.Invoke(parts);
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, new Dictionary<string, byte[]>());
        string output = Path.Combine(Path.GetTempPath(), "merge-hull-tests", Guid.NewGuid().ToString("N") + ".oar");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        return (MergeArchiveWriter.Write(source, output), output, parts);
    }

    private static MeshAssetModel Mesh(string archive, MergedPartReport part) => MeshCodec.Decode(Entry(archive, "assets/" + part.MeshId + "_mesh.llmesh"));

    [Fact]
    public void WhenEveryMemberIsConvexTheMergedPartCarriesOneHullForEachMemberAndKeepsTheTriangleMesh()
    {
        (MergeWriteResult r, string output, _) = Write();
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.Equal(part.Members, part.PhysicsHulls);
        Assert.Equal("", part.PhysicsReason);
        MeshAssetModel model = Mesh(output, part);
        List<List<Vector3>> hulls = PhysicsBlock.ReadHullList(model.PhysicsConvex);
        Assert.Equal(part.Members, hulls.Count);
        Assert.All(hulls, h => Assert.InRange(h.Count, 4, ConvexPhysics.MaxHullVertices));
        // the triangle mesh is still there, for a region that reads it
        Assert.NotNull(model.PhysicsMesh);
        // the hulls reach as far as the full mesh does: every hull point is inside the merged part's box
        Assert.All(hulls.SelectMany(h => h), p => Assert.InRange(Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))), 0f, 0.5f + 1e-3f));
    }

    [Fact]
    public void AHollowMemberKeepsTheWholePartOnTheTriangleMeshAndNoHullFillsItsHole()
    {
        (MergeWriteResult r, string output, List<KitPart> parts) = Write(p => p[2].Shape = FaceMatrix.Shape("box", "hollow"));
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.Equal(0, part.PhysicsHulls);
        Assert.Contains("Wall 3", part.PhysicsReason);
        Assert.Contains("not convex", part.PhysicsReason);
        MeshAssetModel model = Mesh(output, part);
        Assert.Empty(PhysicsBlock.ReadHullList(model.PhysicsConvex));
        Assert.NotNull(model.PhysicsMesh);
    }

    [Fact]
    public void ATwistedMemberAlsoKeepsTheTriangleMesh()
    {
        (MergeWriteResult r, _, _) = Write(p => p[1].Shape = FaceMatrix.Shape("box", "twist"));
        Assert.Equal(0, Assert.Single(r.Parts).PhysicsHulls);
    }

    [Fact]
    public void TheReportSaysWhichPhysicsShapeEachMergedPartGot()
    {
        (MergeWriteResult convex, _, _) = Write();
        Assert.Contains("hulls", convex.ToText());
        (MergeWriteResult hollow, _, _) = Write(p => p[2].Shape = FaceMatrix.Shape("box", "hollow"));
        Assert.Contains("triangle mesh", hollow.ToText());
    }
}
