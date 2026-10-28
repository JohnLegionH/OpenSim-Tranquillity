<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4.4: the script list of --relink-from, and the next owner column labelled as derived

0.4.4 changes two things in the 0.4.3 modes. There is no new program. `oar-export`, `oar-restore`, `oar-import` and `oar-texfetch` write the same files as in 0.4.3 and
only print `0.4.4` for `--version`. The earlier notes ([OAR-TOOLS-0.4.3.md](OAR-TOOLS-0.4.3.md), [OAR-TOOLS-0.4.2.md](OAR-TOOLS-0.4.2.md),
[OAR-TOOLS-0.4.1.md](OAR-TOOLS-0.4.1.md), [OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md)) are in the package beside this file.

## The next owner column is labelled as derived

`oar-inspect` shows the effective permissions of each object. The owner, group and everyone columns follow the region's own function, `SceneObjectGroup.AggregatePerms`. The
**next owner column does not**: no single region function gives it. It is this tool's combination of the root part's next owner mask, what every item in every part passes on
(`SceneObjectPartInventory.MaskEffectivePermissions`) and every part's base mask (`SceneObjectGroup.CurrentAndFoldedNextPermissions`). Read it as a derived reading, not as the region's answer.
0.4.4 says so wherever the column appears:

* **Text:** the object line reads `next owner (derived) ...`, and the legend in the summary explains why.
* **CSV:** the column `effective_next` is now **`effective_next_derived`**.
* **JSON:** the `effective` entry `nextOwner` is now **`nextOwnerDerived`**.
* `oar-inspect --help` says it too.

If you read the CSV or JSON of 0.4.3 with a script of your own, change the column or entry name. The letters are the same as in 0.4.3.

## The script list of --relink-from

`oar-convert <archive> --relink-from <reference> --out <new>` lists every script whose use of link numbers changes, with its object, its part, the script's name and the line of each use.
This is the safety net for scripts that address parts by number: when parts are renumbered, such a script acts on other parts than it did.

* **What is listed.** A script that names a link number whose part changed (`llSetLinkPrimitiveParams`, `llSetLinkPrimitiveParamsFast`, `llMessageLinked` to a number, `llGetLinkName`,
  `llGetLinkKey`, the other `llSetLink...`/`llGetLink...` calls, and `PRIM_LINK_TARGET` inside a list of rules), a script that counts the parts or asks which part it is in
  (`llGetNumberOfPrims`, `llGetLinkNumber`, `llDetectedLinkNumber`), a script whose link number the check cannot work out (a variable, for example), and a `link_message` handler that reads the sender's
  link number. Each entry reads like `line 13: llGetLinkName - link 3 was part "Post A" and is now part "Post B"`.
* **What is not listed.** A script that uses only `LINK_SET`, `LINK_ALL_OTHERS`, `LINK_ALL_CHILDREN`, `LINK_THIS`, `LINK_ROOT` or link 1 (`llMessageLinked(LINK_ALL_OTHERS, ...)` among them), and any script of an
  object whose parts keep their numbers.
* **New: SLua scripts.** The check reads LSL text only. A script written in SLua (Lua) cannot be read by it, and 0.4.3 left such a script out of the list without a word. 0.4.4 recognises an SLua script
  (its first line is a Lua comment `--`, or the header `//SLua:slua` or `//SLua:lua`) and lists it as `not read: an SLua (Lua) script ...` whenever any part of its object changes number, so you can check it by hand.
  A script whose text the archive does not hold is still not listed, as in 0.4.3.

How to use it: run `--relink-from` with `--report <folder>`, read `SCRIPTS WHOSE LINK NUMBER USE CHANGES` in the report (or `scripts` in the JSON), and open each script named there. After the load, a script
that addresses a part by number acts on the part that now holds that number, which is the reference's part.

## Limits

* The check reads script text. A link number it cannot work out is listed as such whenever the numbering changed; it never decides a script is harmless.
* One mode per run, as in 0.4.3: `--fix-perms` and `--relink-from` cannot be combined with each other or with `--merge`.

## Packages

The Windows zip and the Linux tar.gz carry the same six programs as 0.4.3. `SHA256SUMS.txt` lists the packages.
