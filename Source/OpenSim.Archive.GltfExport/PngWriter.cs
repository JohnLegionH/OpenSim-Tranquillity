/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// Writes 8-bit RGB or RGBA pixels as a PNG exactly as given: no resampling, no colour conversion, no gamma chunk.
/// Written to the PNG specification (W3C, https://www.w3.org/TR/png-3/).
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = MakeTable();

    private static uint[] MakeTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <param name="pixels">Row by row, top row first, 3 bytes (red green blue) or 4 bytes (and alpha, not premultiplied) per pixel.</param>
    public static byte[] Write(int width, int height, bool alpha, byte[] pixels)
    {
        int channels = alpha ? 4 : 3;
        if (width < 1 || height < 1 || pixels.Length != width * height * channels)
            throw new ArgumentException("pixel data does not match the image size");

        using MemoryStream raw = new MemoryStream(height * (1 + width * channels));
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter type: none
            raw.Write(pixels, y * width * channels, width * channels);
        }
        byte[] compressed;
        using (MemoryStream z = new MemoryStream())
        {
            using (ZLibStream zs = new ZLibStream(z, CompressionLevel.Optimal, true))
                zs.Write(raw.GetBuffer(), 0, (int)raw.Length);
            compressed = z.ToArray();
        }

        using MemoryStream png = new MemoryStream(compressed.Length + 128);
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        byte[] ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)width);
        WriteBe(ihdr, 4, (uint)height);
        ihdr[8] = 8;                      // bit depth
        ihdr[9] = (byte)(alpha ? 6 : 2);  // colour type: RGBA or RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] t = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        byte[] len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len);
        s.Write(t);
        s.Write(data);
        byte[] crc = new byte[4];
        WriteBe(crc, 0, Crc(t, data));
        s.Write(crc);
    }

    private static void WriteBe(byte[] into, int at, uint v)
    {
        into[at] = (byte)(v >> 24);
        into[at + 1] = (byte)(v >> 16);
        into[at + 2] = (byte)(v >> 8);
        into[at + 3] = (byte)v;
    }
}
