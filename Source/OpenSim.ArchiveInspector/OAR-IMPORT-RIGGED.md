<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar-import: rigged mesh (clothing and other things worn on a body)

`oar-import` (see [OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md)) turns a folder of `.glb` files into a mesh object in an archive. When the files carry a **skin**
(joints, inverse bind matrices and per-vertex weights, as Blender and other tools write them), the mesh asset gets the skin block and the weights block
of every level of detail, so a viewer can wear the mesh on an avatar. A folder whose files have no skin is imported exactly as before.

## What the files must hold

The same folder as for any import: `<base>.glb`, `<base>_lod2.glb`, `<base>_lod1.glb`, `<base>_lod0.glb` and, optionally, `<base>_physics.glb`. For a
rigged part:

* **All four levels of detail carry a skin.** A level may weight fewer joints than the full mesh; it may not weight a joint the full mesh does not.
* **The joints are named as the viewer names them.** The bones are the deform bones of the Second Life skeleton, those with an `m` prefix (`mPelvis`,
  `mTorso`, `mChest`, `mShoulderLeft` ...). The collision volume names (`PELVIS`, `L_UPPER_LEG` ...), the attachment point names and the aliases the viewer
  accepts for a bone (`hip` for `mPelvis`) are accepted too. **A skin with any other bone stops the file**, and the message lists them: export only the
  deform bones. (Animation controls and helper bones of a body rig are not joints of the viewer's skeleton.)
* **Every vertex has weight.** A vertex with none would follow the pelvis, so the file is stopped and the number of such vertices is reported.
* **At most 110 joints** carry weight (the viewer's limit for one mesh). A vertex keeps its **four heaviest influences**, renormalised to add up to one;
  influences under 0.001 are dropped (the viewer clamps a stored weight to that floor). Both are reported in the notes when they happen.
* The skin needs **inverse bind matrices**, and the mesh node must not be moved or turned (apply the transforms before exporting). The nodes of the
  skeleton may have positions: they are joints.
* The mesh node's own transform is not applied to a skinned mesh (glTF); the vertices are taken as they stand in the space the inverse bind matrices start from.

## What is written

* **The skin block**: the joint names, one inverse bind matrix for each, and the **bind shape matrix**, which scales the unit box the vertices are stored in
  up to the part's size and moves it to the part's centre. The viewer draws a rigged mesh with this matrix and does not use the part's own scale.
* **Inverse bind matrices of bones are rebuilt from the joint's rest position.** The viewer's bones are never turned or scaled, but a modelling tool's bones
  often are (a Blender bone points along its length), and a matrix that carries that turn bends the mesh. For each bone (an `m` joint) only the position of
  the joint in the file is used; a note names the bones whose rest frame was turned. A collision volume or attachment point keeps the file's matrix
  (changed to the viewer's axes), because the viewer's own volumes are turned and scaled.
* **The weights**: for every level of detail, per vertex up to four (joint, weight) pairs, the weight in 16 bits, in the numbers of the skin's joint list.
  Joints that no vertex of any level uses are left out of the list.
* **Joint positions are left out.** The wearer's joints then stay where the wearer's shape puts them, so a worn garment resizes with the wearer's sliders.
  `--joint-positions` (or `"jointPositions": true` in a manifest) also writes the joint positions of the file (the viewer's "include joint positions"): each
  joint's offset from its parent, plus a pelvis offset of 0. The wearer's joints then move to those positions and stop following the shape sliders.

## Checking

`--check <folder>` (see the command's help) writes the archive's object back out to glTF and compares it with the files. For a rigged part it also prints a
line `skin and weights`: the joints, the inverse bind matrices, the bind shape matrix and, for every stored vertex of all four levels, the weights against
the file's vertex at that place.

## What this does not do

* It does not fit a mesh to a body. Weights come from the file; copy them from the body mesh in the modelling tool before exporting.
* It cannot show how the garment looks when worn: that needs a viewer.
