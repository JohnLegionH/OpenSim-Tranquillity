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
viewer's `llface.cpp`) and the merged face is drawn with default mapping and no transform. A face cannot be baked when it uses a mapping other than default or planar, names a legacy material that is not in the archive, or names
one with a normal or specular map that is drawn with a transform that is a shear relative to the face's (see below). Such a default-mapped
face keeps its own slot, keyed with its transform, and its transform stays on the merged face.

### PBR faces

A part holds the PBR (glTF) material of each such face by face number: in its extra parameters (a block of type 0x80, one material id for
each face), and in its material overrides (the `MatOvrd` element, one JSON text for each face). A merged part writes both again for the
faces it makes, renumbered to the merged face, with each override text unchanged. Faces share a merged face only when texture, colour,
material, override and the rest of the slot key are equal, so two faces that differ in their override stay two faces.

The viewer does not apply a PBR face's texture transform to its coordinates (the material has transforms of its own), and it ignores the
legacy material on a PBR face; the merged coordinates are therefore the base ones (projected first when the face is planar) and the merged
face carries no transform. A PBR face is alpha-blended when its effective alpha mode is blend: the override's `am` when it has one, else the
material asset's `alphaMode`; a material the archive does not hold counts as blending. The textures of a PBR material are not known, so
the precision guard assumes 2048 pixels, times the largest scale the material (`KHR_texture_transform`) or its override (`ti`) gives them.

A part stays (PBR_DATA) when the region would not read its material data the same way: an assignment block too short for its count, an
override that does not fit the data, an override or assignment for a face the part does not have, a face given twice, or override text that
starts with `{"asset` (the region drops all overrides of such a part).

### Media faces

A part holds the media entries in its `Media` element (one entry, or an empty place, for each face, by face number) and marks the faces in its
texture entry. A merged part writes the entries again for the faces it makes, renumbered, and marks the same faces; faces share a merged
face only when their entries are equal. The viewer draws the coordinates of a media face as it draws any other face's, so the faces bake as
usual. A part stays (MEDIA_DATA) when its media list cannot be read or holds an entry for a face the part does not have. A face that is
marked as media and has no entry is carried as marked, with no entry.

### Legacy materials with normal and specular maps

The viewer draws the diffuse texture with the face's transform F and each map with the material's own transform M for that map, all from
the same base coordinates (projected first when the face is planar). A merged mesh has one set of coordinates, and they carry F. The merged
face therefore names a material that draws each map with N, where N(F(p)) = M(p) for every p: M taken relative to F. A material states a
transform as a turn, then repeats, then an offset (`xform` in `llface.cpp`), and N can be stated in those five figures unless it shears: with
L = S_m R(d) S_f^-1 (S the repeats, R the turn, d the difference of the two turns), the rows of L must be at right angles. That holds when the
face is scaled alike in both directions, or when the map and the face are turned alike (or a quarter turn apart); otherwise the face is not
baked and keeps its own transform and material. A map drawn like the diffuse composes to the identity.

The figures are stored in ten-thousandths, so every corner is compared, in texels of the map's own texture, with the transform the material will
state applied to the merged coordinates, and the face bakes when no map is more than 0.25 texel away (a texture entry and a material keep the
same figure in different steps, so equal figures differ by a little). The coordinates must also be stored to half a texel at the size of the
largest texture the face shows (diffuse, normal or specular map; 2048 pixels for a texture the archive does not hold), times the largest repeat
N states, or the part stays (UV_PRECISION).

When a baked face's material states a map transform other than the one it has, the merged face names a new material: the old one with the
maps' transforms replaced by N, encoded the way the region encodes a legacy material, with the id the region derives (the MD5 of its bytes).
It is written into the archive once however many faces name it; faces that need the same N share it. A material whose maps need no change is
kept as it is.

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
| part | CONTENTS, LIGHT, FLEXI, SIT_TARGET, SIT_TEXT, TOUCH_TEXT, CLICK_ACTION, SALE_TYPE, SALE_PRICE, HOVER_TEXT, HOVER_TEXT_COLOR, PAY_PRICE, PARTICLES, SOUND, PHYSICAL, VOLUME_DETECT, CONVEX_PHYSICS, ROOT_PHYSICS_NONE, TEXTURE_ANIMATION, PBR_DATA, MEDIA_DATA, UNBAKED_MAPPING, UV_PRECISION, NOT_PRIM, NOT_CONVERTED | per-part settings a merged mesh cannot carry |
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
