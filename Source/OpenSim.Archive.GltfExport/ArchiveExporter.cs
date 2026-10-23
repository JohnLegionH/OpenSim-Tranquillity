/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// Reads an OAR with the archive inspector's reader (so its limits and damage handling apply) and writes one folder
/// per linkset: the glTF binary and its manifest. The archive is only read.
///
/// The archive is read once: the pass parses every object file and keeps the asset bodies the builds name (a saved
/// archive holds the objects first, so the names are known when the assets come by). An asset that came by before
/// anything named it, such as a texture a material names, is fetched by a short second pass for just those ids.
/// Bodies over a small size go to a cache folder and are read back when a build needs them, so memory does not grow with
/// the archive. A build that fails is recorded and the rest go on. Each finished build is journaled, so a rerun into the
/// same folder leaves the builds that are done (same object file, same tool version) and does the rest.
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

    private static readonly JsonSerializerOptions JournalOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = false };

    /// <summary>One line per finished build, appended as each is written.</summary>
    public const string JournalName = "export-progress.jsonl";

    public static string ToolVersion { get; } =
        typeof(ArchiveExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>Called before each build is written; the tests use it to make a build fail.</summary>
    internal static Action<ParsedGroup> BeforeBuild;

    private sealed class Pending
    {
        public ParsedGroup Group;
        public string Entry;
        public string SourceHash;
        public int Occurrence;
        public SummaryBuild Resumed;
    }

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
        ExportSummary summary = new ExportSummary { Archive = archiveName, ToolVersion = ToolVersion };
        HashSet<string> usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stopwatch clock = Stopwatch.StartNew();
        Action<string> say = options.Progress ?? (_ => { });

        // what an earlier run into this folder finished
        string journalPath = Path.Combine(fullOut, JournalName);
        Dictionary<string, SummaryBuild> done = options.Force ? new Dictionary<string, SummaryBuild>() : ReadJournal(journalPath, fullOut);
        if (options.Force)
            File.WriteAllText(journalPath, string.Empty, new UTF8Encoding(false));

        ArchiveAssets assets = new ArchiveAssets
        {
            CacheDirectory = string.IsNullOrEmpty(options.CacheDirectory) ? Path.Combine(fullOut, ".asset-cache") : Path.GetFullPath(options.CacheDirectory)
        };
        if (options.MaxAssetMegabytes > 0)
            assets.MaxBytes = options.MaxAssetMegabytes * 1024L * 1024;

        List<Pending> groups = new List<Pending>();
        Dictionary<string, int> occurrences = new Dictionary<string, int>();
        int objectsRead = 0;
        long lastNote = clock.ElapsedMilliseconds;

        // The one pass over the archive: objects, and the asset bodies they name.
        InspectorOptions inspect = new InspectorOptions { StandardChecks = false, MaxRetainedRecords = 1, HashObjectBodies = true };
        inspect.ObjectObserver = (obj, entry, regionDir) =>
        {
            objectsRead++;
            if (clock.ElapsedMilliseconds - lastNote > 10_000)
            {
                lastNote = clock.ElapsedMilliseconds;
                say(string.Format(CultureInfo.InvariantCulture, "reading the archive: {0} object files, {1:0} s", objectsRead, clock.Elapsed.TotalSeconds));
            }
            foreach (ParsedGroup group in obj.Groups)
            {
                if (group.Parts.Count == 0)
                    continue;
                if (!Matches(group, options.Object))
                    continue;
                if (options.Limit > 0 && groups.Count >= options.Limit)
                    return;

                string idKey = group.RootId.ToString() + "|" + obj.BodyHash;
                occurrences.TryGetValue(idKey, out int occurrence);
                occurrences[idKey] = occurrence + 1;
                Pending p = new Pending { Group = group, Entry = entry, SourceHash = obj.BodyHash, Occurrence = occurrence };
                if (done.TryGetValue(idKey + "|" + occurrence.ToString(CultureInfo.InvariantCulture), out SummaryBuild row) && row.ToolVersion == ToolVersion)
                    p.Resumed = row;
                groups.Add(p);
                // a build that is already written needs none of its assets
                if (p.Resumed == null)
                    foreach (ParsedPart part in group.Parts)
                        FaceMaterials.CollectAssetIds(part, assets.Wanted);
            }
        };
        inspect.WantAssetBody = (id, type, size) => assets.Wants(id, type, size);
        inspect.AssetBodyObserver = (id, type, entry, bytes) =>
        {
            assets.Add(id, type, entry, bytes);
            Discover(assets, type, bytes);
        };

        summary.ArchivePasses = 1;
        ArchiveReport report = new ArchiveInspector(inspect).Inspect(fullArchive);
        summary.ReadStatus = report.Status.ToString();
        summary.StoppedAt = report.StoppedAt;
        if (report.Status != InspectionStatus.NotAnArchive && report.Archive.Kind == ArchiveKind.Iar)
            throw new InvalidDataException("the file is an inventory archive (IAR); only region archives (OAR) can be exported");

        // Assets that came by before anything named them (a texture of a material, say): one more pass for exactly those.
        for (int round = 0; round < 4; round++)
        {
            HashSet<UUID> todo = assets.Wanted.Where(id => assets.Seen.Contains(id) && !assets.Has(id) && !assets.OverLimit.Contains(id)).ToHashSet();
            if (todo.Count == 0)
                break;
            say(string.Format(CultureInfo.InvariantCulture, "going back to the archive for {0} assets that came by before they were named", todo.Count));
            InspectorOptions again = new InspectorOptions { StandardChecks = false, MaxRetainedRecords = 1 };
            again.WantAssetBody = (id, type, size) => todo.Contains(id) && !assets.Has(id);
            again.AssetBodyObserver = (id, type, entry, bytes) =>
            {
                assets.Add(id, type, entry, bytes);
                Discover(assets, type, bytes);
            };
            summary.ArchivePasses++;
            new ArchiveInspector(again).Inspect(fullArchive);
        }

        // The builds.
        List<SummaryBuild> rows = new List<SummaryBuild>();
        int total = groups.Count, finished = 0, written = 0;
        int every = options.ProgressEvery > 0 ? options.ProgressEvery : 25;
        Stopwatch writing = Stopwatch.StartNew();
        long lastReport = writing.ElapsedMilliseconds;
        int fresh = groups.Count(g => g.Resumed == null);
        say(string.Format(CultureInfo.InvariantCulture, "{0} builds to do ({1} already written by an earlier run); read in {2:0.0} s", fresh, total - fresh, clock.Elapsed.TotalSeconds));

        // folders that builds from an earlier run already hold are not given to a new one
        foreach (Pending kept in groups)
            if (kept.Resumed != null)
                usedFolders.Add(kept.Resumed.Folder);

        foreach (Pending p in groups)
        {
            SummaryBuild row;
            if (p.Resumed != null)
            {
                row = p.Resumed;
                summary.BuildsResumed++;
            }
            else
            {
                try
                {
                    BeforeBuild?.Invoke(p.Group);
                    row = WriteBuild(p.Group, archiveName, p.Entry, fullOut, usedFolders, options.World, assets);
                }
                catch (Exception e)
                {
                    row = new SummaryBuild
                    {
                        Status = "failed",
                        Error = Sanitize.Text(e.GetType().Name + ": " + e.Message),
                        Name = Text.Clean(p.Group.Name),
                        RootPartUuid = p.Group.RootId.ToString(),
                        Parts = p.Group.Parts.Count
                    };
                    say("build failed, continuing: " + row.Name + " (" + row.RootPartUuid + "): " + row.Error);
                }
                row.SourceHash = p.SourceHash;
                row.ToolVersion = ToolVersion;
                row.ArchiveEntry = Text.Clean(p.Entry);
                row.Occurrence = p.Occurrence;
                File.AppendAllText(journalPath, JsonSerializer.Serialize(row, JournalOptions) + "\n", new UTF8Encoding(false));
                written++;
            }
            rows.Add(row);
            finished++;

            if (written > 0 && p.Resumed == null && (finished % every == 0 || writing.ElapsedMilliseconds - lastReport >= 10_000 || finished == total))
            {
                lastReport = writing.ElapsedMilliseconds;
                double perBuild = writing.Elapsed.TotalSeconds / written;
                double left = perBuild * Math.Max(0, fresh - written);
                say(string.Format(CultureInfo.InvariantCulture, "{0}/{1} builds ({2:0.0}%), elapsed {3}, about {4} left",
                    finished, total, 100.0 * finished / Math.Max(1, total), Clock(clock.Elapsed), Clock(TimeSpan.FromSeconds(left))));
            }
        }

        Total(summary, rows);
        clock.Stop();
        summary.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2);
        summary.CacheBytesWritten = assets.CacheBytesWritten;
        using (Process me = Process.GetCurrentProcess())
            summary.PeakWorkingSetMegabytes = Math.Round(me.PeakWorkingSet64 / 1048576.0, 1);
        File.WriteAllText(Path.Combine(fullOut, "export-summary.json"), JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        return summary;
    }

    private static string Clock(TimeSpan t) => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);

    /// <summary>The builds an earlier run finished, by root part, source hash and occurrence; a build counts only when its files are still there.</summary>
    private static Dictionary<string, SummaryBuild> ReadJournal(string path, string outDir)
    {
        Dictionary<string, SummaryBuild> done = new Dictionary<string, SummaryBuild>();
        if (!File.Exists(path))
            return done;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;
            SummaryBuild row;
            try
            {
                row = JsonSerializer.Deserialize<SummaryBuild>(line, JournalOptions);
            }
            catch (JsonException)
            {
                continue;   // a line cut short by a stop in the middle of writing it
            }
            if (row == null || row.Status != "ok" || string.IsNullOrEmpty(row.SourceHash) || string.IsNullOrEmpty(row.Folder))
                continue;
            string dir = Path.Combine(outDir, row.Folder);
            if (!File.Exists(Path.Combine(dir, "manifest.json")) || (row.File != null && !File.Exists(Path.Combine(dir, row.File))))
                continue;
            done[row.RootPartUuid + "|" + row.SourceHash + "|" + row.Occurrence.ToString(CultureInfo.InvariantCulture)] = row;
        }
        return done;
    }

    /// <summary>What a kept asset names in turn: a legacy material's normal and specular maps, a PBR material's textures.</summary>
    private static void Discover(ArchiveAssets assets, int type, byte[] bytes)
    {
        if (bytes == null)
            return;
        try
        {
            if (type == (int)OpenMetaverse.AssetType.OSMaterial)
            {
                LegacyMaterial m = LegacyMaterial.Parse(bytes);
                if (m != null)
                {
                    if (m.NormalMap != UUID.Zero) assets.Wanted.Add(m.NormalMap);
                    if (m.SpecularMap != UUID.Zero) assets.Wanted.Add(m.SpecularMap);
                }
            }
            else if (type == (int)OpenMetaverse.AssetType.Material)
            {
                PbrMaterial pbr = PbrMaterial.Parse(bytes);
                if (pbr != null)
                    foreach (UUID id in pbr.All)
                        assets.Wanted.Add(id);
            }
        }
        catch (Exception)
        {
            // a damaged material names nothing more
        }
    }

    private static bool Matches(ParsedGroup g, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return true;
        if (Guid.TryParse(filter, out Guid id))
            return g.Parts.Any(p => p.Part.PartId.Guid == id && p.Part.IsRoot) || g.RootId.Guid == id;
        return string.Equals(g.Name, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static SummaryBuild WriteBuild(ParsedGroup group, string archiveName, string entry, string outDir, HashSet<string> usedFolders, bool world, ArchiveAssets assets)
    {
        string baseName = FileSafe(group.Name);
        string idPart = group.RootId.ToString().Replace("-", string.Empty);
        string folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length));
        for (int n = 2; !usedFolders.Add(folder); n++)
            folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length)) + "-" + n;

        string glbName = folder + ".glb";
        BuiltBuild build = LinksetExporter.Build(group, archiveName, entry, glbName, world, assets);

        string dir = Path.Combine(outDir, folder);
        Directory.CreateDirectory(dir);
        long bytes = 0;
        if (build.Glb != null)
        {
            File.WriteAllBytes(Path.Combine(dir, glbName), build.Glb);
            bytes += build.Glb.Length;
        }
        foreach (AssetBlob blob in build.Copies)
        {
            string copyDir = Path.Combine(dir, blob.Folder);
            Directory.CreateDirectory(copyDir);
            File.WriteAllBytes(Path.Combine(copyDir, blob.Id + blob.Extension), blob.Bytes);
            bytes += blob.Bytes.Length;
        }
        string manifest = JsonSerializer.Serialize(build.Manifest, JsonOptions);
        byte[] manifestBytes = new UTF8Encoding(false).GetBytes(manifest);
        // the manifest goes last: a build whose manifest is there was written completely
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), manifestBytes);
        bytes += manifestBytes.Length;

        BuildManifest m = build.Manifest;
        SummaryBuild row = new SummaryBuild
        {
            Folder = folder,
            Name = m.Name,
            RootPartUuid = m.RootPartUuid,
            Parts = m.PartCount,
            Exported = m.Exported,
            Placeheld = m.Placeheld,
            Skipped = m.Skipped,
            TexturesEmbedded = m.TexturesEmbedded,
            TexturesMissing = m.TexturesMissing,
            TexturesCopied = m.TexturesCopied,
            AssetsCopied = m.AssetsCopied,
            OutputBytes = bytes,
            File = build.Glb != null ? glbName : null,
            Reasons = new Dictionary<string, int>()
        };
        foreach (ManifestPart p in m.Parts)
        {
            if (p.Status == ExportStatus.Exported && p.Kind == PartKinds.Sculpt)
                row.SculptsExported++;
            if (p.Status == ExportStatus.Exported && p.Kind == PartKinds.Mesh)
            {
                row.MeshesExported++;
                if (p.Rigged == true)
                    row.RiggedMeshes++;
            }
            if (p.Status != ExportStatus.Exported)
            {
                string key = p.Status + ": " + (p.Reason ?? string.Empty);
                // the sculpt and mesh reasons name an asset; count them by kind instead
                if (p.Status == ExportStatus.Placeheld && (p.Kind == PartKinds.Sculpt || p.Kind == PartKinds.Mesh))
                    key = p.Status + ": " + p.Kind + " - " + (p.GeometryAssetStatus is null or "copied" ? "geometry could not be built" : p.GeometryAssetStatus);
                row.Reasons[key] = row.Reasons.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        }
        return row;
    }

    /// <summary>The summary's totals, from the builds' rows (the same sum whether a row is new or kept from an earlier run).</summary>
    private static void Total(ExportSummary summary, List<SummaryBuild> rows)
    {
        foreach (SummaryBuild row in rows)
        {
            summary.Builds++;
            summary.BuildList.Add(row);
            if (row.Status != "ok")
            {
                summary.BuildsFailed++;
                continue;
            }
            if (row.File != null)
                summary.BuildsWithAFile++;
            summary.Parts += row.Parts;
            summary.Exported += row.Exported;
            summary.Placeheld += row.Placeheld;
            summary.Skipped += row.Skipped;
            summary.TexturesEmbedded += row.TexturesEmbedded;
            summary.TexturesMissing += row.TexturesMissing;
            summary.TexturesCopied += row.TexturesCopied;
            summary.AssetsCopied += row.AssetsCopied;
            summary.SculptsExported += row.SculptsExported;
            summary.MeshesExported += row.MeshesExported;
            summary.RiggedMeshes += row.RiggedMeshes;
            summary.OutputBytes += row.OutputBytes;
            if (row.Reasons != null)
                foreach (KeyValuePair<string, int> r in row.Reasons)
                    summary.Reasons[r.Key] = summary.Reasons.TryGetValue(r.Key, out int c) ? c + r.Value : r.Value;
        }
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
