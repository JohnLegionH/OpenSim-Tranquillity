/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ObjectInspect;
using ReportJson = OpenSim.Archive.ObjectInspect.ReportJson;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-inspect command: every part in link order and every permission mask of an OAR or an IAR.</summary>
internal static class InspectCommand
{
    public const string Usage =
@"Usage: oar-inspect <archive.oar|archive.iar> [--csv | --json] [--filter <text>] [--strict] [--names <file>] [--compare <reference archive>] [--out <file>]

Reads an OAR or an IAR (found out from the archive itself) and reports, for every object, every part in link order and every
permission mask of every part and of every item inside it. The archive is only read.
  For each part: link number as stored, place in the file, name, description, position inside the object, physics shape type
    (Prim, None or Convex), creator, owner, last owner, group, and the five masks (base, owner, group, everyone, next owner)
    as the letters M (modify), C (copy) and T (transfer), a dash where the mask lacks it. A base, owner or next owner mask that
    lacks one of the three is flagged and says which; the group and everyone masks are shown but not flagged (see --strict).
    Names are shown where the archive carries them (creator data); otherwise the id, with a count per id at the top.
  For each object: its effective permissions, worked out the way the region combines the root part, the contents of every part
    and the base mask (an object with one no-copy item inside is no-copy), and the part or item that restricts them.
  For an IAR: every inventory item with its own masks and folder; object items are opened, and a coalesced item lists each
    linkset on its own with its link order and offset.
  --csv           one row per part, per item in a part and per inventory item, for a spreadsheet
  --json          the same report as JSON
  --strict        flag all five masks, group and everyone included
  --names <file>  a list of account ids and names, one per line (the id, then a space, comma, tab or equals sign, then the name; blank
                  lines and lines starting with # are skipped); names the creators, owners and last owners the archive does not name
  --filter <text> keep only objects whose name contains the text (any case); in an IAR also items whose name contains it
  --compare <ref> compare with a reference archive: objects found in both (matched by name and, where several share a name,
                  by part count and shape) are compared part by part for link order and for permissions, ids and items
  --out <file>    write the report to a file instead of standard output
  --version       print the program and its release

Exit codes: 0 read fully (with --compare: no differences), 1 read partly (see the report), 2 not an archive or unreadable,
            3 --compare found differences, 64 bad command line.";

    public static int Run(string[] args)
    {
        string input = null, compare = null, filter = null, outPath = null;
        bool csv = false, json = false, strict = false;
        string namesPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                    Console.Out.WriteLine("oar-inspect " + ToolRelease.Version);
                    return 0;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                case "--csv": csv = true; break;
                case "--json": json = true; break;
                case "--strict": strict = true; break;
                case "--names":
                    if (i + 1 >= args.Length) return Fail("--names needs a file");
                    namesPath = args[++i];
                    break;
                case "--filter":
                    if (i + 1 >= args.Length) return Fail("--filter needs the text to look for");
                    filter = args[++i];
                    break;
                case "--compare":
                    if (i + 1 >= args.Length) return Fail("--compare needs a reference archive");
                    compare = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a file");
                    outPath = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || input != null)
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    input = args[i];
                    break;
            }
        }

        if (input == null)
            return Fail("no archive file given");
        if (csv && json)
            return Fail("only one of --csv and --json can be given");
        if (outPath != null && (Same(outPath, input) || (compare != null && Same(outPath, compare))))
            return Fail("--out is the archive itself");

        ReadOptions options = new ReadOptions { NameFilter = filter, Strict = strict };
        if (namesPath != null)
        {
            try
            {
                options.Names = NameList.Parse(File.ReadAllText(namesPath));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
            {
                Console.Error.WriteLine("oar-inspect: the names file " + Sanitize.Text(namesPath) + " cannot be used: " + Sanitize.Text(e.Message));
                return 2;
            }
        }
        ObjectReport report = ObjectReader.Read(input, options);
        if (report.Status == InspectionStatus.NotAnArchive)
        {
            Console.Error.WriteLine("oar-inspect: " + Sanitize.Text(input) + " is not an archive: " + Sanitize.Text(report.StopReason));
            return 2;
        }

        int code = report.Status == InspectionStatus.Partial ? 1 : 0;
        string text;
        if (compare == null)
            text = csv ? ReportCsv.Build(report) : json ? ReportJson.Build(report, input) : ReportText.Build(report, input);
        else
        {
            ObjectReport reference = ObjectReader.Read(compare, options);
            if (reference.Status == InspectionStatus.NotAnArchive)
            {
                Console.Error.WriteLine("oar-inspect: the reference " + Sanitize.Text(compare) + " is not an archive: " + Sanitize.Text(reference.StopReason));
                return 2;
            }
            if (reference.Status == InspectionStatus.Partial)
                code = 1;
            CompareResult result = ReportCompare.Compare(reference, report);
            text = csv ? result.ToCsv() : json ? result.ToJson(compare, input) : result.ToText(compare, input);
            if (!result.Identical)
                code = 3;
        }

        if (outPath == null)
            Console.Out.Write(text);
        else
        {
            try
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("cannot write " + Sanitize.Text(outPath) + ": " + Sanitize.Text(e.Message));
                return 2;
            }
        }
        return code;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 64;
    }
}
