<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4.2: oar-inspect lists link order and permissions

0.4.2 adds one program, **oar-inspect**, and changes nothing in the other five: `oar-convert`, `oar-export`, `oar-restore`, `oar-import` and `oar-texfetch`
write the same files as in 0.4.1 and only print `0.4.2` for `--version`. The earlier notes ([OAR-TOOLS-0.4.1.md](OAR-TOOLS-0.4.1.md),
[OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md)) are in the package beside this file.

`oar-inspect` reads an OAR or an IAR (it finds out which from the archive itself) and prints, for every object, every part in link order and every
permission mask of every part and of every item inside it. With `--compare` it shows what changed in link order and permissions between two archives,
which is what you want before and after combining builds. It only reads: it never writes into an archive.

## Using it

    oar-inspect <archive.oar|archive.iar> [--csv | --json] [--filter <text>] [--compare <reference archive>] [--out <file>]

| Option | What it does |
|---|---|
| (none) | A readable report on standard output: a summary, a count per account id, then every object. |
| `--csv` | One row per part, per item inside a part and per IAR inventory item, for a spreadsheet. Text that a spreadsheet would take for a formula (it starts with `=`, `+`, `-` or `@`) gets a leading apostrophe. |
| `--json` | The same report for other tools. |
| `--filter <text>` | Keep only objects whose name contains the text, in any case. In an IAR it also keeps an inventory item whose name matches. The summary counts only what is kept. |
| `--compare <reference>` | Compare with a reference archive (below). Works with the text, `--csv` and `--json` outputs. |
| `--out <file>` | Write the report to a file. Refused when the file is the archive itself. |

Exit codes: 0 read fully (with `--compare`: no differences), 1 read partly (the report says why), 2 not an archive or unreadable, 3 `--compare` found
differences, 64 bad command line.

### What the report holds

* **Link order, per object.** Each part with its link number as stored in the file, its place in the file (the root part first, then the others in the order
  the file lists them), name, description, position inside the object and physics shape type (Prim, None or Convex; a part with no stored shape type is
  Prim, which is what the region uses). A phantom part is marked. The object line says `link order: ok` or `PROBLEM` with the reason: link numbers that are not in
  file order, a number stored twice, a gap, or parts that store no number (the loader numbers those by file order). A single part may store 0 or 1.
* **Permissions, per part and per item inside a part.** Creator, owner, last owner, group, and the five masks (base, owner, group, everyone, next owner) as the
  letters `M` (modify), `C` (copy) and `T` (transfer), with a dash where the mask lacks it. A mask that lacks one of the three is **flagged** and the report says
  which mask lacks which permission. Every mask counts, so a group or everyone mask of 0 is flagged; the summary lists how many parts and items lack something in
  each mask, so those expected zeros can be told apart from a next owner mask that lacks copy.
* **Names.** Creator names come from the creator data an archive carries. An owner or last owner is given a name when the same id is named as a creator
  anywhere in the archive. Otherwise only the id is shown, and the summary at the top counts each id by the roles it plays so the ids can be mapped to people.
* **IARs.** Every inventory item with its folder path and the masks of its own inventory record. An IAR item has no group mask in the format (the item
  serializer writes none), so the group column reads `n/a` and is not flagged. Object items are opened and listed as above. A coalesced object item holds
  several linksets in one asset; each is listed on its own with its link order and its offset inside the coalesced object (the `offsetx`, `offsety` and `offsetz`
  attributes that `CoalescedSceneObjectsSerializer.ToXml` writes). An object item whose asset is not in the archive is listed with a note.

### Comparing two archives

`oar-inspect after.oar --compare before.oar` pairs the objects of the two archives by name. When several objects share a name they are paired by part count and
then by shape, so objects in a different order still find each other. Objects found in only one archive are listed. For each pair, parts are matched by part id,
then by name and shape in file order, and the report lists, part by part: a changed link number or place in the file; a changed mask (the letters before and
after, and the numbers when the letters are the same); a changed creator, owner, last owner or group; parts and items found in only one of the two. An
OAR and an IAR can be compared with each other.

## Two cases

### Other avatars see no modify or no transfer

1. Run `oar-inspect archive.oar` and read the `LACKS` lines. The root part (link 1) matters most: in the region, the masks of the root part are the masks of the
   object, and the masks of the other parts are not used (`SceneObjectPart.AggregateInnerPerms`, "child prims permissions masks are irrelevant on a linkset").
2. What others may do with the object comes from the root part's everyone mask, narrowed by every item inside **every** part
   (`SceneObjectGroup.AggregatePerms`, `SceneObjectPartInventory.AggregateInnerPerms`). One script or notecard with an everyone or owner mask that lacks a
   permission is enough. The region never gives others modify through the everyone mask (`everyone &= ~Modify` in `AggregatePerms`), so a missing `M` in the
   everyone column is expected.
3. What the next owner gets comes from the next owner mask and from copy and transfer of the items inside every part
   (`SceneObjectGroup.CurrentAndFoldedNextPermissions`, `SceneObjectPartInventory.MaskEffectivePermissions`; modify does not propagate outwards). So one item inside
   any part that lacks copy or transfer in its owner or next owner mask takes copy or transfer from the whole object. The `items in part contents` lines and the
   `LACKS` lines under them name those items.
4. Fix the masks in the program that wrote the archive, or in a viewer, and write the archive again; `oar-inspect --compare` then shows the masks that changed.

### Checking link order before and after combining builds

1. Run `oar-inspect before.oar --filter "<build name>"` on each source archive and check that each object says `link order: ok`.
2. Combine the builds, then run `oar-inspect combined.oar --compare before.oar --filter "<build name>"` (once for each source archive). `LINK ORDER` lists every part whose
   link number or place in the file changed, with the old and new number. `ONLY IN REFERENCE` and `ONLY IN ARCHIVE` list objects that did not survive.
3. Use `--csv` to sort and filter the same information in a spreadsheet. The `link_order` column says `PROBLEM` for every row of an object whose numbers are off.

## Limits

* Names exist only where the archive carries creator data. An OAR carries no owner names at all.
* The reader holds the parts of every object in memory, so a very large OAR needs memory in proportion to its number of parts.
* A binary object payload from another fork is counted by the inspection library but not decoded, so it is not listed here.
* Only the object, inventory and part data are read; assets such as textures and meshes are not opened.

## Packages

The Windows zip and the Linux tar.gz carry the six programs: `oar-convert`, `oar-export`, `oar-restore`, `oar-import`, `oar-texfetch` and `oar-inspect`. Like the
other offline programs, `oar-inspect` runs on a Linux without the system's OpenSSL library: the publish step swaps the library's hash objects for managed ones,
as described in the 0.4.1 notes. `SHA256SUMS.txt` lists the packages.
