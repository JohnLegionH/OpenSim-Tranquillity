/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Numerics;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Restore.Import;

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-import command: write an archive holding one mesh object made from a folder of .glb files.</summary>
internal static class ImportCommand
{
    public const string Usage =
@"Usage: OpenSim.ArchiveInspector oar-import <folder> --out <new.oar> --avatar <uuid> [--name <text>] [--description <text>] [--base <name>] [--position <x,y,z>] [--check <folder>]

Writes an archive that holds one mesh object, made from the .glb files in <folder> (the files a mesh tool writes for an upload):
  <base>.glb          the full mesh (high level of detail); one face for each material, and the pictures of the materials inside the file
  <base>_lod2.glb     the medium level      <base>_lod1.glb   the low level      <base>_lod0.glb   the lowest level
  <base>_physics.glb  the collision shape (optional): a convex shape becomes one convex hull, any other becomes a triangle physics mesh;
                      without the file the object collides as the bounding hull of its mesh
A level that leaves a material out leaves that face empty; a level that uses a material the full mesh does not have is refused.
A material's base colour and picture become the colour and texture of its face; a normal map becomes the face's normal-map material.
The size of the object is the size of the full mesh in metres. The archive loads with load oar --merge.
  --out <new.oar>       the archive to write (required; it must not exist)
  --avatar <uuid>       the creator and the owner of the object (required); the object has copy, modify and transfer permission
  --name <text>         the object's name (default: the base name)
  --description <text>  the object's description
  --base <name>         the file name prefix, when the folder holds more than one model
  --position <x,y,z>    where the model's origin lands in the region, in metres (default 128,128,25)
  --check <folder>      after writing, write the object back out to glTF in <folder> and compare it with the input: triangle counts equal
                        for every level, positions within half a millimetre, texture coordinates and pictures the same
The input files are only read.
Exit codes: 0 done (and the check passed, if asked); 1 written, but the check failed; 2 the folder cannot be imported, nothing written; 64 bad command line.";

    public static int Run(string[] args)
    {
        string folder = null, outPath = null, checkFolder = null;
        ImportOptions options = new ImportOptions { Progress = m => Console.Error.WriteLine(m) };
        bool haveAvatar = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) return Fail("--out needs a path");
                    outPath = args[++i];
                    break;
                case "--avatar":
                    if (i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out Guid avatar) || avatar == Guid.Empty)
                        return Fail("--avatar needs a UUID");
                    options.Avatar = avatar;
                    haveAvatar = true;
                    i++;
                    break;
                case "--name":
                    if (i + 1 >= args.Length) return Fail("--name needs a text");
                    options.Name = args[++i];
                    break;
                case "--description":
                    if (i + 1 >= args.Length) return Fail("--description needs a text");
                    options.Description = args[++i];
                    break;
                case "--base":
                    if (i + 1 >= args.Length) return Fail("--base needs a name");
                    options.BaseName = args[++i];
                    break;
                case "--check":
                    if (i + 1 >= args.Length) return Fail("--check needs a folder");
                    checkFolder = args[++i];
                    break;
                case "--position":
                    string[] parts = i + 1 < args.Length ? args[i + 1].Split(',') : Array.Empty<string>();
                    if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        return Fail("--position needs three numbers, x,y,z");
                    options.Position = new Vector3(x, y, z);
                    i++;
                    break;
                case "--version":
                    Console.Out.WriteLine("oar-import " + ToolRelease.Version);
                    return 0;
                case "-h":
                case "--help":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || folder != null)
                        return Fail("unexpected argument '" + Sanitize.Text(args[i]) + "'");
                    folder = args[i];
                    break;
            }
        }
        if (folder == null) return Fail("no folder given");
        if (outPath == null) return Fail("--out is needed");
        if (!haveAvatar) return Fail("--avatar is needed");

        ImportResult result;
        try
        {
            result = ObjectImporter.Import(folder, outPath, options);
        }
        catch (ImportException e)
        {
            Console.Error.WriteLine("oar-import: " + Sanitize.Text(e.Message));
            return 2;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("oar-import: " + Sanitize.Text(e.Message));
            return 2;
        }
        Console.Out.Write(result.ToText());

        if (checkFolder == null)
            return 0;
        ImportCheckResult check = ImportCheck.Run(outPath, folder, options, checkFolder);
        Console.Out.Write(check.ToText());
        return check.Passed ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 64;
    }
}
