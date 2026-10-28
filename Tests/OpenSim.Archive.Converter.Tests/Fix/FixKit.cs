/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Converter.Tests.Fix;

/// <summary>Reading archives back for the tests of the archive-writing modes of oar-convert.</summary>
internal static class FixKit
{
    /// <summary>Every file entry of a gzip tar (or plain tar), in file order.</summary>
    public static List<(string name, byte[] data)> Entries(byte[] archive)
    {
        Stream raw = new MemoryStream(archive);
        if (archive.Length > 2 && archive[0] == 0x1f && archive[1] == 0x8b)
            raw = new GZipStream(raw, CompressionMode.Decompress);
        TarStreamReader tar = new TarStreamReader(raw);
        List<(string, byte[])> list = new List<(string, byte[])>();
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            if (h.IsFile)
                list.Add((h.Name, tar.ReadBody()));
        }
        return list;
    }

    public static List<(string name, byte[] data)> Entries(string path) => Entries(File.ReadAllBytes(path));

    /// <summary>The text of the one entry whose name starts with the prefix.</summary>
    public static string Text(List<(string name, byte[] data)> entries, string prefix) =>
        Encoding.UTF8.GetString(Assert.Single(entries, e => e.name.StartsWith(prefix, StringComparison.Ordinal)).data);

    public static string WriteInput(string folder, string name, byte[] archive)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, archive);
        return path;
    }
}
