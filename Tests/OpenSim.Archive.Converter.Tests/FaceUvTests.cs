/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The texture coordinates baked for a merged face, against numbers worked out by hand from the viewer's xform and
/// planarProjection (indra/newview/llface.cpp). No test here shares state, so the class runs in parallel.
/// </summary>
public class FaceUvTests
{
    private const float Tol = 1e-5f;

    [Fact]
    public void TheIdentityTransformLeavesACoordinateAlone()
    {
        float s = 0.37f, t = 0.81f;
        FaceUv.Transform(ref s, ref t, FaceTransform.Identity);
        Assert.Equal(0.37f, s, Tol);
        Assert.Equal(0.81f, t, Tol);
    }

    [Fact]
    public void ARotationAboutTheCentreThenRepeatsThenOffsetGivesTheWorkedNumbers()
    {
        // (0.75, 0.5): s = 0.25, t = 0 about the centre; a quarter turn gives s = 0, t = -0.25; repeats (2, 1) leave them;
        // the offset (0.1, 0) and the centre give (0.6, 0.25)
        float s = 0.75f, t = 0.5f;
        FaceUv.Transform(ref s, ref t, new FaceTransform(2, 1, 0.1f, 0, MathF.PI / 2));
        Assert.Equal(0.6f, s, Tol);
        Assert.Equal(0.25f, t, Tol);
    }

    [Fact]
    public void ANegativeRepeatFlipsTheCoordinateAboutTheCentre()
    {
        float s = 0.2f, t = 0.3f;
        FaceUv.Transform(ref s, ref t, new FaceTransform(-1, 1, 0, 0, 0));
        Assert.Equal(0.8f, s, Tol);
        Assert.Equal(0.3f, t, Tol);
    }

    [Theory]
    [InlineData(1, 0, 0, 2.5f, 1.1f)]
    [InlineData(-1, 0, 0, -1.5f, 1.1f)]
    [InlineData(0, 1, 0, -1.5f, 1.1f)]
    [InlineData(0, -1, 0, 2.5f, 1.1f)]
    [InlineData(0, 0, 1, 2.5f, 2.5f)]
    [InlineData(0, 0, -1, 2.5f, 2.5f)]
    public void PlanarProjectionOfAVertexMatchesTheWorkedNumbersForEachDominantNormal(float nx, float ny, float nz, float u, float v)
    {
        // position (0.5, 0.25, 0.3) at size (2, 4, 1) is (1, 1, 0.3) in the viewer's arithmetic
        FaceUv.Planar(new Vector3(nx, ny, nz), new Vector3(0.5f, 0.25f, 0.3f), new Vector3(2, 4, 1), out float gotU, out float gotV);
        Assert.Equal(u, gotU, Tol);
        Assert.Equal(v, gotV, Tol);
    }

    [Fact]
    public void ABakedPlanarFaceOfABoxMatchesTheViewerProjectionAtEveryCorner()
    {
        Vector3 scale = new Vector3(2, 4, 1);
        List<EncoderFace> faces = FaceBuilder.FromPrim(FaceMatrix.Shape("box", "plain"), out _);
        FaceTransform xf = new FaceTransform(3, -2, 0.25f, -0.4f, 0.7f);
        int checkedCorners = 0;
        foreach (EncoderFace f in faces.Where(f => !f.IsEmpty))
        {
            float[] baked = FaceUv.Bake(f.Positions, f.Normals, f.Uvs, xf, planar: true, scale);
            Assert.Equal(f.VertexCount * 2, baked.Length);
            for (int i = 0; i < f.VertexCount; i++)
            {
                Vector3 n = new Vector3(f.Normals[i * 3], f.Normals[i * 3 + 1], f.Normals[i * 3 + 2]);
                Vector3 p = new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]) * scale;
                // the viewer's binormal and tangent for an axis-aligned normal, written out
                Vector3 b = MathF.Abs(n.X) >= 0.5f ? new Vector3(0, n.X < 0 ? -1 : 1, 0) : new Vector3(n.Y > 0 ? -1 : 1, 0, 0);
                Vector3 tg = Vector3.Cross(b, n);
                float s = 1f + (Vector3.Dot(b, p) * 2 - 0.5f);
                float t = -(Vector3.Dot(tg, p) * 2 - 0.5f);
                float c = MathF.Cos(xf.Rotation), sn = MathF.Sin(xf.Rotation);
                s -= 0.5f; t -= 0.5f;
                float s2 = s * c + t * sn, t2 = -s * sn + t * c;
                s2 *= xf.RepeatU; t2 *= xf.RepeatV;
                s2 += xf.OffsetU + 0.5f; t2 += xf.OffsetV + 0.5f;
                Assert.Equal(s2, baked[i * 2], Tol);
                Assert.Equal(t2, baked[i * 2 + 1], Tol);
                checkedCorners++;
            }
        }
        Assert.True(checkedCorners >= 24, "a box has 24 corners");
    }

    [Fact]
    public void ABakedDefaultMappedFaceTransformsTheFacesOwnCoordinates()
    {
        List<EncoderFace> faces = FaceBuilder.FromPrim(FaceMatrix.Shape("box", "plain"), out _);
        EncoderFace f = faces.First(x => !x.IsEmpty);
        FaceTransform xf = new FaceTransform(2, 3, 0.5f, 0.25f, 0.3f);
        float[] baked = FaceUv.Bake(f.Positions, f.Normals, f.Uvs, xf, planar: false, new Vector3(1, 1, 1));
        for (int i = 0; i < f.VertexCount; i++)
        {
            float s = f.Uvs[i * 2], t = f.Uvs[i * 2 + 1];
            FaceUv.Transform(ref s, ref t, xf);
            Assert.Equal(s, baked[i * 2], Tol);
            Assert.Equal(t, baked[i * 2 + 1], Tol);
        }
    }
}
