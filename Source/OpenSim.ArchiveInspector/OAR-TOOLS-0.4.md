<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4 (draft): oar-import, linked objects, and textures the viewer can decode

This is the draft of the notes that follow [OAR-TOOLS-0.3.1.md](OAR-TOOLS-0.3.1.md). The programs still print `0.3.1`: the version is set when the
release is made.

## oar-import

`oar-import` writes an archive that holds one object made from `.glb` files, ready for `load oar --merge`. It has two forms.

### One mesh part

    oar-import <folder> --out <new.oar> --avatar <uuid> [--name <text>] [--description <text>] [--base <name>] [--position <x,y,z>] [--check <folder>]

`<folder>` holds the files a mesh tool writes: `base.glb` (the full mesh, one face for each material, the pictures inside the file), `base_lod2.glb`
(medium), `base_lod1.glb` (low), `base_lod0.glb` (lowest) and, optionally, `base_physics.glb` (the collision shape: a convex shape becomes one convex
hull, any other a triangle physics mesh; without it the part collides as the bounding hull of its mesh).

### A linked object: the manifest

    oar-import --manifest <linkset.json> --out <new.oar> --avatar <uuid> [--position <x,y,z>] [--check <folder>]
    oar-import <folder holding linkset.json> --out <new.oar> --avatar <uuid> [--position <x,y,z>] [--check <folder>]

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

`--position` puts the root part's model origin in the region, as for one mesh part.

### Contents

| `type` | `file` | What is stored |
|---|---|---|
| `script` | LSL text | The text as written, in an item of the part. The script has no saved state, so the region compiles it when the object loads and it starts running. |
| `notecard` | a text file | A notecard in the Linden text format; line endings are written as line feeds. At most 65 536 bytes. |
| `texture` | a PNG or JPEG picture | A lossless JPEG 2000 texture asset (see the next section), in an item of the part. |

Each item has the key `name` (default: the file name without its extension) and an optional `description`. Creator, owner and last owner of the parts and
the items are the `--avatar`; the parts and items have full permissions (copy, modify and transfer) for the base, owner and next owner.

### The check

`--check <folder>` writes the archive out again with the exporter into `<folder>` and compares it with the input. For a linked object it compares link
order; each part's name and description; each part's place and turn against the manifest (places within half a millimetre); each part's triangle count; and
the contents: the names and types the exporter lists, and, from the archive itself, every script byte for byte, every notecard's text and every texture's
pixels. Exit code 1 means the archive was written and the check failed.

## Textures the viewer can decode

A texture asset the tools write is a bare JPEG 2000 codestream: one tile, reversible (lossless), resolution-position-component order and as many wavelet
levels as the viewer's discard levels need. It is not wrapped in JP2 boxes. The viewer's decoder is created for a bare codestream only
(`opj_create_decompress(OPJ_CODEC_J2K)`, indra/llimagej2coj/llimagej2coj.cpp lines 311 and 385), so a JP2 file shows as a grey face, while the region's own
decoder reads either. This applies to the pictures of `oar-import` and to the new pictures `oar-restore` gives an added part. An archive written by an
earlier build of `oar-import` holds JP2 texture assets; write it again to get textures a viewer shows.
