<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar-convert 0.3: merging parts

0.3 adds `--merge` to `oar-convert`: the parts of each linkset that can merge become fewer mesh parts, and the parts that cannot stay as they are.
Without `--merge` the command does what 0.2 did, byte for byte. `oar-export` and `oar-restore` are unchanged; the version stamped into a `.glb`
is still 0.2, so files made by 0.2 still restore. The details of the merge (what merges, why a part stays, levels of detail, collision) are in
[MERGE-PLAN.md](MERGE-PLAN.md).

## Usage

    oar-convert <archive.oar> --out <new.oar>
    oar-convert <archive.oar> --merge --plan-only --merge-plan-dir <folder>
    oar-convert <archive.oar> --merge --out <new.oar> --merge-plan-dir <folder> [--report <folder>] [--lod-pixel-limit <pixels>]
    oar-convert --version

- The source archive is only read; the output must not exist yet.
- `--merge --plan-only` writes the plan and no archive. The plan folder holds `merge-plan.json`, `merge-plan.txt` and one file per linkset under
  `linksets/`, naming every merged part and, for every part that stays, the rules that keep it. Start here: it shows what a merge would do to an
  archive before anything is written.
- `--merge --out` writes the new archive. A merged part is a mesh part with every face drawn as it was; it holds a notecard listing the parts it
  replaced. The command ends with a check of the new archive against the source, as it does without `--merge`.
- `--lod-pixel-limit <pixels>` (only with `--merge`; a positive number; default 1) sets how far a lower level of detail of a merged part may
  differ from the full mesh, in screen pixels at the distance where the viewer switches to it. Without `--merge` it is refused with exit code 2 and
  nothing is written.
- Exit codes are those of `oar-convert`: 0 the check passed, 1 the check failed or a mesh failed its validity check (nothing written), 2 the
  program could not run.

## The cost line

Merging lowers the number of parts in the region. It can raise what a viewer is charged to stream the build, because the viewer prices a simple prim
such as a box as one triangle and a mesh by the size of its levels of detail. The report puts the two side by side:

    before -> after: region part count 464 -> 95; viewer streaming cost 29.18 -> 250.62

The first number is the region's part count (what land impact counts). The second is the viewer's streaming cost, worked out with the viewer's own
formula (see "Viewer streaming cost" in MERGE-PLAN.md). The second number rises because a box prim is priced at one triangle however large it is, while a
merged mesh holds the real triangles of its members. The line is a report and never a limit: the merge does not keep prims to hold the cost down. A
larger `--lod-pixel-limit` lowers the viewer cost at the price of a difference that can be seen at the switch distance. On the 464-prim test
archive the viewer cost was 29.18 before merging and 250.62, 221.03, 198.23 and 76.82 after, at limits of 1, 4, 8 and 64 pixels; the part
count fell from 464 to 95 at every limit. Physics and server costs are the region's own
model and are not part of the figure.

## Link safety

A merge does not change anything a script in the archive can observe about its own linkset: the link numbers it can reach, the names and
descriptions of the parts, the number of parts, the root, the sit targets, and which parts hold scripts. The scan reads script text, not what the
script does when it runs, so it can only keep more than is needed, never less.

- A part that holds a script (SCRIPTED), a part a script addresses by a link number (ADDRESSED), and every part numbered at or below the highest
  number a script addresses (PROTECTED_LINK_NUMBER) stay. The root stays when a script addresses it.
- A script that counts the parts, reads its own or a touched part's link number, reads the sender number in a `link_message` handler, addresses
  the parts with LINK_SET, LINK_ALL_OTHERS or LINK_ALL_CHILDREN, or passes a link number the scan cannot work out locks its linkset
  (LINKSET_LOCKED): every part of it stays. A script that cannot be read locks its linkset too.
- The link number is read wherever the script gives one: as the first argument of the llSetLink, llGetLink, llLink and osXxxLink calls, as the
  second argument of llSitOnLink and llGetObjectLinkKey, as the third of osNpcTouch, and after PRIM_LINK_TARGET in the parameter list of
  llSetPrimitiveParams, llGetPrimitiveParams, llSetLinkPrimitiveParams, llSetLinkPrimitiveParamsFast, llGetLinkPrimitiveParams and
  osGetLinkPrimitiveParams. A parameter list the scan cannot follow (a variable, for example) could hold a PRIM_LINK_TARGET, so it counts as a
  link number that cannot be worked out and locks the linkset.
- A part with a sit target of its own stays (SIT_TARGET).

The plan codes that report this are SCRIPTED, ADDRESSED, PROTECTED_LINK_NUMBER, LINKSET_LOCKED and SIT_TARGET; every kept part of a scripted linkset carries a
code and a detail line, and for a lock the detail names the script and the line.

Tried on a scripted test archive of seven script variants (56 parts): in a region, the original, the merged and the twice-merged archive give
the same hover text from every script (each script writes what it sees into its hover text: the number of parts, its own link number, the names
and descriptions of parts it addresses, the root, a sit target), and the merged archive reads back with 0 field differences in the parts' extra
parameter blocks.

## Running the merge again

A merged part is a mesh whose description starts with `oarx-merge ` and whose inventory holds a `Merge manifest` notecard. A second run over the
output keeps such a part (plan code ALREADY_MERGED) and merges nothing more, so a second run at the same `--lod-pixel-limit` writes the same bytes as
the first. This was checked on seven test archives, of which three merge, and on a third run. The archive records the limit its parts were made at
(`lodPixelLimit` in `merge-plan.json` when a merge was carried out, and the line `Levels of detail made at a limit of N pixels.` at the end of every
manifest notecard); a run at another limit warns that the merged parts keep the levels of detail they were made with and does not make them again.

A second pass may merge parts that a size cap kept apart in the first. That merges more and damages nothing: the parts the first pass made are not
touched. It did not happen in any archive tried. An archive merged by an earlier build records no limit; a second pass over it cannot name the
limit, still merges nothing more, and says nothing about it.

## What is not covered

- Collision: only the Jolt triangle mesh was measured. A merged part carries the triangle mesh of its members, and a hull list when every member is
  convex. A region on the Jolt physics module collided with the triangle mesh in a walk test (walls, a shelf, a dresser and a back door gave the
  same stopping places for the merged and the original archive). Other physics engines were not measured, and what they do with the hull list is
  unknown.
- Regions older than 6.4.2 were not tried. A region without the Jolt native-shape fix can treat a merged part with few faces as a solid box (see
  "Collision" in MERGE-PLAN.md).
- Viewers were not measured: the viewer cost is the viewer's formula applied by the tool, not a measurement in a running viewer.
- A script that builds a link number or a parameter list in a way the scan cannot follow locks its linkset, so such linksets keep every part. On
  the test archives this cost one single-part linkset and no parts; an archive full of such scripts would keep more parts.
- The front door of the walk-test shop is closed in the original archive as well, so the walk through a door was proved for the back door only.

## Packages

`oar-convert-windows-x64.zip` and `oar-convert-linux-x64.tar.gz` (64-bit, glibc; the program needs the system's OpenSSL library on Linux). Each holds
the program, the two native libraries, QUICKSTART.txt, MPL-2.0.txt and THIRD-PARTY-NOTICES.txt. `oar-convert --version` prints `oar-convert 0.3`.
