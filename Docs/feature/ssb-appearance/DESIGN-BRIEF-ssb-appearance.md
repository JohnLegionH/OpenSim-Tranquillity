# Design Brief — Server-Side Baking for NGC-Tranquillity

**Programme:** Track L / L-2. **Status:** DRAFT for decision. **Date:** 2026-09-03
**Companion docs:** RECON (CC, 2026-09-02) + RECON addendum, ADR set, Build Plan, Ledger — all under `Docs/feature/ssb-appearance/`.

## 1. Problem

The stock LL viewer only bakes server-side (V1–V5). Tranquillity has no `UpdateAvatarAppearance` cap, no compositor, no appearance service, and sends no `AppearanceData` block. An LL-viewer user on a Tranquillity grid is a permanent cloud, and so is any appearance-passive client such as the web-viewer gateway. Firestorm masks this by baking client-side.

The web viewer has, in the meantime, built the hard part (a faithful compositor) in the wrong place: a per-client gateway whose bake the sim persists for everyone. That is both duplicated effort and a fidelity hazard. The correct home for baking on a grid that runs NGC code is the sim.

## 2. Goals

G1. Stock LL viewer (`--loginuri` pointed at Legion Grid) sees itself and others fully textured on an SSB-enabled region.
G2. Firestorm on the same region continues to work — with SSB (its SL codepath) when the region flag is on, with client bake when it is off.
G3. The web-viewer gateway becomes a pure consumer on SSB regions: no baking code runs for a session on a region advertising bit 0.
G4. One compositor, one test harness, shared by the gateway and the sim (`OpenSimNGC.Appearance.Baking`).
G5. Bakes persist across logins (Halcyon rule) and expire so they do not accumulate in an operator's asset store (D-6).
G6. Ordinary OpenSim grid owners can run the web viewer without any of this; SSB is an NGC-Tranquillity feature behind a per-region flag. Add-only: no legacy handler is removed.

## 3. Non-goals (this programme)

- Outfit changes from the LL viewer (that is AIS v3, L-1). SSB ships "log in as yourself" first.
- Bakes-on-Mesh **rendering** in the web viewer (rendering-side, [[web-viewer]] S13). The sim *does* produce the 5 BoM aux bakes (§6.4).
- Physics wearables, hover height, animesh, PBR overrides.
- The `appearance-utility-bin` (LL's GL-based baker). Kept only as a future `IBakeBackend` for exact-parity operators; not built.
- Bake sizes above 512 (D-7). The library is parameterised; the sim default is 512.

## 4. Architecture

```
                        +-------------------------------+
   LL viewer /          |  Region (Tranquillity sim)    |
   Firestorm(bit0) ---->|  AppearanceBakeModule         |         +---------------------------+
      POST UpdateAvatar |   - cap UpdateAvatarAppearance|         |  OpenSimNGC.Appearance.   |
        Appearance      |   - login-time bake trigger   |-------->|  Baking  (shared library) |
                        |   - COF/wearable resolver     |  in-proc|   - LLWearable parser     |
                        |   - IBakeBackend (in-proc)    |         |   - avatar_lad layer sets |
                        |   - BakeStore (persist/expire)|         |   - compositor (Skia)     |
                        |   - AvatarAppearance sender   |         |   - J2K encode            |
                        +-------+---------------+-------+         |   - fidelity report       |
                                |               |                  +------------+--------------+
                 assets (bakes) |               | avatar-service keys           ^
                                v               v                                |
                        +-------+-------+ +-----+------+                         |
                        | AssetService  | | Avatar Svc |                         |
                        +-------+-------+ +------------+                         |
                                |                                                |
                        +-------v----------------------+                         |
   LL viewer GET        | Robust: AppearanceService    |                         |
   texture/<agent>/     |  texture/<agent>/<ch>/<uuid> |                         |
   <channel>/<uuid> --->|  (proxy to AssetService)     |                         |
                        +------------------------------+                         |
                                                                                 |
   Web-viewer gateway (D:\web-viewer) ---- on non-SSB grids only ----------------+
      on SSB regions: appearance-passive, consumes AvatarAppearance + asset route
```

### 4.1 Components

| # | Component | Repo / location | New or changed |
|---|---|---|---|
| C1 | `OpenSimNGC.Appearance.Baking` — shared compositor library | Tranquillity tree (placement: ADR-003) | **new project**, code lifted from `D:\web-viewer\gateway\src\Gateway\Baking\` |
| C2 | `AppearanceBakeModule` — region module: orchestration, cap, triggers, sender | `Addons/` or `Source/OpenSim.Region.OptionalModules` (ADR-003) | new |
| C3 | `BakeStore` — persist bakes as assets, record channel→UUID + input hash + COF version in the avatar service, expiry reaper | region module + Robust reaper | new |
| C4 | `AppearanceServiceConnector` — Robust HTTP handler for `texture/<agent>/<channel>/<uuid>` and login-response `agent_appearance_service` | Robust | new (ADR-002) |
| C5 | `LLClientView.SendAppearance` — emit `AppearanceData{AppearanceVersion=1, CofVersion}` when the avatar is server-baked; unchanged otherwise | `OpenSim.Region.ClientStack.Linden.UDP` | changed, add-only |
| C6 | `RegionHandshake` — set bit 0 of `RegionProtocols` when `[Appearance] ServerSideBaking = true` | ClientStack | changed, flag-gated |
| C7 | Gateway SSB-aware mode | `D:\web-viewer` | changed (Build Plan S6) |

### 4.2 Bake pipeline (C2 → C1 → C3)

1. **Trigger** (one of): login/`MakeRootAgent` on an SSB region; `UpdateAvatarAppearance` POST; console `appearance bake <first> <last>`; wearables changed via legacy UDP path (`AvatarNowWearing`, Firestorm on a bit-0 region still sends it — VERIFY).
2. **Resolve inputs**: read the agent's COF folder from the inventory service → link items → wearable items → wearable assets (types 5/13) → parse `LLWearable` text → visual params + per-face texture UUIDs; collect `VisualParams` from `AvatarAppearance` as the authoritative param vector (the two must agree — mismatch is a fidelity report entry, not an error).
3. **Hash**: per bake channel, hash (wearable asset IDs, texture IDs, the subset of params that feed that channel's layer set, bake size). Compare with the avatar-service record. Unchanged → skip compute, reuse stored bake UUIDs, still send `AvatarAppearance`.
4. **Fetch** every referenced texture asset once; decode J2K.
5. **Composite** each of the 11 channels via C1 (head, upper, lower, eyes, skirt, hair, leftarm, leftleg, aux1–3; skirt/hair/aux only if the layer set has content).
6. **Encode** J2K, store as assets with the bake flag (ADR-004), record UUIDs + hash + COF version in the avatar service, update `AvatarAppearance.Texture` baked faces in the ScenePresence.
7. **Send** `AvatarAppearance` to self (with `AppearanceData`) and to everyone in view (with `AppearanceData` too — harmless for Firestorm, required for LL viewers observing).
8. **Report**: a structured fidelity report per bake (unsupported layers, missing textures, param/COF disagreement) goes to the log at INFO and to the cap response `error` field only when the bake was refused.

### 4.3 COF version handshake without AIS

The viewer's `cof_version` is the COF folder's inventory `Version`. The sim reads the same number from the inventory service at bake time and records it. Cap response:

- `cof_version == server's` → bake (or reuse) → `{success:true}` and `AvatarAppearance` follows with `CofVersion = cof_version`.
- `cof_version < server's` → `{success:false, expected:<server>}`; viewer re-requests.
- `cof_version > server's` → the viewer changed the COF through a path the sim hasn't seen yet; re-read the folder once, then respond as above. Never livelock: after N mismatches within T seconds, bake anyway with the server's version and log it (Ledger R-2).

Pre-AIS the LL viewer cannot change the COF, so in practice only the login case fires. Firestorm on a bit-0 region *can* change the COF via UDP and will POST; the path above handles it as long as the inventory service bumps `Version` on UDP link changes (S0 verification).

### 4.4 Persistence and expiry (G5)

- Bakes are assets. They carry a marker (ADR-004) so a reaper can find them.
- The avatar service holds, per agent: `Bake:<channel>` = asset UUID, `BakeHash:<channel>`, `BakeCOFVersion`, `BakeSize`, `BakeUpdated` (UTC). No schema change — the Avatars table is key/value.
- A bake is **superseded** when a new one for the same channel is stored; the old asset is deleted immediately (no reaper needed for the common case).
- A bake is **expired** by the reaper when `BakeUpdated` is older than `[Appearance] BakeTTLDays` (default 30) *and* the agent has not logged in since; expiry deletes the assets and clears the keys, so the next login rebakes. This is the "don't clog someone's database" rule (D-6) — the reaper is Robust-side, opt-in, off by default for standalone.
- Stored bakes are also what the web-viewer gateway and any observer fetch via the ordinary asset route, so no second copy is ever needed.

### 4.5 Rollout hazards (from BP-v2)

- `[Appearance] ServerSideBaking` is per region. Default `false`. Turn on for one test region with both viewers present before any other region.
- Flipping bit 0 makes Firestorm switch to `UpdateAvatarAppearance` immediately. If the compositor produces a worse bake than Firestorm would, every Firestorm user on that region degrades — which is why the fidelity harness (S0) precedes the flag (S3), not follows it.
- Turning the flag **off** again is safe: Firestorm reverts to client bake on next login; the LL viewer reverts to cloud; stored bakes are ignored, not deleted.

## 5. Interaction with the web viewer (G3, G4)

| Situation | Gateway behaviour |
|---|---|
| Region advertises bit 0 | Do not bake. Accept the sim's `AvatarAppearance` for self (must accept `AppearanceData`-bearing messages), fetch bakes via the existing asset route, render. `appearance.status = "server"`. |
| Region does not advertise bit 0 (ordinary OpenSim, or flag off) | Current S11/S12 behaviour: gateway-side bake with the complete-or-nothing invariant and the fidelity gate. |
| Library update | Both consumers reference the same `OpenSimNGC.Appearance.Baking` version; golden-fixture tests live with the library, run in both CI paths. |

The gateway's compositor directory is **deleted** once the library reference lands (S0b); no two copies.

## 6. Open design questions for John

Listed in the Ledger as D-1 … D-5 and Q-1 … Q-4. The three that block S0:
- D-1: SSB before AIS (order change from BP-v2's AIS→SSB).
- D-3: sim-side fidelity policy — bake best-effort with a logged report (recommended) vs refuse like the gateway.
- ADR-003: library placement (in-tree project vs NGC NuGet package — affects Mike and the gateway's reference style).
