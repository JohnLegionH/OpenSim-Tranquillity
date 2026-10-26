/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Which faces count as alpha-blended, as the viewer decides it: LLPipeline::getPoolTypeFromTE (pipeline.cpp 1757-1811) puts a face in the alpha
/// pool when its colour alpha is below 0.999 or its texture has two or four components (whatever the pixels hold), a legacy material of
/// alpha mode blend keeps it there and any other mode takes the texture's part away; LLFace::canRenderAsMask (llface.cpp 1168-1204) then draws
/// a face of the alpha pool as an alpha test, not a blend, when its colour alpha is exactly 1, it does not glow, its material is not blend and
/// LLImageGL::analyzeAlpha (llimagegl.cpp 2190-2288) finds the texture's alpha is mostly 0 or 1. The textures are built in memory; the class
/// shares no state and runs in parallel.
/// </summary>
public class FaceAlphaTests
{
    private static LegacyMaterialInfo Mode(int mode) => new LegacyMaterialInfo { DiffuseAlphaMode = mode };

    private static TextureFacts Channel(bool mask) => new TextureFacts(true, mask, 64, 64);
    private static readonly TextureFacts NoChannel = new TextureFacts(false, false, 64, 64);

    // ---------------------------------------------------------------- the textures

    [Fact]
    public void AnOpaqueFourChannelTextureHasAnAlphaChannelAndQualifiesForMasking()
    {
        TextureFacts f = TextureAlpha.Inspect(TextureKit.Solid(16, 16, withAlphaChannel: true, alpha: 255));
        Assert.True(f.HasAlphaChannel);
        Assert.True(f.AutoMask);
        Assert.Equal((16, 16), (f.Width, f.Height));
    }

    [Fact]
    public void AnOpaqueFourChannelTextureEncodedLossyStillQualifiesWhateverTheDecoderMakesOfItsAlpha()
    {
        // the decoder here reads the constant alpha 255 of a lossy codestream back as 254; the viewer's decoder reads 255
        byte[] j2k = TextureKit.SolidLossy(64, 64);
        Assert.Equal(4, TextureKit.Components(j2k));
        TextureFacts f = TextureAlpha.Inspect(j2k);
        Assert.True(f.HasAlphaChannel);
        Assert.True(f.AutoMask);
    }

    [Fact]
    public void AThreeChannelTextureHasNoAlphaChannel()
    {
        byte[] j2k = TextureKit.Solid(16, 16, withAlphaChannel: false);
        Assert.Equal(3, TextureKit.Components(j2k));
        TextureFacts f = TextureAlpha.Inspect(j2k);
        Assert.False(f.HasAlphaChannel);
        Assert.False(f.AutoMask);
        Assert.Equal((16, 16), (f.Width, f.Height));
    }

    [Fact]
    public void ATextureWhoseAlphaIsHalfEverywhereHasAChannelAndDoesNotQualifyForMasking()
    {
        TextureFacts f = TextureAlpha.Inspect(TextureKit.Solid(16, 16, withAlphaChannel: true, alpha: 128));
        Assert.True(f.HasAlphaChannel);
        Assert.False(f.AutoMask);
    }

    [Fact]
    public void ATextureWhoseAlphaIsClearOnOneHalfAndOpaqueOnTheOtherQualifiesForMasking()
    {
        TextureFacts f = TextureAlpha.Inspect(TextureKit.HalvesClearAndOpaque(16, 16));
        Assert.True(f.HasAlphaChannel);
        Assert.True(f.AutoMask);
    }

    [Fact]
    public void ATextureWithOnlyAFewTransparentPixelsInAnOpaqueFieldDoesNotQualify()
    {
        // the viewer counts a 2 x 2 average as well as the pixels, which puts a lone clear pixel among opaque ones in the middle of the range
        TextureFacts f = TextureAlpha.Inspect(TextureKit.Solid(8, 8, withAlphaChannel: true, oneClearPixel: true));
        Assert.True(f.HasAlphaChannel);
        Assert.False(f.AutoMask);
    }

    [Fact]
    public void ATwoComponentTextureHasAnAlphaChannel()
    {
        Assert.True(TextureAlpha.HasAlphaChannel(TextureKit.HeaderWithComponents(2)));
        Assert.False(TextureAlpha.HasAlphaChannel(TextureKit.HeaderWithComponents(1)));
        Assert.False(TextureAlpha.HasAlphaChannel(TextureKit.HeaderWithComponents(3)));
    }

    [Fact]
    public void BytesThatAreNotATextureHaveNoAlphaChannel()
    {
        Assert.False(TextureAlpha.HasAlphaChannel(null));
        Assert.False(TextureAlpha.HasAlphaChannel(new byte[] { 1, 2, 3 }));
        Assert.Equal(new TextureFacts(false, false, 0, 0), TextureAlpha.Inspect(new byte[] { 1, 2, 3 }));
    }

    // ---------------------------------------------------------------- the faces

    [Fact]
    public void AColourAlphaBelowThePointNineNineNineBlendsWhateverTheMaterialSays()
    {
        Assert.True(FaceAlpha.IsAlpha(0.5f, 0f, NoChannel, null));
        Assert.True(FaceAlpha.IsAlpha(0.5f, 0f, NoChannel, Mode(0)));
        Assert.True(FaceAlpha.IsAlpha(0.5f, 0f, NoChannel, Mode(2)));
        Assert.True(FaceAlpha.IsAlpha(0.5f, 0f, Channel(mask: true), null));
        Assert.True(FaceAlpha.IsAlpha(0.998f, 0f, NoChannel, null));
    }

    [Fact]
    public void ATextureWithAnAlphaChannelThatIsNotAMaskBlendsWithNoMaterialOrABlendMaterial()
    {
        Assert.True(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: false), null));
        Assert.True(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: false), Mode(1)));
    }

    [Fact]
    public void AMaterialWithAlphaModeNoneMaskOrEmissiveTakesTheTexturesAlphaAway()
    {
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: false), Mode(0)));
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: false), Mode(2)));
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: false), Mode(3)));
    }

    [Fact]
    public void ATextureThatQualifiesForMaskingIsDrawnAsAnAlphaTestAndDoesNotBlend()
    {
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: true), null));
    }

    [Fact]
    public void AMaskedTextureBlendsAgainWhenTheFaceGlowsOrItsMaterialBlendsOrItsColourIsNotExactlyOpaque()
    {
        Assert.True(FaceAlpha.IsAlpha(1f, 0.2f, Channel(mask: true), null));
        Assert.True(FaceAlpha.IsAlpha(1f, 0f, Channel(mask: true), Mode(1)));
        Assert.True(FaceAlpha.IsAlpha(0.9995f, 0f, Channel(mask: true), null));
    }

    [Fact]
    public void AnOpaqueTextureBlendsOnlyWhenTheMaterialBlends()
    {
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, NoChannel, null));
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, NoChannel, Mode(0)));
        Assert.False(FaceAlpha.IsAlpha(1f, 0f, NoChannel, Mode(3)));
        Assert.True(FaceAlpha.IsAlpha(1f, 0f, NoChannel, Mode(1)));
    }
}
