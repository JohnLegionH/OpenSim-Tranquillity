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

namespace OpenSim.Archive.ScriptScan;

/// <summary>Writes a <see cref="ScanResult"/> as plain text and as JSON (schema version 1).</summary>
public static class ScanReport
{
    public const int SchemaVersion = 1;

    /// <summary>What a text scan cannot see; printed in every report.</summary>
    public static readonly string[] Limits =
    {
        "Scripts with no source in the archive (missing asset, compiled bytecode only) are not read; they keep their whole build as prims.",
        "Rule lists, link numbers and face numbers that come from variables, function results, notecards or link messages are not worked out; the part they could reach stays a prim.",
        "Objects stored inside prim contents, and objects another script rezzes into this one, are not read.",
        "A clean result means nothing was seen, not that nothing is there."
    };

    private static string Level(PartReason r) => r.Level;

    private static string ReasonText(PartReason r)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(r.Code);
        if (!string.IsNullOrEmpty(r.Script))
            sb.Append(" in script '").Append(Sanitize.Text(r.Script)).Append('\'');
        if (r.Line > 0)
            sb.Append(" line ").Append(r.Line);
        if (!string.IsNullOrEmpty(r.Function))
            sb.Append(" (").Append(r.Function).Append(')');
        if (!string.IsNullOrEmpty(r.Detail))
            sb.Append(": ").Append(r.Detail);
        if (!string.IsNullOrEmpty(r.Reach) && r.Reach != "this part")
            sb.Append(" reaching ").Append(r.Reach);
        if (r.Count > 1)
            sb.Append(" x").Append(r.Count);
        if (!r.Literal && r.Code != LslScriptAnalyzer.ScriptUnscanned && r.Code != LslScriptAnalyzer.ScriptTruncated)
            sb.Append(" [argument not worked out]");
        return sb.ToString();
    }

    public static string ToText(ScanResult r)
    {
        StringBuilder sb = new StringBuilder();
        int total = r.Parts.Count;
        sb.Append("oar-scan: ").Append(Sanitize.Text(r.Archive)).Append(" (").Append(r.ArchiveKind).Append(", ").Append(r.ReadStatus).AppendLine(")");
        if (r.ReadNote != null)
            sb.Append("  read note: ").AppendLine(Sanitize.Text(r.ReadNote));
        sb.Append("Tool ").AppendLine(r.ToolVersion);
        sb.AppendLine();
        sb.AppendLine("SUMMARY");
        sb.Append("  parts assessed:              ").AppendLine(total.ToString(CultureInfo.InvariantCulture));
        foreach (PartDecision d in Enum.GetValues(typeof(PartDecision)))
            sb.Append("  ").Append((PartDecisionNames.Text(d) + ":").PadRight(28)).AppendLine(r.Count(d).ToString(CultureInfo.InvariantCulture));
        sb.Append("  script items:                ").AppendLine(r.ScriptItems.ToString(CultureInfo.InvariantCulture));
        sb.Append("  script sources read:         ").AppendLine(r.ScriptAssetsRead.ToString(CultureInfo.InvariantCulture));
        sb.Append("  script items without source: ").AppendLine(r.Unscanned.Count.ToString(CultureInfo.InvariantCulture));
        if (r.NestedObjectParts > 0 || r.NestedObjectScripts > 0)
            sb.Append("  parts in contents objects (not assessed): ").Append(r.NestedObjectParts).Append(", scripts there: ").AppendLine(r.NestedObjectScripts.ToString(CultureInfo.InvariantCulture));
        if (r.AssumeSafeUnscanned)
            sb.AppendLine("  unreadable scripts were assumed safe (--assume-safe-unscanned)");
        sb.Append("  seconds:                     ").AppendLine(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture));

        if (r.ReasonCounts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REASONS (parts that have each code)");
            foreach (KeyValuePair<string, int> kv in r.ReasonCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                sb.Append("  ").Append(kv.Key.PadRight(18)).AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
        }

        List<PartRow> kept = r.Parts.Where(p => PartDecisionNames.IsKept(p.Decision)).ToList();
        sb.AppendLine();
        sb.Append("KEPT AS PRIMS (").Append(kept.Count).AppendLine(")");
        foreach (PartRow p in kept)
        {
            PartReason first = p.Reasons.OrderByDescending(x => x.Level == "unsafe").FirstOrDefault();
            sb.Append("  ").Append(PartDecisionNames.Text(p.Decision)).Append(" | ").Append(Sanitize.Text(p.Build)).Append(" | link ").Append(p.LinkNum)
              .Append(" | ").Append(Sanitize.Text(p.Name)).Append(" | ")
              .Append(string.Join(",", p.Reasons.Select(x => x.Code).Distinct()));
            if (first != null)
                sb.Append(" | ").Append(ReasonText(first));
            if (p.Reasons.Count > 1)
                sb.Append(" (+").Append(p.Reasons.Count - 1 + p.ReasonsOmitted).Append(" more)");
            sb.AppendLine();
        }

        List<PartRow> warned = r.Parts.Where(p => p.Decision == PartDecision.ConvertibleWithWarnings).ToList();
        if (warned.Count > 0)
        {
            sb.AppendLine();
            sb.Append("CONVERTIBLE WITH WARNINGS (").Append(warned.Count).AppendLine(")");
            foreach (PartRow p in warned)
                sb.Append("  ").Append(Sanitize.Text(p.Build)).Append(" | link ").Append(p.LinkNum).Append(" | ").Append(Sanitize.Text(p.Name)).Append(" | ")
                  .AppendLine(string.Join("; ", p.Reasons.Select(ReasonText)));
        }

        if (r.Unscanned.Count > 0)
        {
            sb.AppendLine();
            sb.Append("SCRIPTS WITHOUT SOURCE (").Append(r.Unscanned.Count).AppendLine(")");
            foreach (UnscannedScript u in r.Unscanned)
                sb.Append("  ").Append(Sanitize.Text(u.Build)).Append(" | part ").Append(Sanitize.Text(u.Part)).Append(" | script '").Append(Sanitize.Text(u.Script)).Append("' | ").AppendLine(u.Reason);
        }

        sb.AppendLine();
        sb.AppendLine("WHAT THIS SCAN CANNOT SEE");
        foreach (string l in Limits)
            sb.Append("  - ").AppendLine(l);
        return sb.ToString();
    }

    public static string ToJson(ScanResult r)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "oar-scan");
            w.WriteNumber("version", SchemaVersion);
            w.WriteString("tool", r.ToolVersion);
            w.WriteString("archive", r.Archive);
            w.WriteString("archiveKind", r.ArchiveKind);
            w.WriteString("readStatus", r.ReadStatus);
            if (r.ReadNote != null) w.WriteString("readNote", r.ReadNote);
            w.WriteBoolean("assumeSafeUnscanned", r.AssumeSafeUnscanned);

            w.WriteStartObject("summary");
            w.WriteNumber("parts", r.Parts.Count);
            w.WriteNumber("convertible", r.Count(PartDecision.Convertible));
            w.WriteNumber("convertibleWithWarnings", r.Count(PartDecision.ConvertibleWithWarnings));
            w.WriteNumber("keepPrimUnsafe", r.Count(PartDecision.KeepPrimUnsafe));
            w.WriteNumber("keepPrimUncertain", r.Count(PartDecision.KeepPrimUncertain));
            w.WriteNumber("scriptItems", r.ScriptItems);
            w.WriteNumber("scriptSourcesRead", r.ScriptAssetsRead);
            w.WriteNumber("scriptItemsWithoutSource", r.Unscanned.Count);
            w.WriteNumber("partsInContentsObjectsNotAssessed", r.NestedObjectParts);
            w.WriteNumber("scriptsInContentsObjectsNotAssessed", r.NestedObjectScripts);
            w.WriteNumber("seconds", Math.Round(r.Seconds, 2));
            w.WriteStartObject("reasonCounts");
            foreach (KeyValuePair<string, int> kv in r.ReasonCounts)
                w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("limits");
            foreach (string l in Limits) w.WriteStringValue(l);
            w.WriteEndArray();

            w.WriteStartArray("parts");
            foreach (PartRow p in r.Parts)
            {
                w.WriteStartObject();
                w.WriteString("partId", p.PartId.ToString());
                w.WriteString("linksetId", p.LinksetId.ToString());
                w.WriteString("build", p.Build);
                w.WriteString("entry", p.Entry);
                if (p.BoxedAssetId != Guid.Empty) w.WriteString("boxedAssetId", p.BoxedAssetId.ToString());
                w.WriteNumber("linkNum", p.LinkNum);
                w.WriteBoolean("isRoot", p.IsRoot);
                w.WriteString("name", p.Name);
                w.WriteString("decision", PartDecisionNames.Text(p.Decision));
                w.WriteString("decisionCode", p.Decision == PartDecision.Convertible ? "convertible"
                    : p.Decision == PartDecision.ConvertibleWithWarnings ? "convertible-with-warnings"
                    : p.Decision == PartDecision.KeepPrimUnsafe ? "keep-prim-unsafe" : "keep-prim-uncertain");
                w.WriteBoolean("keepPrim", PartDecisionNames.IsKept(p.Decision));
                w.WriteBoolean("faceCountDependent", p.FaceCountDependent);
                w.WriteNumber("reasonsOmitted", p.ReasonsOmitted);
                w.WriteStartArray("reasons");
                foreach (PartReason x in p.Reasons)
                {
                    w.WriteStartObject();
                    w.WriteString("code", x.Code);
                    w.WriteString("level", Level(x));
                    w.WriteString("script", x.Script);
                    w.WriteString("scriptAssetId", x.ScriptAssetId.ToString());
                    w.WriteString("hostPart", x.HostPartName);
                    w.WriteString("hostPartId", x.HostPartId.ToString());
                    w.WriteNumber("hostLinkNum", x.HostLinkNum);
                    w.WriteNumber("line", x.Line);
                    if (x.Function != null) w.WriteString("function", x.Function);
                    if (x.Detail != null) w.WriteString("detail", x.Detail);
                    w.WriteNumber("count", x.Count);
                    w.WriteString("reach", x.Reach);
                    w.WriteBoolean("literal", x.Literal);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("unscannedScripts");
            foreach (UnscannedScript u in r.Unscanned)
            {
                w.WriteStartObject();
                w.WriteString("script", u.Script);
                w.WriteString("assetId", u.AssetId.ToString());
                w.WriteString("part", u.Part);
                w.WriteString("partId", u.PartId.ToString());
                w.WriteString("build", u.Build);
                w.WriteString("reason", u.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
