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
    /// Folder for the fresh export of the original archive that the .glb files are compared with. Null: a folder next to the
    /// output archive, named after it with ".work" added. The folder is left in place, so a second run reuses it.
    /// </summary>
    public string WorkFolder { get; set; }
    public GeometryTolerance Tolerance { get; set; } = new GeometryTolerance();
    public Action<string> Progress { get; set; }
}

public static class PartOutcome
{
    public const string Unchanged = "unchanged";
    public const string Rebuilt = "rebuilt";
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
        sb.Append("allow unsafe: ").Append(AllowUnsafe ? "yes" : "no").Append('\n');
        sb.Append("builds in the archive: ").Append(BuildsInArchive.ToString(c)).Append(", with a .glb: ").Append((BuildsInArchive - BuildsWithoutGlb.Count).ToString(c))
          .Append(", without (kept exactly as they were): ").Append(BuildsWithoutGlb.Count.ToString(c)).Append('\n');
        sb.Append("parts: unchanged ").Append(Count(PartOutcome.Unchanged).ToString(c)).Append(", rebuilt ").Append(Count(PartOutcome.Rebuilt).ToString(c))
          .Append(", refused ").Append(Count(PartOutcome.Refused).ToString(c)).Append('\n');
        sb.Append("meshes added: ").Append(MeshesAdded.ToString(c)).Append(" (").Append(MeshBytes.ToString(c)).Append(" bytes)\n");
        if (Parity != null)
            sb.Append("parity check: ").Append(Parity.Pass ? "PASS" : "FAIL").Append('\n');
        sb.Append('\n');
        foreach (PartReport p in Parts)
        {
            sb.Append(p.Outcome.ToUpperInvariant()).Append("  link ").Append(p.LinkNum.ToString(c)).Append("  '").Append(p.Name).Append("'  ").Append(p.PartId.ToString("D"));
            if (p.Outcome == PartOutcome.Refused)
                sb.Append("  ").Append(p.Code).Append(": ").Append(p.Detail);
            else if (p.Outcome == PartOutcome.Rebuilt)
                sb.Append("  ").Append(p.Faces.ToString(c)).Append(" faces, ").Append(p.Triangles.ToString(c)).Append(" triangles, mesh ").Append(p.MeshId.ToString("D"));
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
        tolerance = new { position = TolerancePosition, normal = ToleranceNormal, uv = ToleranceUv },
        buildsInArchive = BuildsInArchive,
        buildsWithoutGlb = BuildsWithoutGlb,
        unchanged = Count(PartOutcome.Unchanged),
        rebuilt = Count(PartOutcome.Rebuilt),
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
