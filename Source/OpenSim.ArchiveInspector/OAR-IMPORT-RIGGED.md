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
  accepts for a bone (`hip` for `mPelvis`) are accepted too. **A bone that is no joint of the viewer's skeleton and carries weight stops the file**, and the
  message lists those bones: export only the deform bones. A bone that is no joint and carries **no** weight (an animation control or a helper bone) is left
  out with a warning in the notes. (The viewer's own glTF loader drops such a bone the same way, from the skin it uploads.)
* **Every vertex has weight.** A vertex with none would follow the pelvis, so the file is stopped and the number of such vertices is reported.
* **At most 110 joints** carry weight (the viewer's limit for one mesh). A vertex keeps its **four heaviest influences**, renormalised to add up to one;
  influences under 0.001 are dropped (the viewer clamps a stored weight to that floor). Both are reported in the notes when they happen.
* The skin needs **inverse bind matrices**, and the mesh node must not be moved or turned (apply the transforms before exporting). The nodes of the
  skeleton may have positions: they are joints.
* The mesh node's own transform is not applied to a skinned mesh (glTF); the vertices are taken as they stand in the space the inverse bind matrices start from.

## What is written

* **The skin block**: the joint names, one inverse bind matrix for each, and the **bind shape matrix**, which scales the unit box the vertices are stored in
  up to the part's size and moves it to the part's centre. The viewer draws a rigged mesh with this matrix and does not use the part's own scale.
* **The model is turned to face the avatar when it lies a quarter turn off.** The avatar's left is its +y axis. A character modelled facing +z in glTF (the usual
  export of a Blender model that faces -y) has its left on +x and would stand sideways. When the file has a left and a right joint (`mShoulderLeft` and
  `mShoulderRight`, the hips, the knees ...), the vertices, normals, inverse bind matrices, skeleton nodes and the collision shape are turned together, in whole
  quarter turns about the vertical axis, until the left joints lie on the avatar's left; a note says by how much. The viewer's own glTF loader does the same for
  one case, a model turned a quarter, which it recognises from the shoulders. A model with no such pair is left as it is. The whole file is turned, so a file
  shared by several parts turns all of them.
* **Inverse bind matrices of bones are rebuilt from the joint's rest position.** The viewer's bones are never turned or scaled, but a modelling tool's bones
  often are (a Blender bone points along its length), and a matrix that carries that turn bends the mesh. For each bone (an `m` joint) the matrix is the move
  from the joint's rest position in the file (after the turn above), with no turn of its own; a note names the bones whose rest frame was turned. This is what
  the viewer's glTF loader writes for a bone (`buildOverrideMatrix` in `indra/newview/gltf/llgltfloader.cpp`): the position of the joint in the file, in the
  avatar's axes, relative to the avatar's joint above it. The matrices are not taken from the avatar's default skeleton, so a garment weighted to the same
  joints as a body uploaded through the viewer sits on the body whatever the body's skeleton looks like. A collision volume or attachment point keeps the file's
  matrix (changed to the viewer's axes); the viewer's loader also scales a collision volume by its own scale, which is not done here (use the `m` bones).
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
