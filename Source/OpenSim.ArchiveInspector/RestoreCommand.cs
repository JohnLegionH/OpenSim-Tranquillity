/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Restore;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-restore command: write a new OAR from the original and the exporter's .glb files, possibly edited in Blender.</summary>
internal static class RestoreCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-restore <original.oar> <glb folder> --out <new.oar> [--allow-unsafe] [--report <file>] [--work <folder>]

Takes the archive the .glb files were exported from and the folder that holds the .glb files (the exporter's output, or the
files after Blender opened and saved them) and writes a new archive.
  - A part whose geometry in the .glb is the geometry a fresh export of the original gives (within a small tolerance) is left
    exactly as it was, byte for byte.
  - A part whose geometry changed becomes a mesh part. Its link number, name, description, scripts, contents and the texture
    settings of each face are kept; only its shape changes.
  - A changed part whose scripts the script scan rates UNSAFE or UNCERTAIN is refused (it stays as it was) and the report says
    why. --allow-unsafe rebuilds it anyway and records that it did.
  - A .glb that does not match the original build part for part (a part added, deleted or renumbered) is refused as a whole and
    nothing is written. A build with no .glb is kept as it was.
  --out <new.oar>       the archive to write (required; it must not exist)
  --report <file>       also write the report to <file>, the same as JSON to <file>.json, and the plan the check used to <file>.plan.json
  --allow-unsafe        rebuild a changed part even when its scripts are rated UNSAFE or UNCERTAIN
  --work <folder>       where the original is exported for the comparison (default: the output name with .work added; kept)
  --assume-safe-unscanned   treat a script with no readable source as harmless (see oar-eligibility)
  --position-tolerance <m>, --normal-tolerance <n>, --uv-tolerance <n>   how far geometry may differ and count as unchanged
The original archive and the .glb files are only read.
Exit codes: 0 done and the new archive passed the check against the original; 1 the check failed or a mesh was invalid;
            2 the tool could not run, or a .glb was refused as a whole; 3 written, but at least one part was refused.";

    public static int Run(string[] args)
    {
        string original = null, folder = null, outPath = null, reportPath = null;
        RestoreOptions options = new RestoreOptions { Progress = m => Console.Error.WriteLine(m) };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a path");
                    outPath = args[++i];
                    break;
                case "--report":
                    if (i + 1 >= args.Length) return Fail("--report needs a path");
                    reportPath = args[++i];
                    break;
                case "--work":
                    if (i + 1 >= args.Length) return Fail("--work needs a folder");
                    options.WorkFolder = args[++i];
                    break;
                case "--allow-unsafe":
                    options.AllowUnsafe = true;
                    break;
                case "--assume-safe-unscanned":
                    options.AssumeSafeUnscanned = true;
                    break;
                case "--position-tolerance":
                case "--normal-tolerance":
                case "--uv-tolerance":
                    if (i + 1 >= args.Length || !float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value < 0)
                        return Fail(args[i] + " needs a number");
                    if (args[i] == "--position-tolerance") options.Tolerance.Position = value;
                    else if (args[i] == "--normal-tolerance") options.Tolerance.Normal = value;
                    else options.Tolerance.Uv = value;
                    i++;
                    break;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    if (original == null) original = args[i];
                    else if (folder == null) folder = args[i];
                    else return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    break;
            }
        }
        if (original == null || folder == null) return Fail("an original archive and a .glb folder are required");
        if (outPath == null) return Fail("--out is required");

        try
        {
            RestoreResult result = new ArchiveRestorer(options).Restore(original, folder, outPath);
            string text = result.ToText();
            Console.Out.Write(text);
            if (reportPath != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(reportPath, text);
                File.WriteAllText(reportPath + ".json", result.ToJson());
                if (result.PlanJson != null)
                    File.WriteAllText(reportPath + ".plan.json", result.PlanJson);
            }
            return result.ExitCode;
        }
        catch (Exception e) when (e is RestoreException || e is ScanException || e is CompareException || e is IOException
            || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException || e is InvalidOperationException)
        {
            Console.Error.WriteLine("oar-restore could not run: " + Sanitize.Text(e.Message));
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
