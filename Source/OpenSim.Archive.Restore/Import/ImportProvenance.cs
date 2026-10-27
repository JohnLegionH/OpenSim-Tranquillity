/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text;

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// Where an imported archive came from: the tool and its release, the manifest, each part's source folder or .glb node, and the SHA-256 of every input file the
/// import read. The same text goes into the archive (as the notes file) and into the run report. Paths are written relative to the manifest's folder (or to the
/// model folder of a single-part import) with forward slashes, and the text uses line feeds only, so the same inputs give the same bytes wherever they are kept
/// and whatever platform ran the import.
/// </summary>
internal sealed class ImportProvenance
{
    /// <summary>The archive entry that holds the text. The region ignores entries it does not know, so the archive loads as before.</summary>
    public const string ArchiveEntry = "oar-import-notes.txt";

    private readonly StringBuilder m_text = new StringBuilder();

    public ImportProvenance(string toolVersion)
    {
        if (!string.IsNullOrEmpty(toolVersion))
            m_text.Append("oar-import ").Append(toolVersion).Append('\n');
    }

    /// <summary>The manifest file, with its hash. A manifest that was parsed from text has no file and no line.</summary>
    public void Manifest(string file, string baseFolder, string sha256)
    {
        if (file != null)
            m_text.Append("manifest: ").Append(Relative(baseFolder, file)).Append(" sha256 ").Append(sha256).Append('\n');
    }

    /// <summary>The model folder of a single-part import. Only the folder's own name is written.</summary>
    public void SourceFolder(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        m_text.Append("source folder: ").Append(Path.GetFileName(full)).Append('\n');
    }

    /// <summary>The heading of one part of a linked object; <paramref name="source"/> says where its mesh comes from.</summary>
    public void Part(int linkNumber, string name, string source) =>
        m_text.Append("part ").Append(linkNumber).Append(" '").Append(name).Append("': ").Append(source).Append('\n');

    /// <summary>One input file and its hash, under the heading before it.</summary>
    public void File(string path, string baseFolder) =>
        m_text.Append("  ").Append(Relative(baseFolder, path)).Append(" sha256 ").Append(Sha256(path)).Append('\n');

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))).ToLowerInvariant();

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The path from the base folder, with forward slashes; a path outside the base folder is written in full.</summary>
    public static string Relative(string baseFolder, string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(Path.GetFullPath(baseFolder), full).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            relative = full.Replace('\\', '/');
        return relative;
    }

    public override string ToString() => m_text.ToString();

    public byte[] ToBytes() => new UTF8Encoding(false).GetBytes(m_text.ToString());
}
