<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4.3: fix permissions and restore link order

0.4.3 lets you fix two problems that `oar-inspect` (0.4.2) shows, safely: **permissions** of your own creations, and **link order** lost when builds were combined.
Both fixes are modes of `oar-convert`, which already writes archives; there is no new program. `oar-export`, `oar-restore`, `oar-import` and `oar-texfetch`
write the same files as in 0.4.2 and only print `0.4.3` for `--version`. The earlier notes ([OAR-TOOLS-0.4.2.md](OAR-TOOLS-0.4.2.md),
[OAR-TOOLS-0.4.1.md](OAR-TOOLS-0.4.1.md), [OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md)) are in the package beside this file.

Both modes follow the same rules. The input archive is only read, and the output goes to the file you name with `--out`, which must not exist yet. The same
command on the same input writes the same bytes. Everything a mode does not need to change is copied byte for byte, and an object file that does change is
edited in place: only the digits of the changed numbers (and, for link order, the order of the part elements) differ. Every run prints a report of exactly what
changed, and `--report <folder>` also saves it as a text file and a JSON file. Both work on OARs and on IARs.

## What changed in oar-inspect

* **Fewer flags by default.** A part or item is flagged only when its **base, owner or next owner** mask lacks modify, copy or transfer. The group and everyone masks
  are still shown, but a group or everyone mask of 0 no longer flags every part. `--strict` flags all five masks, as 0.4.2 did.
* **Effective permissions of each object.** Under each object the report shows what the region makes of the object's permissions from its root part, the contents of
  every part and the base mask, as `effective  owner MC-  group ...  everyone ...  next owner ...`, and under it `restricted by:` lines naming the part or item that
  takes modify, copy or transfer away. One no-copy item inside any part makes the whole object no-copy, and the line names the item and its part. The rules are the
  region's: `SceneObjectGroup.AggregatePerms` with `SceneObjectPart.AggregateInnerPerms` and `SceneObjectPartInventory.AggregateInnerPerms` for the owner, group
  and everyone columns (the root part's masks are the object's masks, the other parts' own masks are not used, every item in every part narrows the result, a
  landmark is ignored, modify comes only from the root part, and everyone never gets modify), and `SceneObjectGroup.CurrentAndFoldedNextPermissions` with
  `SceneObjectPartInventory.MaskEffectivePermissions` for the next owner column (copy and transfer pass outwards from an item whose current and next owner masks
  lack them; the owner mask is also limited by every part's base mask). The CSV has the columns `effective_owner`, `effective_group`, `effective_everyone`,
  `effective_next` and `restricted_by`; the JSON has an `effective` object.
* **`--names <file>`.** A list of account ids and names, one per line: the id, then a space, tab, comma or equals sign, then the name. Blank lines and lines starting
  with `#` are skipped. A name from the list is used for a creator, owner or last owner the archive does not name itself; a name the archive carries (creator data)
  is kept. A line that is not an id and a name is refused with its line number.

## Fixing permissions

    oar-convert <archive.oar|archive.iar> --fix-perms --creator <id>[,<id>...] --out <new archive> [--report <folder>]

Every part, and every item in a part's contents, created by one of the named creators gets modify, copy and transfer on its **base, owner and next owner** masks
(the three bits are added; every other bit of the mask stays). In an IAR the inventory items they created get the same, and so do the parts and contents of the
objects the IAR holds, including each linkset of a coalesced object.

* **`--creator` is required.** Without it the program refuses and writes nothing: it will not change what someone else made. Several creators are separated by commas.
* **Anyone else's work is left exactly as it was**, part by part and item by item: a part you made can hold a script someone else made, and the script keeps its masks.
  The report lists every part and item left alone with its creator.
* **Not touched:** the group and everyone masks, creator, owner, last owner, group, and every other field.
* A mask the file does not hold is reported and nothing is written for it.

How to use it:

1. `oar-inspect build.oar` and look at the `ACCOUNTS` list at the top for your account id (the id that is creator of your parts). Put it in a names file and pass
   `--names` if you want to read names instead of ids.
2. `oar-convert build.oar --fix-perms --creator <your id> --out build-fixed.oar --report fix-report` and read the `CHANGED` list: each line gives the object, part or item,
   the mask, and its letters and number before and after.
3. `oar-inspect build-fixed.oar --compare build.oar` should show only those permission changes.

## Restoring link order

    oar-convert <archive.oar|archive.iar> --relink-from <reference archive> --out <new archive> [--report <folder>]

When builds are combined or rebuilt, the order of the parts of a linkset can change, and scripts that address parts by link number then act on the wrong parts.
This writes a copy of your archive in which each object that is also in the reference archive has the reference's link order back.

* **Objects are paired as `oar-inspect --compare` pairs them:** by name, and where several share a name, by part count and shape. Parts are matched by **name,
  shape and position relative to the root**.
* **The reference's order** is its stored link numbers (an unnumbered part comes after the numbered ones), then its place in the file.
* **The root stays the root.** Parts the reference does not hold keep their order, after the matched ones. In the output the link numbers are 1, 2, 3 ... in file
  order, so `oar-inspect` reports `link order: ok`.
* **Objects and parts that cannot be matched are left unchanged and listed** under `NOT MATCHED`. An object that already has the reference's order is not touched.
* **Scripts.** Every script whose use of link numbers changes is listed with its object, part and the lines concerned: a script that addresses a link number whose part
  changed, a script that counts the parts or asks which part it is in, and one that reads the sender's link number in `link_message`. A script that addresses only
  `LINK_SET`, `LINK_ROOT`, `LINK_THIS` and the like is not affected and is not listed. The check reads script text, so a script with no readable source is not listed.

How to use it: keep the archive you made before combining as the reference, run the command on the combined archive, read the report, then `oar-inspect combined-fixed.oar --compare
reference.oar`; for the objects found in both, the link order differences are gone.

## What the region does to permissions when it loads an OAR

Loading an OAR into a region does **not change the permission masks** of parts or of items in their contents: `ArchiveReadRequest.ModifySceneObject` handles only creator,
owner, last owner and group, and the masks and the link numbers come through as stored (the loader keeps a stored non-zero link number, `SceneObjectSerializer.FromXml2Format`).
What it does change, for every part and every item in a part's contents:

* **Owner and last owner.** If the id is not an account of that grid (or a group), it is replaced by the *default user*, which is the region's estate owner unless the load names
  another with `--default-user` (`ResolveUserAliasOrDefault`; `--no-defaultuser` keeps the id as it is). An id that exists on the grid is kept.
* **Creator.** Kept when the part carries creator data (the "home;First Last" form that visitors from other grids have); otherwise an id unknown to the grid is replaced the same way.
* **Group.** An id with no matching group on the grid becomes empty.

With `load oar --merge` an object that is already in the region with the same id is skipped (the log says how many) unless `--mergeReplaceObjects` is given, so to see your fixed
version of objects already in the region, replace them or load into a fresh region. A load without `--merge` empties the region first.

## Limits

* One mode per run: `--fix-perms` and `--relink-from` cannot be combined with each other or with `--merge`. To do both, run the fix and then the relink on its output.
* Link order is restored by matching names, shapes and positions; two parts that agree in all three are paired in file order.
* The script check reads script text; a link number the check cannot work out is listed as such whenever the numbering changed.

## Packages

The Windows zip and the Linux tar.gz carry the same six programs as 0.4.2. `SHA256SUMS.txt` lists the packages.
