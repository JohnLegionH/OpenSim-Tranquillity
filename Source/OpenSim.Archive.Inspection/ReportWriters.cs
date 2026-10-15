/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

public static class ReportJson
{
    private sealed class UuidConverter : JsonConverter<UUID>
    {
        public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            UUID.TryParse(reader.GetString() ?? string.Empty, out UUID id);
            return id;
        }

        public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableUuidConverter : JsonConverter<UUID?>
    {
        public override UUID? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            return UUID.TryParse(reader.GetString() ?? string.Empty, out UUID id) ? id : null;
        }

        public override void Write(Utf8JsonWriter writer, UUID? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString());
            else
                writer.WriteNullValue();
        }
    }

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UuidConverter());
        o.Converters.Add(new NullableUuidConverter());
        return o;
    }

    public static string Serialize(ArchiveReport report) => JsonSerializer.Serialize(report, Options);

    public static void Write(ArchiveReport report, Stream stream)
    {
        JsonSerializer.Serialize(stream, report, Options);
    }
}

/// <summary>The plain text report. Every name and text taken from the archive passes through <see cref="Sanitize"/>.</summary>
public static class TextReport
{
    private static string S(string s) => Sanitize.Text(s ?? string.Empty);

    private static string Size(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? bytes + " B" : v.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private static string Date(int? unix) =>
        unix.HasValue && unix.Value > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";

    private static string Who(PartyTotals p)
    {
        string name = S(p.Name);
        if (name.Length > 0 && p.Home.Length > 0)
            name += " @ " + S(p.Home);
        return name;
    }

    public static string Build(ArchiveReport r, bool all)
    {
        StringBuilder sb = new StringBuilder();
        ArchiveInfo a = r.Archive;
        int top = all ? int.MaxValue : 50;

        if (r.Status == InspectionStatus.NotAnArchive)
        {
            sb.AppendLine("Not an OAR or IAR archive: " + S(r.StopReason));
            return sb.ToString();
        }

        // creator analysis first when it fires or has a note
        if (r.CreatorAnalysis.Fired || r.CreatorAnalysis.Notes.Count > 0)
        {
            sb.AppendLine("CREATOR ANALYSIS");
            foreach (CreatorFlag f in r.CreatorAnalysis.Flags)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0} (grouped by {1}): {2} is credited on {3} ({4:P1}).",
                    f.Code, f.Grouping, S(f.Creator), f.Coverage, f.Share));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Records: {0:N0} total, {1:N0} library content, {2:N0} blank creators and {3:N0} built-in trees and grass left out, {4:N0} counted.",
                r.CreatorAnalysis.TotalRecords, r.CreatorAnalysis.LibraryRecords, r.CreatorAnalysis.BlankRecords, r.CreatorAnalysis.PlantRecords, r.CreatorAnalysis.CountedRecords));
            foreach (string note in r.CreatorAnalysis.Notes)
                sb.AppendLine("  Note: " + S(note));
            foreach (AnalysisMatch m in r.CreatorAnalysis.Matches)
                sb.AppendLine("  - " + S(m.Statement));
            if (r.CreatorAnalysis.Advisory != null)
                sb.AppendLine("  " + r.CreatorAnalysis.Advisory);
            sb.AppendLine();
        }

        AppendChecks(sb, r);

        sb.AppendLine("ARCHIVE");
        sb.AppendLine("  kind: " + a.Kind + (a.Version != null ? "  version " + a.Version : "") + (a.MultiRegion ? "  (multi-region)" : ""));
        sb.AppendLine("  status: " + r.Status + (r.StoppedAt != null ? "  stopped at: " + S(r.StoppedAt) : "") + (r.StopReason != null ? "  reason: " + S(r.StopReason) : ""));
        if (a.CreationTime.HasValue)
            sb.AppendLine("  created: " + DateTimeOffset.FromUnixTimeSeconds(a.CreationTime.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        if (r.CreatorAnalysis.PlantRecords > 0)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  built-in trees and grass: {0:N0} parts (counted apart, left out of the creator shares)", r.CreatorAnalysis.PlantRecords));
        sb.AppendLine("  assets included: " + (a.AssetsIncluded.HasValue ? a.AssetsIncluded.Value.ToString() : "not stated"));
        sb.AppendLine("  size: " + Size(a.FileBytes) + " on disk, " + Size(a.UncompressedBytes) + " uncompressed (" + (a.Compressed ? "gzip" : "plain tar") + "), " + a.EntryCount + " entries");
        if (!a.ControlFileFound)
            sb.AppendLine("  control file archive.xml: not found");
        else if (!a.ControlFileFirst)
            sb.AppendLine("  control file archive.xml: not the first entry");
        foreach (RegionInfo reg in a.Regions)
            sb.AppendLine("  region " + (reg.Dir.Length > 0 ? S(reg.Dir) + " " : "") + (reg.Id.HasValue ? reg.Id.Value.ToString() : "?") + (reg.SizeX.HasValue ? "  " + reg.SizeX + "x" + reg.SizeY + " m" : ""));
        if (a.Kind == ArchiveKind.Oar)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  linksets: {0}   parts: {1}   prim inventory items: {2}   parcels: {3}   boxed objects: {4} (holding {5} linksets, {6} parts, {7} items)",
                r.LinksetCount, r.PartCount, r.PrimItemCount, r.ParcelCount, r.BoxedObjectCount, r.BoxedLinksetCount, r.BoxedPartCount, r.BoxedItemCount));
        else
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  folders: {0}   items: {1}   boxed objects: {2} (holding {3} linksets, {4} parts, {5} items)",
                r.FolderCount, r.IarItemCount, r.BoxedObjectCount, r.BoxedLinksetCount, r.BoxedPartCount, r.BoxedItemCount));
        sb.AppendLine();

        sb.AppendLine("CREATORS (" + r.Creators.Count + ")");
        sb.AppendLine("  " + "id".PadRight(36) + "  " + "name".PadRight(28) + "   parts linksets   items boxed  first       last");
        foreach (PartyTotals c in r.Creators.Take(top))
        {
            string label = c.IsLibrary ? Who(c) + " [library]" : Who(c);
            string data = c.Records > 0 && c.WithCreatorData == 0 ? "no creator data" : (c.WithCreatorData < c.Records ? "creator data on " + c.WithCreatorData : "creator data");
            sb.AppendLine("  " + c.Id.ToString().PadRight(36) + "  " + Trim(label, 28).PadRight(28) +
                string.Format(CultureInfo.InvariantCulture, " {0,7} {1,8} {2,7} {3,5}  {4} {5}  {6}", c.Parts, c.Linksets, c.Items, c.BoxedObjects, Date(c.EarliestDate), Date(c.LatestDate), data));
        }
        if (r.Creators.Count > top)
            sb.AppendLine("  ... " + (r.Creators.Count - top) + " more (use --all)");
        if (r.BlankCreatorRecords > 0)
            sb.AppendLine("  " + r.BlankCreatorRecords + " records have a blank creator (counted separately, left out of the shares)");
        sb.AppendLine();

        sb.AppendLine("OWNERS (" + r.Owners.Count + ")");
        foreach (PartyTotals o in r.Owners.Take(top))
            sb.AppendLine("  " + o.Id.ToString().PadRight(36) + string.Format(CultureInfo.InvariantCulture, " {0,7} parts {1,7} items  {2} {3}", o.Parts, o.Items, Date(o.EarliestDate), Date(o.LatestDate)));
        if (r.Owners.Count > top)
            sb.AppendLine("  ... " + (r.Owners.Count - top) + " more (use --all)");
        sb.AppendLine();

        if (a.Kind == ArchiveKind.Iar)
        {
            sb.AppendLine("FOLDERS (" + r.FolderCount + ")");
            foreach (FolderRecord f in r.Folders.Take(top))
                sb.AppendLine("  " + S(f.Path) + "  (" + f.ItemCount + " items)");
            if (r.Folders.Count > top)
                sb.AppendLine("  ... " + (r.Folders.Count - top) + " more (use --all)");
        }
        else
        {
            sb.AppendLine("LINKSETS (" + r.LinksetCount + ")");
            foreach (LinksetRecord l in r.Linksets.Take(top))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0}  {1}  region {2}  at <{3:0},{4:0},{5:0}>  {6} prims", l.LinksetId, Trim(S(l.Name), 32), l.RegionDir.Length > 0 ? S(l.RegionDir) : (l.RegionId?.ToString() ?? "-"), l.X, l.Y, l.Z, l.PrimCount));
            if (r.Linksets.Count > top)
                sb.AppendLine("  ... " + (r.LinksetCount - top) + " more (use --all)");
        }
        sb.AppendLine();

        sb.AppendLine("ASSETS (" + r.AssetCount + ")");
        foreach (AssetTypeSummary t in r.AssetsByType)
            sb.AppendLine("  " + S(t.TypeName).PadRight(14) + t.Count.ToString().PadLeft(8) + "  " + Size(t.Bytes));
        sb.AppendLine("  referenced but missing: " + r.MissingAssetCount + (a.AssetsIncluded == false ? "  (archive saved without assets, so none are expected)" : ""));
        foreach (UUID id in r.MissingAssets.Take(top))
            sb.AppendLine("    missing " + id);
        sb.AppendLine("  present but unreferenced: " + r.UnreferencedAssetCount);
        foreach (UUID id in r.UnreferencedAssets.Take(top))
            sb.AppendLine("    unreferenced " + id);
        sb.AppendLine();

        sb.AppendLine("ASSET REFERENCES WITH NO CREATOR OF THEIR OWN (" + r.AssetsWithoutCreator.Count + ")");
        foreach (AssetUse u in r.AssetsWithoutCreator.Take(top))
        {
            string users = string.Join(", ", u.Users.Take(4).Select(x => (x.CreatorName.Length > 0 ? S(x.CreatorName) + " " : "") + (x.CreatorId != UUID.Zero ? x.CreatorId.ToString() : "(no creator id)") + " x" + x.Uses));
            sb.AppendLine("  " + u.AssetId + " " + S(u.TypeName) + (u.Present ? "" : " (not in archive)") + (u.Uncertain ? " (id found in text)" : "") + "  used " + u.Uses + "x by " + users);
        }
        if (r.AssetsWithoutCreator.Count > top)
            sb.AppendLine("  ... " + (r.AssetsWithoutCreator.Count - top) + " more (use --all)");
        sb.AppendLine();

        if (a.Kind == ArchiveKind.Oar)
        {
            sb.AppendLine("PARCELS (" + r.ParcelCount + ")");
            foreach (ParcelRecord p in r.Parcels.Take(top))
                sb.AppendLine("  " + Trim(S(p.Name), 32) + "  owner " + p.OwnerId + "  group " + p.GroupId + (p.IsGroupOwned ? "  group-owned" : ""));
            foreach (RegionSettingsRecord s in r.RegionSettings)
                sb.AppendLine("  region settings " + S(s.RegionName) + ": terrain textures " + string.Join(", ", s.TerrainTextures));
            sb.AppendLine();
        }

        sb.AppendLine("LEGACY PAYLOADS (never decoded)");
        if (r.LegacyPayloads.Count == 0)
            sb.AppendLine("  none found");
        foreach (LegacySummary l in r.LegacyPayloads)
            sb.AppendLine("  " + S(l.Kind) + ": " + l.Count + " (" + Size(l.Bytes) + ")");
        if (r.ScriptStateBlobs > 0)
            sb.AppendLine("  script state blobs present in object XML (not read): " + r.ScriptStateBlobs);
        sb.AppendLine();

        if (r.Problems.Count > 0)
        {
            sb.AppendLine("PROBLEMS AND NOTES (" + r.ProblemCount + ")");
            foreach (ProblemRecord p in r.Problems.Take(top))
                sb.AppendLine("  " + (p.Entry != null ? S(p.Entry) + ": " : "") + S(p.Message));
            sb.AppendLine();
        }

        sb.AppendLine("WHAT A LOADER WOULD DO");
        foreach (string line in r.LoaderWarnings)
            sb.AppendLine("  " + line);
        sb.AppendLine("  " + r.EditNote);

        if (r.Stats != null)
        {
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "STATS  wall time {0:0.00} s, bytes read {1}, peak working set {2}", r.Stats.WallSeconds, r.Stats.BytesRead, Size(r.Stats.PeakWorkingSetBytes)));
        }
        return sb.ToString();
    }

    private const int ExamplesShown = 20;

    /// <summary>The checks' findings, Warnings first, each with up to 20 example records. The creator analysis prints its own section.</summary>
    private static void AppendChecks(StringBuilder sb, ArchiveReport r)
    {
        List<Finding> shown = r.Findings.Where(f => f.Source != "creator-analysis").ToList();
        sb.AppendLine("CHECKS");
        if (shown.Count == 0)
        {
            sb.AppendLine("  no findings");
            sb.AppendLine();
            return;
        }

        foreach (FindingSeverity sev in new[] { FindingSeverity.Warning, FindingSeverity.Notice, FindingSeverity.Info })
        {
            List<Finding> group = shown.Where(f => f.Severity == sev).ToList();
            if (group.Count == 0)
                continue;
            sb.AppendLine("  " + sev.ToString().ToUpperInvariant() + " (" + group.Count + ")");
            foreach (Finding f in group)
            {
                sb.AppendLine("    " + S(f.Code) + ": " + S(f.Title));
                sb.AppendLine("      " + S(f.Detail));
                foreach (KeyValuePair<string, string> kv in f.Numbers.Take(12))
                    sb.AppendLine("      " + S(kv.Key) + ": " + S(kv.Value));
                if (f.Numbers.Count > 12)
                    sb.AppendLine("      ... " + (f.Numbers.Count - 12) + " more numbers in the JSON");
                foreach (FindingExample e in f.Examples.Take(ExamplesShown))
                    sb.AppendLine("      - " + ExampleLine(e));
                long more = Math.Max(f.Count, f.Examples.Count) - Math.Min(f.Examples.Count, ExamplesShown);
                if (f.Examples.Count > 0 && more > 0)
                    sb.AppendLine("      ... " + more.ToString("N0", CultureInfo.InvariantCulture) + " more" + (f.ExamplesTruncated ? " (the JSON lists " + f.Examples.Count.ToString("N0", CultureInfo.InvariantCulture) + ")" : " in the JSON"));
            }
        }
        sb.AppendLine();
    }

    private static string ExampleLine(FindingExample e)
    {
        List<string> bits = new List<string>();
        if (!string.IsNullOrEmpty(e.Name)) bits.Add(Trim(S(e.Name), 70));
        if (!string.IsNullOrEmpty(e.Id)) bits.Add(S(e.Id));
        if (!string.IsNullOrEmpty(e.Where)) bits.Add("at " + Trim(S(e.Where), 110));
        if (!string.IsNullOrEmpty(e.Note)) bits.Add("(" + Trim(S(e.Note), 260) + ")");
        return string.Join("  ", bits);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "...";
}
