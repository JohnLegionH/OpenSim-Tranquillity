/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-export command: one glTF binary and one manifest per build in an OAR.</summary>
internal static class ExportCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-export <archive.oar> --out <folder> [--object <uuid|name>] [--limit N]
       [--self-contained] [--max-memory-mb N] [--max-cache-mb N] [--skip-space-check]
       [--world] [--force] [--no-clean] [--cache-dir <folder>] [--progress-every N] [--diagnostics <file>] [--diagnostics-every N] [--diagnostics-live]

Writes one folder per build (linkset) under <folder>: <name>_<id>.glb and manifest.json, plus export-summary.json and
export-summary.txt. Every build's textures are PNG files in one shared <folder>/textures (the original .jp2 and the
material files beside them) and its mesh assets in one shared <folder>/assets, each file named by its asset id; the .glb
points at the PNG files by relative path, so keep the folders together. The manifest says where every file lives.
The archive is only read. Parts whose creator did not allow export are listed in the manifest as skipped.
  --out <folder>       where to write (created when missing); required
  --object <uuid|name> only the build whose root part has this id, or whose name is this
  --limit N            write at most N builds
  --self-contained     give each build its own textures and assets folders and put the PNG images inside its .glb, so one
                       build can be moved alone (uses far more disk space when builds share textures)
  --max-memory-mb N    memory to stay within (default 2048, or half of the machine's
                       memory if that is smaller); bodies beyond a share of it wait in the cache folder. An asset
                       too large to handle within it is reported over-memory-limit and the run ends with exit code 4
  --max-cache-mb N     most megabytes of asset bodies to keep in the cache folder and memory together (default: no limit);
                       an asset beyond it is reported over-size-limit and the run ends with exit code 4.
                       (--max-asset-mb is the same option under its earlier name)
  --skip-space-check   accepted and ignored (the free-space estimate is only a warning now); kept for old command lines
  --cache-dir <folder> where asset bodies wait while the builds are written (default: .asset-cache in the output folder);
                       left in place at the end, safe to remove
  --no-clean           keep the geometry as the mesher builds it: by default duplicate and loose vertices and zero-area triangles are
                       removed from every face (texture coordinates, faces and face numbers are never changed) and the file is
                       indexed and stamped with the version; with --no-clean the file has no stamp, as before version 0.2
  --force              write every build again; by default a build that an earlier run into the same folder wrote from the
                       same object file with the same tool version is left as it is
  --progress-every N   print progress after every N builds (default 25) and at least every ten seconds, on standard error
  --diagnostics <file> append a memory log to <file>: one JSON line every N builds (see --diagnostics-every) with the managed
                       heap, the working set and the size of what is kept across builds (default: no log)
  --diagnostics-every N  builds (or object files, while the archive is read) between two lines of that log (default 100)
  --diagnostics-live   collect garbage before each line of that log, so the heap figures show what the run holds (slower)
  --world              put each build at its root part's position and rotation in the region (default: at the origin, the
                       region position and rotation are in the build node's extras)

A build that fails is recorded in export-summary.json with its error and the others go on. A damaged or cut-short
archive is exported as far as it can be read; the first line of the summary then says ARCHIVE INCOMPLETE.

Exit codes: 0 done (also when some builds failed), 2 not a region archive or unreadable, or the image library does not
              work (nothing is written: the program decodes a small built-in image first), 3 every build failed,
            4 the archive was incomplete, or a memory or disk cap was reached (everything else was exported),
            5 stopped between builds because the free space on the output drive fell below 2 GB (run the same line
              again to carry on),
            64 bad command line.";

    /// <summary>Replaces the image library's start-up check; the tests use it. Null runs the real check.</summary>
    internal static Func<string> ImageCheck;

    public static int Run(string[] args)
    {
        string input = null;
        ExportOptions options = new ExportOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a folder");
                    options.OutputDirectory = args[++i];
                    break;
                case "--object":
                    if (i + 1 >= args.Length) return Fail("--object needs a uuid or a name");
                    options.Object = args[++i];
                    break;
                case "--limit":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                        return Fail("--limit needs a number of at least 1");
                    options.Limit = n;
                    break;
                case "--max-asset-mb":
                case "--max-cache-mb":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) || mb < 1)
                        return Fail(args[i - 1] + " needs a number of at least 1");
                    options.MaxCacheMegabytes = mb;
                    break;
                case "--max-memory-mb":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mem) || mem < 1)
                        return Fail("--max-memory-mb needs a number of at least 1");
                    options.MaxMemoryMegabytes = mem;
                    break;
                case "--self-contained":
                    options.SelfContained = true;
                    break;
                case "--skip-space-check":
                    options.SkipSpaceCheck = true;
                    break;
                case "--world":
                    options.World = true;
                    break;
                case "--force":
                    options.Force = true;
                    break;
                case "--cache-dir":
                    if (i + 1 >= args.Length) return Fail("--cache-dir needs a folder");
                    options.CacheDirectory = args[++i];
                    break;
                case "--progress-every":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int every) || every < 1)
                        return Fail("--progress-every needs a number of at least 1");
                    options.ProgressEvery = every;
                    break;
                case "--diagnostics":
                    if (i + 1 >= args.Length) return Fail("--diagnostics needs a file");
                    options.DiagnosticsPath = args[++i];
                    break;
                case "--diagnostics-live":
                    options.DiagnosticsCollect = true;
                    break;
                case "--diagnostics-every":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dEvery) || dEvery < 1)
                        return Fail("--diagnostics-every needs a number of at least 1");
                    options.DiagnosticsEvery = dEvery;
                    break;
                case "--no-clean":
                    options.Clean = false;
                    break;
                case "--version":
                    Console.Out.WriteLine("oar-export " + OpenSim.Archive.GltfExport.ExportStamp.CurrentVersion);
                    return 0;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || input != null)
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    input = args[i];
                    break;
            }
        }

        if (input == null) return Fail("no archive file given");
        if (string.IsNullOrEmpty(options.OutputDirectory)) return Fail("--out is required");

        options.Progress = line => Console.Error.WriteLine(Sanitize.Text(line));
        options.ImageCheck = ImageCheck;
        // the default depends on the machine's memory, which the runtime reports differently once the heap is limited
        options.MaxMemoryMegabytes = ArchiveExporter.MaxMemoryMegabytesFor(options);
        LimitManagedMemory(options.MaxMemoryMegabytes);
        ExportSummary summary;
        try
        {
            summary = ArchiveExporter.Export(input, options);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidDataException)
        {
            Console.Error.WriteLine(Sanitize.Text(e.Message));
            return ExitCodes.Unreadable;
        }

        Console.Out.Write(SummaryText.Build(summary));
        return summary.ExitCode;
    }

    /// <summary>Holds the managed heap to the memory cap, so the cap is a limit and not only a target. Best effort: the runtime refuses a limit below what is already in use.</summary>
    private static void LimitManagedMemory(int megabytes)
    {
        try
        {
            AppContext.SetData("GCHeapHardLimit", (ulong)megabytes * 1024UL * 1024UL);
            GC.RefreshMemoryLimit();
        }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is NotSupportedException)
        {
            Console.Error.WriteLine("note: the memory cap could not be applied to the runtime (" + Sanitize.Text(e.Message) + "); it only sizes the caches");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 64;
    }
}
