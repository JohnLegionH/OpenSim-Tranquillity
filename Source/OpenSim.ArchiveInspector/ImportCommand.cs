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
@"Usage: OpenSim.ArchiveInspector oar-import <folder> --out <new.oar> --avatar <uuid> [--name <text>] [--description <text>] [--base <name>] [--position <x,y,z>] [--created <seconds>] [--pbr] [--check <folder>]
       OpenSim.ArchiveInspector oar-import (--manifest <linkset.json> | <folder with a linkset.json>) --out <new.oar> --avatar <uuid> [--position <x,y,z>] [--created <seconds>] [--pbr] [--check <folder>]

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
  --created <seconds>   the creation time written into the archive and the object, in seconds since 1970 (default: now). Give the same value with the same
                        input files to get the same archive, byte for byte.
  --pbr                 also write a glTF PBR material for every face that has a material in the .glb (base colour, normal, occlusion-roughness-metallic and
                        emissive pictures and the factors), beside the legacy texture and normal-map material the face always has. A manifest can ask for it
                        with ""pbr"": true.
  --check <folder>      after writing, write the object back out to glTF in <folder> and compare it with the input: triangle counts equal
                        for every level, positions within half a millimetre, texture coordinates and pictures the same
  --manifest <file>     a linked object (see below); a folder that holds a linkset.json is read the same way
The input files are only read.

A linked object: a JSON manifest, linkset.json, lists the parts, root part first, and the folder it is in is where its relative paths start.
  {
    ""name"": ""Test House"", ""description"": ""text"",
    ""parts"": [
      { ""name"": ""Shell"", ""description"": ""the walls"", ""folder"": ""shell"",
        ""contents"": [ { ""type"": ""notecard"", ""file"": ""readme.txt"", ""name"": ""Read me"" } ] },
      { ""name"": ""Door"", ""glb"": ""building.glb"", ""node"": ""Door"", ""position"": [0.3, 0.2, 0], ""rotation"": [0, 0, 0, 1],
        ""contents"": [ { ""type"": ""script"", ""file"": ""door.lsl"", ""name"": ""Door script"" } ] },
      { ""name"": ""Window"", ""glb"": ""building.glb"", ""node"": ""Window"" }
    ]
  }
  A part is made from one source: ""folder"" (a mesh tool output folder with its four levels and collision shape, as above; ""base"" names the model when the folder holds
  more than one) or ""glb"" and ""node"" (one mesh node of a .glb file; the other levels and the collision shape are the nodes named in ""lod2"", ""lod1"", ""lod0"" and
  ""physics"", each ""{ glb, node }"", and without them every level is the full mesh and the part collides as its bounding hull).
  The first part is the root and link number 1: its model origin is the origin of the object, so it has no ""position"" and cannot be turned. Every other part sits
  relative to the root part: ""position"" is where its model origin lands, in metres in the viewer axes (x east, y north, z up), and ""rotation"" is its turn as x, y, z, w.
  A node source without them takes its node place in the file (as seen from the root part node when both are nodes of one file); a folder source takes 0, 0, 0 and no turn.
  A building is one manifest: the shell is the root, each door leaf and window a part.
  A part can also have:
    ""sit"": { ""position"": [x, y, z], ""rotation"": [x, y, z, w] }   where an avatar sits, in the part's own axes (rotation left out is no turn)
    ""faces"": [ { ""material"": ""<glTF material name>"" (or ""face"": <number>), ""glow"": 0 to 1, ""fullbright"": true,
                 ""specular"": { ""map"": ""<picture file>"", ""color"": [r, g, b] (0 to 1), ""glossiness"": 0 to 255, ""environment"": 0 to 255 } } ]
  A specular map is a legacy one: colour in red, green and blue, glossiness in alpha. It goes in the face's legacy material beside the normal map.
  ""contents"" puts items into the part: ""script"" (LSL text, set running; the region compiles it when the object loads), ""notecard"" (a text file) or ""texture""
  (a PNG or JPEG picture, stored as a JPEG 2000 codestream the viewer can decode), each with a ""file"" and an optional ""name"" and ""description"". Creator and owner are the
  avatar, with full permission on parts and items. Unknown keys are refused by name.
  The check for a linked object writes the archive out with the exporter and compares link order, names, descriptions, places and turns (half a millimetre), triangle
  counts and contents (scripts byte for byte, notecard text, texture pixels) with the manifest and the files.
Exit codes: 0 done (and the check passed, if asked); 1 written, but the check failed; 2 the folder cannot be imported, nothing written; 64 bad command line.";

    public static int Run(string[] args)
    {
        string folder = null, outPath = null, checkFolder = null, manifestPath = null;
        ImportOptions options = new ImportOptions { Progress = m => Console.Error.WriteLine(m), ToolVersion = ToolRelease.Version };
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
                case "--manifest":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return Fail("--manifest needs a file");
                    manifestPath = args[++i];
                    break;
                case "--check":
                    if (i + 1 >= args.Length) return Fail("--check needs a folder");
                    checkFolder = args[++i];
                    break;
                case "--pbr":
                    options.Pbr = true;
                    break;
                case "--created":
                    if (i + 1 >= args.Length || !long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0)
                        return Fail("--created needs a time in whole seconds since 1970");
                    options.Created = created;
                    i++;
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
        if (manifestPath != null && folder != null) return Fail("give the folder or --manifest, not both");
        if (manifestPath == null && folder != null && File.Exists(Path.Combine(folder, "linkset.json")))
            manifestPath = Path.Combine(folder, "linkset.json");
        if (manifestPath == null && folder == null) return Fail("no folder given");
        if (outPath == null) return Fail("--out is needed");
        if (!haveAvatar) return Fail("--avatar is needed");
        if (manifestPath != null)
            return RunLinkset(manifestPath, outPath, checkFolder, options);

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

    private static int RunLinkset(string manifestPath, string outPath, string checkFolder, ImportOptions options)
    {
        ImportResult result;
        LinksetManifest manifest;
        try
        {
            manifest = LinksetManifest.Load(manifestPath);
            result = ObjectImporter.ImportLinkset(manifest, outPath, options);
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
        ImportCheckResult check = ImportCheck.RunLinkset(outPath, manifest, options, checkFolder);
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
