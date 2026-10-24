/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// Face counts, face numbers and cap texture coordinates against the Second Life viewer's rule (LLProfile::generate,
/// LLPath::genNGon and LLVolumeFace::createCap in indra/llmath/llvolume.cpp), for the seven basic shapes and their cuts.
/// </summary>
public class FaceNumberingTests
{
    private static PrimParams Base(string shape)
    {
        PrimParams p = new PrimParams { ProfileEnd = 1f, PathEnd = 1f, PathScaleX = 1f, PathScaleY = 1f, PathRevolutions = 1f };
        switch (shape)
        {
            case "box": p.ProfileCurve = 1; p.PathCurve = 16; break;
            case "cylinder": p.ProfileCurve = 0; p.PathCurve = 16; break;
            case "prism": p.ProfileCurve = 3; p.PathCurve = 16; break;
            case "sphere": p.ProfileCurve = 5; p.PathCurve = 32; break;
            case "torus": p.ProfileCurve = 0; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
            case "tube": p.ProfileCurve = 1; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
            case "ring": p.ProfileCurve = 3; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
        }
        return p;
    }

    public static readonly string[] Shapes = { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring" };

    private static readonly (string name, Action<PrimParams> apply)[] Modifiers =
    {
        ("plain", p => { }),
        ("hollow", p => p.ProfileHollow = 0.4f),
        ("hollow-circle-hole", p => { p.ProfileHollow = 0.4f; p.HoleType = 16; }),
        ("hollow-triangle-hole", p => { p.ProfileHollow = 0.4f; p.HoleType = 48; }),
        ("profile-cut-quarter", p => { p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; }),
        ("profile-cut-off-grid", p => { p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; }),
        ("profile-cut-begin-only", p => p.ProfileBegin = 0.3f),
        ("profile-cut-end-only", p => p.ProfileEnd = 0.6f),
        ("hollow+profile-cut", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; }),
        ("hollow+profile-cut-off-grid", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; }),
        ("path-cut", p => { p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("hollow+path-cut", p => { p.ProfileHollow = 0.4f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("profile-cut+path-cut", p => { p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("hollow+profile-cut+path-cut", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("twist", p => p.PathTwist = 0.25f),
        ("taper", p => { p.PathTaperX = 0.3f; p.PathScaleX = 0.7f; }),
        ("skew", p => p.PathSkew = 0.2f),
        ("radius-offset", p => p.PathRadiusOffset = 0.3f),
        ("hollow+twist", p => { p.ProfileHollow = 0.4f; p.PathTwist = 0.25f; }),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in Shapes)
            foreach ((string name, _) in Modifiers)
                yield return new object[] { shape, name };
    }

    internal static PrimParams Make(string shape, string modifier)
    {
        PrimParams p = Base(shape);
        Modifiers.Single(m => m.name == modifier).apply(p);
        return p;
    }

    private static string Name(PrimFaceKind k) => k switch
    {
        PrimFaceKind.Top => FaceRuleOracle.Top,
        PrimFaceKind.Outer => FaceRuleOracle.Outer,
        PrimFaceKind.Inner => FaceRuleOracle.Inner,
        PrimFaceKind.Bottom => FaceRuleOracle.Bottom,
        PrimFaceKind.ProfileCutBegin => FaceRuleOracle.CutBegin,
        _ => FaceRuleOracle.CutEnd
    };

    // ------------------------------------------------------------ the rule, written out for the plain shapes

    [Theory]
    [InlineData("box", "plain", "top,outer,outer,outer,outer,bottom")]
    [InlineData("box", "hollow", "top,outer,outer,outer,outer,inner,bottom")]
    [InlineData("box", "profile-cut-quarter", "top,outer,outer,bottom,cutBegin,cutEnd")]
    [InlineData("box", "path-cut", "top,outer,outer,outer,outer,bottom")]
    [InlineData("cylinder", "plain", "top,outer,bottom")]
    [InlineData("cylinder", "hollow", "top,outer,inner,bottom")]
    [InlineData("cylinder", "profile-cut-quarter", "top,outer,bottom,cutBegin,cutEnd")]
    [InlineData("cylinder", "path-cut", "top,outer,bottom")]
    [InlineData("prism", "plain", "top,outer,outer,outer,bottom")]
    [InlineData("prism", "hollow", "top,outer,outer,outer,inner,bottom")]
    [InlineData("prism", "profile-cut-quarter", "top,outer,outer,outer,bottom,cutBegin,cutEnd")]
    [InlineData("prism", "path-cut", "top,outer,outer,outer,bottom")]
    [InlineData("sphere", "plain", "outer")]
    [InlineData("sphere", "hollow", "outer,inner,cutBegin,cutEnd")]
    [InlineData("sphere", "profile-cut-quarter", "outer,cutBegin,cutEnd")]
    [InlineData("sphere", "path-cut", "top,outer,bottom")]
    [InlineData("torus", "plain", "outer")]
    [InlineData("torus", "hollow", "outer,inner")]
    [InlineData("torus", "profile-cut-quarter", "outer,cutBegin,cutEnd")]
    [InlineData("torus", "path-cut", "top,outer,bottom")]
    [InlineData("tube", "plain", "outer,outer,outer,outer")]
    [InlineData("tube", "hollow", "outer,outer,outer,outer,inner")]
    [InlineData("tube", "profile-cut-quarter", "outer,outer,cutBegin,cutEnd")]
    [InlineData("tube", "path-cut", "top,outer,outer,outer,outer,bottom")]
    [InlineData("ring", "plain", "outer,outer,outer")]
    [InlineData("ring", "hollow", "outer,outer,outer,inner")]
    [InlineData("ring", "profile-cut-quarter", "outer,outer,outer,cutBegin,cutEnd")]
    [InlineData("ring", "path-cut", "top,outer,outer,outer,bottom")]
    public void TheRuleGivesTheFaceListOfTheBasicShapes(string shape, string modifier, string expected)
    {
        Assert.Equal(expected, string.Join(",", ViewerFaceRule.Faces(Make(shape, modifier)).Select(Name)));
    }

    // ------------------------------------------------------------ the exporter against the rule, for every variant

    [Theory]
    [MemberData(nameof(Cases))]
    public void FaceCountsAndNumbersMatchTheViewerRule(string shape, string modifier)
    {
        PrimParams p = Make(shape, modifier);
        List<string> expected = FaceRuleOracle.Faces(p);
        List<PrimFace> got = PrimGeometry.BuildFaces(p, DetailLevel.High, out int faceCount, out _);

        Assert.Equal(expected.Count, faceCount);
        Assert.Equal(expected, ViewerFaceRule.Faces(p).Select(Name).ToList());
        foreach (PrimFace f in got)
        {
            Assert.InRange(f.Number, 0, expected.Count - 1);
            Assert.Equal(expected[f.Number], Name(f.Kind));
        }
        Assert.Equal(got.Count, got.Select(f => f.Number).Distinct().Count());

        if (shape != "sphere")
        {
            // every face of these shapes has area, so every number is used
            Assert.Equal(Enumerable.Range(0, expected.Count), got.Select(f => f.Number));
        }
        else
        {
            // the cut faces of a sphere lie on its axis where they meet a pole, and a face with no area is not exported
            foreach (int missing in Enumerable.Range(0, expected.Count).Except(got.Select(f => f.Number)))
                Assert.True(expected[missing] == FaceRuleOracle.CutBegin || expected[missing] == FaceRuleOracle.CutEnd, modifier + ": missing " + expected[missing]);
        }
    }

    [Fact]
    public void ASphereIsOneFaceWithItsPoleFansMerged()
    {
        List<PrimFace> faces = PrimGeometry.BuildFaces(Make("sphere", "plain"));
        PrimFace only = Assert.Single(faces);
        Assert.Equal(0, only.Number);
        Assert.Equal(504 + 24, only.TriangleCount);
    }

    [Fact]
    public void ASphereCutAtOneEndKeepsTheNumberOfTheCutFaceThatHasArea()
    {
        // begin cut only: the cut face at the end lies on the axis, so the one with area is the first
        Assert.Equal(new[] { 0, 1 }, PrimGeometry.BuildFaces(Make("sphere", "profile-cut-begin-only")).Select(f => f.Number));
        // end cut only: the first cut face lies on the axis, so the number 1 is unused and the face with area is 2
        Assert.Equal(new[] { 0, 2 }, PrimGeometry.BuildFaces(Make("sphere", "profile-cut-end-only")).Select(f => f.Number));
    }

    [Fact]
    public void ASphereWithARadiusOffsetStaysOneClosedFaceWithNoEndCaps()
    {
        // a sphere's hole size is 1, so the viewer ignores a radius offset and the sphere stays one closed face; the mesher
        // used to add end caps, which belonged to no face of the viewer's list (see RadiusOffsetPathTests)
        List<PrimFace> faces = PrimGeometry.BuildFaces(Make("sphere", "radius-offset"), DetailLevel.High, out int count, out int unnumbered);
        Assert.Equal(1, count);
        Assert.Single(faces);
        Assert.Equal(0, unnumbered);
    }

    [Fact]
    public void AHollowShapeHasNoWebBetweenItsOuterAndInnerWalls()
    {
        // box hollowed: top ring 8, four sides of 2, inner wall 4 sides of 2, bottom ring 8
        Assert.Equal(new[] { 8, 2, 2, 2, 2, 8, 8 }, PrimGeometry.BuildFaces(Make("box", "hollow")).Select(f => f.TriangleCount));
        // cylinder: 24 sides, so a ring of 48 triangles at each end and a wall of 48
        Assert.Equal(new[] { 48, 48, 48, 48 }, PrimGeometry.BuildFaces(Make("cylinder", "hollow")).Select(f => f.TriangleCount));
        Assert.Equal(new[] { 48, 48, 48, 48 }, PrimGeometry.BuildFaces(Make("cylinder", "hollow+path-cut")).Select(f => f.TriangleCount));
    }

    // ------------------------------------------------------------ what each face is, by where its triangles lie

    private static IEnumerable<(float x, float y, float z, float nx, float ny, float nz)> Vertices(PrimFace f)
    {
        for (int i = 0; i < f.Positions.Length; i += 3)
            yield return (f.Positions[i], f.Positions[i + 1], f.Positions[i + 2], f.Normals[i], f.Normals[i + 1], f.Normals[i + 2]);
    }

    [Theory]
    [InlineData("box", "plain")]
    [InlineData("box", "hollow")]
    [InlineData("box", "profile-cut-quarter")]
    [InlineData("box", "hollow+profile-cut")]
    [InlineData("box", "path-cut")]
    [InlineData("cylinder", "plain")]
    [InlineData("cylinder", "hollow")]
    [InlineData("cylinder", "profile-cut-quarter")]
    [InlineData("cylinder", "hollow+profile-cut")]
    [InlineData("cylinder", "path-cut")]
    [InlineData("prism", "plain")]
    [InlineData("prism", "hollow")]
    [InlineData("prism", "profile-cut-quarter")]
    [InlineData("prism", "path-cut")]
    public void EachFaceOnAStraightPathIsWhatItsNumberSays(string shape, string modifier)
    {
        PrimParams p = Make(shape, modifier);
        float zTop = p.PathEnd - 0.5f, zBottom = p.PathBegin - 0.5f;
        foreach (PrimFace f in PrimGeometry.BuildFaces(p))
        {
            List<(float x, float y, float z, float nx, float ny, float nz)> vs = Vertices(f).ToList();
            switch (f.Kind)
            {
                case PrimFaceKind.Top:
                    Assert.All(vs, v => { Assert.Equal(zTop, v.z, 3); Assert.Equal(1f, v.nz, 3); });
                    break;
                case PrimFaceKind.Bottom:
                    Assert.All(vs, v => { Assert.Equal(zBottom, v.z, 3); Assert.Equal(-1f, v.nz, 3); });
                    break;
                case PrimFaceKind.Outer:
                    Assert.True(vs.Average(v => v.x * v.nx + v.y * v.ny) > 0.1f, "outer face " + f.Number + " faces outwards");
                    break;
                case PrimFaceKind.Inner:
                    Assert.True(vs.Average(v => v.x * v.nx + v.y * v.ny) < -0.01f, "inner face " + f.Number + " faces the axis");
                    break;
                default:
                    // a cut face is a flat sheet on a plane through the axis
                    var reference = vs.First(w => w.x * w.x + w.y * w.y > 1e-6f);
                    Assert.All(vs, v => Assert.Equal(0f, v.x * reference.y - v.y * reference.x, 3));
                    break;
            }
        }
    }

    [Theory]
    [InlineData("cylinder", 90.0, 270.0)] // a circle profile runs counter-clockwise from +x: a cut from 0.25 to 0.75 starts at 90 degrees
    [InlineData("box", -45.0, 135.0)]     // a square profile starts at -135 degrees: the same cut starts at the corner at -45
    public void TheCutFacesRunFromTheBeginToTheEndOfTheProfileCut(string shape, double beginDegrees, double endDegrees)
    {
        List<PrimFace> faces = PrimGeometry.BuildFaces(Make(shape, "profile-cut-quarter"));
        foreach ((PrimFaceKind kind, double degrees) in new[] { (PrimFaceKind.ProfileCutBegin, beginDegrees), (PrimFaceKind.ProfileCutEnd, endDegrees) })
        {
            PrimFace f = faces.Single(x => x.Kind == kind);
            (float x, float y, float z, float nx, float ny, float nz) far = Vertices(f).OrderByDescending(v => v.x * v.x + v.y * v.y).First();
            double angle = Math.Atan2(far.y, far.x) * 180.0 / Math.PI;
            double diff = Math.Abs(((angle - degrees) % 360.0 + 540.0) % 360.0 - 180.0);
            Assert.True(diff < 1.0, shape + " " + kind + ": the cut face lies at " + angle + " degrees, expected " + degrees);
        }
    }

    // ------------------------------------------------------------ cap texture coordinates

    private static void AssertCapUv(PrimFace f, bool top)
    {
        // The viewer's rule: u = x + 0.5 and v = y + 0.5 on the top cap, v = 0.5 - y on the bottom cap, with v up. The
        // coordinates here have v = 0 at the top of the image, the glTF convention, so v is 1 minus the viewer's. The
        // profile point is, on a plain straight-path prim, the vertex's own x and y.
        for (int i = 0; i < f.Positions.Length / 3; i++)
        {
            float x = f.Positions[i * 3], y = f.Positions[i * 3 + 1];
            Assert.Equal(x + 0.5f, f.Uvs[i * 2], 3);
            Assert.Equal(top ? 0.5f - y : 0.5f + y, f.Uvs[i * 2 + 1], 3);
        }
    }

    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("prism")]
    public void CapUvsFollowTheViewersRuleOnAStraightPath(string shape)
    {
        List<PrimFace> faces = PrimGeometry.BuildFaces(Make(shape, "plain"));
        AssertCapUv(faces.Single(f => f.Kind == PrimFaceKind.Top), true);
        AssertCapUv(faces.Single(f => f.Kind == PrimFaceKind.Bottom), false);
    }

    private static HashSet<(int, int)> UvSet(PrimFace f) =>
        Enumerable.Range(0, f.Uvs.Length / 2).Select(i => ((int)MathF.Round(f.Uvs[i * 2] * 100f), (int)MathF.Round(f.Uvs[i * 2 + 1] * 100f))).ToHashSet();

    [Theory]
    [InlineData("box", "tube")]
    [InlineData("prism", "ring")]
    [InlineData("cylinder", "torus")]
    public void CapUvsOnACircularPathAreTheSamePointsAsOnAStraightPath(string straight, string circular)
    {
        // the same profile, cut so it is not symmetric, gives the same set of cap coordinates whichever path carries it
        PrimParams a = Make(straight, "profile-cut-off-grid"), b = Make(circular, "profile-cut+path-cut");
        b.ProfileBegin = a.ProfileBegin;
        b.ProfileEnd = a.ProfileEnd;
        List<PrimFace> fa = PrimGeometry.BuildFaces(a), fb = PrimGeometry.BuildFaces(b);
        foreach (PrimFaceKind kind in new[] { PrimFaceKind.Top, PrimFaceKind.Bottom })
            Assert.Equal(UvSet(fa.Single(f => f.Kind == kind)), UvSet(fb.Single(f => f.Kind == kind)));
    }

    [Fact]
    public void ABottomCapIsTheTopCapMirroredInV()
    {
        List<PrimFace> faces = PrimGeometry.BuildFaces(Make("prism", "plain"));
        HashSet<(int, int)> top = UvSet(faces.Single(f => f.Kind == PrimFaceKind.Top)), bottom = UvSet(faces.Single(f => f.Kind == PrimFaceKind.Bottom));
        Assert.Equal(top.Select(t => (t.Item1, 100 - t.Item2)).ToHashSet(), bottom);
    }
}
