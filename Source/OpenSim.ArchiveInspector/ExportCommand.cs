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

Writes one folder per build (linkset) under <folder>: <name>_<id>.glb and manifest.json, plus export-summary.json.
The archive is only read. Parts whose creator did not allow export are listed in the manifest as skipped.
  --out <folder>       where to write (created when missing); required
  --object <uuid|name> only the build whose root part has this id, or whose name is this
  --limit N            write at most N builds
  --world              put each build at its root part's position and rotation in the region (default: at the origin, the
                       region position and rotation are in the build node's extras)

Exit codes: 0 done, 1 the archive was only partly readable (what was read is written), 2 not a region archive or
            unreadable, 64 bad command line.";

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
                case "--world":
                    options.World = true;
                    break;
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

        ExportSummary summary;
        try
        {
            summary = ArchiveExporter.Export(input, options);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidDataException)
        {
            Console.Error.WriteLine(Sanitize.Text(e.Message));
            return 2;
        }

        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "read: {0}; builds: {1} ({2} with a file); parts: {3} (exported {4}, placeheld {5}, skipped {6}); {7:0.0} s; {8} bytes written",
            summary.ReadStatus, summary.Builds, summary.BuildsWithAFile, summary.Parts, summary.Exported, summary.Placeheld, summary.Skipped, summary.Seconds, summary.OutputBytes));
        foreach (KeyValuePair<string, int> r in summary.Reasons.OrderByDescending(r => r.Value))
            Console.Out.WriteLine("  " + r.Value.ToString(CultureInfo.InvariantCulture) + " x " + Sanitize.Text(r.Key));

        if (summary.ReadStatus == nameof(InspectionStatus.NotAnArchive)) return 2;
        return summary.ReadStatus == nameof(InspectionStatus.Partial) ? 1 : 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 64;
    }
}
