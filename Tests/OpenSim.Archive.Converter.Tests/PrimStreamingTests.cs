/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The viewer prices a plain prim from the triangles it estimates for the prim's four levels of detail
/// (LLVolume::getLoDTriangleCounts, llvolume.cpp 3693: path points and profile points at detail 1, 1.5, 2.5 and 4) and
/// ten bytes a triangle (LLVOVolume::getCostData, llvovolume.cpp 4393). The expected counts come from an independent
/// single-precision port of the viewer's point counting (a script kept with the order's notes), not from this code.
/// New classes share no process state; they run in parallel.
/// </summary>
public class PrimStreamingTests
{
    private static ShapeData Shape(int profile, int path, int hollow = 0, int twist = 0, int profileBegin = 0, int profileEnd = 0) =>
        new ShapeData { ProfileCurve = profile, PathCurve = path, ProfileHollow = hollow, PathTwist = twist, ProfileBegin = profileBegin, ProfileEnd = profileEnd, PCode = 9 };

    [Theory]
    [InlineData(1, 16, 0, 0, 0, 0, 18, 18, 18, 18)]            // box
    [InlineData(0, 16, 0, 0, 0, 0, 26, 38, 62, 98)]            // cylinder
    [InlineData(5, 32, 0, 0, 0, 0, 62, 110, 272, 674)]         // sphere
    [InlineData(0, 32, 0, 0, 0, 0, 74, 164, 452, 1154)]        // torus
    [InlineData(1, 16, 25000, 0, 0, 0, 38, 38, 38, 38)]        // hollow box
    [InlineData(1, 16, 0, 100, 0, 0, 26, 42, 74, 114)]         // box twisted a half turn
    [InlineData(3, 16, 0, 0, 0, 0, 14, 14, 14, 14)]            // prism
    [InlineData(0, 48, 0, 0, 0, 0, 86, 182, 482, 1202)]        // ring
    [InlineData(0, 16, 0, 0, 12500, 12500, 22, 26, 42, 54)]    // cylinder cut to its middle half
    public void TheCountsAreTheViewersEstimateAtEachLevel(int profile, int path, int hollow, int twist, int pBegin, int pEnd, int lowest, int low, int medium, int high)
    {
        Assert.Equal(new[] { lowest, low, medium, high }, PrimStreaming.TriangleCounts(Shape(profile, path, hollow, twist, pBegin, pEnd)));
    }

    [Fact]
    public void ABoxCostsTheFloorOfSixHundredthsAtAnySize()
    {
        // 18 triangles at ten bytes is under the viewer's metadata discount, so every level counts the minimum of one triangle
        Assert.Equal(0.06, PrimStreaming.Cost(Shape(1, 16), new Vector3(0.5f, 0.5f, 0.5f)), 9);
        Assert.Equal(0.06, PrimStreaming.Cost(Shape(1, 16), new Vector3(40, 40, 40)), 9);
    }

    [Fact]
    public void ASphereCostsWhatTheStreamingFormulaGivesForItsFourSizes()
    {
        Vector3 scale = new Vector3(2, 2, 2);
        double expected = new StreamingCost(62 * 10, 110 * 10, 272 * 10, 674 * 10).StaticCost(0.5 * scale.Length());
        Assert.Equal(expected, PrimStreaming.Cost(Shape(5, 32), scale), 9);
        Assert.True(expected > 0.06);
    }

    [Fact]
    public void AnArchiveOfBoxesAndCylindersHasTheViewerCostOfEachPart()
    {
        // MapsFixture: eight parts in each of two objects, parts 3 and 6 cylinders (26, 38, 62 and 98 triangles), the rest boxes; scale (0.8 + 0.3 i, 1.2 - 0.1 i, 0.6 + 0.2 (i mod 4)) for part i from 0
        double Cylinder(float x, float y, float z) => new StreamingCost(260, 380, 620, 980).StaticCost(0.5 * new Vector3(x, y, z).Length());
        double expected = MapsFixture.ObjectCount * (6 * 0.06 + Cylinder(1.4f, 1.0f, 1.0f) + Cylinder(2.3f, 0.7f, 0.8f));
        LandImpactReport r = LandImpact.Measure(MapsFixture.Write());
        Assert.Equal(expected, r.ViewerStreamingCostPrims, 4);
        Assert.Equal(r.ViewerStreamingCost + r.ViewerStreamingCostPrims, r.ViewerStreamingCostTotal, 9);
    }
}
