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
    /// <summary>Remove duplicate and loose vertices and zero-area triangles from every face (on by default; the command line's --no-clean turns it off).</summary>
    public bool Clean { get; set; } = true;
    /// <summary>
    /// The start-up check of the image library: returns null when it works, otherwise the reason it does not (the export
    /// then stops before writing anything). Null runs the built-in check, <see cref="ImageSelfCheck.Problem"/>; tests replace it.
    /// </summary>
    public Func<string> ImageCheck { get; set; }
    /// <summary>Only the build whose root part has this id or whose name is this (case-insensitive).</summary>
    public string Object { get; set; }
    /// <summary>Export at most this many builds (after the filter); 0 for all.</summary>
    public int Limit { get; set; }
    /// <summary>
    /// Memory the export aims to stay within, in megabytes; 0 for the default: 2048, or half of the machine's memory if that is
    /// smaller (<see cref="ArchiveExporter.DefaultMaxMemoryMegabytes(long)"/>). Asset bodies beyond a share of it wait
    /// in the cache folder, and the decoded-image and read-back caches are sized from it. An asset that is larger than half
    /// of it cannot be handled: it is reported over-memory-limit and the run ends with exit code 4.
    /// </summary>
    public int MaxMemoryMegabytes { get; set; }
    /// <summary>The machine's memory in bytes, for choosing the default memory cap; 0 (the default) to ask the runtime. A test seam.</summary>
    public long PhysicalMemoryBytes { get; set; }
    /// <summary>
    /// Most megabytes of asset bodies the cache folder and memory may hold together; 0 (the default) for no limit. An asset
    /// that would take the total past it is reported over-size-limit and the run ends with exit code 4.
    /// </summary>
    public int MaxCacheMegabytes { get; set; }
    /// <summary>
    /// Folder the asset bodies are kept in while the builds are written, so memory does not grow with the archive. Left
    /// in place when the run ends (the files can be removed by hand). Default: a folder named .asset-cache in the output folder.
    /// </summary>
    public string CacheDirectory { get; set; }
    /// <summary>
    /// Keep each build's textures and assets in its own folder, and the PNG images inside its glTF file (one build can then be
    /// moved alone). The default is one shared textures folder and one shared assets folder beside the build folders, each
    /// file named by its id, and the glTF files point at the shared PNG files by relative path.
    /// </summary>
    public bool SelfContained { get; set; }
    /// <summary>Accepted and ignored: the up-front free-space estimate is a warning only. Kept so earlier command lines still work.</summary>
    public bool SkipSpaceCheck { get; set; }
    /// <summary>Free bytes on the output drive below which the run stops after the current build; 0 for the default of 2 GB. A test hook.</summary>
    public long MinFreeBytes { get; set; }
    /// <summary>Free bytes on the drive that holds a path; null reads the real drive. A test hook.</summary>
    public Func<string, long?> FreeBytes { get; set; }
    /// <summary>Writes an asset body to the cache folder; null uses File.WriteAllBytes. A test hook.</summary>
    public Action<string, byte[]> WriteCacheFile { get; set; }
    /// <summary>Write every build again, even one the output folder already holds from this archive by this tool version.</summary>
    public bool Force { get; set; }
    /// <summary>
    /// File a memory log is appended to (one JSON line every <see cref="DiagnosticsEvery"/> builds, and while the archive is
    /// read, every that many object files): managed heap, working set, generation sizes and the size of every structure kept
    /// across builds. Null (the default) writes nothing.
    /// </summary>
    public string DiagnosticsPath { get; set; }
    /// <summary>Builds (or object files) between two lines of the memory log; 0 for 100.</summary>
    public int DiagnosticsEvery { get; set; }
    /// <summary>Collect garbage before each line of the memory log, so it shows what the run holds (slower; for measuring).</summary>
    public bool DiagnosticsCollect { get; set; }
    /// <summary>Report progress after this many builds (and at least every ten seconds); 0 for 25.</summary>
    public int ProgressEvery { get; set; }
    /// <summary>Receives the progress lines and the notes about a build that failed; null for none.</summary>
    public Action<string> Progress { get; set; }
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
    /// <summary>Numbers of the faces cleaning left as they were because every triangle of them had no area; absent when there are none.</summary>
    public List<int> UncleanedFaces { get; set; }
    /// <summary>The sculpt map or mesh asset the part is built from (sculpt and mesh parts only).</summary>
    public string GeometryAssetId { get; set; }
    /// <summary>Status of that asset: copied, missing-from-archive, over-size-limit or undecodable.</summary>
    public string GeometryAssetStatus { get; set; }
    /// <summary>A mesh that is rigged: it is exported static, in its bind pose.</summary>
    public bool? Rigged { get; set; }
    public List<string> JointNames { get; set; }
}

/// <summary>A texture (or other image asset) a build's faces use.</summary>
public sealed class ManifestTexture
{
    public string TextureId { get; set; }
    /// <summary>diffuse, normal or specular.</summary>
    public string Role { get; set; }
    /// <summary>embedded (a texture of the glTF material), copied (kept as a file only: a specular map), missing-from-archive, library-default, over-size-limit, or undecodable.</summary>
    public string Status { get; set; }
    public string Reason { get; set; }
    /// <summary>Path of the unchanged copy of the asset, relative to the build's folder (empty when the asset is not in the archive).</summary>
    public string CopiedTo { get; set; }
    /// <summary>Path of the PNG file the glTF file points at, relative to the build's folder (shared layout only; in the self-contained layout the PNG is inside the glTF file).</summary>
    public string ImagePath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool? HasAlpha { get; set; }
    /// <summary>Number of faces that use it in this role (for a sculpt map or a mesh asset, the number of parts).</summary>
    public int Faces { get; set; }
}

/// <summary>A file a build uses that is kept outside its glTF file.</summary>
public sealed class ManifestFile
{
    public string Id { get; set; }
    /// <summary>original (the asset exactly as the archive holds it) or png (the decoded texture the glTF file points at).</summary>
    public string Kind { get; set; }
    /// <summary>Path relative to the build's folder.</summary>
    public string Path { get; set; }
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
    /// <summary>"shared" (textures and assets in folders beside the build folders) or "self-contained" (in the build's own folder).</summary>
    public string Layout { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    /// <summary>Every file this build uses outside its glTF file, with where it lives (a path relative to the build's folder).</summary>
    public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();
    /// <summary>Parts in link-number order.</summary>
    public List<ManifestPart> Parts { get; set; } = new List<ManifestPart>();
    public int TexturesEmbedded { get; set; }
    public int TexturesMissing { get; set; }
    public int TexturesCopied { get; set; }
    /// <summary>Mesh assets copied unchanged into the build's assets folder.</summary>
    public int AssetsCopied { get; set; }
    public List<ManifestTexture> Textures { get; set; } = new List<ManifestTexture>();
}

public sealed class SummaryBuild
{
    /// <summary>ok, or failed (the build's error is in <see cref="Error"/> and nothing of it is relied on).</summary>
    public string Status { get; set; } = "ok";
    public string Error { get; set; }
    /// <summary>SHA-256 of the archive's object file the build was read from, and the tool version that wrote it: a rerun skips the build when both match.</summary>
    public string SourceHash { get; set; }
    public string ToolVersion { get; set; }
    public string ArchiveEntry { get; set; }
    /// <summary>Which of the builds with the same root part and source hash this is (0 for the first).</summary>
    public int Occurrence { get; set; }
    public long OutputBytes { get; set; }
    
    public int AssetsCopied { get; set; }
    public int SculptsExported { get; set; }
    public int MeshesExported { get; set; }
    public int RiggedMeshes { get; set; }
    /// <summary>shared or self-contained: a rerun leaves a build only when it was written in the layout asked for now.</summary>
    public string Layout { get; set; }
    /// <summary>The shared files (paths relative to the output folder) this build points at; a rerun leaves the build only when they are still there.</summary>
    public List<string> SharedFiles { get; set; }
    public Dictionary<string, int> Reasons { get; set; }
    public string Folder { get; set; }
    public string Name { get; set; }
    public string RootPartUuid { get; set; }
    public int Parts { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    public string File { get; set; }
    public int TexturesEmbedded { get; set; }
    public int TexturesMissing { get; set; }
    public int TexturesCopied { get; set; }
}

public static class ExitCodes
{
    public const int Done = 0, Unreadable = 2, EveryBuildFailed = 3, Incomplete = 4, LowDiskSpace = 5, BadCommandLine = 64;
}

public sealed class ExportSummary
{
    public string Tool { get; set; } = "OpenSim.Archive.GltfExport";
    public string Archive { get; set; }
    /// <summary>The exit code the command line ends with.</summary>
    public int ExitCode { get; set; }
    /// <summary>True when the archive was damaged or cut short: everything readable was exported and the rest is missing.</summary>
    public bool ArchiveIncomplete { get; set; }
    /// <summary>shared or self-contained.</summary>
    public string Layout { get; set; }
    /// <summary>Each memory or disk cap that was reached, with what it cost; any entry makes the exit code 4.</summary>
    public List<string> CapsReached { get; set; } = new List<string>();
    /// <summary>Set when the run stopped between builds because the output drive's free space fell below the floor; the exit code is then 5.</summary>
    public string DiskSpaceStop { get; set; }
    /// <summary>Builds left undone by that stop; running the same line again does them.</summary>
    public int BuildsNotDone { get; set; }
    /// <summary>Notes that do not change the exit code, such as a free-space estimate that looks too small.</summary>
    public List<string> Warnings { get; set; } = new List<string>();
    public string ReadStatus { get; set; }
    public string StoppedAt { get; set; }
    public int Builds { get; set; }
    public int BuildsWithAFile { get; set; }
    /// <summary>Builds whose export failed; each is listed in the build list with its error.</summary>
    public int BuildsFailed { get; set; }
    /// <summary>Builds left as an earlier run wrote them (same source hash and tool version).</summary>
    public int BuildsResumed { get; set; }
    public string ToolVersion { get; set; }
    /// <summary>Highest working set of the process, in megabytes.</summary>
    public double PeakWorkingSetMegabytes { get; set; }
    /// <summary>Highest managed heap size seen when a build finished (the memory cap limits this), in megabytes; 0 when no build ran.</summary>
    public double PeakManagedHeapMegabytes { get; set; }
    /// <summary>Asset bytes written to the cache folder in this run.</summary>
    public long CacheBytesWritten { get; set; }
    /// <summary>Times the archive was read: 1, plus one for each round that went back for assets seen before a build asked for them.</summary>
    public int ArchivePasses { get; set; }
    public int Parts { get; set; }
    public int Exported { get; set; }
    public int Placeheld { get; set; }
    public int Skipped { get; set; }
    /// <summary>Textures embedded in a build's file, counted once per build.</summary>
    public int TexturesEmbedded { get; set; }
    /// <summary>Textures a build's faces name that are not in the archive or could not be decoded, counted once per build.</summary>
    public int TexturesMissing { get; set; }
    /// <summary>Asset files copied unchanged into a build's textures folder.</summary>
    public int TexturesCopied { get; set; }
    /// <summary>Mesh assets copied unchanged into a build's assets folder.</summary>
    public int AssetsCopied { get; set; }
    /// <summary>Sculpt parts whose geometry is in a file.</summary>
    public int SculptsExported { get; set; }
    /// <summary>Mesh parts whose geometry is in a file.</summary>
    public int MeshesExported { get; set; }
    /// <summary>Of those, meshes that are rigged (exported static).</summary>
    public int RiggedMeshes { get; set; }
    /// <summary>Count of parts per placeheld or skipped reason.</summary>
    public Dictionary<string, int> Reasons { get; set; } = new Dictionary<string, int>();
    public double Seconds { get; set; }
    public long OutputBytes { get; set; }
    /// <summary>Of those, the bytes of the shared textures and assets folders (shared layout).</summary>
    public long SharedBytes { get; set; }
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
