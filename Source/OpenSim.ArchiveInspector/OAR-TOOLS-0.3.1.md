<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.3.1: one package, and the same bytes on every platform

0.3.1 changes two things since 0.3:

1. **Line endings.** The notecard a merged part holds (the list of the parts it replaced) is written with line feeds on every platform. In 0.3 it
   used the line ending of the platform, so a merge made on Windows and the same merge made on Linux differed in those notecards. They now give
   the same bytes.
2. **One package with all three programs.** 0.3 packaged `oar-convert` alone, and `oar-export` and `oar-restore` stayed at 0.2. The 0.3.1 packages
   hold `oar-convert`, `oar-export` and `oar-restore`, and each prints `0.3.1` with `--version`. `oar-export` and `oar-restore` behave as they did in
   0.2. The version stamped into a `.glb` is still 0.2: it is the file format version and changes only when the `.glb` changes, so files made by
   0.2 still restore.

A merge made by 0.3 on Windows differs from the 0.3.1 merge of the same archive in the manifest notecards and in the object files that name them
(the notecard's asset id is part of the object file), and in nothing else. Measured on seven test archives: four merge nothing and their output is
byte-identical to 0.3's; for the other three the 0.3.1 output differs from 0.3's in 28 notecards and 19 object files (the 404-prim shop archive),
34 and 22 (the 464-prim one) and 3 and 3 (the 90-prim one), and every other entry is equal. With the notecard asset ids matched, each differing
object file is equal to its 0.3 counterpart.

The rest of this file is the 0.3 text, carried forward.

# oar-convert 0.3: merging parts

0.3 adds `--merge` to `oar-convert`: the parts of each linkset that can merge become fewer mesh parts, and the parts that cannot stay as they are.
Without `--merge` the command does what 0.2 did, byte for byte. The details of the merge (what merges, why a part stays, levels of detail,
collision) are in [MERGE-PLAN.md](MERGE-PLAN.md).

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
- A merge gives the same archive, byte for byte, on Windows and on Linux (0.3.1; in 0.3 the manifest notecards used the line ending of the
  platform). Checked on seven test archives, each merged twice on each platform: the seven first-pass archives agree across the platforms and
  the second pass equals the first on both. The files in the plan folder (`merge-plan.json`, `merge-plan.txt`, `linksets/`) are still written with
  the line ending of the platform: on three of the archives every plan file differs between Windows and Linux and none differs once the line
  endings are ignored.
- The front door of the walk-test shop is closed in the original archive as well, so the walk through a door was proved for the back door only.

## Packages

`oar-tools-windows-x64.zip` and `oar-tools-linux-x64.tar.gz` (64-bit, glibc; `oar-convert` and `oar-restore` need the system's OpenSSL library on
Linux). Each holds `oar-convert`, `oar-export` and `oar-restore`, the two native libraries they share, QUICKSTART.txt, MPL-2.0.txt and
THIRD-PARTY-NOTICES.txt. Each program prints `0.3.1` with `--version`.

Checked on the unpacked packages, on Windows and, as an ordinary user with an empty home folder, in an Ubuntu 24.04 container:
- all three programs print their name and `0.3.1`;
- `oar-export` of a 48-part test archive followed by `oar-restore` of the untouched `.glb` files writes an archive equal to the original in all 56
  entries, on both platforms; the 103 files of the two exports other than the two export-summary files are equal;
- `oar-convert --merge` over seven test archives, twice on each platform (the second pass over the first pass's output): the first-pass archives
  agree between Windows and Linux, and on each platform the second pass equals the first.

## Checksums (SHA-256)

```
cdd862814b6b0ba5710a3ee06537840fdbb622e8a03e827467c6130108eadbb1  oar-tools-windows-x64.zip  (129135480 bytes)
e2e599c941ab5cca2e990b7e7cdbfa089adedd0c9e6c06d7d369f57103ee2cef  oar-tools-linux-x64.tar.gz  (128325068 bytes)
```

Built from commit d641a09bc8770fa36b64dfc6f6d77095ed56d5a2 (the program is the same in any commit after it that changes documentation only).
