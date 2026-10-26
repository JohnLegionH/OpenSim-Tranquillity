/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>Which faces count as alpha-blended. The textures are built in memory; the class shares no state and runs in parallel.</summary>
public class FaceAlphaTests
{
    private static byte[] Texture(byte alpha, bool oneTransparentPixel = false)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                bmp.SetPixel(x, y, new SKColor(200, 100, 50, oneTransparentPixel && x == 3 && y == 4 ? (byte)0 : alpha));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    private static LegacyMaterialInfo Mode(int mode) => new LegacyMaterialInfo { DiffuseAlphaMode = mode };

    [Fact]
    public void ATextureWithATransparentPixelIsTranslucent() => Assert.True(TextureAlpha.IsTranslucent(Texture(255, oneTransparentPixel: true)));

    [Fact]
    public void AFullyOpaqueTextureIsNot() => Assert.False(TextureAlpha.IsTranslucent(Texture(255)));

    [Fact]
    public void AHalfTransparentTextureIsTranslucent() => Assert.True(TextureAlpha.IsTranslucent(Texture(128)));

    [Fact]
    public void BytesThatAreNotATextureAreNotTranslucent()
    {
        Assert.False(TextureAlpha.IsTranslucent(null));
        Assert.False(TextureAlpha.IsTranslucent(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void AColourAlphaBelowOneBlendsWhateverTheMaterialSays()
    {
        Assert.True(FaceAlpha.IsAlpha(0.5f, false, null));
        Assert.True(FaceAlpha.IsAlpha(0.5f, false, Mode(0)));
        Assert.True(FaceAlpha.IsAlpha(0.5f, false, Mode(2)));
    }

    [Fact]
    public void ATranslucentTextureBlendsWithNoMaterialOrABlendOrEmissiveMaterial()
    {
        Assert.True(FaceAlpha.IsAlpha(1f, true, null));
        Assert.True(FaceAlpha.IsAlpha(1f, true, Mode(1)));
        Assert.True(FaceAlpha.IsAlpha(1f, true, Mode(3)));
    }

    [Fact]
    public void ATranslucentTextureDoesNotBlendWhenTheMaterialsAlphaModeIsNoneOrMask()
    {
        Assert.False(FaceAlpha.IsAlpha(1f, true, Mode(0)));
        Assert.False(FaceAlpha.IsAlpha(1f, true, Mode(2)));
    }

    [Fact]
    public void AnOpaqueTextureBlendsOnlyWhenTheMaterialBlends()
    {
        Assert.False(FaceAlpha.IsAlpha(1f, false, null));
        Assert.False(FaceAlpha.IsAlpha(1f, false, Mode(0)));
        Assert.True(FaceAlpha.IsAlpha(1f, false, Mode(1)));
    }
}
