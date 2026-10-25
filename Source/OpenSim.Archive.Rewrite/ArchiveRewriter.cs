/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Rewrite;

/// <summary>What one rewrite did. Written beside the output as JSON by <see cref="ToJson"/>.</summary>
public sealed class RewriteSummary
{
    /// <summary>Entries read from the source archive.</summary>
    public long SourceEntries { get; set; }
    /// <summary>Source entries written unchanged.</summary>
    public long EntriesCopied { get; set; }
    /// <summary>Object files that had at least one part patched.</summary>
    public long ObjectFilesPatched { get; set; }
    /// <summary>Parts patched.</summary>
    public long ObjectsPatched { get; set; }
    public long AssetsAdded { get; set; }
    /// <summary>New parts inserted into object files.</summary>
    public long PartsAdded { get; set; }
    /// <summary>Parts cut out of object files.</summary>
    public long PartsRemoved { get; set; }
    /// <summary>Entries in the output (copied, patched and added).</summary>
    public long EntriesWritten { get; set; }
    public bool ControlFileFirst { get; set; }
    public List<string> PatchedEntries { get; set; } = new List<string>();
    public List<string> AddedAssets { get; set; } = new List<string>();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Copies an OAR to a new OAR, entry by entry in the original order. An entry is written byte for byte unless a
/// registered part patch names a part in it (the object file is then edited in place, see <see cref="ObjectXml"/>).
/// New assets are appended after the last source entry. The source is only read.
/// </summary>
public sealed class ArchiveRewriter
{
    private readonly Dictionary<Guid, PartPatch> m_patches = new Dictionary<Guid, PartPatch>();
    private readonly List<PartInsertion> m_insertions = new List<PartInsertion>();
    private readonly HashSet<Guid> m_removals = new HashSet<Guid>();
    private readonly List<(string name, byte[] data)> m_assets = new List<(string, byte[])>();

    /// <summary>Registers the edits for one part. A part can be patched once per rewrite.</summary>
    public void AddPatch(PartPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!m_patches.TryAdd(patch.PartId, patch))
            throw new InvalidOperationException("part " + patch.PartId + " already has a patch in this rewrite");
    }

    /// <summary>Registers a new part to be added to the group of an existing part.</summary>
    public void AddPart(PartInsertion insertion)
    {
        ArgumentNullException.ThrowIfNull(insertion);
        m_insertions.Add(insertion);
    }

    /// <summary>Registers a part to be cut out of its object file together with its Part element. The root part of a group cannot be removed.</summary>
    public void RemovePart(Guid partId) => m_removals.Add(partId);

    /// <summary>Registers a new asset, written as assets/&lt;uuid&gt;&lt;extension of the type&gt;.</summary>
    public void AddAsset(Guid id, AssetType type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!ArchiveConstants.ASSET_TYPE_TO_EXTENSION.TryGetValue((sbyte)type, out string extension))
            throw new ArgumentException("the archive format has no file extension for asset type " + type, nameof(type));
        string name = ArchiveConstants.ASSETS_PATH + id.ToString("D") + extension;
        foreach ((string existing, byte[] _) in m_assets)
            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("asset " + name + " is added twice");
        m_assets.Add((name, data));
    }

    /// <summary>Rewrites <paramref name="sourcePath"/> to <paramref name="destPath"/> (a gzip-compressed tar).</summary>
    public RewriteSummary Rewrite(string sourcePath, string destPath)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the output path is the source archive; the source is never overwritten");

        // Everything that can be refused is checked before the output file is created.
        PlanResult plan = Plan(() => File.OpenRead(sourcePath));

        using FileStream dest = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return Write(() => File.OpenRead(sourcePath), dest, plan);
    }

    /// <summary>Rewrites a source stream (gzip tar or plain tar) into a gzip-compressed tar on <paramref name="dest"/>.</summary>
    /// <param name="openSource">Opens the source; called once or twice, each time for a fresh read from the start.</param>
    public RewriteSummary Rewrite(Func<Stream> openSource, Stream dest) => Write(openSource, dest, Plan(openSource));

    private sealed class PlanResult
    {
        public Dictionary<string, HashSet<Guid>> PartsByEntry = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
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

    private static bool IsObjectFile(TarEntryHeader h) =>
        h.IsFile && h.Name.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal) && h.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>First pass: finds which object file holds each patched part and checks the new assets do not collide.</summary>
    private PlanResult Plan(Func<Stream> openSource)
    {
        PlanResult plan = new PlanResult();
        if (m_patches.Count == 0 && m_assets.Count == 0 && m_insertions.Count == 0 && m_removals.Count == 0)
            return plan;

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<Guid> wanted = new HashSet<Guid>(m_patches.Keys);
        foreach (PartInsertion ins in m_insertions)
            wanted.Add(ins.AnchorPartId);
        foreach (Guid id in m_removals)
            wanted.Add(id);
        string[] wantedText = wanted.Select(g => g.ToString("D")).ToArray();
        Dictionary<Guid, string> where = new Dictionary<Guid, string>();

        using Stream source = openSource();
        TarStreamReader tar = new TarStreamReader(Decompressed(source));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            names.Add(h.Name);
            if (wanted.Count == 0 || !IsObjectFile(h))
                continue;

            byte[] body = tar.ReadBody();
            string probe = Encoding.UTF8.GetString(body);
            if (!wantedText.Any(t => probe.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            ObjectXml.Decoded d = ObjectXml.Decode(body);
            foreach (Guid id in ObjectXml.FindParts(d, wanted))
            {
                if (where.TryGetValue(id, out string other))
                    throw new InvalidDataException("part " + id + " is in both '" + other + "' and '" + h.Name + "'");
                where[id] = h.Name;
                if (!plan.PartsByEntry.TryGetValue(h.Name, out HashSet<Guid> set))
                    plan.PartsByEntry[h.Name] = set = new HashSet<Guid>();
                set.Add(id);
            }
        }

        foreach (Guid id in wanted)
            if (!where.ContainsKey(id))
                throw new InvalidDataException("part " + id + " is not in any object file of the source archive");
        foreach ((string name, byte[] _) in m_assets)
            if (names.Contains(name))
                throw new InvalidOperationException("the source archive already holds " + name);
        return plan;
    }

    private RewriteSummary Write(Func<Stream> openSource, Stream dest, PlanResult plan)
    {
        RewriteSummary summary = new RewriteSummary();
        using Stream source = openSource();
        TarStreamReader tar = new TarStreamReader(Decompressed(source));

        using (GZipStream gz = new GZipStream(dest, CompressionLevel.Optimal, leaveOpen: true))
        using (TarWriter writer = new TarWriter(gz, leaveOpen: true))
        {
            TarEntryHeader h;
            while ((h = tar.ReadNext()) != null)
            {
                if (summary.SourceEntries == 0)
                    summary.ControlFileFirst = h.Name == ArchiveConstants.CONTROL_FILE_PATH;
                summary.SourceEntries++;

                if (h.IsDirectory)
                {
                    writer.WriteDirectory(h.Name);
                    summary.EntriesCopied++;
                }
                else if (plan.PartsByEntry.TryGetValue(h.Name, out HashSet<Guid> parts))
                {
                    ObjectXml.Decoded d = ObjectXml.Decode(tar.ReadBody());
                    Dictionary<Guid, PartPatch> mine = parts.Where(g => m_patches.ContainsKey(g)).ToDictionary(g => g, g => m_patches[g]);
                    List<PartInsertion> added = m_insertions.Where(i => parts.Contains(i.AnchorPartId)).ToList();
                    List<Guid> removed = m_removals.Where(parts.Contains).ToList();
                    writer.WriteFile(h.Name, h.TypeFlag, ObjectXml.Apply(d, mine, added, removed, h.Name));
                    summary.ObjectFilesPatched++;
                    summary.ObjectsPatched += mine.Count;
                    summary.PartsAdded += added.Count;
                    summary.PartsRemoved += removed.Count;
                    summary.PatchedEntries.Add(h.Name);
                }
                else
                {
                    writer.WriteEntry(h.Name, h.TypeFlag == '\0' ? '0' : h.TypeFlag, h.Size, new MemoryStream(tar.ReadBody(), false));
                    summary.EntriesCopied++;
                }
            }

            foreach ((string name, byte[] data) in m_assets)
            {
                writer.WriteFile(name, data);
                summary.AssetsAdded++;
                summary.AddedAssets.Add(name);
            }
            summary.EntriesWritten = writer.EntriesWritten;
        }
        return summary;
    }
}
