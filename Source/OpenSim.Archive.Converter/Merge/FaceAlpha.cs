/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using SkiaSharp;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// Decides whether a face is drawn alpha-blended, the way the Second Life viewer decides it. This follows the viewer's behaviour, which is
/// the reference (the code below is written from that behaviour, not copied): LLPipeline::getPoolTypeFromTE in pipeline.cpp (1757-1811)
/// chooses the alpha pool, LLFace::canRenderAsMask in llface.cpp (1168-1204) takes a face back out of blending, and the alpha test of a
/// texture is LLImageGL::analyzeAlpha in llimagegl.cpp (2190-2288). A legacy material of alpha mode blend draws its face in the alpha pass
/// in llvovolume.cpp (6763-6771), which is why a blend material blends even an opaque texture here.
/// </summary>
public static class FaceAlpha
{
    /// <summary>The colour alpha under which the viewer treats a face as transparent.</summary>
    private const float ColorAlphaLimit = 0.999f;

    /// <summary>
    /// A face blends when it is in the alpha pool and cannot be drawn as an alpha test. It is in the pool when its colour alpha is below
    /// 0.999 or its texture has an alpha channel (the channel counts whatever the pixels hold); a legacy material with an alpha mode other
    /// than blend takes the texture's part away and leaves only the colour alpha, and a blend material keeps the face in. A face is drawn
    /// as an alpha test instead when its colour alpha is exactly 1, it does not glow and the texture's alpha is mostly clear or opaque.
    /// </summary>
    /// <param name="material">The face's legacy material, or null when it has none.</param>
    public static bool IsAlpha(float colorAlpha, float glow, TextureFacts texture, LegacyMaterialInfo material)
    {
        if ((material?.DiffuseAlphaMode ?? -1) == 1)
            return true;
        if (colorAlpha < ColorAlphaLimit)
            return true;
        bool inPool = texture.HasAlphaChannel && material == null;
        return inPool && !(colorAlpha == 1f && glow == 0f && texture.AutoMask);
    }
}

/// <summary>What the merge reads from a texture: whether it has an alpha channel (two or four components), whether the viewer would draw it as an alpha test, and its size in pixels (0 when the size cannot be read).</summary>
public readonly record struct TextureFacts(bool HasAlphaChannel, bool AutoMask, int Width, int Height);

/// <summary>Reads the components, the size and the alpha test of a texture.</summary>
public static class TextureAlpha
{
    /// <summary>True when the JPEG 2000 texture has two or four components, which the viewer draws through its alpha pool.</summary>
    public static bool HasAlphaChannel(byte[] jpeg2000) => Inspect(jpeg2000).HasAlphaChannel;

    public static TextureFacts Inspect(byte[] jpeg2000)
    {
        if (!J2kHeader.TryRead(jpeg2000, out int components, out int width, out int height))
            return new TextureFacts(false, false, 0, 0);
        bool channel = components == 2 || components == 4;
        return new TextureFacts(channel, channel && AutoMask(jpeg2000), width, height);
    }

    /// <summary>True when the texture decodes and the viewer would take its alpha for a mask.</summary>
    private static bool AutoMask(byte[] jpeg2000)
    {
        if (!SkiaImageUtils.TryDecodeFromJ2K(jpeg2000, out SKBitmap bitmap) || bitmap == null)
            return false;
        using (bitmap)
        {
            using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888 || bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Rgba8888);
            SKBitmap source = rgba ?? bitmap;
            if (source == null)
                return false;
            int width = source.Width, height = source.Height, stride = source.RowBytes;
            ReadOnlySpan<byte> px = source.GetPixelSpan();
            byte[] alpha = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    alpha[y * width + x] = Level(px[y * stride + x * 4 + 3]);
            return AlphaTest.Qualifies(alpha, width, height);
        }
    }

    /// <summary>
    /// The decoder here reads a constant alpha of 255 from a lossy file as 254, where the viewer's decoder reads 255, and the alpha test is
    /// exact about an alpha that is all opaque or all clear. A level within one of either end is taken as that end.
    /// </summary>
    private static byte Level(byte a) => a >= 254 ? (byte)255 : a <= 1 ? (byte)0 : a;
}

/// <summary>
/// The viewer's test for a texture whose alpha can be drawn as a mask. The steps and limits are the viewer's own figures.
/// Reference (behaviour followed, no code copied): LLImageGL::analyzeAlpha, indra/llrender/llimagegl.cpp lines 2190-2288, whose result
/// LLFace::canRenderAsMask (indra/newview/llface.cpp lines 1168-1204) reads through getIsAlphaMask (llimagegl.cpp line 2086).
/// </summary>
public static class AlphaTest
{
    /// <summary>
    /// The alpha values are sorted into 16 steps, counting every pixel and, for each 2 x 2 block, its average four times (which pushes
    /// finely patterned alpha towards the middle). The alpha is not a mask when more than 1/48 of the counts lie in steps 2 to 12, or all
    /// lie in the lower half but not all are clear, or all lie in the upper half but not all are opaque.
    /// </summary>
    /// <param name="alpha">One byte for each pixel, row by row.</param>
    public static bool Qualifies(byte[] alpha, int width, int height)
    {
        if (width <= 0 || height <= 0 || alpha.Length < width * height)
            return false;
        long[] steps = new long[16];
        long sum = 0, count = 0;
        if (width >= 2 && height >= 2)
        {
            for (int y = 0; y + 1 < height; y += 2)
                for (int x = 0; x + 1 < width; x += 2)
                {
                    int a = alpha[y * width + x], b = alpha[y * width + x + 1], c = alpha[(y + 1) * width + x], d = alpha[(y + 1) * width + x + 1];
                    int block = a + b + c + d;
                    steps[a / 16]++;
                    steps[b / 16]++;
                    steps[c / 16]++;
                    steps[d / 16]++;
                    steps[block / 64] += 4;
                    sum += 2L * block;
                    count += 8;
                }
        }
        else
        {
            for (int i = 0; i < width * height; i++)
            {
                steps[alpha[i] / 16]++;
                sum += alpha[i];
                count++;
            }
        }

        long middle = 0, lower = 0, upper = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i >= 2 && i < 13)
                middle += steps[i];
            if (i < 8)
                lower += steps[i];
            else
                upper += steps[i];
        }
        bool notAMask = middle > count / 48
            || (lower == count && sum != 0)
            || (upper == count && sum != 255 * count);
        return !notAMask;
    }
}

/// <summary>The size segment at the start of a JPEG 2000 codestream, which holds the number of components and the picture size.</summary>
internal static class J2kHeader
{
    /// <summary>Reads the first size segment (the codestream may sit inside a JP2 file). Returns false when there is none.</summary>
    public static bool TryRead(byte[] data, out int components, out int width, out int height)
    {
        components = width = height = 0;
        if (data == null)
            return false;
        for (int i = 0; i + 42 <= data.Length; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0x4F || data[i + 2] != 0xFF || data[i + 3] != 0x51)
                continue;
            // after the marker: length (2), capabilities (2), picture size x and y, origin x and y (4 bytes each), tile size and origin, components (2)
            int s = i + 6;
            uint U32(int at) => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
            width = (int)(U32(s + 2) - U32(s + 10));
            height = (int)(U32(s + 6) - U32(s + 14));
            components = (data[s + 34] << 8) | data[s + 35];
            return components > 0 && width > 0 && height > 0;
        }
        return false;
    }
}
