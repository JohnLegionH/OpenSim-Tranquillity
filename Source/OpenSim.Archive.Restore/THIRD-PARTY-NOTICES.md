# Third-party notices — OpenSim.Archive.Restore

## `Import/ViewerJointNames.cs`

**Origin:** Linden Lab Second Life viewer, files `indra/newview/character/avatar_skeleton.xml` (the bone names, the collision volume names and the
aliases of the bones) and `indra/newview/character/avatar_lad.xml` (the attachment point names), as shipped in the viewer source.

**What was taken:** only the names and the aliases, as lists of strings. No positions, rotations, scales or other data of those files, and no code. The lists
let `oar-import` tell a rigged mesh's joints from other nodes of a glTF file and write them under the names the viewer knows.

**Licence:** the same as the two files named above, which the viewer source places under the GNU Lesser General Public License v2.1 (with the viewer's
linking exception). The licence texts are in the repository's `ThirdPartyLicenses` folder, the same ones that ship with `avatar_lad.xml` (the viewer repository does not
say which of them covers these data files, so all are kept): `ThirdPartyLicenses/SecondLifeViewer-LGPL-2.1.txt` (the GNU Lesser General Public License, version 2.1),
`ThirdPartyLicenses/SecondLifeViewer-LICENSE-source.txt` (the viewer's source licence statement) and `ThirdPartyLicenses/SecondLifeViewer-LICENSE-logos.txt` (the
Creative Commons Attribution-Share Alike 3.0 statement for the viewer's data and logos). See also `Source/OpenSimNGC.Appearance.Baking/THIRD-PARTY-NOTICES.md`.

**Modifications:** the names were copied into C# string lists and sets; the aliases into a dictionary. Nothing else.

## `Import/CollisionVolumeFrames.cs`

**Origin:** Linden Lab Second Life viewer, file `indra/newview/character/avatar_skeleton.xml`, as shipped in the viewer source.

**What was taken:** for each of the 26 collision volumes, the name of its parent bone and its `pos`, `rot` and `scale` values, as numbers in a table. No code.
`oar-import` uses them to give a mesh weighted to a collision volume the inverse bind matrix the viewer poses that joint with (the volume's own scale and turn).

**Licence:** the same as the file named above and as `Import/ViewerJointNames.cs` (see that section): the GNU Lesser General Public License v2.1 and the viewer's
other licence statements, kept in `ThirdPartyLicenses/SecondLifeViewer-LGPL-2.1.txt`, `ThirdPartyLicenses/SecondLifeViewer-LICENSE-source.txt` and
`ThirdPartyLicenses/SecondLifeViewer-LICENSE-logos.txt`.

**Modifications:** the values were copied into a C# dictionary of records. Nothing else.
