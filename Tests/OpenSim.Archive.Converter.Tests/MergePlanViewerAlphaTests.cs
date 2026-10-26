/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The planner marks the faces the viewer draws alpha-blended (see FaceAlphaTests for the viewer's rule), from textures a face shows in an
/// archive. Three boxes of six faces each show one texture, so 18 faces are alpha or none are. Each test writes its own archive and
/// shares nothing, so the class runs in parallel with others.
/// </summary>
public class MergePlanViewerAlphaTests
{
    private static MergePlan Plan(byte[] texture, int? alphaMode = null, float glow = 0f, float colorAlpha = 1f)
    {
        Guid id = Guid.NewGuid(), material = Guid.NewGuid();
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(id));
        te.DefaultTexture.Glow = glow;
        te.DefaultTexture.RGBA = new Color4(1f, 1f, 1f, colorAlpha);
        if (alphaMode.HasValue)
            te.DefaultTexture.MaterialID = new UUID(material);
        string teBase64 = Convert.ToBase64String(te.GetBytes());
        List<KitPart> parts = Enumerable.Range(1, 3).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1", TextureEntry = teBase64 }).ToList();
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]> { ["assets/" + id + "_texture.jp2"] = texture };
        if (alphaMode.HasValue)
            assets["assets/" + material + "_material.xml"] = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><llsd><map><key>DiffuseAlphaMode</key><integer>" + alphaMode.Value + "</integer></map></llsd>");
        return ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets), new MergeOptions());
    }

    [Fact]
    public void AnAllOpaqueFourChannelTextureMakesNoFaceAlphaAsTheViewerDrawsItMasked()
    {
        Assert.Equal(0, Plan(TextureKit.Solid(16, 16, withAlphaChannel: true, alpha: 255)).AlphaFaces);
    }

    [Fact]
    public void AnAllOpaqueFourChannelTextureWrittenLossyMakesNoFaceAlphaEither()
    {
        // the case of the 90 textures of the shop: a constant alpha of 255 that this library's decoder reads as 254
        byte[] j2k = TextureKit.SolidLossy(64, 64);
        Assert.True(SkiaImageUtilsDecodesBelow255(j2k), "the premise of this test: the decoder reads the opaque alpha of a lossy file below 255");
        MergePlan plan = Plan(j2k);
        Assert.Equal(0, plan.AlphaFaces);
        Assert.False(Assert.Single(plan.Linksets[0].Merged).Alpha);
    }

    private static bool SkiaImageUtilsDecodesBelow255(byte[] j2k)
    {
        Assert.True(OpenSim.Framework.SkiaImageUtils.TryDecodeFromJ2K(j2k, out SkiaSharp.SKBitmap bmp));
        using (bmp)
            return bmp.GetPixel(0, 0).Alpha < 255;
    }

    [Fact]
    public void AFourChannelTextureWithRealTransparencyMakesEveryFaceAlpha()
    {
        MergePlan plan = Plan(TextureKit.Solid(16, 16, withAlphaChannel: true, alpha: 128));
        Assert.Equal(18, plan.AlphaFaces);
        Assert.True(Assert.Single(plan.Linksets[0].Merged).Alpha);
    }

    [Fact]
    public void AOneBitMaskTextureIsDrawnAsAnAlphaTestSoItMakesNoFaceAlphaUnlessTheFaceGlows()
    {
        byte[] mask = TextureKit.HalvesClearAndOpaque(16, 16);
        Assert.Equal(0, Plan(mask).AlphaFaces);
        Assert.Equal(18, Plan(mask, glow: 0.5f).AlphaFaces);
    }

    [Fact]
    public void AColourAlphaBelowOneMakesTheFacesAlphaWhateverTheTexture()
    {
        Assert.Equal(18, Plan(TextureKit.Solid(16, 16, withAlphaChannel: false), colorAlpha: 0.5f).AlphaFaces);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void ALegacyMaterialWithAlphaModeNoneMaskOrEmissiveTakesTheTexturesAlphaAway(int mode)
    {
        Assert.Equal(0, Plan(TextureKit.Solid(16, 16, withAlphaChannel: true, alpha: 128), mode).AlphaFaces);
    }

    [Fact]
    public void ALegacyMaterialWithAlphaModeBlendMakesTheFacesAlphaEvenOnAnOpaqueTexture()
    {
        Assert.Equal(18, Plan(TextureKit.Solid(16, 16, withAlphaChannel: false), alphaMode: 1).AlphaFaces);
        Assert.Equal(18, Plan(TextureKit.HalvesClearAndOpaque(16, 16), alphaMode: 1).AlphaFaces);
    }
}
