<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar-convert --merge --plan-only

Plans how the parts of each linkset in an archive could be merged into fewer mesh parts. It writes the plan and nothing
else: no archive, and the source is only read. Merging itself is not built yet, so `--merge` needs `--plan-only`.

    OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --plan-only --merge-plan-dir <folder>

The folder gets `merge-plan.json` (totals and a list of linksets), `merge-plan.txt` (the same as a report) and
`linksets/<n>-<name>-<id>.json`, one file for each linkset with every merged part and every part that stays. The same
archive gives the same plan, byte for byte.

## What merges

Parts of one linkset merge when nothing sets them apart. Parts of different objects never merge, so an object of one part
has nothing to merge with. A group of parts that may share a mesh has the same creator, permission masks, surface
material, physics class (solid, phantom or none) and alpha class (alpha-blended faces or not). Each group is cut into
merged parts that stay inside these limits: at most 8 faces (material slots) per part, 65,535 vertices per face, 65,535
vertices in the physics block, a longest side of 24 m (4 m when the part holds alpha-blended faces), and nothing over
64 m. The plan chooses between cutting by place and cutting by material, whichever gives fewer parts.

A face's slot is set by its texture, colour, glow, shininess, bump, fullbright, texture mapping, legacy material and alpha.
Repeats, offset and rotation are not part of it when the face can be baked into the merged texture coordinates. A face
cannot be baked when it is a PBR or media face, uses planar mapping, or names a legacy material whose normal or specular
map is drawn with a different transform from the diffuse texture (or whose material is not in the archive). Such a face
keeps its own slot, keyed with its transform.

## Why a part stays (the `rules` in the plan)

| rule | code | meaning |
|---|---|---|
| 7 | SINGLE_PART_OBJECT | the object has one part |
| 9 | SOFT_CAP, HARD_CAP, SPLIT_BY_CAPS | the part is longer than the cap, or no neighbour fits with it |
| 10 | SOLE_CREATOR_OR_PERMISSIONS | the part differs from the others in creator or permissions |
| 11 | ALREADY_MESH | an existing mesh keeps its makers' levels of detail |
| 17 | SCRIPTED, ADDRESSED, PROTECTED_LINK_NUMBER, LINKSET_LOCKED | see below |
| 18 | ALPHA_CAP | alpha-blended parts merge only within the alpha size cap |
| part | CONTENTS, LIGHT, FLEXI, SIT_TARGET, HOVER_TEXT, PARTICLES, SOUND, PHYSICAL, VOLUME_DETECT, CONVEX_PHYSICS, ROOT_PHYSICS_NONE, NOT_PRIM, NOT_CONVERTED | per-part settings a merged mesh cannot carry |
| group | SOLE_IN_CLASS | no other part that can merge shares its group |
| sculpt | SCULPT_NOT_PLANNED | sculpts are converted on their own |

A part can have several reasons; the plan lists all of them, and the totals count the first and every one.

Scripts (rule 17) are read with the same scan `oar-eligibility` uses. A part that holds a script stays. A part that a script
addresses by a literal link number stays, and so does every part numbered at or below the highest such number, so those
numbers do not change. A script that addresses a part in a way the scan cannot work out (a variable link number,
LINK_SET, LINK_ALL_OTHERS, LINK_ALL_CHILDREN), counts or numbers parts (llGetNumberOfPrims, llGetObjectPrimCount,
llGetLinkNumber, llDetectedLinkNumber), reads the sender number in a link_message handler, or cannot be read at all locks
the whole linkset: every part stays. A message to LINK_SET with llMessageLinked does not lock it.

## After the merge

Link numbers are written 1 to N in the order of each output part's lowest original number, so the root stays 1. Each merged
part carries a manifest (the original part names, creators and ids, and the plan file name) for a notecard in the merged
root. A merged part's collision shape is a triangle mesh: a region older than 6.4.2 (the Jolt native-shape fix, NGC PR #378)
can treat a merged part with few faces as a solid box. The tool runs offline and only warns.
