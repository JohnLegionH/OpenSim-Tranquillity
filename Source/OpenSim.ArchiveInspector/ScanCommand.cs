/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-scan command: which parts of an archive could have a script broken by turning them into mesh.</summary>
internal static class ScanCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-scan <archive.oar|archive.iar> [--out <folder>] [--assume-safe-unscanned]

Reads the scripts in an OAR or IAR and decides, for every part, whether converting it to a mesh could break a script.
The archive is only read. Prints a summary and one line per part that has to stay a prim.
  --out <folder>             also write scan-report.txt and scan-report.json there
  --assume-safe-unscanned    treat a script with no readable source as harmless (by default it keeps its whole build as prims)

Decisions: CONVERTIBLE, CONVERTIBLE-WITH-WARNINGS, KEEP-PRIM (UNSAFE), KEEP-PRIM (UNCERTAIN).
The scan reads script text; a clean result means nothing was seen, not that nothing is there.
Exit codes: 0 every part can be converted, 3 at least one part stays a prim, 2 the tool could not run (unreadable archive,
            bad command line).";

    public static int Run(string[] args)
    {
        string archive = null, outDir = null;
        ScanOptions options = new ScanOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a folder");
                    outDir = args[++i];
                    break;
                case "--assume-safe-unscanned":
                    options.AssumeSafeUnscanned = true;
                    break;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    if (archive != null)
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    archive = args[i];
                    break;
            }
        }
        if (archive == null)
            return Fail("an archive is required");

        try
        {
            ScanResult result = new ArchiveScriptScanner(options).Scan(archive);
            string text = ScanReport.ToText(result);
            Console.Out.Write(text);

            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "scan-report.txt"), text);
                File.WriteAllText(Path.Combine(outDir, "scan-report.json"), ScanReport.ToJson(result));
            }
            return result.AnyKept ? 3 : 0;
        }
        catch (Exception e) when (e is ScanException || e is IOException || e is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("oar-scan could not run: " + Sanitize.Text(e.Message));
            return 2;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
