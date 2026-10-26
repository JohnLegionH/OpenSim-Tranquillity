/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using SkiaSharp;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Decides whether a face is drawn alpha-blended.</summary>
public static class FaceAlpha
{
    /// <summary>
    /// A face blends when its colour alpha is below 1, or its legacy material blends, or its texture has an alpha channel that is not
    /// fully opaque and the face's legacy material does not say otherwise (alpha mode none or mask).
    /// </summary>
    /// <param name="material">The face's legacy material, or null when it has none.</param>
    public static bool IsAlpha(float colorAlpha, bool textureTranslucent, LegacyMaterialInfo material)
    {
        if (colorAlpha < 0.999f)
            return true;
        int mode = material?.DiffuseAlphaMode ?? -1;
        if (mode == 1)
            return true;
        return textureTranslucent && mode != 0 && mode != 2;
    }
}

/// <summary>What the merge reads from a texture: whether it has a pixel that is not fully opaque, and its size in pixels (0 when it cannot be decoded).</summary>
public readonly record struct TextureFacts(bool Translucent, int Width, int Height);

/// <summary>Reads the alpha channel and the size of a texture.</summary>
public static class TextureAlpha
{
    /// <summary>True when the JPEG 2000 texture decodes and has at least one pixel that is not fully opaque; false when it is opaque or cannot be decoded.</summary>
    public static bool IsTranslucent(byte[] jpeg2000) => Inspect(jpeg2000).Translucent;

    public static TextureFacts Inspect(byte[] jpeg2000)
    {
        if (!SkiaImageUtils.TryDecodeFromJ2K(jpeg2000, out SKBitmap bitmap) || bitmap == null)
            return new TextureFacts(false, 0, 0);
        using (bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            if (bitmap.AlphaType == SKAlphaType.Opaque || bitmap.ColorType == SKColorType.Rgb888x)
                return new TextureFacts(false, width, height);
            using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888 || bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Rgba8888);
            SKBitmap source = rgba ?? bitmap;
            if (source == null)
                return new TextureFacts(false, width, height);
            ReadOnlySpan<byte> px = source.GetPixelSpan();
            int stride = source.RowBytes;
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    if (px[y * stride + x * 4 + 3] != 255)
                        return new TextureFacts(true, width, height);
            return new TextureFacts(false, width, height);
        }
    }
}
