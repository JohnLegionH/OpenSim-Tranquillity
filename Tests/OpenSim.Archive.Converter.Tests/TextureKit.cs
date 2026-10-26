/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>JPEG 2000 textures for tests, with the number of components each one is written with.</summary>
internal static class TextureKit
{
    /// <summary>A texture of one colour. An opaque bitmap is written with three components, a bitmap with an alpha type with four.</summary>
    public static byte[] Solid(int width, int height, bool withAlphaChannel, byte alpha = 255, bool oneClearPixel = false)
    {
        using SKBitmap bmp = withAlphaChannel
            ? new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
            : new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(200, 100, 50, withAlphaChannel ? alpha : (byte)255));
        if (withAlphaChannel && oneClearPixel)
            bmp.SetPixel(1, 1, new SKColor(200, 100, 50, 0));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    /// <summary>The number of components in the SIZ marker segment of a JPEG 2000 codestream (or of the codestream in a JP2 file); 0 when there is none.</summary>
    public static int Components(byte[] j2k)
    {
        for (int i = 0; i + 42 < j2k.Length; i++)
            if (j2k[i] == 0xFF && j2k[i + 1] == 0x4F && j2k[i + 2] == 0xFF && j2k[i + 3] == 0x51)
                return (j2k[i + 40] << 8) | j2k[i + 41];
        return 0;
    }
}
