/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using CoreJ2K.Configuration;
using CoreJ2K.Skia;
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

    /// <summary>An opaque four-component texture written lossy, as the texture tools write them; this library's decoder reads its alpha back as 254.</summary>
    public static byte[] SolidLossy(int width, int height)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.Erase(new SKColor(200, 100, 50, 255));
        byte[] j2k = bmp.EncodeToJ2K(new J2KEncoderConfiguration().WithFileFormat(true));
        Assert.NotEmpty(j2k);
        return j2k;
    }

    /// <summary>A four-component texture whose left half is fully clear and whose right half is fully opaque.</summary>
    public static byte[] HalvesClearAndOpaque(int width, int height)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bmp.SetPixel(x, y, new SKColor(200, 100, 50, x < width / 2 ? (byte)0 : (byte)255));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    /// <summary>Only the start of a codestream: the start-of-codestream marker and a size segment that names the number of components. It cannot be decoded.</summary>
    public static byte[] HeaderWithComponents(int components)
    {
        List<byte> b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        int length = 38 + 3 * components;
        b.Add((byte)(length >> 8)); b.Add((byte)length);
        b.Add(0); b.Add(0);                                                  // capabilities
        foreach (int v in new[] { 64, 64, 0, 0, 64, 64, 0, 0 })               // size, origin, tile size, tile origin
            b.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        b.Add((byte)(components >> 8)); b.Add((byte)components);
        for (int c = 0; c < components; c++)
            b.AddRange(new byte[] { 7, 1, 1 });
        return b.ToArray();
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
