<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar-import: sit targets, glow and fullbright, specular maps and PBR materials

`oar-import` (see [OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md)) writes the legacy material of a face (its picture and, when the `.glb` material has one, its normal map).
These additions put more of what a mesh looks like and how it behaves into the archive, so a built object needs no script for them. Every addition is off
until the manifest or the command line asks for it: an archive made without them is the one the earlier release made.

## Manifest keys of a part

```json
{ "name": "Bench seat", "folder": "bench/seat",
  "sit":   { "position": [0.34, -0.45, 0.10], "rotation": [0, 0, 0, 1] },
  "faces": [ { "material": "bench_mat", "glow": 0.0, "fullbright": false,
               "specular": { "map": "bench/bench_spec.png", "color": [1, 1, 1], "glossiness": 120, "environment": 0 } } ] }
```

* **`sit`**: where an avatar sits on this part, in the part's own axes (metres, and a turn as x, y, z, w; no `rotation` is no turn). It is written as the
  region archives a sit target: `SitTargetPosition`, `SitTargetOrientation`, the same values under the `LL` names, and `SitTargetActive` after them, so a
  target at the exact centre still counts as set. Two parts with a sit target make two seats: a second avatar who clicks a part whose target is taken is
  given the next free sit target of the object.
* **`faces`**: settings for the faces of the part, each named by `material` (the glTF material name; every face of that material) or by `face` (the face
  number). A face can be named once.
  * `glow` (0 to 1) and `fullbright` (true or false) are written into the face of the texture entry.
  * `specular` gives the face a legacy specular map, in the face's legacy material beside the normal map. The picture is a PNG or JPEG with the specular
    colour in red, green and blue and the glossiness in alpha. `color` (red, green, blue, 0 to 1) tints it, `glossiness` (0 to 255) is the specular exponent
    and `environment` (0 to 255) the environment intensity. Defaults: white, 51, 0. A face with a specular map and no normal map gets a legacy material with
    no normal map.
* The manifest itself can say **`"pbr": true`**.

## PBR materials: `--pbr` or `"pbr": true`

With the option on, every face that has a material in the `.glb` also gets a glTF PBR material, beside the legacy texture and material it has always had
(a viewer without PBR shows the legacy one):

* One material asset for each distinct material (asset type Material, archive file `<id>_prmat.dat`), with the id taken from the asset's bytes, so the same input
  gives the same archive. The asset is the binary LLSD map a viewer uploads for a material: `version` `1.1`, `type` `GLTF 2.0` and `data`, the glTF text of one
  material. The text holds the base colour factor, `metallicFactor`, `roughnessFactor`, `emissiveFactor`, `alphaMode`, `alphaCutoff`, `doubleSided` and the
  four pictures the `.glb` material points at: base colour, normal, metallic-roughness (occlusion in red, roughness in green, metal in blue; the same texture
  is named as `occlusionTexture`) and emissive. Factors the `.glb` leaves out take the glTF defaults.
* The pictures are written as bare JPEG 2000 codestreams like every other texture, and a picture used twice is one asset.
* The face reference is a block in the part's `ExtraParams` (type `0x80`: a count, then a face number and the material id for each), kept beside the mesh block.

Not done: texture transforms (`KHR_texture_transform`) are not applied to a PBR face, as before for a legacy one (a note says so); a separate occlusion picture that
is not the metallic-roughness picture is not used; material overrides (`MatOvrd`) are not written.

The programs that read PBR material assets elsewhere in this package (`oar-convert`'s merge planner and `oar-export`) read the glTF text directly and do not yet
unwrap the LLSD map, so on an archive made with `--pbr` they treat the material as unreadable.
