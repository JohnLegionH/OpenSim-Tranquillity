/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using OpenSim.Archive.Inspection;

namespace OpenSim.ArchiveInspector;

public static class Program
{
    private const string Usage =
@"Usage: OpenSim.ArchiveInspector <file.oar|file.iar> [--json <path>] [--all] [--stats]

Reads an OAR or IAR and reports who is credited for what, without extracting or changing anything.
  --json <path>  also write the full report as JSON to <path>
  --all          list everything instead of the top 50 of each table
  --stats        print wall time, bytes read and peak working set

Exit codes: 0 read fully with no Warning, 1 partial (see the report), 2 not an archive or unreadable,
            3 read fully with at least one Warning (the creator flags are Warnings), 64 bad command line.";

    public static int Main(string[] args)
    {
        string input = null, jsonPath = null;
        bool all = false, stats = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    if (i + 1 >= args.Length)
                        return Fail("--json needs a path");
                    jsonPath = args[++i];
                    break;
                case "--all": all = true; break;
                case "--stats": stats = true; break;
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

        if (input == null)
            return Fail("no archive file given");

        if (jsonPath != null && string.Equals(Path.GetFullPath(jsonPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            return Fail("--json path is the archive itself");

        InspectorOptions options = new InspectorOptions();
        if (all)
            options.MaxRetainedRecords = int.MaxValue;

        Stopwatch watch = Stopwatch.StartNew();
        ArchiveReport report = new Archive.Inspection.ArchiveInspector(options).Inspect(input);
        watch.Stop();

        report.Stats = new RunStats
        {
            WallSeconds = watch.Elapsed.TotalSeconds,
            BytesRead = report.Archive.CompressedBytes,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
        };

        Console.Out.Write(TextReport.Build(report, all));
        if (stats)
            Console.Out.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "wall time: {0:0.00} s, bytes read: {1}, peak working set: {2} bytes", report.Stats.WallSeconds, report.Stats.BytesRead, report.Stats.PeakWorkingSetBytes));

        if (jsonPath != null)
        {
            try
            {
                using FileStream fs = new FileStream(jsonPath, FileMode.Create, FileAccess.Write, FileShare.None);
                ReportJson.Write(report, fs);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("cannot write " + Sanitize.Text(jsonPath) + ": " + Sanitize.Text(e.Message));
                return report.ExitCode == 0 ? 1 : report.ExitCode;
            }
        }

        return report.ExitCode;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 64;
    }
}
