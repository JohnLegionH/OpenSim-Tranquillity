/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Converter.Fix;

/// <summary>
/// Copies an OAR or IAR to a new gzip-compressed tar, entry by entry in the original order, handing the chosen file entries to a function
/// that may return new bytes. The same input and the same function always give the same output bytes. The source is only read, the
/// output must not exist, and it takes its name only when it is complete.
/// </summary>
internal static class TarRewrite
{
    /// <param name="change">Called for each file entry with the entry header and its bytes; returns the new bytes, or null to copy the entry as it is.</param>
    public static void Run(string sourcePath, string destPath, Func<TarEntryHeader, byte[], byte[]> change)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the output is the source archive; the source is never overwritten");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("the archive '" + sourcePath + "' does not exist");
        if (File.Exists(destPath) || Directory.Exists(destPath))
            throw new IOException("the output '" + destPath + "' already exists; it is never overwritten");

        // The archive is built under a temporary name beside the output; a failed run leaves "<output>.<id>.incomplete", never a file that looks like a result.
        string temporary = destPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            using (FileStream dest = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(sourcePath, dest, change);
            File.Move(temporary, destPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Move(temporary, Path.ChangeExtension(temporary, ".incomplete"));
            throw;
        }
    }

    private static Stream Decompressed(Stream raw)
    {
        // A gzip file starts 1f 8b; anything else is taken to be a plain tar.
        BufferedStream buffered = new BufferedStream(raw, 1 << 16);
        int b0 = buffered.ReadByte();
        int b1 = buffered.ReadByte();
        if (b0 < 0)
            throw new InvalidDataException("the source archive is empty");
        raw.Seek(0, SeekOrigin.Begin);
        return b0 == 0x1f && b1 == 0x8b ? new GZipStream(new BufferedStream(raw, 1 << 16), CompressionMode.Decompress) : raw;
    }

    private static void Write(string sourcePath, Stream dest, Func<TarEntryHeader, byte[], byte[]> change)
    {
        long memberStart = dest.CanSeek ? dest.Position : 0;
        using Stream source = File.OpenRead(sourcePath);
        TarStreamReader tar = new TarStreamReader(Decompressed(source));

        using (GZipStream gz = new GZipStream(dest, CompressionLevel.Optimal, leaveOpen: true))
        using (TarWriter writer = new TarWriter(gz, leaveOpen: true))
        {
            TarEntryHeader h;
            while ((h = tar.ReadNext()) != null)
            {
                if (h.IsDirectory)
                {
                    writer.WriteDirectory(h.Name);
                    continue;
                }
                byte[] body = tar.ReadBody();
                byte[] changed = h.IsFile ? change(h, body) : null;
                if (changed != null)
                    writer.WriteFile(h.Name, h.TypeFlag, changed);
                else
                    writer.WriteEntry(h.Name, h.TypeFlag == '\0' ? '0' : h.TypeFlag, h.Size, new MemoryStream(body, false));
            }
        }
        GzipHeader.FixOsField(dest, memberStart);
    }
}
