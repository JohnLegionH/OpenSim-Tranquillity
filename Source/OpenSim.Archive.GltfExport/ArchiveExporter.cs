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
        NewLine = "\n",   // the same bytes on Windows and Linux
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
        // before anything is read or written: a missing image library would otherwise turn every texture into a placeholder
        string imageProblem = (options.ImageCheck ?? ImageSelfCheck.Problem)();
        if (imageProblem != null)
            throw new ImageLibraryException(imageProblem);
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
        Dictionary<string, SummaryBuild> done = options.Force ? new Dictionary<string, SummaryBuild>() : ReadJournal(journalPath, fullOut, options.SelfContained ? "self-contained" : "shared");
        if (options.Force)
            File.WriteAllText(journalPath, string.Empty, new UTF8Encoding(false));

        ArchiveAssets assets = new ArchiveAssets
        {
            CacheDirectory = string.IsNullOrEmpty(options.CacheDirectory) ? Path.Combine(fullOut, ".asset-cache") : Path.GetFullPath(options.CacheDirectory)
        };
        assets.WriteFile = options.WriteCacheFile;
        assets.MaxMemoryBytes = MaxMemoryMegabytesFor(options) * 1024L * 1024;
        if (options.MaxCacheMegabytes > 0)
            assets.MaxBytes = options.MaxCacheMegabytes * 1024L * 1024;
        summary.Layout = options.SelfContained ? "self-contained" : "shared";
        bool shared = !options.SelfContained;
        Func<string, long?> freeBytes = options.FreeBytes ?? FreeBytesOnDrive;
        long floor = options.MinFreeBytes > 0 ? options.MinFreeBytes : MinFreeBytes;

        // The cache holds, at most, the asset bodies of the archive: say so if its drive looks too small. A warning only;
        // the run itself stops cleanly when the free space falls below MinFreeBytes.
        {
            long archiveBytes = new FileInfo(fullArchive).Length;
            string cacheRoot = assets.CacheDirectory;
            long? free = freeBytes(cacheRoot);
            if (free.HasValue && free.Value < archiveBytes * 2)
                Warn(summary, say, string.Format(CultureInfo.InvariantCulture,
                    "LOW DISK SPACE WARNING: the asset cache folder {0} has {1:N0} MB free and about {2:N0} MB are needed to read this archive. Starting anyway; give --cache-dir a folder on a drive with more room if it stops.",
                    cacheRoot, free.Value / 1048576, archiveBytes * 2 / 1048576));
        }

        List<Pending> groups = new List<Pending>();
        Dictionary<string, int> occurrences = new Dictionary<string, int>();
        int objectsRead = 0;
        long lastNote = clock.ElapsedMilliseconds;
        List<SummaryBuild> rows = new List<SummaryBuild>();
        HashSet<string> sharedWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HeapGovernor governor = null;
        ExportDiagnostics diagnostics = string.IsNullOrEmpty(options.DiagnosticsPath) ? null : new ExportDiagnostics(Path.GetFullPath(options.DiagnosticsPath), options.DiagnosticsEvery, options.DiagnosticsCollect);
        Action<string, int> sample = (phase, count) =>
            diagnostics?.Write(phase, count, new[]
            {
                new KeyValuePair<string, long>("buildsHeld", groups.Count),
                new KeyValuePair<string, long>("partsHeld", groups.Sum(g => (long)g.Group.Parts.Count)),
                new KeyValuePair<string, long>("occurrenceEntries", occurrences.Count),
                new KeyValuePair<string, long>("journalEntriesHeld", done.Count),
                new KeyValuePair<string, long>("summaryRowsHeld", rows.Count),
                new KeyValuePair<string, long>("usedFolderNames", usedFolders.Count),
                new KeyValuePair<string, long>("sharedFileNames", sharedWritten.Count),
                new KeyValuePair<string, long>("governorCollections", governor?.Collections ?? 0)
            }.Concat(assets.Sizes()));

        // The one pass over the archive: objects, and the asset bodies they name.
        InspectorOptions inspect = new InspectorOptions { StandardChecks = false, MaxRetainedRecords = 1, HashObjectBodies = true };
        inspect.ObjectObserver = (obj, entry, regionDir) =>
        {
            objectsRead++;
            if (diagnostics != null && diagnostics.Due("read", objectsRead))
                sample("read", objectsRead);
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
                if (done.TryGetValue(idKey + "|" + occurrence.ToString(CultureInfo.InvariantCulture), out SummaryBuild row) && row.ToolVersion == (options.Clean ? ToolVersion : ToolVersion + " (not cleaned)"))
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
            HashSet<UUID> todo = assets.Wanted.Where(id => assets.Seen.Contains(id) && !assets.Has(id) && !assets.OverLimit.ContainsKey(id)).ToHashSet();
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

        if (report.Status == InspectionStatus.Partial)
        {
            summary.ArchiveIncomplete = true;
            say("ARCHIVE INCOMPLETE: the archive is damaged or cut short" + (string.IsNullOrEmpty(report.StoppedAt) ? string.Empty : " (reading stopped at " + Sanitize.Text(report.StoppedAt) + ")") + "; everything readable is being exported");
        }
        assets.CloseCapEvents();
        foreach (string cap in assets.CapEvents)
            say(cap);

        // Will the output fit? A warning only: the estimate can be wrong either way.
        {
            long estimate = EstimateOutputBytes(groups.Where(g => g.Resumed == null).Select(g => g.Group), assets, shared);
            long? free = freeBytes(fullOut);
            if (free.HasValue && free.Value < estimate + estimate / 10)
                Warn(summary, say, string.Format(CultureInfo.InvariantCulture,
                    "LOW DISK SPACE WARNING: the output folder {0} has {1:N0} MB free and this export is estimated at {2:N0} MB. Starting anyway; the run stops cleanly if the free space falls below {3:N0} MB, and running the same line again carries on.",
                    fullOut, free.Value / 1048576, estimate / 1048576, floor / 1048576));
        }

        // The builds.
        int memoryFailures = 0;
        long peakHeapBytes = 0;
        int total = groups.Count, finished = 0, written = 0;
        governor = new HeapGovernor(assets.MaxMemoryBytes);
        int every = options.ProgressEvery > 0 ? options.ProgressEvery : 25;
        Stopwatch writing = Stopwatch.StartNew();
        long lastReport = writing.ElapsedMilliseconds;
        int fresh = groups.Count(g => g.Resumed == null);
        say(string.Format(CultureInfo.InvariantCulture, "{0} builds to do ({1} already written by an earlier run); read in {2:0.0} s", fresh, total - fresh, clock.Elapsed.TotalSeconds));

        // folders that builds from an earlier run already hold are not given to a new one
        foreach (Pending kept in groups)
            if (kept.Resumed != null)
                usedFolders.Add(kept.Resumed.Folder);

        // the disk filled up while the archive was being read into the cache folder: nothing can be built from what is missing
        string diskFullWhere = assets.DiskFullMessage == null ? null : "the asset cache folder " + assets.CacheDirectory + " (" + assets.DiskFullMessage + ")";

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
                // Stop cleanly between builds when the output drive is nearly full; a rerun carries on from the journal.
                long? freeNow = freeBytes(fullOut);
                if (diskFullWhere != null)
                {
                    StopForFullDisk(summary, say, groups.Count - finished, written, diskFullWhere);
                    break;
                }
                if (freeNow.HasValue && freeNow.Value < floor)
                {
                    summary.BuildsNotDone = groups.Count - finished;
                    summary.DiskSpaceStop = string.Format(CultureInfo.InvariantCulture,
                        "STOPPED: LOW DISK SPACE. The output drive has {0:N0} MB free, below the {1:N0} MB this program keeps free. {2} builds were written in this run and {3} are not done. Free some space and run the same line again: it carries on where it stopped.",
                        freeNow.Value / 1048576, floor / 1048576, written, summary.BuildsNotDone);
                    say(summary.DiskSpaceStop);
                    break;
                }
                try
                {
                    BeforeBuild?.Invoke(p.Group);
                    row = WriteBuild(p.Group, archiveName, p.Entry, fullOut, usedFolders, options.World, assets, shared, sharedWritten, options.Clean);
                }
                catch (Exception e) when (DiskFull.Is(e))
                {
                    diskFullWhere = "the output folder " + fullOut + " (" + e.Message + ")";
                    StopForFullDisk(summary, say, groups.Count - finished, written, diskFullWhere);
                    break;   // not journaled: a rerun writes this build again
                }
                catch (Exception e)
                {
                    if (e is OutOfMemoryException)
                        memoryFailures++;
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
                row.ToolVersion = options.Clean ? ToolVersion : ToolVersion + " (not cleaned)";
                row.ArchiveEntry = Text.Clean(p.Entry);
                row.Occurrence = p.Occurrence;
                try
                {
                    File.AppendAllText(journalPath, JsonSerializer.Serialize(row, JournalOptions) + "\n", new UTF8Encoding(false));
                }
                catch (IOException e) when (DiskFull.Is(e))
                {
                    diskFullWhere = "the output folder " + fullOut + " (" + e.Message + ")";
                    StopForFullDisk(summary, say, groups.Count - finished, written, diskFullWhere);
                    break;   // the build is on disk but not journaled: a rerun writes it again
                }
                written++;
            }
            rows.Add(row);
            finished++;
            peakHeapBytes = Math.Max(peakHeapBytes, Math.Max(GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes));
            governor.AfterBuild();
            if (diagnostics != null && (diagnostics.Due("build", finished) || finished == total))
                sample("build", finished);

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
        if (shared)
        {
            summary.SharedBytes = FolderBytes(Path.Combine(fullOut, "textures")) + FolderBytes(Path.Combine(fullOut, "assets"));
            summary.OutputBytes += summary.SharedBytes;
        }
        summary.CapsReached.AddRange(assets.CapEvents);
        if (memoryFailures > 0)
            summary.CapsReached.Add("MEMORY CAP REACHED (--max-memory-mb): " + memoryFailures + " build(s) ran out of memory and are listed as failed. Run again with a larger --max-memory-mb.");
        summary.ExitCode = ExitCodeOf(summary);
        clock.Stop();
        summary.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2);
        summary.CacheBytesWritten = assets.CacheBytesWritten;
        using (Process me = Process.GetCurrentProcess())
            summary.PeakWorkingSetMegabytes = Math.Round(me.PeakWorkingSet64 / 1048576.0, 1);
        summary.PeakManagedHeapMegabytes = Math.Round(peakHeapBytes / 1048576.0, 1);
        try
        {
            File.WriteAllText(Path.Combine(fullOut, "export-summary.json"), JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fullOut, "export-summary.txt"), SummaryText.Build(summary), new UTF8Encoding(false));
        }
        catch (IOException e) when (DiskFull.Is(e))
        {
            say("the summary files could not be written because the disk is full; the summary is printed instead");
        }
        return summary;
    }

    /// <summary>The most memory the export aims to stay within when the caller names none, in megabytes.</summary>
    public const int DefaultMaxMemoryLimitMegabytes = 2048;

    /// <summary>The cap used when the machine's memory size cannot be read: the value earlier releases used for every machine.</summary>
    public const int UnknownMemoryMaxMegabytes = 1024;

    /// <summary>
    /// The memory cap used when the caller names none: <see cref="DefaultMaxMemoryLimitMegabytes"/>, or half of the machine's
    /// memory if that is smaller. <paramref name="physicalMemoryBytes"/> of zero or less means the size is unknown.
    /// </summary>
    public static int DefaultMaxMemoryMegabytes(long physicalMemoryBytes)
    {
        if (physicalMemoryBytes <= 0)
            return UnknownMemoryMaxMegabytes;
        long half = physicalMemoryBytes / 2 / (1024 * 1024);
        return (int)Math.Max(1, Math.Min(DefaultMaxMemoryLimitMegabytes, half));
    }

    /// <summary>
    /// The memory cap for a run, in megabytes: the one named in the options, or the default for this machine
    /// (<see cref="ExportOptions.PhysicalMemoryBytes"/>, else the memory the runtime reports as available, which is a
    /// container's limit inside a container). Read it before the managed heap is limited, because after that the runtime
    /// reports the limit.
    /// </summary>
    public static int MaxMemoryMegabytesFor(ExportOptions options)
    {
        if (options.MaxMemoryMegabytes > 0)
            return options.MaxMemoryMegabytes;
        return DefaultMaxMemoryMegabytes(options.PhysicalMemoryBytes > 0 ? options.PhysicalMemoryBytes : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    /// <summary>The output drive is not written to once its free space is below this: 2 GB.</summary>
    public const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Records the clean stop for a full disk: the summary then leads with STOPPED: LOW DISK SPACE and the exit code is 5.</summary>
    private static void StopForFullDisk(ExportSummary summary, Action<string> say, int notDone, int written, string where)
    {
        summary.BuildsNotDone = notDone;
        summary.DiskSpaceStop = string.Format(CultureInfo.InvariantCulture,
            "STOPPED: LOW DISK SPACE. The disk is full: writing to {0} failed. {1} builds were written in this run and {2} are not done. Free some space and run the same line again: it carries on where it stopped.",
            where, written, notDone);
        say(summary.DiskSpaceStop);
    }

    private static void Warn(ExportSummary summary, Action<string> say, string text)
    {
        summary.Warnings.Add(text);
        say(text);
    }

    /// <summary>0 done; 2 not a region archive; 5 stopped for low disk space; 3 every build failed; 4 the archive was incomplete or a cap was reached.</summary>
    internal static int ExitCodeOf(ExportSummary s)
    {
        if (s.ReadStatus == nameof(InspectionStatus.NotAnArchive))
            return ExitCodes.Unreadable;
        if (!string.IsNullOrEmpty(s.DiskSpaceStop))
            return ExitCodes.LowDiskSpace;
        if (s.Builds > 0 && s.BuildsFailed == s.Builds)
            return ExitCodes.EveryBuildFailed;
        if (s.ArchiveIncomplete || s.CapsReached.Count > 0)
            return ExitCodes.Incomplete;
        return ExitCodes.Done;
    }

    private static long? FreeBytesOnDrive(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;   // a network path or an unknown drive: no way to ask, so no refusal
        }
    }

    private static long FolderBytes(string folder)
    {
        long total = 0;
        if (Directory.Exists(folder))
            foreach (string file in Directory.EnumerateFiles(folder))
                if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    total += new FileInfo(file).Length;
        return total;
    }

    /// <summary>
    /// What the builds still to do will write, in bytes, estimated from the asset sizes: a PNG takes about twelve times the
    /// JPEG 2000 data it was decoded from, a mesh or other asset is copied as it is, and each build adds about 200 KB of
    /// geometry and manifest. The shared layout writes each asset once, the self-contained layout once per build that uses it.
    /// </summary>
    internal static long EstimateOutputBytes(IEnumerable<ParsedGroup> groups, ArchiveAssets assets, bool shared)
    {
        const long PerBuild = 200 * 1024;
        const int PngFactor = 12;
        long total = 0;
        HashSet<UUID> counted = new HashSet<UUID>();
        foreach (ParsedGroup group in groups)
        {
            total += PerBuild;
            HashSet<UUID> ids = new HashSet<UUID>();
            foreach (ParsedPart part in group.Parts)
                FaceMaterials.CollectAssetIds(part, ids);
            foreach (UUID id in ids)
            {
                if (shared && !counted.Add(id))
                    continue;
                if (!assets.TryGetInfo(id, out int type, out long size))
                    continue;
                total += size + (type == (int)OpenMetaverse.AssetType.Texture ? size * PngFactor : 0);
            }
        }
        return total;
    }

    private static string Clock(TimeSpan t) => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);

    /// <summary>The builds an earlier run finished, by root part, source hash and occurrence; a build counts only when its files are still there.</summary>
    private static Dictionary<string, SummaryBuild> ReadJournal(string path, string outDir, string layout)
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
        // The last line for a build says how it stands now. One written in the other layout, or whose shared files are gone, is written again.
        foreach (string key in done.Where(d => d.Value.Layout != layout || (d.Value.SharedFiles != null && d.Value.SharedFiles.Any(f => !File.Exists(Path.Combine(outDir, f))))).Select(d => d.Key).ToList())
            done.Remove(key);
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

    private static SummaryBuild WriteBuild(ParsedGroup group, string archiveName, string entry, string outDir, HashSet<string> usedFolders, bool world, ArchiveAssets assets, bool shared, HashSet<string> sharedWritten, bool clean = true)
    {
        string baseName = FileSafe(group.Name);
        string idPart = group.RootId.ToString().Replace("-", string.Empty);
        string folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length));
        for (int n = 2; !usedFolders.Add(folder); n++)
            folder = baseName + "_" + idPart.Substring(0, Math.Min(12, idPart.Length)) + "-" + n;

        string glbName = folder + ".glb";
        BuiltBuild build;
        try
        {
            build = LinksetExporter.Build(group, archiveName, entry, glbName, world, assets, shared, clean);
        }
        catch (OutOfMemoryException)
        {
            // near the memory cap a build can fail only because the caches and a fragmented heap leave no room: drop the
            // caches, compact the heap and try the build once more. A second failure is the cap, reported as such.
            assets.ReleaseCaches();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            build = LinksetExporter.Build(group, archiveName, entry, glbName, world, assets, shared, clean);
        }

        string dir = Path.Combine(outDir, folder);
        Directory.CreateDirectory(dir);
        long bytes = 0;
        if (build.Glb != null)
        {
            File.WriteAllBytes(Path.Combine(dir, glbName), build.Glb);
            bytes += build.Glb.Length;
        }
        List<string> sharedFiles = shared ? new List<string>() : null;
        foreach (AssetBlob blob in build.Copies)
        {
            if (shared)
            {
                string rel = blob.Folder + "/" + blob.Id + blob.Extension;
                WriteShared(outDir, rel, blob.Bytes, sharedWritten);
                sharedFiles.Add(rel);
                continue;
            }
            string copyDir = Path.Combine(dir, blob.Folder);
            Directory.CreateDirectory(copyDir);
            File.WriteAllBytes(Path.Combine(copyDir, blob.Id + blob.Extension), blob.Bytes);
            bytes += blob.Bytes.Length;
        }
        foreach (KeyValuePair<UUID, byte[]> png in build.Pngs)
        {
            string rel = "textures/" + png.Key + ".png";
            WriteShared(outDir, rel, png.Value, sharedWritten);
            sharedFiles.Add(rel);
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
            Layout = shared ? "shared" : "self-contained",
            SharedFiles = sharedFiles,
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

    /// <summary>
    /// Writes a file of the shared textures or assets folder unless it is there already (a file is named by its asset id, so a
    /// file that is there holds the same content). It is written under a temporary name and moved into place, so a run that
    /// is stopped never leaves a half-written file under the real name.
    /// </summary>
    private static void WriteShared(string outDir, string relative, byte[] bytes, HashSet<string> written)
    {
        if (!written.Add(relative))
            return;
        string path = Path.Combine(outDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        FileInfo existing = new FileInfo(path);
        if (existing.Exists && existing.Length == bytes.Length)
            return;
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
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
