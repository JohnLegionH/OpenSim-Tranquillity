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
       OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --out <new.oar> --merge-plan-dir <folder> [--report <folder>] [--lod-pixel-limit <pixels>] [--assume-safe-unscanned]
       OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --plan-only --merge-plan-dir <folder> [--assume-safe-unscanned]
       OpenSim.ArchiveInspector oar-convert <archive.oar|archive.iar> --fix-perms --creator <id>[,<id>...] --out <new archive> [--report <folder>]

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
  --merge                    merge the parts of each linkset that can merge into fewer mesh parts and write the archive to --out. A merged
                             part is its lowest-numbered member rewritten as a mesh part with every face drawn as it was (texture
                             repeats, offset, rotation and planar mapping are baked into the coordinates); it holds a notecard that
                             lists the parts it replaced. Parts that cannot merge stay as they are. Without --merge the command
                             converts every convertible part one for one, as before.
  --merge --plan-only        only plan which parts would merge and why the others stay; writes the plan and no archive.
  --lod-pixel-limit <px>     with --merge: the most a lower level of detail of a merged part may differ from the full mesh, in screen pixels
                             (1080 pixels high window) at the distance the viewer switches to it; a positive number, default 1. A larger
                             limit keeps fewer triangles in the lower levels and lowers the viewer's streaming cost, at the price of a
                             difference that can be seen at the switch distance. The report states the streaming cost before and after.
  --merge-plan-dir <folder>  where the plan goes: merge-plan.json, merge-plan.txt and one file per linkset under linksets/

Permission fix (--fix-perms): writes a copy of an OAR or IAR in which every part and every item in a part's contents created by the
named creators, and in an IAR every inventory item they created, has modify, copy and transfer on its base, owner and next owner masks
(the bits are added; no other bit changes). Parts and items made by anyone else, the group and everyone masks, and creator, owner and last
owner stay exactly as they were, and everything the fix does not need to change is copied byte for byte. The change report lists every mask
that changed with its value before and after, and every part and item left alone with its creator.
  --fix-perms                write the permission-fixed copy to --out; no conversion is done
  --creator <id>[,<id>...]   the account ids whose creations are fixed (required with --fix-perms; the program refuses without it)
  --report <folder>          also write fix-perms-report.txt and fix-perms-report.json there

The mesh assets are written with OpenSim.MeshAsset: the faces as the high level of detail, the lower levels made with the
viewer's profile, a bounding-hull physics block, and a physics_mesh block holding the collision shape the region's own prim
mesher makes for the part, so the converted part collides as the prim did. Every mesh is decoded and read back (OpenSim.MeshAsset and OpenSim's
physics mesher) before anything is written; if one fails, the report lists the parts and no archive is written.
Exit codes: 0 the check passed, 1 the parity check failed or a mesh failed its validity check (nothing written), 2 the tool could not run (unreadable archive, bad command line, output exists).";

    public static int Run(string[] args)
    {
        string archive = null, outPath = null, planOut = null, reportDir = null, mergePlanDir = null, creatorArg = null, relinkFrom = null;
        bool merge = false, planOnly = false, lodGiven = false, fixPerms = false;
        double pixelLimit = new MergeOptions().Lod.PixelLimit;
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
                case "--fix-perms":
                    fixPerms = true;
                    break;
                case "--creator":
                    if (i + 1 >= args.Length) return Fail("--creator needs one or more account ids");
                    creatorArg = args[++i];
                    break;
                case "--relink-from":
                    if (i + 1 >= args.Length) return Fail("--relink-from needs a reference archive");
                    relinkFrom = args[++i];
                    break;
                case "--plan-only":
                    planOnly = true;
                    break;
                case "--merge-plan-dir":
                    if (i + 1 >= args.Length) return Fail("--merge-plan-dir needs a folder");
                    mergePlanDir = args[++i];
                    break;
                case "--lod-pixel-limit":
                    lodGiven = true;
                    if (i + 1 >= args.Length) return Fail("--lod-pixel-limit needs a number of pixels");
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pixelLimit) || !double.IsFinite(pixelLimit) || pixelLimit <= 0)
                        return Fail("--lod-pixel-limit needs a positive number of pixels, not '" + Sanitize.Text(args[i]) + "'");
                    break;
                case "--version":
                    Console.Out.WriteLine("oar-convert " + ToolRelease.Version);
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
        if (creatorArg != null && !fixPerms) return Fail("--creator goes with --fix-perms: it names the creators whose parts and items the fix changes");
        if (fixPerms)
        {
            if (merge || relinkFrom != null) return Fail("--fix-perms cannot be combined with --merge or --relink-from");
            if (planOut != null || planOnly || lodGiven || mergePlanDir != null) return Fail("--fix-perms takes only --creator, --out and --report");
            if (creatorArg == null) return Fail("--creator is required with --fix-perms: name the creators whose parts and items are to be fixed, as account ids separated by commas");
            if (outPath == null) return Fail("--out is required with --fix-perms");
            return RunFixPerms(archive, outPath, creatorArg, reportDir);
        }
        if (planOnly && !merge) return Fail("--plan-only goes with --merge");
        if (lodGiven && !merge) return Fail("--lod-pixel-limit goes with --merge: it sets the levels of detail of merged parts, and without --merge no part is merged");
        if (merge && mergePlanDir == null) return Fail("--merge-plan-dir is required with --merge");
        if (merge && !planOnly && outPath == null) return Fail("--out is required with --merge (or add --plan-only)");
        if (merge)
            return planOnly ? RunMergePlan(archive, mergePlanDir, reportDir, options) : RunMerge(archive, outPath, mergePlanDir, reportDir, options, pixelLimit);
        if (outPath == null) return Fail("--out is required");

        try
        {
            ConvertResult result = new ArchiveConverter(options).Convert(archive, outPath, planOut);
            string text = result.ToText();
            Console.Out.Write(text);
            if (reportDir != null)
            {
                Directory.CreateDirectory(reportDir);
                ReportFile.Write(Path.Combine(reportDir, "convert-report.txt"), text);
                ReportFile.Write(Path.Combine(reportDir, "convert-report.json"), result.ToJson());
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

    private static int RunFixPerms(string archive, string outPath, string creatorArg, string reportDir)
    {
        List<OpenMetaverse.UUID> creators = new List<OpenMetaverse.UUID>();
        foreach (string part in creatorArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!OpenMetaverse.UUID.TryParse(part, out OpenMetaverse.UUID id) || id == OpenMetaverse.UUID.Zero)
                return Fail("--creator needs account ids (36 characters each, separated by commas), not '" + Sanitize.Text(part) + "'");
            creators.Add(id);
        }
        if (creators.Count == 0)
            return Fail("--creator needs at least one account id");

        try
        {
            OpenSim.Archive.Converter.Fix.PermissionFixResult result = OpenSim.Archive.Converter.Fix.PermissionFix.Run(archive, outPath, creators);
            string text = result.ToText();
            Console.Out.Write(text);
            if (reportDir != null)
            {
                Directory.CreateDirectory(reportDir);
                ReportFile.Write(Path.Combine(reportDir, "fix-perms-report.txt"), text);
                ReportFile.Write(Path.Combine(reportDir, "fix-perms-report.json"), result.ToJson());
            }
            return 0;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException || e is InvalidOperationException)
        {
            Console.Error.WriteLine("oar-convert could not run: " + Sanitize.Text(e.Message));
            return 2;
        }
    }

    private static int RunMerge(string archive, string outPath, string folder, string reportDir, ConvertOptions options, double pixelLimit)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, MergePlanner.PlanFileName)))
                throw new IOException("'" + folder + "' already holds a merge plan");
            MergeOptions mergeOptions = new MergeOptions();
            mergeOptions.Lod.PixelLimit = pixelLimit;
            MergeWriteResult result = MergeArchiveWriter.Write(archive, outPath, mergeOptions, options);
            result.Cost = CostComparison.Measure(archive, outPath);
            MergePlanFiles.Write(result.Plan, folder);
            string text = result.ToText();
            Console.Out.Write(text);
            if (reportDir != null)
            {
                Directory.CreateDirectory(reportDir);
                ReportFile.Write(Path.Combine(reportDir, "merge-report.txt"), text + Environment.NewLine + MergePlanFiles.ReportText(result.Plan));
            }
            return 0;
        }
        catch (Exception e) when (e is ScanException || e is ConvertException || e is IOException || e is UnauthorizedAccessException || e is InvalidDataException
            || e is ArgumentException || e is InvalidOperationException)
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
                ReportFile.Write(Path.Combine(reportDir, "merge-report.txt"), text);
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
