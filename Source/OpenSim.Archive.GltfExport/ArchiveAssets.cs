/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using SkiaSharp;

namespace OpenSim.Archive.GltfExport;

/// <summary>An asset body as the archive holds it.</summary>
internal sealed class AssetBlob
{
    public UUID Id;
    public int Type;
    public string Entry;
    /// <summary>The body unchanged, or null when it was over the size limit.</summary>
    public byte[] Bytes;

    /// <summary>File extension for a copy of this asset, from its type.</summary>
    public string Extension => Type switch
    {
        (int)AssetType.Texture => ".jp2",
        (int)AssetType.TextureTGA => ".tga",
        (int)AssetType.ImageTGA => ".tga",
        (int)AssetType.ImageJPEG => ".jpg",
        (int)AssetType.OSMaterial => ".material.xml",
        (int)AssetType.Material => ".gltf-material.json",
        _ => ".bin"
    };
}

/// <summary>A texture decoded to PNG without any change to its pixels.</summary>
internal sealed class TextureImage
{
    public int Width, Height;
    /// <summary>The image has an alpha channel with at least one pixel that is not fully opaque.</summary>
    public bool HasAlpha;
    public byte[] Png;
}

/// <summary>The asset bodies an export needs, collected from the archive, and the decoded textures.</summary>
internal sealed class ArchiveAssets
{
    private readonly Dictionary<UUID, AssetBlob> m_blobs = new Dictionary<UUID, AssetBlob>();
    private readonly Dictionary<UUID, TextureImage> m_decoded = new Dictionary<UUID, TextureImage>();
    private readonly Dictionary<UUID, string> m_decodeFailures = new Dictionary<UUID, string>();
    private long m_bytes;
    private long m_decodedBytes;

    public HashSet<UUID> Wanted { get; } = new HashSet<UUID>();
    /// <summary>Wanted assets that were not kept because the size limit was reached or they were over the per-asset limit.</summary>
    public HashSet<UUID> OverLimit { get; } = new HashSet<UUID>();
    public long MaxBytes { get; set; } = 2048L * 1024 * 1024;
    /// <summary>Decoded PNGs kept for reuse by later builds, in bytes.</summary>
    public long MaxDecodedBytes { get; set; } = 512L * 1024 * 1024;

    public bool Has(UUID id) => m_blobs.ContainsKey(id);

    public bool Wants(UUID id, int type, long size)
    {
        if (!Wanted.Contains(id) || m_blobs.ContainsKey(id))
            return false;
        _ = type;
        _ = size;
        return true;
    }

    public void Add(UUID id, int type, string entry, byte[] bytes)
    {
        if (bytes == null || m_bytes + bytes.Length > MaxBytes)
        {
            OverLimit.Add(id);
            return;
        }
        m_bytes += bytes.Length;
        m_blobs[id] = new AssetBlob { Id = id, Type = type, Entry = entry, Bytes = bytes };
    }

    public AssetBlob Get(UUID id) => m_blobs.TryGetValue(id, out AssetBlob b) ? b : null;

    /// <summary>The texture as PNG, or null with the reason in <paramref name="problem"/>.</summary>
    public TextureImage Decode(UUID id, out string problem)
    {
        problem = null;
        if (m_decoded.TryGetValue(id, out TextureImage cached))
            return cached;
        if (m_decodeFailures.TryGetValue(id, out problem))
            return null;
        AssetBlob blob = Get(id);
        if (blob == null)
        {
            problem = "not loaded";
            return null;
        }

        TextureImage image = TextureDecoder.Decode(blob, out problem);
        if (image == null)
        {
            m_decodeFailures[id] = problem;
            return null;
        }
        if (m_decodedBytes + image.Png.Length <= MaxDecodedBytes)
        {
            m_decoded[id] = image;
            m_decodedBytes += image.Png.Length;
        }
        return image;
    }
}

internal static class TextureDecoder
{
    public static TextureImage Decode(AssetBlob blob, out string problem)
    {
        problem = null;
        if (blob.Type != (int)AssetType.Texture)
        {
            problem = "texture format " + blob.Extension + " is not decoded";
            return null;
        }
        if (!SkiaImageUtils.TryDecodeFromJ2K(blob.Bytes, out SKBitmap bitmap) || bitmap == null)
        {
            problem = "the JPEG 2000 data could not be decoded";
            return null;
        }
        using (bitmap)
            return FromBitmap(bitmap, out problem);
    }

    /// <summary>Reads the pixels as stored in the bitmap: no scaling, no change of colour.</summary>
    internal static TextureImage FromBitmap(SKBitmap bitmap, out string problem)
    {
        problem = null;
        int w = bitmap.Width, h = bitmap.Height;
        if (w < 1 || h < 1)
        {
            problem = "the image is empty";
            return null;
        }

        SKBitmap source = bitmap;
        bool own = false;
        if (bitmap.ColorType != SKColorType.Rgb888x && bitmap.ColorType != SKColorType.Bgra8888 && bitmap.ColorType != SKColorType.Rgba8888)
        {
            source = bitmap.Copy(SKColorType.Rgba8888);
            own = true;
            if (source == null)
            {
                problem = "the pixel format " + bitmap.ColorType + " is not handled";
                return null;
            }
        }

        try
        {
            ReadOnlySpan<byte> px = source.GetPixelSpan();
            int stride = source.RowBytes;
            bool bgra = source.ColorType == SKColorType.Bgra8888;
            bool hasAlphaChannel = source.ColorType != SKColorType.Rgb888x && source.AlphaType != SKAlphaType.Opaque;
            bool partial = false;
            if (hasAlphaChannel)
                for (int y = 0; y < h && !partial; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * stride + x * 4 + 3] != 255)
                        {
                            partial = true;
                            break;
                        }

            int channels = partial ? 4 : 3;
            byte[] outPixels = new byte[w * h * channels];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * stride + x * 4, d = (y * w + x) * channels;
                    outPixels[d] = px[s + (bgra ? 2 : 0)];
                    outPixels[d + 1] = px[s + 1];
                    outPixels[d + 2] = px[s + (bgra ? 0 : 2)];
                    if (partial)
                        outPixels[d + 3] = px[s + 3];
                }

            return new TextureImage { Width = w, Height = h, HasAlpha = partial, Png = PngWriter.Write(w, h, partial, outPixels) };
        }
        finally
        {
            if (own)
                source.Dispose();
        }
    }
}
