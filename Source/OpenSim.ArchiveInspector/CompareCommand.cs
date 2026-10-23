/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Compare;
using OpenSim.Archive.Inspection;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-compare command: did anything change between an original OAR and a rewritten one that was not planned.</summary>
internal static class CompareCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-compare <original.oar> <rewritten.oar> [--plan <plan.json>] [--out <folder>]

Compares two region archives without loading them. Parts are matched by part UUID. Every entry that is not an object
file must have the same bytes. For each part it compares link number, name, description, position, rotation, scale, shape,
face count, texture entry, contents (item name, type, asset, permissions), creator, owner and permission masks.
The archives are only read.
  --plan <plan.json>   the changes the rewrite was meant to make; a difference the plan names passes, any other fails,
                       and a planned change that is not found fails
  --out <folder>       also write compare-report.txt and compare-report.json there

Prints PASS or FAIL first, then one line per difference grouped by build.
Exit codes: 0 pass, 1 unplanned differences (or planned changes not found), 2 the tool could not run (unreadable archive,
            bad plan, bad command line).";

    public static int Run(string[] args)
    {
        string original = null, rewritten = null, planPath = null, outDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plan":
                    if (i + 1 >= args.Length) return Fail("--plan needs a file");
                    planPath = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a folder");
                    outDir = args[++i];
                    break;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    if (original == null) original = args[i];
                    else if (rewritten == null) rewritten = args[i];
                    else return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    break;
            }
        }
        if (original == null || rewritten == null)
            return Fail("two archives are required");

        try
        {
            ComparePlan plan = planPath == null ? ComparePlan.Empty() : ComparePlan.Load(planPath);
            CompareResult result = new ArchiveComparer().Compare(original, rewritten, plan);
            Console.Out.Write(result.ToText());

            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "compare-report.txt"), result.ToText());
                File.WriteAllText(Path.Combine(outDir, "compare-report.json"), result.ToJson());
            }
            return result.Pass ? 0 : 1;
        }
        catch (Exception e) when (e is CompareException || e is IOException || e is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("oar-compare could not run: " + Sanitize.Text(e.Message));
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
