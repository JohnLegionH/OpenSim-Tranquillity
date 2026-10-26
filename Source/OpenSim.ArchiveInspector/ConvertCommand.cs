/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Compare;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-convert command: write a new OAR in which every convertible prim and sculpt part is a mesh part.</summary>
internal static class ConvertCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-convert <archive.oar> --out <new.oar> [--plan-out <plan.json>] [--report <folder>] [--assume-safe-unscanned] [--no-clean]
       OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --plan-only --merge-plan-dir <folder> [--assume-safe-unscanned]

Turns every part the eligibility rules pass (see oar-eligibility) into a mesh part and writes a new archive. A converted part
keeps its link number, name, description, contents, size and texture entry; its faces, their numbers and their texture
coordinates are the ones the viewer gives the prim. The source archive is only read, and the output must not exist yet.
Then the new archive is checked against the source with a plan that states exactly what changed (the oar-compare check).
  --out <new.oar>            the archive to write (required)
  --plan-out <plan.json>     also write the plan the check used
  --report <folder>          also write convert-report.txt and convert-report.json there
  --assume-safe-unscanned    treat a script with no readable source as harmless (see oar-eligibility)
  --no-clean                 keep the faces as the mesher builds them; by default duplicate and loose vertices and zero-area triangles
                             are removed before the levels of detail are made (texture coordinates and faces are never changed)
  --merge --plan-only        plan which parts of each linkset would merge into fewer mesh parts, and why the others stay; writes the plan
                             and no archive. Merging itself is not built yet, so --merge needs --plan-only.
  --merge-plan-dir <folder>  where the plan goes: merge-plan.json, merge-plan.txt and one file per linkset under linksets/

The mesh assets are written with OpenSim.MeshAsset: the faces as the high level of detail, the lower levels made with the
viewer's profile, a bounding-hull physics block, and a physics_mesh block holding the collision shape the region's own prim
mesher makes for the part, so the converted part collides as the prim did. Every mesh is decoded and read back (OpenSim.MeshAsset and OpenSim's
physics mesher) before anything is written; if one fails, the report lists the parts and no archive is written.
Exit codes: 0 the check passed, 1 the parity check failed or a mesh failed its validity check (nothing written), 2 the tool could not run (unreadable archive, bad command line, output exists).";

    public static int Run(string[] args)
    {
        string archive = null, outPath = null, planOut = null, reportDir = null, mergePlanDir = null;
        bool merge = false, planOnly = false;
        ConvertOptions options = new ConvertOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a path");
                    outPath = args[++i];
                    break;
                case "--plan-out":
                    if (i + 1 >= args.Length) return Fail("--plan-out needs a path");
                    planOut = args[++i];
                    break;
                case "--report":
                    if (i + 1 >= args.Length) return Fail("--report needs a folder");
                    reportDir = args[++i];
                    break;
                case "--assume-safe-unscanned":
                    options.AssumeSafeUnscanned = true;
                    break;
                case "--no-clean":
                    options.Clean = false;
                    break;
                case "--merge":
                    merge = true;
                    break;
                case "--plan-only":
                    planOnly = true;
                    break;
                case "--merge-plan-dir":
                    if (i + 1 >= args.Length) return Fail("--merge-plan-dir needs a folder");
                    mergePlanDir = args[++i];
                    break;
                case "--version":
                    Console.Out.WriteLine("oar-convert " + OpenSim.Archive.GltfExport.ExportStamp.CurrentVersion);
                    return 0;
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
        if (archive == null) return Fail("an archive is required");
        if (planOnly && !merge) return Fail("--plan-only goes with --merge");
        if (merge && !planOnly) return Fail("--merge only plans for now: add --plan-only");
        if (merge && mergePlanDir == null) return Fail("--merge-plan-dir is required with --merge");
        if (merge)
            return RunMergePlan(archive, mergePlanDir, reportDir, options);
        if (outPath == null) return Fail("--out is required");

        try
        {
            ConvertResult result = new ArchiveConverter(options).Convert(archive, outPath, planOut);
            string text = result.ToText();
            Console.Out.Write(text);
            if (reportDir != null)
            {
                Directory.CreateDirectory(reportDir);
                File.WriteAllText(Path.Combine(reportDir, "convert-report.txt"), text);
                File.WriteAllText(Path.Combine(reportDir, "convert-report.json"), result.ToJson());
            }
            return result.Pass ? 0 : 1;
        }
        catch (Exception e) when (e is ScanException || e is ConvertException || e is CompareException || e is IOException
            || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException || e is InvalidOperationException)
        {
            Console.Error.WriteLine("oar-convert could not run: " + Sanitize.Text(e.Message));
            return 2;
        }
    }

    private static int RunMergePlan(string archive, string folder, string reportDir, ConvertOptions options)
    {
        try
        {
            MergePlan plan = ArchiveMergePlanner.Plan(archive, new MergeOptions(), options);
            MergePlanFiles.Write(plan, folder);
            string text = MergePlanFiles.ReportText(plan);
            Console.Out.Write(text);
            if (reportDir != null)
            {
                Directory.CreateDirectory(reportDir);
                File.WriteAllText(Path.Combine(reportDir, "merge-report.txt"), text);
            }
            return 0;
        }
        catch (Exception e) when (e is ScanException || e is IOException || e is UnauthorizedAccessException || e is InvalidDataException
            || e is ArgumentException || e is InvalidOperationException)
        {
            Console.Error.WriteLine("oar-convert could not run: " + Sanitize.Text(e.Message));
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
