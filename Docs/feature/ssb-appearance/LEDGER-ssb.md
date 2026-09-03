# Ledger — Server-Side Baking / Server-Side Appearance

**Artifact type:** Ledger — LIVING. Append-only; amend in place with a date; closing an entry means stating how it was resolved.
**Opened:** 2026-09-02, against `tranquillity-develop` at `a68d59f232` (branch `feature/voice-visibility-matrix` checkout; 167 commits after Pass 3's `645b0f3bb3`) [SRC: `git log`].
**Authority:** none yet — `RECON-ssb-appearance.md` is a draft for review; a DESIGN brief follows the decisions below.
**Feeds from:** `RECON-03-avatar-appearance.md` (Pass 3, `645b0f3`), `RECON-ssb-appearance.md` (this recon).

---

## 1. Findings of record

| ID | Finding | Evidence | Status |
|---|---|---|---|
| F-1 | No SSB cap exists; `UpdateAvatarAppearance` is registered nowhere. Only the client-bake `UploadBakedTexture` cap is. | cap-name enumeration over `LindenCaps` + `CoreModules/Framework`; `UploadBakedTextureModule.cs:97-112` | confirmed at HEAD |
| F-2 | `RegionProtocols` is hard-coded to `1UL << 63` — SSB bit 0 clear, BOM/eleven-slot bit 63 set — with no config path. | `LLClientView.cs:986-995` | confirmed at HEAD |
| F-3 | `AvatarAppearance` packets carry no `AppearanceData` block; LL-upstream resolves that to appearance version **1** (server-baked), Firestorm to **0**. A stock LL viewer therefore treats *every* avatar as server-baked and cannot fetch bakes (`AgentAppearanceServiceURL not set`). Refines Pass 3: not only self clouds. | `LLClientView.cs:4532-4533`; `llvoavatar.cpp:10864-10886`, `:11015`, `:6825` | new (refinement) |
| F-4 | The sim never composes; it relays viewer bakes, validates a cache, and asks the viewer to rebake on a miss. | `AvatarFactoryModule.cs:168-215`, `:345-470`, `:485-718` | confirmed |
| F-5 | Neither lineage has a compositor. Stock OpenSim 0.9.3: no `UpdateAvatarAppearance` anywhere; `XBakes` is a file cache of viewer bakes. Halcyon: client-bake with a grid-wide `cachedbakedtextures` cache and persistent bake assets. | stock tree grep; `XBakes.cs:56-107`; Halcyon `AssetCapsModule.cs:387-420`, `UserServerAvatarAppearanceModule.cs:267-310`, `MySQLUserData.cs:1565-1610` | confirmed |
| F-6 | The SL SSB client contract is fully observable in the viewer source: bit 0 → CBV; login field `agent_appearance_service`; `POST cof_version` to the cap; `success`/`error`/`expected` reply; `AppearanceVersion = 1` + `CofVersion` broadcast; bake URL `texture/<avatar>/<bakeName>/<textureId>`. | `llviewerregion.cpp:3316`; `llstartup.cpp:5161-5166`; `llappearancemgr.cpp:4243-4400`; `llvoavatar.cpp:6831`, `:10729-10732` | confirmed |
| F-7 | LibreMetaverse 3.1.4 (web viewer gateway) already implements that client contract (`UpdateAvatarAppearanceAsync`, `RequestServerBakedImageAsync`, `AgentAppearanceServiceURL`, `RegionProtocols.AgentAppearanceService`). Meeting the SL contract serves both gated viewers with no gateway protocol work. | NuGet XML doc, 3.1.4 | confirmed |
| F-8 | In-tree building blocks: CoreJ2K decode + encode (`SkiaImageUtils.TryEncodeToJ2KLossless`), SkiaSharp 4.151.1, `AssetWearable`/`VisualParams` in `UtopiaSkye.OpenMetaverse` 1.1.6, `openmetaverse_data/` (avatar_lad.xml + TGA masks) shipped with the region server. Missing: the compositor (`Imaging.Baker` is not in the linked libomv). | `SkiaImageUtils.cs:27-52`; `Directory.Build.props:13-17`; DLL string check; `/d/legiongrid/regionserver/openmetaverse_data/` | confirmed |
| F-9 | Live grid: Robust hosts `XBakes` (`Robust.ini:281-282`) but no region sets `[XBakes] URL`, so the cache is unused; `PersistBakedTextures = false`. | live config | confirmed |
| F-10 | AIS v3 is not built (`OpenSim.Services.AISv3` = weather-forecast scaffold). SSB does not hard-depend on it: the sim reads COF/wearables through `IInventoryService` (`AvatarFactoryModule.cs:839-1116`); AIS only tightens `cof_version` agreement. | tree | confirmed |
| F-11 | The Pass 3 `Docs/feature/sl-parity-audit/` tree and any Track-L document are not present on this machine; the accepted order is taken from the brief. | search of all checkouts under `/d` | gap |

## 2. Decisions needed from HIM

| ID | Decision | Options | Recommendation | Why it matters now |
|---|---|---|---|---|
| D-1 | Lineage | (a) SL-contract SSB in the region; (b) Halcyon-style cache port | **(a)**, with (b)'s "bakes persist as real assets" rule | everything else keys off this |
| D-2 | Where the appearance service URL points | region-hosted route per region; Robust-hosted route serving from the asset service; reverse proxy | Robust-hosted for this single-host grid (one URL, asset-service backed); region route kept for milestone 1 | login field is grid-wide; HG visitors and the gateway host need one stable URL |
| D-3 | Compositor sourcing | port libomv `BakeLayer.cs`/`ManagedImage.cs`/`TGALoader.cs` (BSD-3) onto SkiaSharp; reference LibreMetaverse NuGet; write from scratch against `LLTexLayer` | **port BSD code onto SkiaSharp** | a NuGet reference doubles libomv in-process (RK-5); from-scratch delays milestone 1 |
| D-4 | Ship milestone 0 now (explicit `AppearanceVersion = 0`, per-region protocol bit from config, login-field plumbing dark) | yes / fold into milestone 1 | **yes, separately** — zero behaviour change for Firestorm, removes a stock-viewer failure mode | small, reviewable, de-risks the handshake edit |
| D-5 | Test region | create `SSB-Test`; use an existing region off-peak | **create `SSB-Test`** | §D.5 rollout order depends on it |
| D-6 | Bake retention | last N per slot (N=2); time-based; keep all | last 2 per (avatar, slot) + sweeper | asset-store growth (RK-4) |
| D-7 | Bake resolution and encoding | 512² lossy J2K (viewer-like); 512² lossless; 1024² | 512² lossy at viewer-comparable quality; lossless behind a debug flag | bandwidth for every viewer in range |
| D-8 | Track-L placement | SSB after AIS as accepted; or SSB milestone 0/1 in parallel with AIS since there is no hard dependency (F-10) | keep the accepted order for milestones 2+; allow 0 and 1 to start in parallel | schedule |

## 3. Assumptions

| ID | Assumption | Basis | Falsified by |
|---|---|---|---|
| A-1 | Track-L order is login benefits → AgentProfile → AIS → SSB, as the brief states. | brief; no document found (F-11) | the audit tree, when located, says otherwise |
| A-2 | The stock LL viewer behaves as the Firestorm tree's non-`[Legacy Bake]` code: no `AgentSetAppearance`, missing `AppearanceData` → version 1. | `llvoavatar.cpp:10864-10874` (LL code commented in place) | an LL checkout showing different resolution |
| A-3 | The viewer ignores the LLSD `textures`/`visual_params` in a successful cap reply and relies on the UDP `AvatarAppearance`. | `llappearancemgr.cpp:4380-4386` comment and the absence of reply-field consumers in the coroutine | a viewer build that applies the LLSD directly |
| A-4 | `UtopiaSkye.OpenMetaverse`'s `AssetWearable`/`VisualParams` parse LLWearable assets and `avatar_lad.xml` the same way libomv does (the fork removed imaging, not parsing). | DLL exports; `openmetaverse_data` present | parser gaps found in milestone 1 |
| A-5 | The libomv `BakeLayer.cs` algorithm is close enough to the viewer's `LLTexLayer` for a first demo; gaps are listed in §B.4 of the recon. | bots have used it for years | milestone 2 comparison |
| A-6 | One in-region bake queue with a concurrency of 2 is adequate for this grid's population. | region sizes on this grid | queue-depth metric in soak |

## 4. Open questions

| ID | Question | Owner | Needed by |
|---|---|---|---|
| Q-1 | Does the Pass 3 audit tree (with RECON-03 and the Track-L note) live in another clone or on another machine? Needed to reconcile F-11 and A-1. | HIM | before the DESIGN brief |
| Q-2 | Which LL viewer build is the target (release channel/version)? A-2 should be verified against an LL checkout, not Firestorm's markers. | HIM | milestone 1 verification |
| Q-3 | Should bakes be stored through the grid asset service (durable, HG-visible) or region-local (`XBakes`-style)? Affects D-2/D-6 and NPC re-bake. | HIM | DESIGN |
| Q-4 | Do foreign (HG) avatars get baked here, or do we honour their home bakes? Their `AvatarAppearance` arrives with their own TE; a legacy-baked visitor on an SSB region is fine, a server-baked visitor whose home service is unreachable clouds. | HIM | milestone 4 |
| Q-5 | Is a Firestorm user with the legacy client-bake path on an SSB region allowed to keep client-baking (accept their `AgentSetAppearance` and skip the server bake), or is SSB authoritative per region? | HIM | milestone 1 (§C last row) |
| Q-6 | Is there an ignore-list / rate-limit policy wanted for the cap (a misbehaving client can request bakes in a loop)? | HIM | milestone 3 |
| Q-7 | Where do the web viewer's gateway sessions log in from (same host as Robust?) — decides whether the region-hosted bake route is reachable to it in milestone 1. | HIM / web-viewer | milestone 1 |
