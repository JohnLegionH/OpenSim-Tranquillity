/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using Xunit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// A circular path with a radius offset, against the Second Life viewer's rule (LLPath::genNGon in indra/llmath/llvolume.cpp):
/// the start radius is 0.5 * (1 - hole size Y), multiplied by (1 + offset) when the offset is negative, and the end radius by
/// (1 - offset) otherwise; the path is open, and so gets end caps, only when the two radii differ by more than 0.001. A hole
/// size of 1 makes both radii zero, so such a path is closed whatever the offset.
/// </summary>
public class RadiusOffsetPathTests
{
    private static PrimParams Shape(string shape, float holeY, float offset)
    {
        PrimParams p = new PrimParams { ProfileEnd = 1f, PathEnd = 1f, PathScaleX = 1f, PathScaleY = holeY, PathRevolutions = 1f, PathCurve = 32, PathRadiusOffset = offset };
        p.ProfileCurve = shape switch { "torus" => 0, "tube" => 1, "ring" => 3, _ => throw new ArgumentException(shape) };
        return p;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "torus", "tube", "ring" })
            foreach (float hole in new[] { 0.25f, 0.9f, 0.97f, 0.995f, 1f })
                foreach (float offset in new[] { 0.3f, -0.3f, 0.01f, -0.01f, 0.5f, -0.5f })
                    yield return new object[] { shape, hole, offset };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePathIsClosedExactlyWhenTheViewersRadiiAreWithinOneThousandth(string shape, float hole, float offset)
    {
        PrimParams p = Shape(shape, hole, offset);

        // the viewer's arithmetic, written out from llvolume.cpp rather than taken from the exporter's own rule
        float start = 0.5f * (1f - hole), end = start;
        if (offset < 0f) start *= 1f + offset; else end *= 1f - offset;
        bool open = MathF.Abs(end - start) > 0.001f;

        Assert.Equal(open, ViewerFaceRule.PathIsOpen(p));

        List<PrimFaceKind> kinds = ViewerFaceRule.Faces(p);
        List<PrimFace> faces = PrimGeometry.BuildFaces(p, DetailLevel.High, out int count, out int unnumbered);

        Assert.Equal(open, kinds.Contains(PrimFaceKind.Top));
        Assert.Equal(open, kinds.Contains(PrimFaceKind.Bottom));
        Assert.Equal(kinds.Count, count);
        // every triangle the mesher built belongs to a face of the viewer's list, so a closed path has no end caps
        Assert.Equal(0, unnumbered);
        Assert.Equal(open, faces.Any(f => f.Kind == PrimFaceKind.Top));
        Assert.Equal(open, faces.Any(f => f.Kind == PrimFaceKind.Bottom));
        Assert.All(faces, f => Assert.Equal(kinds[f.Number], f.Kind));
    }

    [Theory]
    [InlineData("torus")]
    [InlineData("tube")]
    [InlineData("ring")]
    public void AHoleSizeOfOneGivesTheSameGeometryWhateverTheOffset(string shape)
    {
        List<PrimFace> plain = PrimGeometry.BuildFaces(Shape(shape, 1f, 0f));
        foreach (float offset in new[] { 0.5f, -0.5f, 0.99f })
        {
            List<PrimFace> shifted = PrimGeometry.BuildFaces(Shape(shape, 1f, offset));
            Assert.Equal(plain.Select(f => f.Number), shifted.Select(f => f.Number));
            for (int i = 0; i < plain.Count; i++)
            {
                Assert.Equal(plain[i].TriangleCount, shifted[i].TriangleCount);
                Assert.Equal(plain[i].Positions, shifted[i].Positions);
            }
        }
    }

    [Theory]
    [InlineData("torus")]
    [InlineData("tube")]
    [InlineData("ring")]
    public void AnOffsetThatMovesTheEndsApartStillGetsItsEndCaps(string shape)
    {
        // hole size 0.25: the radii differ by 0.5 * 0.75 * 0.3 = 0.1125, far over 0.001, so the path is open
        List<PrimFace> faces = PrimGeometry.BuildFaces(Shape(shape, 0.25f, 0.3f), DetailLevel.High, out _, out int unnumbered);
        Assert.Equal(0, unnumbered);
        Assert.Contains(faces, f => f.Kind == PrimFaceKind.Top);
        Assert.Contains(faces, f => f.Kind == PrimFaceKind.Bottom);
    }

    // ------------------------------------------------------------ physics meshing keeps its earlier rule

    // The outer face's number is one lower when the mesher builds no end caps (the caps are faces 0 and the last).
    private static int OuterFaceNumber(bool viewerMode, float radius)
    {
        PrimMesh mesh = new PrimMesh(24, 0f, 1f, 0f, 24)
        {
            viewerMode = viewerMode, holeSizeX = 1f, holeSizeY = 1f, pathCutBegin = 0f, pathCutEnd = 1f,
            radius = radius, revolutions = 1f, stepsPerRevolution = 24
        };
        mesh.Extrude(PathType.Circular);
        return mesh.ProfileOuterFaceNumber;
    }

    [Fact]
    public void WithoutViewerModeAnyRadiusOffsetStillOpensThePath()
    {
        int closed = OuterFaceNumber(false, 0f);
        // physics meshing: the offset gives the path end caps even at hole size 1, as before
        Assert.Equal(closed + 1, OuterFaceNumber(false, 0.3f));
        // viewer mode: the same input is closed whatever the offset
        Assert.Equal(OuterFaceNumber(true, 0f), OuterFaceNumber(true, 0.3f));
    }
}
