# Ledger — Server-Side Baking (L-2)

Living document. Update at every session close. **Date opened:** 2026-09-03

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

| ID | Question | Owner | Blocks |
|---|---|---|---|
| Q-1 | Does the inventory service bump COF folder `Version` on the UDP link create/delete path? | S0a | S3, S5 |
| Q-2 | `avatar_lad.xml` provenance — vendor as embedded resource with LGPL notice (ADR-007)? | John | S0a |
| Q-3 | Is the Legion `Client_OnAvatarNowWearing` empty-appearance wipe-loop fix present on Tranquillity? | S0a | flipping any production region |
| Q-4 | Which J2K encoder does the tree expose (OpenJPEG via OpenMetaverse vs CoreJ2K/CSJ2K), and does it produce viewer-decodable J2C with the right layer count for 512 bakes? | S0a | S0b |
| Q-5 | Does LibreMetaverse expose `RegionProtocols` from `RegionHandshake` to the gateway, or does the gateway need to read it off the raw packet? | S6 | S6 |
| Q-6 | Does Firestorm on a bit-0 OpenSim region still send `AvatarNowWearing` on outfit change, or only the cap POST? | S5 | S5 |
| Q-7 | Golden fixtures: which 6 (later 11) Firestorm bake asset UUIDs for Truly's stock outfit? | **John** | S0b's diff step |

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

## 5. Cross-references

- [[repo-audit]] — RECON-02 (AIS absent), RECON-03 (appearance surface, viewer contract), BUILD-PLAN-sl-parity-v2 Track L.
- [[web-viewer]] — Sessions 11/12 (compositor, fidelity gate), wire spike, hotfix e881646 (appearance-passive rule), S13 (BoM rendering).
- [[avatar-character-system]] — Ledger Q-3 system body provenance (untouched by this programme).
- [[tranquillity-fork]] — viewer-compatibility policy 2026-08-31 (add-only, per-region flags).
- [[mike-dickson]] — ADR-003 package publication.
