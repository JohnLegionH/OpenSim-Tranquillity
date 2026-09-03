# The 5th component of a baked texture ("bump" pass) — what the LL compositor actually does

Authority (Ledger P-1): the LL viewer source, read read-only at `F:\viewer-develop` (viewer 26.1.1 per
`indra/newview/VIEWER_VERSION.txt`): `indra/llappearance/lltexlayer.cpp`, `lltexlayer.h`,
`lltexlayerparams.cpp`, and `indra/newview/character/avatar_lad.xml`. Line numbers below are from those
files at that version. This document is the spec `TexLayerCompositor` follows for the 5th component.

## 1. Finding: the bump layers are never rendered into a bake

`avatar_lad.xml` marks 22 layers with `render_pass="bump"` (5 in `head`, 8 in `upper_body`, 9 in
`lower_body`; none in `eyes`, `hair`, `skirt` or the five Bakes-on-Mesh sets). The attribute is parsed at
`lltexlayer.cpp:596-604` (`LLTexLayerInfo::parseXml`: `render_pass_name == "bump"` →
`mRenderPass = LLTexLayer::RP_BUMP`; the enum `ERenderPass { RP_COLOR, RP_BUMP, RP_SHINE }` is
`lltexlayer.h:57-62`).

`LLTexLayerSet::render` (`lltexlayer.cpp:357-421`) composites **only** `RP_COLOR` layers:

```
    // composite color layers                                   lltexlayer.cpp:392-401
    for(LLTexLayerInterface* layer : mLayerList)
        if (layer->getRenderPass() == LLTexLayer::RP_COLOR)
            success &= layer->render(x, y, width, height, bound_target);
    renderAlphaMaskTextures(x, y, width, height, bound_target, false);   // :403
```

There is no other reference to `RP_BUMP` in `lltexlayer.cpp` or `lltexlayerparams.cpp`. The bump layers
(`head bump base`, `bump_head_base.tga`, `wrinkles_shading`, `eyebrowsbump`, `facialhair bump`,
`base_upperbody bump`, `upper_clothes bump`, …) are parsed, kept in `mLayerList`, and skipped by the
render loop. **The 5th component of an uploaded bake is not a bump map.** The name in Ledger Q-8 is a
legacy of the 2009 viewers; the current compositor does not produce one, and neither does this library.

`head_wrinkles_highlights_alpha.tga`, referenced only by the bump layer `wrinkles_shading`
(`avatar_lad.xml:9192`), does not exist in the viewer's `character/` directory either (checked S0d), so it
cannot be embedded; nothing that is rendered needs it.

## 2. What the 5th component is: the morph mask

The reference bakes (Truly Bazar, captured 2026-09-03) are five-component J2C. Measured with the library's
decoder: component 3 is the visibility alpha (on the head it is the eyelash mask `head_alpha.tga`; on the
bald hair it is 1 everywhere), and component 4 is a flat 1 on the head and a flat 254 on upper, lower, eyes
and hair. That is exactly the output of `LLTexLayerSet::gatherMorphMaskAlpha`:

```
void LLTexLayerSet::gatherMorphMaskAlpha(U8 *data, ...)        lltexlayer.cpp:460-472
{
    memset(data, 255, width * height);                        // :463  default: 255
    for(LLTexLayerInterface* layer : mLayerList)
        layer->gatherAlphaMasks(data, ...);                   // :467
    // Set alpha back to that of our alpha masks.
    renderAlphaMaskTextures(..., true);                       // :471  (GL buffer only; not `data`)
}
```

The packing of RGB + alpha + this array into a 5-component image happens in
`indra/newview/llviewertexlayer.cpp` (`LLViewerTexLayerSetBuffer::doUpload`), which is outside the files
this session may read; the component order R, G, B, A(visibility), M(morph mask) is inferred from that
code's known `"RGBHM"` comment and **confirmed empirically** by the five reference bakes above.

### 2.1 Which layers contribute

`gatherAlphaMasks` → `addAlphaMask` (`lltexlayer.cpp:1287-1290`, `:1513-1539`):

```
    const U8* alphaData = getAlphaData();                                  // :1517  cached morph mask
    if (!alphaData && hasAlphaParams())                                    // :1518
        renderMorphMasks(..., force_render = false);                       // :1525  → returns unless hasMorph()  (:1294)
    if (alphaData)
        for (i) data[i] = (U8)((data[i] * ((U16)alphaData[i] + 1)) >> 8);  // :1530-1537  multiply
```

`renderMorphMasks` caches its result only `if (hasMorph() && success)` (`:1389`), and refuses to render
at all without `force_render` unless `hasMorph()` (`:1294-1298`). So **only layers with `mHasMorph`
contribute**; every other layer leaves `data` untouched. `mHasMorph` is set through
`LLTexLayerTemplate::setHasMorph` (`lltexlayer.cpp:1717-1727`) for the layers named in the
`<morph_masks>` block of `avatar_lad.xml` (`avatar_lad.xml:17473-17502`):

| body_region | layer | morphs |
|---|---|---|
| head | `facialhair` | Displace_Hair_Facial |
| upper_body | `upper_clothes` | Displace_Loose_Upperbody, Shirtsleeve_flair |
| lower_body | `lower_pants` | Displace_Loose_Lowerbody, Leg_Pantflair, Low_Crotch, Leg_Longcuffs |

No other layer set has morph masks, so eyes, hair, skirt and the five extra bakes always carry 255.

### 2.2 Which wearables contribute

A template layer contributes once per worn wearable of its type (`LLTexLayerTemplate::gatherAlphaMasks`,
`lltexlayer.cpp:1706-1714`, over `updateWearableCache()` `:1615-1637`). The layer's type is its local
texture's wearable type, or, for a layer without a local texture such as `facialhair`, the single wearable
type its colour/alpha parameters belong to (`LLTexLayerInterface::getWearableType`, `:842-880`); a layer
whose parameters belong to several types, or to none, has no instances (`updateWearableCache` returns 0
for `WT_INVALID`). `facialhair`'s parameters (1004–1012) belong to `hair`, so it contributes once per worn
hair; `upper_clothes` once per worn shirt; `lower_pants` once per worn pants.

### 2.3 What each contribution is: the layer's alpha mask

`renderMorphMasks` (`lltexlayer.cpp:1292-1512`) builds the layer's mask in the alpha channel and reads it
back (`:1490-1493`: the alpha of an RGBA readback):

1. If the first alpha parameter is not `multiply_blend`, clear alpha to 0 (`:1309-1320`); otherwise start
   from the alpha already in the buffer.
2. For each alpha parameter, `LLTexLayerParamAlpha::render` (`lltexlayerparams.cpp:262-372`): skipped
   entirely when `getSkip()` (`:234-259`: `skip_if_zero` with an effective weight of 0, or the parameter's
   wearable type not worn); otherwise the mask file is pushed through `decodeAndProcess(domain, weight)`
   (`:330`) and blended **additively** (`BT_ADD`, "approximates max", `:287`) or by **multiplication**
   (`BF_DEST_ALPHA, BF_ZERO`, "approximates min", `:283`) per `multiply_blend`. The effective weight is
   the parameter's own value for the avatar's sex, else its default (`:272`).
3. Multiply by the local texture's alpha if the texture has 4 components (`lltexlayer.cpp:1338-1353`).
4. Multiply by the static mask image if `file_is_mask` (`:1355-1371`).
5. Multiply by the layer colour's alpha if it is not 1 (`:1373-1380`).

The colour pass calls this with `force_render = true` (`LLTexLayer::render`, `:1073-1076`) whenever the
layer has alpha parameters, so a layer all of whose parameters are skipped still produces a mask: all
zero (step 1 cleared it and nothing was added). That is why the female head's morph mask is 0 everywhere:
`facialhair`'s four masks (sideburns 1005, moustache 1007, soul patch 1009, chin curtains 1011 — `sex="male"`, `skip_if_zero`) all skip, the
mask is 0, and 255 × (0 + 1) >> 8 = 0. The reference head bake's component 4 is 1 (lossy coding of 0).

A layer whose net colour alpha is ≈ 0 is not rendered in the colour pass (`:1040-1044`) and so has no
cached mask; `addAlphaMask` then renders it on demand (`:1518-1526`) with the same rules.

## 3. What the library does (`TexLayerCompositor.Bake`)

1. `AvatarLad` parses `<morph_masks>` into `MorphMaskLayers[body_region] = { layer names }`.
2. The colour pass computes each rendered layer instance's mask exactly as before (`ComputeMask`) and
   caches it per (layer, instance), mirroring `mAlphaCache`.
3. After the colour layers, `MorphMask` starts at 255 everywhere (`memset(data, 255)`). For each layer of
   the set named in `MorphMaskLayers` that has alpha parameters, for each worn wearable of the layer's
   type (2.2), the instance's mask (cached, or computed on demand with the same rules) is multiplied in
   with `(m * (mask + 1)) >> 8`.
4. Sets without morph layers, and the invisible-alpha short-circuit, yield 255 everywhere.
5. `J2kCodec.EncodeBake` writes R, G, B, A, M as a five-component single-tile J2C; `Decode` exposes a
   fifth component as `RgbaPlanes.Mask`.

Reference check (S0d golden run, Truly Bazar, 512): head 0 vs reference 1, all other channels 255 vs
254 — the difference is the reference's lossy coding of the same constants.
