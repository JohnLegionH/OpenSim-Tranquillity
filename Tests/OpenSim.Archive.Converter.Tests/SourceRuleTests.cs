/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The rules the converter depends on, checked against the viewer's source: face counts where the server and viewer differ
/// (indra/llmath/llvolume.cpp: LLProfile::generate 770-1003, LLPath::genNGon 1217-1277), and the viewer's default texture
/// coordinates (LLVolumeFace::createSide 6795-6890, createCap 6165-6250).
/// </summary>
public class SourceRuleTests
{
    // ------------------------------------------------------------ face counts where the server and viewer differ

    public static IEnumerable<object[]> Disagreements()
    {
        // shape, modifier, server count, viewer count, settled case
        yield return new object[] { "sphere", "taper", 1, 3, FaceRuleCases.TaperedSphere };
        yield return new object[] { "sphere", "taper-y", 1, 3, FaceRuleCases.TaperedSphere };
        yield return new object[] { "torus", "revolutions", 3, 1, FaceRuleCases.Revolutions };
        yield return new object[] { "tube", "revolutions", 6, 4, FaceRuleCases.Revolutions };
        yield return new object[] { "ring", "revolutions", 5, 3, FaceRuleCases.Revolutions };
    }

    // The third kind of disagreement: a radius offset with a hole size of 1. The viewer's count is right (the path is closed,
    // LLPath::genNGon 1253 and 1273-1277). The exporter's mesher used to draw the path open and build end-cap triangles the
    // viewer has no face for; in viewer mode it now closes the path as the viewer does, so these parts convert.
    [Theory]
    [InlineData("torus", 3, 1)]
    [InlineData("tube", 6, 4)]
    [InlineData("ring", 5, 3)]
    public void AClosedHoleRadiusOffsetHasTheViewersCountAndTheMesherBuildsNoExtraTriangles(string shape, int server, int viewer)
    {
        ShapeData s = FaceMatrix.Shape(shape, "hole-size-one+radius-offset");
        FaceCounts c = FaceAgreement.Measure(s);
        Assert.Equal(server, c.Server);
        Assert.Equal(viewer, c.Viewer);
        Assert.Equal(FaceRuleCases.ClosedHoleRadiusOffset, FaceRuleCases.Cause(s));
        Assert.Equal(FaceRuleCases.ClosedHoleRadiusOffset, FaceRuleCases.Explain(s));

        List<PrimFace> faces = PrimGeometry.BuildFaces(PrimParams.FromShape(s), DetailLevel.High, out int count, out int unnumbered);
        Assert.Equal(viewer, count);
        Assert.Equal(0, unnumbered);
        List<PrimFaceKind> kinds = ViewerFaceRule.Faces(PrimParams.FromShape(s));
        Assert.All(faces, f => Assert.Equal(kinds[f.Number], f.Kind));
    }

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void TheViewerRuleIsRightWhereTheCountsDifferAndTheCaseIsSettled(string shape, string modifier, int server, int viewer, string settled)
    {
        ShapeData s = FaceMatrix.Shape(shape, modifier);
        FaceCounts c = FaceAgreement.Measure(s);
        Assert.Equal(server, c.Server);
        Assert.Equal(viewer, c.Viewer);
        Assert.Equal(settled, FaceRuleCases.Explain(s));
    }

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void TheMesherBuildsTheViewersFaceListInTheseCases(string shape, string modifier, int server, int viewer, string settled)
    {
        PrimParams p = PrimParams.FromShape(FaceMatrix.Shape(shape, modifier));
        List<PrimFace> faces = PrimGeometry.BuildFaces(p, DetailLevel.High, out int count, out int unnumbered);

        Assert.Equal(viewer, count);
        // every face the mesher builds has the number and kind the viewer's rule gives that slot
        List<PrimFaceKind> kinds = ViewerFaceRule.Faces(p);
        Assert.All(faces, f => Assert.Equal(kinds[f.Number], f.Kind));
        Assert.Equal(0, unnumbered);
        Assert.NotEmpty(faces);
    }

    [Fact]
    public void ADisagreementThatIsNotASettledCaseIsNotExplained()
    {
        // a tapered sphere that also has revolutions has two causes, so neither settled case covers it
        ShapeData s = FaceMatrix.Shape("sphere", "taper");
        s.PathRevolutions = 20;
        Assert.NotEqual(FaceAgreement.Measure(s).Server, FaceAgreement.Measure(s).Viewer);
        Assert.Null(FaceRuleCases.Explain(s));
    }

    [Fact]
    public void EveryDisagreementInTheProbeMatricesIsASettledCase()
    {
        int checkedShapes = 0;
        foreach (string shape in new[] { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring" })
            foreach (string modifier in FaceMatrix.Modifiers.Concat(FaceMatrix.ExtraModifiers).Select(m => m.name))
            {
                ShapeData s = FaceMatrix.Shape(shape, modifier);
                FaceCounts c = FaceAgreement.Measure(s);
                checkedShapes++;
                if (!c.Agree)
                    Assert.True(FaceRuleCases.Explain(s) != null,
                        shape + " / " + modifier + " differs (" + c.Server + " against " + c.Viewer + ") and is not a settled case");
            }
        Assert.True(checkedShapes >= 196);
    }

    // ------------------------------------------------------------ the viewer's default texture coordinates

    private static List<EncoderFace> Faces(string shape, string modifier) =>
        FaceBuilder.FromPrim(FaceMatrix.Shape(shape, modifier), out _);

    private static List<PrimFace> PrimFaces(string shape, string modifier) =>
        PrimGeometry.BuildFaces(PrimParams.FromShape(FaceMatrix.Shape(shape, modifier)));

    [Theory]
    [InlineData("box", "plain")]
    [InlineData("box", "hollow")]
    [InlineData("box", "profile-cut-quarter")]
    [InlineData("cylinder", "plain")]
    [InlineData("cylinder", "hollow")]
    [InlineData("prism", "plain")]
    [InlineData("prism", "hollow")]
    public void CapsFollowTheViewersCapRule(string shape, string modifier)
    {
        // viewer (createCap): top cap u = x + 0.5, v = y + 0.5; bottom cap u = x + 0.5, v = 0.5 - y; x and y are the profile point
        List<PrimFace> faces = PrimFaces(shape, modifier);
        int capsSeen = 0;
        foreach (PrimFace f in faces.Where(f => f.Kind == PrimFaceKind.Top || f.Kind == PrimFaceKind.Bottom))
        {
            EncoderFace m = FaceBuilder.ToMeshFace(f);
            capsSeen++;
            for (int i = 0; i < m.VertexCount; i++)
            {
                float x = m.Positions[i * 3], y = m.Positions[i * 3 + 1];
                float u = m.Uvs[i * 2], v = m.Uvs[i * 2 + 1];
                Assert.Equal(x + 0.5f, u, 4);
                Assert.Equal(f.Kind == PrimFaceKind.Top ? y + 0.5f : 0.5f - y, v, 4);
            }
        }
        Assert.Equal(2, capsSeen);
    }

    [Theory]
    [InlineData("box", "plain")]
    [InlineData("box", "hollow")]
    [InlineData("cylinder", "plain")]
    [InlineData("prism", "plain")]
    public void SidesRunTheViewersTexturePathUpTheHeight(string shape, string modifier)
    {
        // viewer (createSide): v is the path's texture position, which on a straight untwisted path is the height plus one half
        foreach (PrimFace f in PrimFaces(shape, modifier).Where(f => f.Kind == PrimFaceKind.Outer || f.Kind == PrimFaceKind.Inner))
        {
            EncoderFace m = FaceBuilder.ToMeshFace(f);
            for (int i = 0; i < m.VertexCount; i++)
                Assert.Equal(m.Positions[i * 3 + 2] + 0.5f, m.Uvs[i * 2 + 1], 4);
        }
    }

    [Fact]
    public void EachSideOfABoxRunsUFromZeroToOneAlongTheCounterclockwiseDirection()
    {
        // viewer (createSide, LLProfile::generate): each side of a square profile has s from 0 to 1 in the direction the profile travels
        List<PrimFace> sides = PrimFaces("box", "plain").Where(f => f.Kind == PrimFaceKind.Outer).ToList();
        Assert.Equal(4, sides.Count);
        foreach (PrimFace f in sides)
        {
            EncoderFace m = FaceBuilder.ToMeshFace(f);
            // the outward normal is in the xy plane; counterclockwise from above is (-ny, nx)
            float nx = m.Normals[0], ny = m.Normals[1];
            float dx = -ny, dy = nx;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < m.VertexCount; i++)
            {
                float along = m.Positions[i * 3] * dx + m.Positions[i * 3 + 1] * dy;
                Assert.Equal(along + 0.5f, m.Uvs[i * 2], 4);
                min = Math.Min(min, m.Uvs[i * 2]);
                max = Math.Max(max, m.Uvs[i * 2]);
            }
            Assert.Equal(0f, min, 4);
            Assert.Equal(1f, max, 4);
        }
    }

    [Fact]
    public void TheOneSideOfACylinderRunsUOnceRoundFromZeroToOne()
    {
        PrimFace side = PrimFaces("cylinder", "plain").Single(f => f.Kind == PrimFaceKind.Outer);
        EncoderFace m = FaceBuilder.ToMeshFace(side);
        float min = m.Uvs.Where((_, i) => i % 2 == 0).Min(), max = m.Uvs.Where((_, i) => i % 2 == 0).Max();
        Assert.Equal(0f, min, 4);
        Assert.Equal(1f, max, 4);
        // u grows with the angle counterclockwise from above: at the vertex nearest the +x axis it is a whole number of quarter turns from the start
        for (int i = 0; i < m.VertexCount; i++)
        {
            float angle = MathF.Atan2(m.Positions[i * 3 + 1], m.Positions[i * 3]);
            float turns = (angle / (2f * MathF.PI) + 1f) % 1f;
            float u = m.Uvs[i * 2];
            // u and the angle differ by a constant offset (mod 1), whatever the start of the profile is
            float offset = ((u - turns) % 1f + 1f) % 1f;
            float first = (((m.Uvs[0] - ((MathF.Atan2(m.Positions[1], m.Positions[0]) / (2f * MathF.PI) + 1f) % 1f)) % 1f) + 1f) % 1f;
            float diff = Math.Abs(offset - first);
            Assert.True(diff < 1e-3f || Math.Abs(diff - 1f) < 1e-3f, "u does not follow the angle at vertex " + i);
        }
    }
}
