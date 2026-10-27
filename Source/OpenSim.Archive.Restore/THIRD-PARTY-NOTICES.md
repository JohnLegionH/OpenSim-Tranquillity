# Third-party notices — OpenSim.Archive.Restore

## `Import/ViewerJointNames.cs`

**Origin:** Linden Lab Second Life viewer, files `indra/newview/character/avatar_skeleton.xml` (the bone names, the collision volume names and the
aliases of the bones) and `indra/newview/character/avatar_lad.xml` (the attachment point names), as shipped in the viewer source.

**What was taken:** only the names and the aliases, as lists of strings. No positions, rotations, scales or other data of those files, and no code. The lists
let `oar-import` tell a rigged mesh's joints from other nodes of a glTF file and write them under the names the viewer knows.

**Licence:** the same as the two files named above, which the viewer source places under the GNU Lesser General Public License v2.1 (with the viewer's
linking exception); see `Source/OpenSimNGC.Appearance.Baking/THIRD-PARTY-NOTICES.md` for the notice and the licence text that ship with `avatar_lad.xml`.

**Modifications:** the names were copied into C# string lists and sets; the aliases into a dictionary. Nothing else.
