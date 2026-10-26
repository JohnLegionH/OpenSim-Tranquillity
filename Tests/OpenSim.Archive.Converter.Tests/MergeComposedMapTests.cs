/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A face whose legacy material draws a map with a transform other than the face transform. The viewer draws the diffuse with the face transform F
/// and the map with the material transform M, both from the same base coordinates (llface.cpp, getGeometryVolume). Once F is in the mesh, the merged
/// face material must draw the map with N, where N(F(p)) = M(p) for every p. That is possible, with the five figures a material stores, when the
/// relative transform has no shear: the merged face then bakes. The viewer transform is written out again here (xform, llface.cpp 734-757).
/// Each test builds its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergeComposedMapTests
{
    private static (float S, float T) Xform(FaceTransform x, float s, float t)
    {
        float cos = (float)Math.Cos(x.Rotation), sin = (float)Math.Sin(x.Rotation);
        float a = s - 0.5f, b = t - 0.5f;
        float turned = a;
        a = a * cos + b * sin;
        b = -turned * sin + b * cos;
        return (a * x.RepeatU + x.OffsetU + 0.5f, b * x.RepeatV + x.OffsetV + 0.5f);
    }

    private static double Worst(FaceTransform face, FaceTransform map, FaceTransform composed)
    {
        double worst = 0;
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++)
            {
                float u = -1f + i * 0.4f, v = -1f + j * 0.4f;
                (float fs, float ft) = Xform(face, u, v);
                (float ns, float nt) = Xform(composed, fs, ft);
                (float ms, float mt) = Xform(map, u, v);
                worst = Math.Max(worst, Math.Max(Math.Abs(ns - ms), Math.Abs(nt - mt)));
            }
        return worst;
    }

    public static IEnumerable<object[]> Expressible() => new[]
    {
        // face, map
        new object[] { new FaceTransform(2f, 2f, 0f, 0f, 0f), new FaceTransform(3f, 3f, 0f, 0f, 0f) },
        new object[] { new FaceTransform(2f, 2f, 0.25f, -0.5f, 0.3f), new FaceTransform(3f, 3f, 0.1f, 0.2f, 0.7f) },
        new object[] { new FaceTransform(2f, 0.5f, -0.125f, 0.0625f, 0f), new FaceTransform(4f, 3f, 0.2f, 0.1f, 0f) },
        new object[] { new FaceTransform(2f, 3f, 0.1f, 0.2f, 0.2f), new FaceTransform(5f, 1.5f, -0.3f, 0.4f, 0.2f) },
        new object[] { new FaceTransform(-1f, 1f, 0f, 0f, 0f), new FaceTransform(1f, 1f, 0.5f, 0f, 0f) },
        new object[] { new FaceTransform(2f, 3f, 0f, 0f, 0.2f), new FaceTransform(2f, 3f, 0f, 0f, 0.2f + (float)(Math.PI / 2)) },
    };

    [Theory]
    [MemberData(nameof(Expressible))]
    public void TheMapTransformRelativeToTheBakedDiffuseDrawsTheMapWhereTheViewerDoes(FaceTransform face, FaceTransform map)
    {
        Assert.True(MaterialBaking.Compose(face, map, out FaceTransform composed));
        Assert.True(Worst(face, map, composed) < 2e-5, "worst " + Worst(face, map, composed) + " with " + composed);
    }

    [Fact]
    public void AMapDrawnLikeTheDiffuseComposesToTheIdentity()
    {
        FaceTransform t = new FaceTransform(2f, 3f, 0.25f, -0.5f, 0.3f);
        Assert.True(MaterialBaking.Compose(t, t, out FaceTransform composed));
        Assert.True(composed.SameAs(FaceTransform.Identity), composed.ToString());
    }

    [Theory]
    [InlineData(2f, 3f, 0.2f, 2f, 3f, 0.9f)]
    [InlineData(1f, 4f, 0f, 1f, 4f, 0.5f)]
    [InlineData(2f, 3f, 0.4f, 3f, 3f, 0.9f)]
    public void ARelativeTransformThatShearsCannotBeStated(float fu, float fv, float fr, float mu, float mv, float mr)
    {
        Assert.False(MaterialBaking.Compose(new FaceTransform(fu, fv, 0.1f, 0.2f, fr), new FaceTransform(mu, mv, 0.1f, 0.2f, mr), out _));
    }

    [Fact]
    public void AFaceWithAnExpressibleMapTransformBakesAndOneWithAShearStaysUnbaked()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(ComposedMapsFixture.Write(), new MergeOptions());
        Assert.Equal(MapsFixture.ObjectCount, plan.MergedParts);
        Assert.Equal(0, plan.KeptParts);
        foreach (LinksetPlan l in plan.Linksets)
        {
            MergedPartPlan merged = Assert.Single(l.Merged);
            // the uniform face, the stretched planar face, the sheared face (alone), and the plain faces
            Assert.Equal(4, merged.Slots.Count);
            Assert.Equal(3, merged.Slots.Count(s => s.Baked));
        }
    }

    [Fact]
    public void TheMergedMaterialsStateTheComposedTransformsAndTheShearedFaceKeepsItsOwn()
    {
        MergeLegacyMapTests.Written w = MergeLegacyMapTests.WriteFixture(ComposedMapsFixture.Write());
        Dictionary<Guid, LegacyMaterialInfo> source = ArchiveMaterials.Read(w.Source);
        bool sawUniform = false, sawStretched = false, sheared = false;
        foreach (LinksetPlan l in w.Plan.Linksets)
        {
            MergedPartPlan mp = l.Merged.Single();
            Primitive.TextureEntry te = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
            for (int slot = 0; slot < mp.Slots.Count; slot++)
            {
                Primitive.TextureEntryFace face = ((te.FaceTextures != null && slot < te.FaceTextures.Length) ? te.FaceTextures[slot] : null) ?? te.DefaultTexture;
                Guid material = face.MaterialID.Guid;
                if (material == ComposedMapsFixture.MaterialSheared)
                {
                    sheared = true;
                    Assert.False(mp.Slots[slot].Baked);
                    Assert.True(new FaceTransform(face.RepeatU, face.RepeatV, face.OffsetU, face.OffsetV, face.Rotation).SameAs(ComposedMapsFixture.FaceSheared));
                    continue;
                }
                if (!mp.Slots[slot].Baked || material == Guid.Empty)
                    continue;
                LegacyMaterialInfo info = w.OutMaterials[material];
                // the merged face names a material the source did not have, whose normal map is drawn with N such that N(F(p)) = M(p)
                Assert.False(source.ContainsKey(material));
                Assert.False(info.NormalTransform.SameAs(FaceTransform.Identity));
                bool uniform = info.HasSpecularMap;
                FaceTransform face0 = uniform ? ComposedMapsFixture.FaceUniform : ComposedMapsFixture.FaceStretched;
                FaceTransform map0 = uniform ? ComposedMapsFixture.NormalUniform : ComposedMapsFixture.NormalStretched;
                Assert.True(Worst(face0, map0, info.NormalTransform) < 1e-3, "worst " + Worst(face0, map0, info.NormalTransform) + " with " + info.NormalTransform);
                if (uniform)
                {
                    sawUniform = true;
                    // the specular map was drawn like the diffuse: its transform is the identity now
                    Assert.True(info.SpecularTransform.SameAs(FaceTransform.Identity), info.SpecularTransform.ToString());
                }
                else
                    sawStretched = true;
            }
        }
        Assert.True(sawUniform && sawStretched && sheared);
    }

    [Fact]
    public void EveryCornerOfTheDiffuseAndOfEachMapIsWhereTheViewerDrawsTheSourceCornerWhenTheMapTransformDiffers()
    {
        MergeLegacyMapTests.CornerProof p = MergeLegacyMapTests.ProveCorners(MergeLegacyMapTests.WriteFixture(ComposedMapsFixture.Write()));
        Assert.True(p.MapCornersDrawnWithOtherTransform > 0, p.Report);
        Assert.True(p.BakedCorners > 0 && p.UnbakedCorners > 0, p.Report);
        Assert.True(p.WorstDiffuse < 1e-5, p.Report);
        Assert.True(p.WorstNormalTexels <= 0.25, p.Report);
        Assert.True(p.WorstSpecularTexels <= 0.25, p.Report);
    }
}
