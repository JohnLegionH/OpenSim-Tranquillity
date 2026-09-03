# Third-party notices — OpenSimNGC.Appearance.Baking

## `Data/avatar_lad.xml`

**Origin:** Linden Lab Second Life viewer, file `indra/newview/character/avatar_lad.xml`.
This is the avatar "LAD" (Linden Avatar Definition) file: it defines the visual
parameters, wearable layers, texture layer sets and morph targets that the bake
compositor reproduces server-side (ADR-007).

**Licence:** GNU Lesser General Public License v2.1, with the Second Life viewer
linking exception granted by Linden Lab (the "Linden Lab Second Life Viewer
Source License" exception that permits linking the viewer source with non-LGPL
code). The file is embedded unmodified as a data resource; it is not compiled or
linked into executable code. The full LGPL 2.1 text is in the viewer's `LICENSE`
file and at <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>.

**Copied from:** a local checkout of the viewer source tree at `F:\viewer-develop`
on 2026-09-03. That directory is not a git repository (no `.git`), so the exact
upstream commit could not be read from it. Identifying data that could be read:

| Field | Value |
|---|---|
| `indra/newview/VIEWER_VERSION.txt` | `26.1.1` |
| `avatar_lad.xml` `wearable_definition_version` | `22` |
| `avatar_lad.xml` `version` | `2.0` |
| File size | 354,436 bytes |
| SHA-256 | `ace7a7aebac5bee593d2ec2f5a487404cf53859e54537d00e53173c8fa1ee2cd` |

The SSB design documents (`Docs/feature/ssb-appearance/RECON-ssb-appearance-addendum.md` §3)
name the viewer commit used for the wire contract as `62033f2`; that identity could
not be confirmed against `F:\viewer-develop` and is recorded here as a claim, not a fact.

**Modifications:** none. Byte-for-byte copy.
