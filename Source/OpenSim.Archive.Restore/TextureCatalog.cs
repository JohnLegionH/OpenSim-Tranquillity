/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;
using OpenSim.Archive.Converter;
using OpenSim.Framework;
using SkiaSharp;

namespace OpenSim.Archive.Restore;

/// <summary>
/// Decides what asset a picture on an added part is. The pictures of the original archive's textures are the PNG files the
/// comparison export wrote (one per texture asset, named by the asset id, pixels unchanged); a picture whose pixels are the same
/// as one of them uses that asset. Any other picture becomes a new texture asset (JPEG 2000, lossless) whose id is made from its bytes.
/// </summary>
internal sealed class TextureCatalog
{
    private readonly string m_folder;
    private readonly Dictionary<(int, int), List<string>> m_byDimensions = new Dictionary<(int, int), List<string>>();
    private readonly Dictionary<string, Guid> m_hashToId = new Dictionary<string, Guid>(StringComparer.Ordinal);
    private readonly Dictionary<string, (Guid id, byte[] data)> m_newByHash = new Dictionary<string, (Guid, byte[])>(StringComparer.Ordinal);
    private readonly HashSet<string> m_hashed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool m_indexed;

    public TextureCatalog(string folderWithPngs)
    {
        m_folder = folderWithPngs;
    }

    /// <summary>The texture assets this run made, by id.</summary>
    public IEnumerable<KeyValuePair<Guid, byte[]>> NewAssets => m_newByHash.Values.Select(v => new KeyValuePair<Guid, byte[]>(v.id, v.data));

    /// <summary>The asset for a picture (PNG or JPEG bytes): an existing one with the same pixels, or a new one. Null with a reason when the picture cannot be read.</summary>
    public Guid? Resolve(byte[] picture, out bool isNew, out string problem)
    {
        isNew = false;
        problem = null;
        if (!SkiaImageUtils.TryDecodeFromBytes(picture, out SKBitmap bitmap) || bitmap == null)
        {
            problem = "the picture cannot be decoded";
            return null;
        }
        using (bitmap)
        {
            string hash = PixelHash(bitmap);
            Index();
            if (m_hashToId.TryGetValue(hash, out Guid known))
                return known;
            if (FindSameSize(bitmap.Width, bitmap.Height, hash, out Guid found))
                return found;
            if (m_newByHash.TryGetValue(hash, out var made))
                return made.id;
            if (!SkiaImageUtils.TryEncodeToJ2KLossless(bitmap, out byte[] j2k) || j2k == null || j2k.Length == 0)
            {
                problem = "the picture cannot be stored as a texture asset";
                return null;
            }
            // the same bytes always give the same id, so a rerun writes the same archive
            Guid id = MeshId.FromBytes(j2k);
            m_newByHash[hash] = (id, j2k);
            isNew = true;
            return id;
        }
    }

    private void Index()
    {
        if (m_indexed)
            return;
        m_indexed = true;
        string textures = Path.Combine(m_folder, "textures");
        if (!Directory.Exists(textures))
            return;
        foreach (string png in Directory.EnumerateFiles(textures, "*.png"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(png), out _))
                continue;
            if (!TryHeader(png, out int w, out int h))
                continue;
            if (!m_byDimensions.TryGetValue((w, h), out List<string> list))
                m_byDimensions[(w, h)] = list = new List<string>();
            list.Add(png);
        }
    }

    private bool FindSameSize(int w, int h, string hash, out Guid id)
    {
        id = Guid.Empty;
        if (!m_byDimensions.TryGetValue((w, h), out List<string> list))
            return false;
        foreach (string png in list)
        {
            if (!m_hashed.Add(png))
                continue;
            if (!SkiaImageUtils.TryDecodeFromBytes(File.ReadAllBytes(png), out SKBitmap existing) || existing == null)
                continue;
            using (existing)
                m_hashToId.TryAdd(PixelHash(existing), Guid.Parse(Path.GetFileNameWithoutExtension(png)));
            if (m_hashToId.TryGetValue(hash, out id))
                return true;
        }
        return false;
    }

    private static string PixelHash(SKBitmap bitmap)
    {
        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType == SKAlphaType.Unpremul ? bitmap.Copy() : bitmap.Copy(SKColorType.Rgba8888);
        byte[] pixels = rgba.Bytes;
        return rgba.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + rgba.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(ManagedDigest.Sha256(pixels));
    }

    private static bool TryHeader(string png, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using FileStream fs = File.OpenRead(png);
            byte[] head = new byte[24];
            if (fs.Read(head, 0, 24) < 24 || head[0] != 0x89 || head[1] != (byte)'P')
                return false;
            width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return width > 0 && height > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
