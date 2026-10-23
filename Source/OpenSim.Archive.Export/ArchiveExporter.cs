/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Export;

/// <summary>
/// Reads an OAR with the archive inspector's reader (so its limits and damage handling apply) and writes one folder
/// per linkset: the glTF binary and its manifest. The archive is only read.
/// </summary>
public static class ArchiveExporter
{
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static ExportSummary Export(string archivePath, ExportOptions options)
    {
        if (string.IsNullOrEmpty(options.OutputDirectory))
            throw new ArgumentException("an output folder is needed");
        string fullArchive = Path.GetFullPath(archivePath);
        string fullOut = Path.GetFullPath(options.OutputDirectory);
        if (string.Equals(fullArchive, fullOut, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the output folder is the archive itself");
        Directory.CreateDirectory(fullOut);

        string archiveName = Path.GetFileName(fullArchive);
        ExportSummary summary = new ExportSummary { Archive = archiveName };
        HashSet<string> usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stopwatch clock = Stopwatch.StartNew();

        InspectorOptions inspect = new InspectorOptions { StandardChecks = false, MaxRetainedRecords = 1 };
        inspect.ObjectObserver = (obj, entry, regionDir) =>
        {
            foreach (ParsedGroup group in obj.Groups)
            {
                if (group.Parts.Count == 0)
                    continue;
                if (!Matches(group, options.Object))
                    continue;
                if (options.Limit > 0 && summary.Builds >= options.Limit)
                    return;
                WriteBuild(group, archiveName, entry, fullOut, usedFolders, summary);
            }
        };

        ArchiveReport report = new ArchiveInspector(inspect).Inspect(fullArchive);
        summary.ReadStatus = report.Status.ToString();
        summary.StoppedAt = report.StoppedAt;
        if (report.Status != InspectionStatus.NotAnArchive && report.Archive.Kind == ArchiveKind.Iar)
            throw new InvalidDataException("the file is an inventory archive (IAR); only region archives (OAR) can be exported");

        clock.Stop();
        summary.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2);
        File.WriteAllText(Path.Combine(fullOut, "export-summary.json"), JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        return summary;
    }

    private static bool Matches(ParsedGroup g, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return true;
        if (Guid.TryParse(filter, out Guid id))
            return g.Parts.Any(p => p.Part.PartId.Guid == id && p.Part.IsRoot) || g.RootId.Guid == id;
        return string.Equals(g.Name, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteBuild(ParsedGroup group, string archiveName, string entry, string outDir, HashSet<string> usedFolders, ExportSummary summary)
    {
        string baseName = FileSafe(group.Name);
        string idPart = group.RootId.ToString().Replace("-", string.Empty);
        string folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length));
        for (int n = 2; !usedFolders.Add(folder); n++)
            folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length)) + "-" + n;

        string glbName = folder + ".glb";
        BuiltBuild build = LinksetExporter.Build(group, archiveName, entry, glbName);

        string dir = Path.Combine(outDir, folder);
        Directory.CreateDirectory(dir);
        long bytes = 0;
        if (build.Glb != null)
        {
            File.WriteAllBytes(Path.Combine(dir, glbName), build.Glb);
            bytes += build.Glb.Length;
        }
        string manifest = JsonSerializer.Serialize(build.Manifest, JsonOptions);
        byte[] manifestBytes = new UTF8Encoding(false).GetBytes(manifest);
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), manifestBytes);
        bytes += manifestBytes.Length;

        BuildManifest m = build.Manifest;
        summary.Builds++;
        if (build.Glb != null)
            summary.BuildsWithAFile++;
        summary.Parts += m.PartCount;
        summary.Exported += m.Exported;
        summary.Placeheld += m.Placeheld;
        summary.Skipped += m.Skipped;
        summary.OutputBytes += bytes;
        foreach (ManifestPart p in m.Parts)
            if (p.Status != ExportStatus.Exported)
            {
                string key = p.Status + ": " + (p.Reason ?? string.Empty);
                // the sculpt and mesh reasons name an asset; count them by kind instead
                if (p.Status == ExportStatus.Placeheld && (p.Kind == PartKinds.Sculpt || p.Kind == PartKinds.Mesh))
                    key = p.Status + ": " + p.Kind + " geometry is not exported yet";
                summary.Reasons[key] = summary.Reasons.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        summary.BuildList.Add(new SummaryBuild
        {
            Folder = folder,
            Name = m.Name,
            RootPartUuid = m.RootPartUuid,
            Parts = m.PartCount,
            Exported = m.Exported,
            Placeheld = m.Placeheld,
            Skipped = m.Skipped,
            File = build.Glb != null ? glbName : null
        });
    }

    private static string FileSafe(string name)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in Text.Clean(name) ?? string.Empty)
        {
            if (sb.Length >= 48)
                break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        }
        string s = sb.ToString().Trim('.', ' ');
        return s.Length == 0 ? "object" : s;
    }
}
