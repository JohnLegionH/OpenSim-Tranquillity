# ADR Set — Server-Side Baking

Status legend: **Proposed** = needs John's ruling · **Accepted** = ruled · **Carried** = ruled earlier (recon 2026-09-02), restated for the record.

---

## ADR-001 — Viewer contract is the stock LL viewer; add-only

**Status:** Carried (D-5 of the parity audit; viewer-compatibility policy 2026-08-31)
**Decision:** The wire behaviour in RECON addendum §3 (V1–V7) is the spec. No UDP appearance handler (`AgentSetAppearance`, `UploadBakedTexture`, `AgentCachedTexture`, `AvatarNowWearing`) is removed or altered in behaviour. All new behaviour is gated by `[Appearance] ServerSideBaking` per region.
**Consequences:** Firestorm client-bake keeps working on flag-off regions forever. Two code paths coexist in `SendAppearance` (with/without `AppearanceData`), selected per avatar by whether the sim baked it.

---

## ADR-002 — Bake compute in the region; appearance service and reaper on Robust

**Status:** Carried (D-2 "Robust-route")
**Decision:** Composition runs in-process in the region module that owns the ScenePresence (it has the params, the TE, and the `AvatarAppearance` sender). The **read** path viewers use for other avatars' bakes (`agent_appearance_service` → `texture/<agent>/<channel>/<uuid>`) is a Robust HTTP handler that resolves the channel to the stored asset UUID via the avatar service and streams the asset. The expiry reaper is a Robust-side timer.
**Alternatives rejected:** (a) Bake on Robust — needs a second copy of appearance state and a new region→Robust bake RPC; more moving parts for no fidelity gain. (b) Serve bakes from the region's HTTP server — regions come and go; the URL in the login response must be stable across teleports.
**Consequences:** Standalone mode: the same handler registers on the standalone's HTTP server (Robust connectors are hosted in-process there already). `IBakeBackend` seam retained so compute *could* move out-of-process later (LL utility, GPU box) without touching the module.

---

## ADR-003 — Shared compositor library, extracted from the web-viewer gateway

**Status:** Proposed
**Decision (recommended):** Create `OpenSimNGC.Appearance.Baking` as a project in the Tranquillity tree under `Source/` (not `Addons/`, because Robust never loads it but the library is core infrastructure, not an optional module), targeting `net10.0`, dependencies SkiaSharp (already in tree since #130) + the tree's existing J2K encoder. Ship it to NuGet as `OpenSimNGC.Appearance.Baking` alongside the other NGC packages so the web-viewer gateway consumes it by package reference, not path.
**Alternative A:** Keep the compositor in the gateway and copy it into Tranquillity. Rejected: two copies, two harnesses, drift within a week.
**Alternative B:** Publish from a third repo. Rejected: one more repo for one library; the gateway already depends on NGC packages.
**Why proposed, not accepted:** publishing a new NGC package is Mike's call as ecosystem maintainer; John should raise it with him before S0b. Until then the gateway can use a local `ProjectReference` to `/d/tranquillity-develop/Source/OpenSimNGC.Appearance.Baking`.
**Consequences:** The library carries the golden-fixture test project; both consumers get the tests. The gateway's `gateway/src/Gateway/Baking/` is deleted, not deprecated.

---

## ADR-004 — Bake persistence: assets with a bake marker, index in the avatar service, supersede-immediately + TTL reaper

**Status:** Carried in part (D-6 "expire bakes"), marker mechanism Proposed
**Decision:** Bakes are stored through `IAssetService` as texture assets. Marker (recommended): `AssetBase.Flags |= AssetFlags.Collectable` is *not* used (it means "temp" in stock code paths and gets purged wrongly); instead the asset **name** is `bake:<agent>:<channel>` and `Description` carries the input hash, and the authoritative index is the avatar-service key set (`Bake:<channel>`, `BakeHash:<channel>`, `BakeCOFVersion`, `BakeSize`, `BakeUpdated`). Supersede = delete the previous asset for that channel synchronously after the new one is confirmed stored. TTL reaper walks avatar-service records whose `BakeUpdated` is older than `BakeTTLDays` and whose presence record shows no login since, deletes assets, clears keys.
**Alternatives rejected:** (a) New `bakes` table — schema change in three DB backends for something the key/value Avatars table already expresses. (b) Never expire — violates D-6. (c) `AssetFlags` marker — see above; also not indexed.
**Consequences:** No migration. Grid owners on plain OpenSim never see any of this. A grid with the reaper off (default standalone) grows only until supersede, i.e. ≤11 bakes per avatar.

---

## ADR-005 — Fidelity policy on the sim: best-effort with a structured report; refusal only for corrupt input

**Status:** Proposed (D-3)
**Decision (recommended):** Unlike the gateway (which refuses anything it cannot reproduce faithfully because its bake would replace a Firestorm bake), the sim on a bit-0 region **is** the only baker for LL viewers, so refusing means a permanent cloud. Policy: bake what is supported; skip unsupported layers; emit a fidelity report (INFO log + `[Appearance] FidelityReportPath` optional JSONL); the cap response is `success:true`. Refuse (`success:false`, `error`) only when inputs are unparseable or a texture fetch fails after retry — and in that case do **not** overwrite an existing good bake.
**Alternative:** Mirror the gateway's strict gate. Rejected for LL viewers; but see consequences.
**Consequences:** Firestorm users on a bit-0 region get the sim's best-effort bake instead of their own. The harness numbers (S0) must show the compositor at or above Firestorm's output on the stock-Library reference *before* any region flips the flag. If John rules strict, the flag stays off on Legion Grid until unsupported types are zero.

---

## ADR-006 — COF version source is the inventory folder `Version`; no AIS dependency

**Status:** Proposed
**Decision (recommended):** The sim reads the COF folder's `Version` from the inventory service and treats it as `cof_version`. AIS v3, when built, updates the same field, so nothing changes later. Anti-livelock rule per Design Brief §4.3.
**Alternative:** Block SSB on AIS (BP-v2 order). Rejected because the LL viewer's "log in as yourself" tier needs only the login-time bake, and the web viewer needs SSB now.
**Consequences:** S0 must verify the UDP link-create/delete path bumps `Version`. If it does not, that fix is a prerequisite slice, not a reason to wait for AIS.

---

## ADR-007 — `avatar_lad.xml` ships with the library

**Status:** Proposed (Q-2)
**Decision (recommended):** Vendor `avatar_lad.xml` (and only it) into the library as an embedded resource, with a `THIRD-PARTY-NOTICES` entry naming its origin (Linden Lab viewer, LGPL 2.1 with the viewer's linking exception). The gateway currently reads it out of the LibreMetaverse package at runtime — that coupling ends with the extraction.
**Alternative:** Reference it from LibreMetaverse in both consumers. Rejected: the sim does not otherwise depend on LibreMetaverse, and a bake compositor must not change behaviour because a client library updated.
**Consequences:** One provenance line in Ledger Q-2 closes; the [[avatar-character-system]] Q-3 provenance question (system body mesh) is unaffected and stays open there.

---

## ADR-008 — Bake size 512, parameterised

**Status:** Carried (D-7)
**Decision:** Sim default 512 px per channel; `[Appearance] BakeSize` accepts 512/1024. Hash includes size so a config change invalidates stored bakes on next login rather than serving mixed sizes.

---

## ADR-009 — Gateway is a consumer on SSB regions

**Status:** Proposed (web-viewer side)
**Decision (recommended):** On `RegionHandshake` with bit 0 set, the gateway session switches to `server` appearance mode: never bakes, accepts `AppearanceData`-bearing `AvatarAppearance` for self, fetches bakes via the existing asset route. On bit-0-clear regions the S11/S12 path stays. Detected per region, re-evaluated on every teleport.
**Consequences:** Web-viewer G6 ("standalone, no grid reliance") holds — the gateway degrades gracefully to its own baker off-NGC. On Legion Grid the corruption hazard that caused e881646 disappears entirely for SSB regions, because the gateway sends nothing.
