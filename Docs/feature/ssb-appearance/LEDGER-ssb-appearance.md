# Ledger — Server-Side Baking (L-2)

Living document. Update at every session close. **Date opened:** 2026-09-03

## 0. Principles

| ID | Principle |
|---|---|
| P-1 | The authority for bake output is the LL viewer compositor (`lltexlayer.cpp` / `llavatarappearance.cpp`) and SL's bake-service behaviour. Firestorm is a capture tool for reference bakes only; no code, config, threshold, or test may be keyed on Firestorm. |

## 1. Decisions

| ID | Decision | Status | Ruled | Notes |
|---|---|---|---|---|
| D-1 | Build SSB **before** AIS v3 (reverses BP-v2 order AIS→SSB) | **OPEN — needs John** | — | Justified by V7: LL viewer gets "log in as yourself"; web viewer needs it now. ADR-006 removes the AIS dependency. |
| D-2 | Appearance service + reaper on Robust; compute in the region | Accepted | 2026-09-02 | ADR-002 |
| D-3 | Sim-side fidelity policy: best-effort + report (recommended) vs strict refuse | **OPEN — needs John** | — | ADR-005. Affects whether Legion Grid can flip the flag while any unsupported wearable type remains. |
| D-4 | Library placement `Source/OpenSimNGC.Appearance.Baking`, NuGet-published | **OPEN — needs John (and Mike)** | — | ADR-003. Interim: gateway `ProjectReference`. |
| D-5 | Test region for flag-on = Transylvania | Proposed | — | Build Plan §4 |
| D-6 | Bakes expire (TTL reaper, default 30 days, off on standalone) | Accepted | 2026-09-02 | ADR-004 |
| D-7 | Bake size 512 default, parameterised | Accepted | 2026-09-02 | ADR-008 |
| D-8 | Add-only: no legacy appearance handler removed | Accepted | 2026-08-31 | ADR-001 |
| D-9 | Gateway is a pure consumer on bit-0 regions | Proposed | — | ADR-009; keeps web-viewer G6 intact |

## 2. Open questions

| ID | Question | Owner | Blocks | Status |
|---|---|---|---|---|
| Q-1 | Does the inventory service bump COF folder `Version` on the UDP link create/delete path? | S0a | S3, S5 | **Resolved S0a V6:** yes — data layer `IncrementFolderVersion` on item store/delete/move and folder store; cap `CreateInventoryCategory` bumps the parent too. Sim must read `Version` fresh via `GetFolder`. |
| Q-2 | `avatar_lad.xml` provenance — vendor as embedded resource with LGPL notice (ADR-007)? | John | S0a | **Resolved S0a (`7dbc092d2e`):** vendored; provenance recorded in `Source/OpenSimNGC.Appearance.Baking/THIRD-PARTY-NOTICES.md` (viewer 26.1.1, wearable_definition_version 22, SHA-256). Viewer commit id not confirmable from `F:iewer-develop` (no `.git`). |
| Q-3 | Is the Legion `Client_OnAvatarNowWearing` empty-appearance wipe-loop fix present on Tranquillity? | S0a | flipping any production region | **Resolved:** bug present (S0a V5, `AvatarFactoryModule.cs:1205` at `7dbc092d2e`), fixed in S0c `a5e88d72f1` — merge into existing wearables via `AvatarFactoryModule.MergeNowWearing`; unlisted slots retained, `UUID.Zero` still clears, save only on change. |
| Q-4 | Which J2K encoder does the tree expose (OpenJPEG via OpenMetaverse vs CoreJ2K/CSJ2K), and does it produce viewer-decodable J2C with the right layer count for 512 bakes? | S0a | S0b | **Resolved S0a V7:** CoreJ2K.Skia 2.3.3.91 (plain NuGet, no OpenJPEG code); single-tile config required (`WithTiles(t => t.SetSize(w, h))`, upstream #201 found multi-tile output renders blank). Layer count for 512 bakes still to be confirmed in S0b. |
| Q-5 | Does LibreMetaverse expose `RegionProtocols` from `RegionHandshake` to the gateway, or does the gateway need to read it off the raw packet? | S6 | S6 | open |
| Q-6 | Does Firestorm on a bit-0 OpenSim region still send `AvatarNowWearing` on outfit change, or only the cap POST? | S5 | S5 | open |
| Q-7 | Golden fixtures: which 6 (later 11) reference bake asset UUIDs (LL compositor output, captured via the Firestorm client-bake path) for Truly's stock outfit? | **John** | S0b's diff step | **Resolved S0b:** the five classic ones are in `Golden/manifest.json` (skirt and the five BoM aux slots are not set for this outfit); fetched by `fetch-fixtures.sh`, never committed. |
| Q-8 | Server bakes are 4-component; the LL compositor emits a 5th legacy-bump channel; parity gap — add in the library before S3 | S0b → library | S3 | **Resolved S0d `30c82d472b`:** the premise was wrong — `lltexlayer.cpp:395` renders only `RP_COLOR` layers, so bump layers never reach a bake; the 5th component is the morph mask (`gatherMorphMaskAlpha`, `lltexlayer.cpp:460-472`), now produced and encoded (5-component single-tile J2C). Spec: `Source/OpenSimNGC.Appearance.Baking/Docs/BUMP-PASS.md`. Reference check: head 0 vs 1, others 255 vs 254. |
| Q-9 | `appearance-utility-bin` as a viewer-independent golden generator; Linux/GL one-off; later | — | — | open (later) |

## 3. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Compositor output worse than Firestorm's on some outfit → Firestorm users on a bit-0 region degrade | S0b diff gate before S3; per-region flag; ADR-005 never overwrites a good bake with a refused one |
| R-2 | COF version livelock (viewer re-requests forever) | Anti-livelock rule §4.3: after N mismatches in T seconds, bake with server version, log |
| R-3 | Bake assets accumulate on operators' grids | Supersede-delete + TTL reaper (ADR-004) |
| R-4 | Wipe-loop bug (Q-3) present → server-initiated bake reads a half-wiped wearable set and persists a wrong bake for every viewer | S0a check is a hard gate before any production flag flip; module never *writes* wearables, only bakes and TE |
| R-5 | Two compositor copies drift (gateway vs sim) | ADR-003: gateway dir deleted at S0b |
| R-6 | NGC package publication delayed (Mike) | `ProjectReference` interim; no code difference |
| R-7 | `AppearanceData` block on `AvatarAppearance` breaks an older third-party viewer | Emitted only for sim-baked avatars; Firestorm parses it on SL daily; flag-off regions unchanged |

## 4. Session log

| Date | Session | Commit | Result | Decisions/questions raised |
|---|---|---|---|---|
| 2026-09-02 | CC recon | — | RECON delivered to `D:\_TO_REVIEW\ssb-appearance\`; D-2/D-6/D-7 ruled; brief paused | — |
| 2026-09-02 | wire spike | (none) | Sim delivers others' bake UUIDs + VisualParams; AppearanceData omitted | — |
| 2026-09-02 | web-viewer S11/S12 | 6 local | Gateway compositor exists, LibreMetaverse Baker disqualified | ADR-003 source |
| 2026-09-03 | doc set | — | Addendum, Design Brief, ADR set, Build Plan, this Ledger | D-1, D-3, D-4 open |
| 2026-09-03 | S0a | `29105ccc44`, `7dbc092d2e` | Verification pass V1–V10 (`S0a-VERIFICATION.md`); `OpenSimNGC.Appearance.Baking` skeleton + test project in `Tranquillity.sln`, `avatar_lad.xml` embedded; solution builds, 1/1 test green | Q-1 yes; Q-2, Q-4 resolved; Q-3 = wipe bug present → hard gate; libomv drift 1.1.6 vs upstream 1.1.7 |
| 2026-09-03 | S0b | `303d2b39c1`, `8a245aa286`, `cbf3284e06` (tranq-ssb); `be67e2d` (web-viewer) | Compositor moved into `OpenSimNGC.Appearance.Baking` (no LibreMetaverse; own enums, TGA reader, 56 character masks embedded); `SkiaBakeBackend`, single-tile J2C, `BakeHash`; gateway is an adapter over the library, local compositor deleted; 24/24 library tests, 13/13 gateway tests. Golden harness vs reference bakes (LL compositor, captured via the Firestorm client-bake path) at 512, mean abs RGB / % pixels >8: head 1.62 / 1.47%, upper 1.41 / 0.64%, lower 1.36 / 0.63%, eyes 0.36 / 0%, hair n/a (both fully transparent). Decoder fix: 5-component viewer bakes now carry alpha. | P-1 recorded; Q-7 resolved; Q-8 (5th channel), Q-9 (appearance-utility-bin) opened; ADR-007 wording needs the masks; bake size 512 vs the reference's 2048 — threshold to be set on these numbers |
| 2026-09-03 | S0d | `30c82d472b` | 5th component per LL `lltexlayer.cpp`: not a bump pass (RP_BUMP never rendered, `:395`) but the morph mask (`gatherMorphMaskAlpha`); compositor computes it from the `<morph_masks>` layers (facialhair / upper_clothes / lower_pants), encoder writes 5-component single-tile J2C via CoreJ2K planar streams, decoder exposes it. Masks re-verified SHA-256-identical to viewer 26.1.1 (0 replaced; `head_wrinkles_highlights_alpha.tga` absent from the viewer too). Golden 5th-component columns: head 0.00 / 0% (ref uniform 1), upper/lower/eyes/hair 1.00 / 0% (ref uniform 254); RGB now head 1.37, upper 1.41, lower 1.36, eyes 0.36, hair 0.00 (planar encode keeps RGB under zero alpha). 26/26 tests. | Q-8 resolved; ADR/Q-8 wording should drop "bump"; RP_BUMP layers stay unrendered by design |
| 2026-09-03 | S0c | `a5e88d72f1` | `Client_OnAvatarNowWearing` merges into existing wearables; 4 new xunit tests green. `OpenSim.Region.CoreModules.Tests` has 35 pre-existing failures at clean `7dbc092d2e` (identical set before/after; NRE/null-asset in Flotsam, IAR, PrimCount, Moap, Serialiser, two legacy AvatarFactory tests) — environmental, not caused by S0c | Q-3 resolved; R-4 gate closed; pre-existing CoreModules test failures need a separate owner |

## 5. Cross-references

- [[repo-audit]] — RECON-02 (AIS absent), RECON-03 (appearance surface, viewer contract), BUILD-PLAN-sl-parity-v2 Track L.
- [[web-viewer]] — Sessions 11/12 (compositor, fidelity gate), wire spike, hotfix e881646 (appearance-passive rule), S13 (BoM rendering).
- [[avatar-character-system]] — Ledger Q-3 system body provenance (untouched by this programme).
- [[tranquillity-fork]] — viewer-compatibility policy 2026-08-31 (add-only, per-region flags).
- [[mike-dickson]] — ADR-003 package publication.
