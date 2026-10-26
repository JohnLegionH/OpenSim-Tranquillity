<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar-convert --merge

Merges the parts of each linkset in an archive into fewer mesh parts. It can plan only, or plan and write the new archive.
The source archive is only read.

    OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --plan-only --merge-plan-dir <folder>
    OpenSim.ArchiveInspector oar-convert <archive.oar> --merge --out <new.oar> --merge-plan-dir <folder> [--report <folder>]

The plan folder gets `merge-plan.json` (totals and a list of linksets), `merge-plan.txt` (the same as a report) and
`linksets/<n>-<name>-<id>.json`, one file for each linkset with every merged part and every part that stays. The same
archive gives the same plan, byte for byte. Without `--merge` the command converts every convertible part one for one and
its output has not changed.

## What merges

Parts of one linkset merge when nothing sets them apart. Parts of different objects never merge, so an object of one part
has nothing to merge with. A group of parts that may share a mesh has the same creator, permission masks, surface
material, physics class (solid, phantom or none) and alpha class (alpha-blended faces or not). Each group is cut into
merged parts that stay inside these limits: at most 8 faces (material slots) per part, 65,535 vertices per face, 65,535
vertices in the physics block, a longest side of 24 m (4 m when the part holds alpha-blended faces), and nothing over
64 m. The plan chooses between cutting by place and cutting by material, whichever gives fewer parts.

A face is alpha-blended when its colour alpha is below 1, when its legacy material blends, or when its texture has an alpha
channel with a pixel that is not fully opaque (unless the legacy material's alpha mode is none or mask).

A face's slot is set by its texture, colour, glow, shininess, bump, fullbright, legacy material and alpha. Its texture
mapping, repeats, offset, rotation and flips are not part of it when the face can be baked into the merged texture
coordinates; they are then written into the coordinates as the viewer computes them (`xform` and `planarProjection` in the
viewer's `llface.cpp`) and the merged face is drawn with default mapping and no transform. A face cannot be baked when it is a
PBR or media face, uses a mapping other than default or planar, names a legacy material that is not in the archive, or names
one with normal or specular maps while the face or the maps are transformed (the viewer draws the maps with the material's own
transform, so baking the face's would move them twice). Such a default-mapped face keeps its own slot, keyed with its
transform, and its transform stays on the merged face.

## The merged part

A merged part is its lowest-numbered member rewritten in place as a mesh part, so it keeps that member's id, owner,
permissions and place in the object. When that member is the root, the merged part is the root and the other parts do not
move. The other members are cut out. The mesh holds one face for each material slot; positions, normals and texture
coordinates of every member are carried into the merged part's frame, and collision triangles of all members are written into
the mesh's physics block. Normals are carried the way the viewer carries them: divided by the size of the part they came from
and multiplied by the size of the merged part.

Each merged part gets a notecard, `Merge manifest`, in its inventory that lists the original part names, creators and ids.
Link numbers are written 1 to N in the order of each output part's lowest original number, so the root stays 1; an object
that ends as one part is link number 0, as the region writes it. Parts that stay are written as they were, with only their
link numbers closed up.

Every merged mesh is decoded and read back (OpenSim.MeshAsset and OpenSim's physics mesher) before anything is written. The
command also compares every corner of every merged mesh with the part it came from, before the mesh is encoded and after it is
read back from the asset bytes, and reports the worst position, texture coordinate and normal difference. A mesh asset stores
positions and coordinates in 16 bits over the mesh's range, so the read-back figures are larger than the exact ones.

## Why a part stays (the `rules` in the plan)

| rule | code | meaning |
|---|---|---|
| 7 | SINGLE_PART_OBJECT | the object has one part |
| 9 | SOFT_CAP, HARD_CAP, SPLIT_BY_CAPS | the part is longer than the cap, or no neighbour fits with it |
| 10 | SOLE_CREATOR_OR_PERMISSIONS | the part differs from the others in creator or permissions |
| 11 | ALREADY_MESH | an existing mesh keeps its makers' levels of detail |
| 17 | SCRIPTED, ADDRESSED, PROTECTED_LINK_NUMBER, LINKSET_LOCKED | see below |
| 18 | ALPHA_CAP | alpha-blended parts merge only within the alpha size cap |
| part | CONTENTS, LIGHT, FLEXI, SIT_TARGET, HOVER_TEXT, PARTICLES, SOUND, PHYSICAL, VOLUME_DETECT, CONVEX_PHYSICS, ROOT_PHYSICS_NONE, TEXTURE_ANIMATION, PBR_FACE, MEDIA_FACE, UNBAKED_MAPPING, NOT_PRIM, NOT_CONVERTED | per-part settings a merged mesh cannot carry |
| group | SOLE_IN_CLASS | no other part that can merge shares its group |
| sculpt | SCULPT_NOT_PLANNED | sculpts are converted on their own |

A part can have several reasons; the plan lists all of them, and the totals count the first and every one.

Scripts (rule 17) are read with the same scan `oar-eligibility` uses. A part that holds a script stays. A part that a script
addresses by a literal link number stays, and so does every part numbered at or below the highest such number, so those
numbers do not change. A script that addresses a part in a way the scan cannot work out (a variable link number,
LINK_SET, LINK_ALL_OTHERS, LINK_ALL_CHILDREN), counts or numbers parts (llGetNumberOfPrims, llGetObjectPrimCount,
llGetLinkNumber, llDetectedLinkNumber), reads the sender number in a link_message handler, or cannot be read at all locks
the whole linkset: every part stays. A message to LINK_SET with llMessageLinked does not lock it.

## Collision

A merged part's collision shape is a triangle mesh: a region older than 6.4.2 (the Jolt native-shape fix, NGC PR #378)
can treat a merged part with few faces as a solid box. The tool runs offline and only warns.
