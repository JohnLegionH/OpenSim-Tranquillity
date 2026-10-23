/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility;

/// <summary>Writes an <see cref="EligibilityResult"/> as plain text, as JSON (schema "oar-eligibility", version 1) and as a draft comparison plan.</summary>
public static class EligibilityReport
{
    public const int SchemaVersion = 1;

    public static readonly string[] Limits =
    {
        "A part is converted when the script scan saw nothing that would break; the scan reads script text only, so \"convert\" means nothing was seen, not that nothing is there.",
        "Objects stored inside prim contents are passed through unchanged and are not converted.",
        "A built-in plant, a part that is already a mesh and a part that is not an ordinary prim are skipped.",
        "The draft plan carries stand-in ids for the new mesh assets; the converter replaces them with the real ids."
    };

    private static string Name(PartAction a) => a == PartAction.Convert ? "CONVERT" : a == PartAction.KeepPrim ? "KEEP-PRIM" : "SKIP";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    public static string ToText(EligibilityResult r)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("oar-eligibility: ").Append(Sanitize.Text(r.Archive)).Append(" (").Append(r.ArchiveKind).Append(", ").Append(r.ReadStatus).AppendLine(")");
        if (r.ReadNote != null)
            sb.Append("  read note: ").AppendLine(Sanitize.Text(r.ReadNote));
        sb.Append("Tool ").AppendLine(r.ToolVersion);
        if (r.AssumeSafeUnscanned)
        {
            sb.AppendLine();
            sb.AppendLine("*** WARNING: --assume-safe-unscanned is ON. Scripts with no readable source were treated as harmless, so builds that");
            sb.AppendLine("*** hold such a script were NOT kept as prims on their account. " + N(r.ScriptItemsWithoutSource) + " script item(s) had no source.");
        }

        sb.AppendLine();
        sb.AppendLine("SUMMARY");
        sb.Append("  parts assessed:       ").AppendLine(N(r.Parts.Count));
        sb.Append("  CONVERT:              ").Append(N(r.Count(PartAction.Convert))).Append(" (").Append(N(r.Warned)).AppendLine(" with a warning)");
        sb.Append("  KEEP-PRIM:            ").AppendLine(N(r.Count(PartAction.KeepPrim)));
        sb.Append("  SKIP:                 ").AppendLine(N(r.Count(PartAction.Skip)));
        sb.Append("  script items:         ").Append(N(r.ScriptItems)).Append(", without source: ").AppendLine(N(r.ScriptItemsWithoutSource));
        if (r.NestedObjectParts > 0)
            sb.Append("  parts in contents objects (passed through, not converted): ").AppendLine(N(r.NestedObjectParts));
        sb.Append("  seconds:              ").AppendLine(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture));

        SortedDictionary<string, int> counts = r.ReasonCounts();
        if (counts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REASONS (parts by first reason)");
            foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                sb.Append("  ").Append(kv.Key.PadRight(20)).AppendLine(N(kv.Value));
        }
        SortedDictionary<string, int> scan = r.ScanCodeCounts();
        if (scan.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SCRIPT SCAN CODES BEHIND SCRIPT_UNSAFE / SCRIPT_UNCERTAIN (parts that have each)");
            foreach (KeyValuePair<string, int> kv in scan.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                sb.Append("  ").Append(kv.Key.PadRight(20)).AppendLine(N(kv.Value));
        }

        List<EligibilityRow> kept = r.Parts.Where(p => p.Action == PartAction.KeepPrim).ToList();
        sb.AppendLine();
        sb.Append("KEPT AS PRIMS (").Append(kept.Count).AppendLine(")");
        foreach (EligibilityRow p in kept)
        {
            sb.Append("  KEEP-PRIM (").Append(p.Code).Append(") | ").Append(Sanitize.Text(p.Build)).Append(" | link ").Append(p.LinkNum)
              .Append(" | ").Append(Sanitize.Text(p.Name)).Append(" | ").Append(p.Reasons[0].Detail);
            List<string> more = p.Reasons.Skip(1).Select(x => x.Code).Where(c => c != p.Code).Distinct().ToList();
            if (more.Count > 0)
                sb.Append(" (also: ").Append(string.Join(",", more)).Append(')');
            sb.AppendLine();
        }

        List<EligibilityRow> warned = r.Parts.Where(p => p.Action == PartAction.Convert && p.Warnings.Count > 0).ToList();
        sb.AppendLine();
        sb.Append("CONVERT WITH WARNINGS (").Append(warned.Count).AppendLine(")");
        foreach (EligibilityRow p in warned)
            sb.Append("  CONVERT -> ").Append(p.TargetFaces).Append(" faces | ").Append(Sanitize.Text(p.Build)).Append(" | link ").Append(p.LinkNum)
              .Append(" | ").Append(Sanitize.Text(p.Name)).Append(" | ").AppendLine(string.Join("; ", p.Warnings.Select(w => "WARN " + w.Code + ": " + w.Detail)));

        sb.AppendLine();
        sb.AppendLine("WHAT THIS REPORT CANNOT SEE");
        foreach (string l in Limits)
            sb.Append("  - ").AppendLine(l);
        return sb.ToString();
    }

    public static string ToJson(EligibilityResult r)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "oar-eligibility");
            w.WriteNumber("version", SchemaVersion);
            w.WriteString("tool", r.ToolVersion);
            w.WriteString("archive", r.Archive);
            w.WriteString("archiveKind", r.ArchiveKind);
            w.WriteString("readStatus", r.ReadStatus);
            if (r.ReadNote != null) w.WriteString("readNote", r.ReadNote);
            w.WriteBoolean("assumeSafeUnscanned", r.AssumeSafeUnscanned);

            w.WriteStartObject("summary");
            w.WriteNumber("parts", r.Parts.Count);
            w.WriteNumber("convert", r.Count(PartAction.Convert));
            w.WriteNumber("convertWithWarning", r.Warned);
            w.WriteNumber("keepPrim", r.Count(PartAction.KeepPrim));
            w.WriteNumber("skip", r.Count(PartAction.Skip));
            w.WriteNumber("scriptItems", r.ScriptItems);
            w.WriteNumber("scriptItemsWithoutSource", r.ScriptItemsWithoutSource);
            w.WriteNumber("partsInContentsObjectsPassedThrough", r.NestedObjectParts);
            w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
            w.WriteStartObject("reasonCounts");
            foreach (KeyValuePair<string, int> kv in r.ReasonCounts())
                w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteStartObject("scanCodeCounts");
            foreach (KeyValuePair<string, int> kv in r.ScanCodeCounts())
                w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("limits");
            foreach (string l in Limits) w.WriteStringValue(l);
            w.WriteEndArray();

            w.WriteStartArray("parts");
            foreach (EligibilityRow p in r.Parts)
            {
                w.WriteStartObject();
                w.WriteString("part", p.PartId.ToString());
                w.WriteString("linkset", p.LinksetId.ToString());
                w.WriteString("build", p.Build);
                w.WriteString("entry", p.Entry);
                w.WriteNumber("linkNum", p.LinkNum);
                w.WriteBoolean("isRoot", p.IsRoot);
                w.WriteString("name", p.Name);
                w.WriteString("kind", p.Kind);
                w.WriteString("decision", Name(p.Action));
                if (p.Code != null) w.WriteString("code", p.Code);
                w.WriteNumber("serverFaces", p.ServerFaces);
                w.WriteNumber("viewerFaces", p.ViewerFaces);
                if (p.Action == PartAction.Convert)
                {
                    w.WriteNumber("targetFaces", p.TargetFaces);
                    w.WriteString("placeholderAsset", p.PlaceholderAsset.ToString());
                }
                w.WriteBoolean("faceCountDependent", p.FaceCountDependent);
                w.WriteString("scanDecision", p.ScanDecision);
                if (p.ScanCodes.Count > 0)
                {
                    w.WriteStartArray("scanCodes");
                    foreach (string c in p.ScanCodes) w.WriteStringValue(c);
                    w.WriteEndArray();
                }
                WriteReasons(w, "reasons", p.Reasons);
                WriteReasons(w, "warnings", p.Warnings);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteReasons(Utf8JsonWriter w, string name, List<EligibilityReason> list)
    {
        if (list.Count == 0)
            return;
        w.WriteStartArray(name);
        foreach (EligibilityReason x in list)
        {
            w.WriteStartObject();
            w.WriteString("code", x.Code);
            w.WriteString("detail", x.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// The plan the comparer takes (schema version 1) for the rewrite this report describes: one shape change per converted part
    /// and one added mesh asset per converted part. The asset ids are stand-ins (see <see cref="ArchiveEligibility.IsPlaceholder"/>).
    /// </summary>
    public static string ToDraftPlan(EligibilityResult r)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("partChanges");
            foreach (EligibilityRow p in r.Parts.Where(x => x.Action == PartAction.Convert))
            {
                w.WriteStartObject();
                w.WriteString("part", p.PartId.ToString());
                w.WriteString("field", "shape");
                w.WriteString("to", "mesh");
                w.WriteNumber("faces", p.TargetFaces);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("addedEntries");
            foreach (EligibilityRow p in r.Parts.Where(x => x.Action == PartAction.Convert))
                w.WriteStringValue("assets/" + p.PlaceholderAsset + "_mesh.llmesh");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
