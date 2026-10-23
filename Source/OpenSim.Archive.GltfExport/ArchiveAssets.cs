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
        (int)AssetType.Mesh => ".llmesh",
        _ => ".bin"
    };

    /// <summary>Folder, beside the glTF file, a copy of this asset goes in: textures (and the materials that belong with them), or assets for a mesh.</summary>
    public string Folder => Type == (int)AssetType.Mesh ? "assets" : "textures";
}

/// <summary>A texture decoded to PNG without any change to its pixels.</summary>
internal sealed class TextureImage
{
    public int Width, Height;
    /// <summary>The image has an alpha channel with at least one pixel that is not fully opaque.</summary>
    public bool HasAlpha;
    public byte[] Png;
}

/// <summary>
/// The asset bodies an export needs, collected from the archive in its one pass, and the decoded textures. A body larger
/// than <see cref="SpillBytes"/> is written to the cache folder (when there is one) and read back when a build asks for it,
/// so the memory held does not grow with the archive.
/// </summary>
internal sealed class ArchiveAssets
{
    private sealed class Stored
    {
        public int Type;
        public string Entry;
        public long Size;
        public byte[] Bytes;
        public string Path;
    }

    private readonly Dictionary<UUID, Stored> m_blobs = new Dictionary<UUID, Stored>();
    private readonly Dictionary<UUID, TextureImage> m_decoded = new Dictionary<UUID, TextureImage>();
    private readonly Dictionary<UUID, string> m_decodeFailures = new Dictionary<UUID, string>();
    private readonly Dictionary<UUID, byte[]> m_loaded = new Dictionary<UUID, byte[]>();
    private readonly Queue<UUID> m_loadedOrder = new Queue<UUID>();
    private long m_loadedBytes;
    private long m_bytes;
    private long m_decodedBytes;

    public HashSet<UUID> Wanted { get; } = new HashSet<UUID>();
    /// <summary>Every asset id the archive's pass came across, wanted at that moment or not.</summary>
    public HashSet<UUID> Seen { get; } = new HashSet<UUID>();
    /// <summary>Wanted assets that were not kept because the size limit was reached or they were over the per-asset limit.</summary>
    public Dictionary<UUID, string> OverLimit { get; } = new Dictionary<UUID, string>();
    /// <summary>Status the manifest gives an asset that a cap kept out: over-size-limit (disk) or over-memory-limit.</summary>
    public const string OverDiskStatus = "over-size-limit", OverMemoryStatus = "over-memory-limit";
    /// <summary>Each cap that was reached, one line per kind with the first asset it hit and how many it hit.</summary>
    public List<string> CapEvents { get; } = new List<string>();
    private int m_overDisk, m_overMemory;
    private string m_firstOverDisk, m_firstOverMemory;

    /// <summary>Memory the asset stores aim to stay within; the shares below are taken from it.</summary>
    public long MaxMemoryBytes
    {
        get => m_maxMemory;
        set
        {
            m_maxMemory = value;
            MaxResidentBytes = value * 10 / 100;
            MaxLoadedBytes = value * 15 / 100;
            MaxDecodedBytes = value * 15 / 100;
        }
    }
    private long m_maxMemory = 1024L * 1024 * 1024;
    /// <summary>Most bytes of asset bodies kept, in memory and in the cache folder together; 0 for no limit.</summary>
    public long MaxBytes { get; set; }
    /// <summary>Decoded PNGs kept for reuse by later builds, in bytes.</summary>
    public long MaxDecodedBytes { get; private set; } = 256L * 1024 * 1024;
    /// <summary>Bodies held in memory (the small ones); beyond it a body goes to the cache folder whatever its size.</summary>
    public long MaxResidentBytes { get; private set; } = 150L * 1024 * 1024;
    /// <summary>Folder bodies over <see cref="SpillBytes"/> are written to; null keeps every body in memory.</summary>
    public string CacheDirectory { get; set; }
    public long SpillBytes { get; set; } = 64 * 1024;
    /// <summary>Bodies read back from the cache folder that stay in memory for the next build.</summary>
    public long MaxLoadedBytes { get; private set; } = 200L * 1024 * 1024;
    public long CacheBytesWritten { get; private set; }
    private long m_residentBytes;

    /// <summary>Stored size and asset type of a kept asset, for the estimate of the output size.</summary>
    public bool TryGetInfo(UUID id, out int type, out long size)
    {
        if (m_blobs.TryGetValue(id, out Stored st))
        {
            type = st.Type;
            size = st.Size;
            return true;
        }
        type = 0;
        size = 0;
        return false;
    }

    /// <summary>Total bytes of the asset bodies kept.</summary>
    public long StoredBytes => m_bytes;

    private void NoteOverDisk(UUID id)
    {
        OverLimit[id] = OverDiskStatus;
        if (m_overDisk++ == 0)
            m_firstOverDisk = id.ToString();
    }

    private void NoteOverMemory(UUID id)
    {
        OverLimit[id] = OverMemoryStatus;
        if (m_overMemory++ == 0)
            m_firstOverMemory = id.ToString();
    }

    /// <summary>Called when the archive has been read: turns the counts of cap hits into the lines for the report.</summary>
    public void CloseCapEvents()
    {
        CapEvents.Clear();
        if (m_overDisk > 0)
            CapEvents.Add("DISK CAP REACHED (--max-cache-mb): " + m_overDisk + " asset(s) were not kept and are reported over-size-limit; the first is " + m_firstOverDisk + ". Run again with a larger --max-cache-mb or none.");
        if (m_decodeOutOfMemory > 0)
            CapEvents.Add("MEMORY CAP REACHED (--max-memory-mb): " + m_decodeOutOfMemory + " texture(s) could not be decoded within the cap and are reported undecodable; the first is " + m_firstDecodeOutOfMemory + ". Run again with a larger --max-memory-mb.");
        if (m_overMemory > 0)
            CapEvents.Add("MEMORY CAP REACHED (--max-memory-mb): " + m_overMemory + " asset(s) are too large to handle within the cap and are reported over-memory-limit; the first is " + m_firstOverMemory + ". Run again with a larger --max-memory-mb.");
    }

    /// <summary>Lets go of the decoded images and the bodies read back from the cache folder (they are read or decoded again when needed).</summary>
    public void ReleaseCaches()
    {
        m_decoded.Clear();
        m_decodedBytes = 0;
        m_loaded.Clear();
        m_loadedOrder.Clear();
        m_loadedBytes = 0;
        m_sculptMaps.Clear();
    }

    private int m_decodeOutOfMemory;
    private string m_firstDecodeOutOfMemory;

    /// <summary>Textures that could not be decoded within the memory cap.</summary>
    public int DecodeOutOfMemory => m_decodeOutOfMemory;

    public bool Has(UUID id) => m_blobs.ContainsKey(id);

    private readonly Dictionary<UUID, (SculptMap map, string problem)> m_sculptMaps = new Dictionary<UUID, (SculptMap, string)>();

    /// <summary>A sculpt map's pixels as the archive's texture holds them, or null with the reason.</summary>
    public SculptMap DecodeSculptMap(UUID id, out string problem)
    {
        if (m_sculptMaps.TryGetValue(id, out var cached))
        {
            problem = cached.problem;
            return cached.map;
        }
        AssetBlob blob = Get(id);
        SculptMap map = null;
        problem = blob == null ? "not loaded" : null;
        if (blob != null)
            map = TextureDecoder.DecodeRgb(blob, out problem);
        // maps are small (usually 64 x 64); keep the count of kept maps bounded
        if (m_sculptMaps.Count < 4096)
            m_sculptMaps[id] = (map, problem);
        return map;
    }

    public bool Wants(UUID id, int type, long size)
    {
        Seen.Add(id);
        if (!Wanted.Contains(id) || m_blobs.ContainsKey(id) || OverLimit.ContainsKey(id))
            return false;
        _ = type;
        // a body that alone is more than half the memory cap cannot be held, read back and decoded within it
        if (size > MaxMemoryBytes / 2)
        {
            NoteOverMemory(id);
            return false;
        }
        return true;
    }

    public void Add(UUID id, int type, string entry, byte[] bytes)
    {
        if (bytes == null || (MaxBytes > 0 && m_bytes + bytes.Length > MaxBytes))
        {
            NoteOverDisk(id);
            return;
        }
        if (bytes.Length > MaxMemoryBytes / 2)
        {
            NoteOverMemory(id);
            return;
        }
        m_bytes += bytes.Length;
        Stored stored = new Stored { Type = type, Entry = entry, Size = bytes.Length, Bytes = bytes };
        if (CacheDirectory != null && (bytes.Length > SpillBytes || m_residentBytes + bytes.Length > MaxResidentBytes))
        {
            Directory.CreateDirectory(CacheDirectory);
            stored.Path = Path.Combine(CacheDirectory, id.ToString() + ".bin");
            File.WriteAllBytes(stored.Path, bytes);
            CacheBytesWritten += bytes.Length;
            stored.Bytes = null;
        }
        else
            m_residentBytes += bytes.Length;
        m_blobs[id] = stored;
    }

    public AssetBlob Get(UUID id)
    {
        if (!m_blobs.TryGetValue(id, out Stored st))
            return null;
        byte[] bytes = st.Bytes;
        if (bytes == null && !m_loaded.TryGetValue(id, out bytes))
        {
            bytes = File.ReadAllBytes(st.Path);
            m_loaded[id] = bytes;
            m_loadedOrder.Enqueue(id);
            m_loadedBytes += bytes.Length;
            while (m_loadedBytes > MaxLoadedBytes && m_loadedOrder.Count > 1)
            {
                UUID old = m_loadedOrder.Dequeue();
                if (m_loaded.Remove(old, out byte[] gone))
                    m_loadedBytes -= gone.Length;
            }
        }
        return new AssetBlob { Id = id, Type = st.Type, Entry = st.Entry, Bytes = bytes };
    }

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

        TextureImage image;
        try
        {
            image = TextureDecoder.Decode(blob, out problem);
        }
        catch (OutOfMemoryException)
        {
            // the cap was too small for this image: said loudly in the report, never a silent placeholder
            image = null;
            problem = "out of memory while decoding the image within --max-memory-mb";
            m_decodeOutOfMemory++;
            m_firstDecodeOutOfMemory ??= id.ToString();
        }
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

    /// <summary>A texture's red, green and blue as stored, top row first, for the sculpt rule (which ignores any alpha channel).</summary>
    public static SculptMap DecodeRgb(AssetBlob blob, out string problem)
    {
        problem = null;
        if (blob.Type != (int)AssetType.Texture)
        {
            problem = "the sculpt map is not a JPEG 2000 texture (asset type " + blob.Type + ")";
            return null;
        }
        if (!SkiaImageUtils.TryDecodeFromJ2K(blob.Bytes, out SKBitmap bitmap) || bitmap == null)
        {
            problem = "the JPEG 2000 data could not be decoded";
            return null;
        }
        using (bitmap)
        {
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
                byte[] rgb = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = y * stride + x * 4, d = (y * w + x) * 3;
                        rgb[d] = px[s + (bgra ? 2 : 0)];
                        rgb[d + 1] = px[s + 1];
                        rgb[d + 2] = px[s + (bgra ? 0 : 2)];
                    }
                return new SculptMap { Width = w, Height = h, Rgb = rgb };
            }
            finally
            {
                if (own)
                    source.Dispose();
            }
        }
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
