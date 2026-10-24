/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-eligibility command: which parts of an archive become mesh, which stay as they are, and why.</summary>
internal static class EligibilityCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-eligibility <archive.oar> [--out <folder>] [--assume-safe-unscanned]

Decides, for every part, whether turning it into a mesh part is safe: CONVERT (with the face count scripts will see
afterwards), KEEP-PRIM (with the reason) or SKIP (already a mesh, a built-in plant, not an ordinary prim). The archive
is only read. Prints a summary and one line per kept or warned part.
  --out <folder>             also write eligibility-report.txt, eligibility-report.json and eligibility-plan.json there
                             (the plan is a draft for oar-compare with stand-in ids for the new mesh assets)
  --assume-safe-unscanned    treat a script with no readable source as harmless (by default it keeps its whole build as
                             prims; the report says loudly when this is used)

Reason codes: ALREADY_MESH, PLANT, OTHER_PCODE, NO_SHAPE (skipped); EXPORT_DENIED, PHYSICAL, FLEXI, FACES_GT8,
SCRIPT_UNSAFE, SCRIPT_UNCERTAIN, FACECOUNT_MISMATCH, SCULPT_MAP_MISSING, SCULPT_SHAPE_UNVERIFIED (kept).
Exit codes: 0 no part is kept, 3 at least one part is kept, 2 the tool could not run (unreadable archive, bad command line).";

    public static int Run(string[] args)
    {
        string archive = null, outDir = null;
        EligibilityOptions options = new EligibilityOptions();

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
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || archive != null)
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    archive = args[i];
                    break;
            }
        }
        if (archive == null)
            return Fail("an archive is required");

        try
        {
            EligibilityResult result = new ArchiveEligibility(options).Evaluate(archive);
            string text = EligibilityReport.ToText(result);
            Console.Out.Write(text);

            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "eligibility-report.txt"), text);
                File.WriteAllText(Path.Combine(outDir, "eligibility-report.json"), EligibilityReport.ToJson(result));
                File.WriteAllText(Path.Combine(outDir, "eligibility-plan.json"), EligibilityReport.ToDraftPlan(result));
            }
            return result.Count(PartAction.KeepPrim) > 0 ? 3 : 0;
        }
        catch (Exception e) when (e is ScanException || e is IOException || e is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("oar-eligibility could not run: " + Sanitize.Text(e.Message));
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
