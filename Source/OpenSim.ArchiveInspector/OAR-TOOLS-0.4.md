<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4: oar-import joins the package

0.4 adds `oar-import`, which writes a new archive from mesh files: one mesh part, or one linked object of several parts with scripts, notecards and
textures. It adds a record of where each imported archive came from, one release number for the four programs, the same bytes for a merge plan folder
on every platform, and a check to run after a load that the textures reach a viewer. `oar-convert`, `oar-export` and `oar-restore` write what 0.3.1
wrote: the seven test archives merged with 0.4.0 are byte-identical to their 0.3.1 merges. The notes of the earlier releases are
[OAR-TOOLS-0.3.1.md](OAR-TOOLS-0.3.1.md) (the merge, in full)
and the 0.3 and 0.2 text it carries.

## What changed

1. **oar-import** (this file, below): a manifest (`linkset.json`) makes one linked object from mesh tool folders and from nodes of a multi-node `.glb`,
   each part with its place, turn, name, description and contents. `--check` writes the archive out again and compares it with the input.
2. **Textures a viewer can decode.** The texture assets the tools write are bare JPEG 2000 codestreams, not JP2 files (see below). `oar-restore` uses the
   same writer for the pictures of added parts.
3. **Where an archive came from.** `oar-import` records the tool's release, the manifest, each part's source and the SHA-256 of every input file inside
   the archive and in its report (below). `--created <seconds>` fixes the creation time, so the same input files and the same value give the same archive.
4. **One release number.** `oar-convert`, `oar-export`, `oar-restore` and `oar-import` print `0.4.0` with `--version`, from one constant in the source.
5. **Plan folder line endings.** `merge-plan.json`, `merge-plan.txt` and `linksets/*.json` are written with line feeds on every platform. In 0.3.1 they
   used the line ending of the platform, so a Windows plan folder and a Linux one differed in every file; this was the open item of the 0.3.1 notes.
6. **The check after a load:** `region-texture-check.py` and its fetch program `oar-texfetch` are in the package (below).

## oar-import

`oar-import` writes an archive that holds one object made from `.glb` files, ready for `load oar --merge`. It has two forms.

### One mesh part

    oar-import <folder> --out <new.oar> --avatar <uuid> [--name <text>] [--description <text>] [--base <name>] [--position <x,y,z>] [--created <seconds>] [--check <folder>]

`<folder>` holds the files a mesh tool writes: `base.glb` (the full mesh, one face for each material, the pictures inside the file), `base_lod2.glb`
(medium), `base_lod1.glb` (low), `base_lod0.glb` (lowest) and, optionally, `base_physics.glb` (the collision shape: a convex shape becomes one convex
hull, any other a triangle physics mesh; without it the part collides as the bounding hull of its mesh).

### A linked object: the manifest

    oar-import --manifest <linkset.json> --out <new.oar> --avatar <uuid> [--position <x,y,z>] [--created <seconds>] [--check <folder>]
    oar-import <folder holding linkset.json> --out <new.oar> --avatar <uuid> [--position <x,y,z>] [--created <seconds>] [--check <folder>]

The manifest is a JSON file; relative paths in it start at the folder it is in. Comments and trailing commas are allowed. A key the manifest does not
know is refused by name, so a misspelt `"positon"` is an error and not a part at the origin.

    {
      "name": "Test House",
      "description": "A house for the import tests",
      "parts": [
        { "name": "Shell", "description": "the walls", "folder": "shell",
          "contents": [ { "type": "notecard", "file": "readme.txt", "name": "Read me" } ] },
        { "name": "Door", "description": "front door leaf", "glb": "building.glb", "node": "Door",
          "position": [0.3, 0.2, 0], "rotation": [0, 0, 0, 1],
          "contents": [ { "type": "script", "file": "door.lsl", "name": "Door script" },
                        { "type": "texture", "file": "badge.png", "name": "Badge" } ] },
        { "name": "Window", "description": "side window", "glb": "building.glb", "node": "Window" }
      ]
    }

| Key | Meaning |
|---|---|
| `name`, `description` | The object's name (used for the file name inside the archive) and description. Each part has its own name and description too. |
| `parts` | The parts in link order. The first is the root part and link number 1; the others are link numbers 2, 3 and so on. One part gives a single mesh part. At most 256. |
| `folder`, `base` | A part made from a mesh tool's output folder, with its four levels of detail and collision shape as for one mesh part. `base` names the model when the folder holds more than one. |
| `glb`, `node` | A part made from one mesh node of a `.glb` file, for example one door leaf of a whole building exported as one file. The node is found by name; the name must be one node. |
| `lod2`, `lod1`, `lod0`, `physics` | For a node part: the nodes (`{ "glb": ..., "node": ... }`) that are the medium, low and lowest levels and the collision shape. Without them every level of detail is the full mesh (a note says so) and the part collides as its bounding hull. |
| `position` | Where the part's model origin lands relative to the root part's model origin, in metres in the viewer's axes (x east, y north, z up). |
| `rotation` | The part's turn relative to the root part as x, y, z, w. |
| `contents` | Items put into the part; see below. |

The root part's model origin is the origin of the object, so the root has no `position` (0, 0, 0 is accepted) and cannot be turned: the other parts are
placed relative to it. A node part that gives no `position` or `rotation` takes its node's place in the file, as seen from the root part's node when both
are nodes of one file (a node below a turned or scaled node is refused). A folder part that gives none sits at the origin, not turned. So a building exported
as one `.glb` becomes one manifest that lists the shell, each door leaf and each window by node name, with no numbers; a part that has to move (a door leaf
that opens about its hinge) is given the numbers.

`--position` puts the root part's model origin in the region. `--created` is the creation time written into the archive and the objects, in whole seconds
since 1970; without it the time of the run is used.

### Contents

| `type` | `file` | What is stored |
|---|---|---|
| `script` | LSL text | The text as written, in an item of the part. The script has no saved state, so the region compiles it when the object loads and it starts running. |
| `notecard` | a text file | A notecard in the Linden text format; line endings are written as line feeds. At most 65 536 bytes. |
| `texture` | a PNG or JPEG picture | A lossless JPEG 2000 texture asset (see below), in an item of the part. |

Each item has the key `name` (default: the file name without its extension) and an optional `description`. Creator, owner and last owner of the parts and
the items are the `--avatar`; the parts and items have full permissions (copy, modify and transfer) for the base, owner and next owner.

### The check

`--check <folder>` writes the archive out again with the exporter into `<folder>` and compares it with the input. For a linked object it compares link
order; each part's name and description; each part's place and turn against the manifest (places within half a millimetre); each part's triangle count; and
the contents: the names and types the exporter lists, and, from the archive itself, every script byte for byte, every notecard's text and every texture's
pixels. Exit code 1 means the archive was written and the check failed.

## Where an archive came from

Every archive `oar-import` writes holds a text file, `oar-import-notes.txt`, and the run report ends with the same lines. They give the release of the tool,
the manifest with its SHA-256, and, under each part, its source (a folder, or a `.glb` and node) and the SHA-256 of every file the import read for it: the
`.glb` files, the pictures, the scripts and the notecards. Paths are relative to the manifest's folder, written with forward slashes; a single-part import
names its folder and lists its files. The text uses line feeds only. For the test house of the checks below it reads:

    oar-import 0.4.0
    manifest: linkset.json sha256 9fdbdb43999d8ab5666dd56c21ff8da96e5d89d87084ec740bfa3407ac610806
    part 1 'House': glb building.glb node 'House'
      building.glb sha256 de9664c59f1ea069e02566d54964a141c8944a73521abdf3b3da7f87a878678e
      readme.txt sha256 4b5c5a274def64960f8d3f9add9651da77c14eda392a317043478f751936c0d7
    part 2 'Door': glb building.glb node 'Door'
      building.glb sha256 de9664c59f1ea069e02566d54964a141c8944a73521abdf3b3da7f87a878678e
      door.lsl sha256 ecc51b24972b7ef26f812fc1f3c5d6f2152875cc8c5e7875277649865c285bfa

Nothing in the text depends on where the files are kept, so the same input files and the same `--created` value give the same archive bytes in any folder
and on any platform. A region that loads the archive ignores the file (the loader's entry loop has no branch for an entry it does not know). The hashes
are taken from the files as they are when the archive is written; the import reads each file once for its content and once for its hash.

## Textures the viewer can decode

A texture asset the tools write is a bare JPEG 2000 codestream: one tile, reversible (lossless), resolution-position-component order and as many wavelet
levels as the viewer's discard levels need. It is not wrapped in JP2 boxes. The viewer's decoder is created for a bare codestream only
(`opj_create_decompress(OPJ_CODEC_J2K)`, indra/llimagej2coj/llimagej2coj.cpp lines 311 and 385), so a JP2 file shows as a grey face, while the region's own
decoder reads either. This applies to the pictures of `oar-import` and to the new pictures `oar-restore` gives an added part. An archive written by an
earlier build of `oar-import` holds JP2 texture assets; write it again to get textures a viewer shows.

## The check after a load

After `load oar --merge`, log an avatar in to the region and run

    python region-texture-check.py --login-uri <login uri> --first <first name> --last <last name> <region> <archive.oar> [<archive.oar> ...]

with the avatar's password in the environment variable `OAR_CHECK_PASSWORD` (it is never read from the command line). The check needs Python 3 with
Pillow and `oar-texfetch` beside it. `oar-texfetch` logs the avatar in, asks the region for each texture of the archives as a viewer does (the region's
GetTexture capability) and logs out. A texture passes when the region serves the bytes the archive holds, they are a bare codestream, and they decode to the
archive's pixels; otherwise the line says which of these failed ("JP2 boxes" is the grey face). Exit 0 all pass, 1 some texture failed, 2 could not run.

Run in a test region with an earlier form of the fetch program (the same steps, built on a newer LibreMetaverse build), the check passed all 5 textures of
two corrected archives and failed all 5 textures of the two archives with JP2 files. `oar-texfetch` as packaged here is built on the public
LibreMetaverse 3.1.4 package and has not been run against a region (see the limits).

## What 0.4 cannot do

- **Part ids are new in the region.** `load oar --merge` gives every loaded part a new id, so the ids in the report and in the notes are the archive's,
  not the region's. A script or a test that needs a part's key reads it from the region after the load.
- **A node part has no lower levels and no collision shape of its own** unless the manifest names the nodes (`lod2`, `lod1`, `lod0`, `physics`). Without
  them every level of detail is the full mesh, so the viewer never gets a cheaper level, and the part collides as the bounding hull of its mesh. A level of detail that uses a
  material the full model does not have is refused.
- **A part cannot be given no collision shape.** A part without `physics` collides as its bounding hull, which fills the room of a hollow part such as a
  vault or an arcade. The way round is a tiny collision node for such a part.
- **Land impact of a linked object of several parts has been measured in one region only.** There it equalled the number of parts (24 for a 24-part
  object); the source of that region says the same (the part count, server cost 0, a constant streaming cost per part). A grid that prices by the
  viewer's streaming formula would charge much more for the same object (about 1 496 by that formula for the 24-part object), and another region was
  not measured.
- **Not done:** roughness and metal of PBR materials, texture transforms, more than one convex hull per part, and a hinge: a door leaf turns about its own
  centre unless its script swings it about an edge.
- **`oar-texfetch` has not been run against a region in this form.** It starts, reports an unreachable login and a missing password correctly on Windows
  and Linux (below), and the check around it is tested with a stand-in fetch program.
- Regions older than 6.4.2 and viewers were not tried; the limits of the merge in the 0.3.1 notes stand.

## Packages

`oar-tools-windows-x64.zip` and `oar-tools-linux-x64.tar.gz` (64-bit, glibc; the programs that use the system's OpenSSL library on Linux, `oar-convert`
and `oar-restore` as in 0.3.1, and `oar-texfetch` for an https login, need it installed). Each holds `oar-convert`, `oar-export`, `oar-restore`,
`oar-import` and `oar-texfetch`, the two native libraries they share, `region-texture-check.py`, QUICKSTART.txt, this file, MPL-2.0.txt and
THIRD-PARTY-NOTICES.txt (the licences of every component, including the LibreMetaverse client library `oar-texfetch` carries). The SHA-256 of each package is
in `SHA256SUMS.txt` beside them on the release page.

## Checks

Run from the unpacked packages, on Windows and, as an ordinary user with an empty home folder, in an Ubuntu 24.04 container (the files are `oar-convert`,
`oar-export`, `oar-restore`, `oar-import` and `oar-texfetch`, from the package):

- all five programs print `0.4.0` with `--version`;
- `oar-convert --merge` over seven test archives, twice on each platform (the second pass over the first pass's output): for every archive the first-pass
  and second-pass archives are byte-identical between Windows and Linux, so are all the files of the plan folders (50 to 76 files each, none with a carriage
  return), and on each platform the second pass equals the first; the seven first-pass archives are byte-identical to the 0.3.1 ones;
- `oar-export` of a 48-part test archive followed by `oar-restore` of the untouched `.glb` files writes an archive equal to the original in all 56
  entries, on both platforms;
- `oar-import` of a one-part radio model and of a two-part test house (a shell with a notecard and a door leaf with a script, both nodes of one `.glb`),
  each twice with the same `--created` and each with `--check`: all runs exit 0 and the check passes; the two runs on one platform are byte-identical; the house
  archive is byte-identical between Windows and Linux; the radio archive differs between the platforms in the notes file only, in the name of the input
  folder, which the test gave different names on the two platforms (the notes record it);
- `oar-texfetch` with an unreachable login address and with no password: it reports the failed login or the missing variable and exits with 2, on both platforms;
  `region-texture-check.py` from the package with it, against the same unreachable address: exit 2, "the fetch client could not run".

Tests: the converter test project 1116 passed, the restore test project 80 passed, and the 11 tests of `region-texture-check.py`
(`region_texture_check_test.py`, run with `python -I`) passed.

Built from commit be83fb201f; the tag names a later commit that changes documentation only.
