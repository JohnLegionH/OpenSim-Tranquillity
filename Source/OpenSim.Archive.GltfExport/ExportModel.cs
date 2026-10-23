/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.GltfExport;

public static class ExportStatus
{
    /// <summary>The part's geometry is in the file.</summary>
    public const string Exported = "exported";
    /// <summary>The part is in the file as a named empty node; its geometry comes in a later release.</summary>
    public const string Placeheld = "placeheld";
    /// <summary>The part is not in the file; the reason is in the manifest.</summary>
    public const string Skipped = "skipped";
}

public static class PartKinds
{
    public const string Prim = "prim";
    public const string Sculpt = "sculpt";
    public const string Mesh = "mesh";
    public const string Tree = "tree";
    public const string Grass = "grass";
    public const string Other = "other";
}

public sealed class ExportOptions
{
    /// <summary>Folder the builds are written to; created when missing.</summary>
    public string OutputDirectory { get; set; }
    /// <summary>Only the build whose root part has this id or whose name is this (case-insensitive).</summary>
    public string Object { get; set; }
    /// <summary>Export at most this many builds (after the filter); 0 for all.</summary>
    public int Limit { get; set; }
    /// <summary>
    /// Put the build node at the root part's position and rotation in the region. When false (the default) the build node
    /// is at the origin with the root part's own frame, and the region position and rotation are in its extras.
    /// </summary>
    public bool World { get; set; }
}

public sealed class ManifestPermissions
{
    public uint? Base { get; set; }
    public uint? Owner { get; set; }
    public uint? Group { get; set; }
    public uint? Everyone { get; set; }
    public uint? NextOwner { get; set; }
}

public sealed class ManifestItem
{
    public string Name { get; set; }
    /// <summary>Asset type name (for example "Notecard"), or the number when unknown.</summary>
    public string Type { get; set; }
    public string InventoryType { get; set; }
}

public sealed class ManifestPart
{
    public int LinkNumber { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string PartUuid { get; set; }
    public bool IsRoot { get; set; }
    public string CreatorId { get; set; }
    public string CreatorName { get; set; }
    public string CreatorHome { get; set; }
    public string OwnerId { get; set; }
    public ManifestPermissions Permissions { get; set; } = new ManifestPermissions();
    /// <summary>Items in the part's inventory. Left out for a skipped part.</summary>
    public List<ManifestItem> Contents { get; set; }
    public string Kind { get; set; }
    public string Status { get; set; }
    /// <summary>Why the part is placeheld or skipped; empty when exported.</summary>
    public string Reason { get; set; }
    /// <summary>Prim faces written (exported parts only).</summary>
    public int? Faces { get; set; }
    public int? Triangles { get; set; }
}

public sealed class BuildManifest
{
    public string Tool { get; set; } = "OpenSim.Archive.GltfExport";
    public int SchemaVersion { get; set; } = 1;
    /// <summary>File name of the archive, without its folder.</summary>
    public string Archive { get; set; }
    public string ArchiveEntry { get; set; }
    public string Name { get; set; }
    public string RootPartUuid { get; set; }
    public string File { get; set; }
    public int PartCount { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    /// <summary>Parts in link-number order.</summary>
    public List<ManifestPart> Parts { get; set; } = new List<ManifestPart>();
}

public sealed class SummaryBuild
{
    public string Folder { get; set; }
    public string Name { get; set; }
    public string RootPartUuid { get; set; }
    public int Parts { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    public string File { get; set; }
}

public sealed class ExportSummary
{
    public string Tool { get; set; } = "OpenSim.Archive.GltfExport";
    public string Archive { get; set; }
    public string ReadStatus { get; set; }
    public string StoppedAt { get; set; }
    public int Builds { get; set; }
    public int BuildsWithAFile { get; set; }
    public int Parts { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    /// <summary>Count of parts per placeheld or skipped reason.</summary>
    public Dictionary<string, int> Reasons { get; set; } = new Dictionary<string, int>();
    public double Seconds { get; set; }
    public long OutputBytes { get; set; }
    public List<SummaryBuild> BuildList { get; set; } = new List<SummaryBuild>();
}

/// <summary>The export-permission rule of the archive inspector: only a creator's explicit refusal blocks a part.</summary>
public static class ExportPermission
{
    private const uint ExportBit = (uint)OpenMetaverse.PermissionMask.Export;

    /// <summary>
    /// A part held by someone other than its creator is refused when its base mask carries the export right and
    /// neither the owner nor the everyone mask grants it (after a transfer the creator's choice stays in the everyone
    /// mask). Parts held by their creator, library content, parts with no known creator or owner, and parts whose base
    /// mask has no export bit (archives saved before the bit existed) are allowed.
    /// </summary>
    public static bool IsAllowed(PartRecord p, out string reason)
    {
        reason = null;
        CreatorRef creator = p.Creator;
        if (creator.IsLibrary || p.OwnerId == OpenMetaverse.UUID.Zero || creator.Id == OpenMetaverse.UUID.Zero || creator.Id == p.OwnerId)
            return true;
        PermissionMasks m = p.Masks;
        if (!m.Base.HasValue || (m.Base.Value & ExportBit) == 0)
            return true;
        if (((m.Owner ?? 0) & ExportBit) != 0 || ((m.Everyone ?? 0) & ExportBit) != 0)
            return true;
        reason = "creator did not allow export: the part is held by someone other than its creator, its base mask has the export right, and neither its owner nor its everyone mask grants it";
        return false;
    }
}
