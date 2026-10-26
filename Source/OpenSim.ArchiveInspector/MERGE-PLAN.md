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

A face is alpha-blended as the viewer draws it (the references are the viewer's `LLPipeline::getPoolTypeFromTE`, `LLFace::canRenderAsMask`
and `LLImageGL::analyzeAlpha`). It goes through the alpha pool when its colour alpha is below 0.999 or its texture has an alpha channel,
that is two or four components in the JPEG 2000 file, whatever the pixels hold; a legacy material with an alpha mode other than blend
takes the texture's part away, and a blend material blends the face even on an opaque texture. A face of the alpha pool is drawn as an
alpha test instead of a blend, and so is not counted, when its colour alpha is exactly 1, it does not glow, and the texture's alpha is
mostly clear or opaque (the viewer sorts alpha into 16 steps, counting each 2 x 2 average too, and takes it for a mask unless more than
1/48 of the counts are in the middle steps, or all are in one half without being all clear or all opaque). A four-channel texture whose
alpha is all 255 therefore does not blend. This library's decoder reads a constant alpha of 255 from a lossy file as 254; levels within
one of 0 or 255 are taken as 0 or 255.

A sculpt merges like a prim. Its faces are built from its sculpt map (read from the archive) by the sculpt mesher the converter uses for a
sculpt on its own, cleaned the same way: texture seams stay split, and triangles with no area are dropped. Its faces then take part in
the same bake, keep rules and guards as a prim's. A sculpt whose map is not in the archive, or cannot be read, stays as it is
(NOT_CONVERTED). The sculpt maps stay in the output archive; a region saves only the assets a part still names.

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

A merged part never has more than eight faces. A face of the mesh asset holds at most 65,535 vertices, and the writer closes a face
as soon as the next triangle might not fit, so the plan counts a material's faces with room for that and for the vertices a cut
inside a source face repeats; the writer still refuses a part that comes to more than eight.

The mesh asset stores each texture coordinate in 16 bits over the range of its face, so a coordinate can be off by half a step,
range / 131,070. The plan keeps that under half a texel of the face's real texture (read from the archive; 2048 pixels, the largest size OpenSim allows, when the
archive does not hold the texture): faces of one material whose coordinates are too far apart to share a mesh face get one each, and
a face that is too wide on its own (UV_PRECISION) keeps its part as a prim. The report gives the worst case and the measured error
in texels.

The output is written under a temporary name beside it and takes its name only when complete. A run that fails leaves no output;
the partly written file is kept beside it as `<output>.<id>.incomplete`.

## Why a part stays (the `rules` in the plan)

| rule | code | meaning |
|---|---|---|
| 7 | SINGLE_PART_OBJECT | the object has one part |
| 9 | SOFT_CAP, HARD_CAP, SPLIT_BY_CAPS | the part is longer than the cap, or no neighbour fits with it |
| 10 | SOLE_CREATOR_OR_PERMISSIONS | the part differs from the others in creator or permissions |
| 11 | ALREADY_MESH | an existing mesh keeps its makers' levels of detail |
| 17 | SCRIPTED, ADDRESSED, PROTECTED_LINK_NUMBER, LINKSET_LOCKED | see below |
| 18 | ALPHA_CAP | alpha-blended parts merge only within the alpha size cap |
| part | CONTENTS, LIGHT, FLEXI, SIT_TARGET, SIT_TEXT, TOUCH_TEXT, CLICK_ACTION, SALE_TYPE, SALE_PRICE, HOVER_TEXT, HOVER_TEXT_COLOR, PAY_PRICE, PARTICLES, SOUND, PHYSICAL, VOLUME_DETECT, CONVEX_PHYSICS, ROOT_PHYSICS_NONE, TEXTURE_ANIMATION, PBR_FACE, MEDIA_FACE, UNBAKED_MAPPING, UV_PRECISION, NOT_PRIM, NOT_CONVERTED | per-part settings a merged mesh cannot carry |
| group | SOLE_IN_CLASS | no other part that can merge shares its group |

The per-part fields (sit target and text, touch text, click action, sale type and price, hover text and its colour, pay prices) are
not carried by a merged part, so a part whose value differs from the default stays. The defaults are the ones a region writes: click
action 0, sale type 0, empty texts, pay prices -2. A sale price counts only when the sale type is not 0 (not for sale), and a hover text colour only when there is hover text.

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
