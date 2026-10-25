/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Restore;

public sealed class RestoreOptions
{
    /// <summary>Rebuild a changed part even when the script scan rates its scripts unsafe or uncertain; the report records that it did.</summary>
    public bool AllowUnsafe { get; set; }
    /// <summary>Treat a script with no readable source as harmless (as oar-eligibility does).</summary>
    public bool AssumeSafeUnscanned { get; set; }
    /// <summary>
    /// A part the .glb no longer has is refused by default. With this on it becomes an invisible stub: fully transparent faces and
    /// no collision, keeping its name, description, scripts and contents, so link numbers never shift or leave a gap.
    /// </summary>
    public bool StubDeleted { get; set; }
    /// <summary>
    /// Folder for the fresh export of the original archive that the .glb files are compared with. Null: a new folder under the
    /// system's temporary folder.
    /// </summary>
    public string WorkFolder { get; set; }
    /// <summary>
    /// Leave the comparison export's folder in place at the end of the run. Without it the tool removes the folder, when the run made it
    /// (the folder did not exist, or was empty, when the run started); a folder that held other files is never emptied.
    /// </summary>
    public bool KeepWork { get; set; }
    /// <summary>The creator named on parts added from the .glb and on new texture assets. Null: the root part's owner.</summary>
    public Guid? Creator { get; set; }
    public GeometryTolerance Tolerance { get; set; } = new GeometryTolerance();
    public Action<string> Progress { get; set; }
}

public static class PartOutcome
{
    public const string Unchanged = "unchanged";
    /// <summary>Only its place or size changed: position, rotation or scale updated, the shape left as it was.</summary>
    public const string Moved = "moved";
    public const string Rebuilt = "rebuilt";
    /// <summary>A part that was not in the original, added after the last link number.</summary>
    public const string Added = "added";
    /// <summary>A deleted part kept as an invisible stub.</summary>
    public const string Stubbed = "stubbed";
    /// <summary>A deleted part removed from the archive, with the link numbers after it moved down.</summary>
    public const string Deleted = "deleted";
    public const string Refused = "refused";
}

/// <summary>The result for one part of a build that had a .glb.</summary>
public sealed class PartReport
{
    public string Glb { get; set; }
    public Guid PartId { get; set; }
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public string Outcome { get; set; }
    /// <summary>For a refused part, a short code (PART_MOVED, SCRIPT_UNSAFE, ...); for a rebuilt one, null.</summary>
    public string Code { get; set; }
    public string Detail { get; set; }
    public int Faces { get; set; }
    public int Triangles { get; set; }
    public Guid MeshId { get; set; }
    /// <summary>For an added part, the creator written on it.</summary>
    public Guid Creator { get; set; }
    /// <summary>The largest differences found between the .glb's geometry and the fresh export, among the matched triangles (an unchanged part), in metres, normal components and texture coordinates.</summary>
    public float MaxPosition { get; set; }
    public float MaxNormal { get; set; }
    public float MaxUv { get; set; }
    public List<string> Notes { get; set; } = new List<string>();
}

public sealed class RestoreResult
{
    public string Source { get; set; }
    public string Output { get; set; }
    public string GlbFolder { get; set; }
    public string WorkFolder { get; set; }
    public bool WorkFolderRemoved { get; set; }
    public bool StubDeleted { get; set; }
    public bool AllowUnsafe { get; set; }
    public float TolerancePosition { get; set; }
    public float ToleranceNormal { get; set; }
    public float ToleranceUv { get; set; }
    public int GlbFiles { get; set; }
    public int BuildsInArchive { get; set; }
    public List<string> BuildsWithoutGlb { get; set; } = new List<string>();
    public List<PartReport> Parts { get; set; } = new List<PartReport>();
    public int MeshesAdded { get; set; }
    public long MeshBytes { get; set; }
    public double Seconds { get; set; }
    public RewriteSummary Rewrite { get; set; }
    public CompareResult Parity { get; set; }
    public string PlanJson { get; set; }
    /// <summary>Set when a rebuilt mesh failed its validity check; nothing is written then.</summary>
    public List<string> Invalid { get; set; } = new List<string>();
    public bool Written { get; set; }

    public int Count(string outcome) => Parts.Count(p => p.Outcome == outcome);

    public int TexturesAdded { get; set; }

    /// <summary>The new texture assets and the creator named for each.</summary>
    public List<(Guid Id, Guid Creator)> NewTextures { get; set; } = new List<(Guid, Guid)>();

    /// <summary>0 all parts restored and the archive passed its check; 1 the check failed or a mesh was invalid; 3 written, but some part was refused.</summary>
    public int ExitCode => Invalid.Count > 0 || (Written && (Parity == null || !Parity.Pass)) ? 1 : Count(PartOutcome.Refused) > 0 ? 3 : 0;

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        sb.Append("oar-restore\n");
        sb.Append("original: ").Append(Source).Append('\n');
        sb.Append(".glb folder: ").Append(GlbFolder).Append(" (").Append(GlbFiles.ToString(c)).Append(" files)\n");
        sb.Append("output: ").Append(Written ? Output : "(not written)").Append('\n');
        sb.Append("tolerance: position ").Append(TolerancePosition.ToString("0.#######", c)).Append(" m, normal ").Append(ToleranceNormal.ToString("0.#######", c))
          .Append(", texture coordinate ").Append(ToleranceUv.ToString("0.#######", c)).Append('\n');
        sb.Append("allow unsafe: ").Append(AllowUnsafe ? "yes" : "no").Append(", stub deleted parts: ").Append(StubDeleted ? "yes" : "no").Append('\n');
        sb.Append("builds in the archive: ").Append(BuildsInArchive.ToString(c)).Append(", with a .glb: ").Append((BuildsInArchive - BuildsWithoutGlb.Count).ToString(c))
          .Append(", without (kept exactly as they were): ").Append(BuildsWithoutGlb.Count.ToString(c)).Append('\n');
        sb.Append("parts: unchanged ").Append(Count(PartOutcome.Unchanged).ToString(c)).Append(", moved ").Append(Count(PartOutcome.Moved).ToString(c))
          .Append(", rebuilt ").Append(Count(PartOutcome.Rebuilt).ToString(c)).Append(", added ").Append(Count(PartOutcome.Added).ToString(c))
          .Append(", stubbed ").Append(Count(PartOutcome.Stubbed).ToString(c)).Append(", refused ").Append(Count(PartOutcome.Refused).ToString(c)).Append('\n');
        sb.Append("meshes added: ").Append(MeshesAdded.ToString(c)).Append(" (").Append(MeshBytes.ToString(c)).Append(" bytes), textures added: ").Append(TexturesAdded.ToString(c)).Append('\n');
        if (Parity != null)
            sb.Append("parity check: ").Append(Parity.Pass ? "PASS" : "FAIL").Append('\n');
        sb.Append('\n');
        // every refused part first, so it is the first thing a reader sees
        List<PartReport> refused = Parts.Where(p => p.Outcome == PartOutcome.Refused).ToList();
        if (refused.Count > 0)
        {
            sb.Append("REFUSED PARTS (").Append(refused.Count.ToString(c)).Append("): these stay as they were in the original\n");
            foreach (PartReport p in refused)
                sb.Append("  link ").Append(p.LinkNum.ToString(c)).Append("  '").Append(p.Name).Append("'  ").Append(p.PartId.ToString("D")).Append("  ").Append(p.Code).Append(": ").Append(p.Detail).Append('\n');
            sb.Append('\n');
        }
        List<PartReport> addedParts = Parts.Where(p => p.Outcome == PartOutcome.Added).ToList();
        if (addedParts.Count > 0 || NewTextures.Count > 0)
        {
            sb.Append("CREATOR OF NEW CONTENT (an archive carries no creator for a texture asset; the creator is named here and on each added part)\n");
            foreach (PartReport p in addedParts)
                sb.Append("  added part link ").Append(p.LinkNum.ToString(c)).Append(" '").Append(p.Name).Append("' ").Append(p.PartId.ToString("D")).Append("  creator ").Append(p.Creator.ToString("D")).Append('\n');
            foreach ((Guid id, Guid creator) in NewTextures)
                sb.Append("  new texture ").Append(id.ToString("D")).Append("  creator ").Append(creator.ToString("D")).Append('\n');
            sb.Append('\n');
        }
        foreach (PartReport p in Parts)
        {
            sb.Append(p.Outcome.ToUpperInvariant()).Append("  link ").Append(p.LinkNum.ToString(c)).Append("  '").Append(p.Name).Append("'  ").Append(p.PartId.ToString("D"));
            if (p.Outcome == PartOutcome.Refused)
                sb.Append("  ").Append(p.Code).Append(": ").Append(p.Detail);
            else if (p.Outcome == PartOutcome.Rebuilt || p.Outcome == PartOutcome.Added)
            {
                sb.Append("  ").Append(p.Faces.ToString(c)).Append(" faces, ").Append(p.Triangles.ToString(c)).Append(" triangles, mesh ").Append(p.MeshId.ToString("D"));
                if (!string.IsNullOrEmpty(p.Detail) && p.Outcome == PartOutcome.Added)
                    sb.Append("  ").Append(p.Detail);
            }
            else if (p.Outcome == PartOutcome.Moved || p.Outcome == PartOutcome.Stubbed)
                sb.Append("  ").Append(p.Detail);
            else
                sb.Append("  within ").Append(p.MaxPosition.ToString("0.0#######", c)).Append(" m");
            sb.Append('\n');
            foreach (string n in p.Notes)
                sb.Append("    note: ").Append(n).Append('\n');
        }
        foreach (string s in Invalid)
            sb.Append("INVALID MESH: ").Append(s).Append('\n');
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schema = "oar-restore",
        version = 1,
        source = Source,
        output = Written ? Output : null,
        glbFolder = GlbFolder,
        glbFiles = GlbFiles,
        allowUnsafe = AllowUnsafe,
        stubDeleted = StubDeleted,
        workFolderRemoved = WorkFolderRemoved,
        tolerance = new { position = TolerancePosition, normal = ToleranceNormal, uv = ToleranceUv },
        buildsInArchive = BuildsInArchive,
        buildsWithoutGlb = BuildsWithoutGlb,
        unchanged = Count(PartOutcome.Unchanged),
        moved = Count(PartOutcome.Moved),
        rebuilt = Count(PartOutcome.Rebuilt),
        added = Count(PartOutcome.Added),
        stubbed = Count(PartOutcome.Stubbed),
        texturesAdded = TexturesAdded,
        refused = Count(PartOutcome.Refused),
        meshesAdded = MeshesAdded,
        meshBytes = MeshBytes,
        parity = Parity == null ? null : (bool?)Parity.Pass,
        seconds = Seconds,
        invalid = Invalid,
        parts = Parts
    }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>The restore could not run, or refused the whole job (a .glb that does not match the original, an unreadable file).</summary>
public sealed class RestoreException : Exception
{
    public RestoreException(string message, Exception inner = null) : base(message, inner) { }
}
