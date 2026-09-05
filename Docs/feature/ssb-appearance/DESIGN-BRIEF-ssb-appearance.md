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

The viewer's `cof_version` is the COF folder's inventory `Version`. The sim reads the same number from the inventory service and records it. Both are the same field with one writer — the data layer's folder-version bump — which S3 proved rather than assumed (`AisMutation.ReportVersion` and `ServerSideBakingModule.CofVersionOf` read the same `InventoryFolderBase.Version`).

Cap response:

- `cof_version == server's` → **accept**: `{success:true}`.
- `cof_version < server's` → `{success:false, expected:<server>}`; viewer re-requests.
- `cof_version > server's` → the viewer changed the COF through a path the sim hasn't seen yet; re-read the folder once, then respond as above. Never livelock: after N mismatches within T seconds, accept anyway at the server's version and log it (Ledger R-2).

**What `success:true` means (revised in S5).** It means *accepted — the bake will follow within the save cycle*. It does **not** mean "baked", which is what S3 shipped and what the first three bullets used to say.

S3 had the cap bake synchronously and answer afterwards. That is bake-on-arrival, which Q-16 rules out: the POST arrives before the region has resolved the new items to asset ids, and the appearance save that resolves them is up to `DelayBeforeAppearanceSave` (5 s) away. A bake at POST time composites wearables still carrying `UUID.Zero` and stores the result as if it were the new look.

**On the ordering of the two signals.** The 5 s save delay is a configured value and is the only interval here that is established. The spread between `AgentIsNowWearing` and the cap POST for one change is **not measured** — an earlier "310 ms" figure was written into these notes without a source and has been withdrawn (Ledger Q-6). It does not affect the design: S5 makes *both* routes queue an appearance save and bake off its completion, so the ordering between them stops mattering. It affects only the debounce, which is sized against the save delay instead.

So the cap now answers the handshake and queues an appearance save. The bake happens when that save completes, off `OnAvatarAppearanceChange` (§4.6). The legacy `AgentIsNowWearing` route already queued a save, so **both signals converge on one trigger with one ordering** — which matters because Q-6 established that both arrive, not one or the other.

Consequences worth stating:

- `BakeReason.Cap` is **no longer produced**. A cap-driven rebake surfaces as `CofChanged`, because that is what it is. The enum value remains valid.
- The `AvatarAppearance` the viewer is waiting for arrives after the save cycle rather than in step with the cap response. The viewer waits for it either way and does not re-request, so this is a latency change, not a protocol one.
- A change whose channels all hash the same still sends the appearance. Nothing is recomputed, but the message must go out: the viewer will not ask again.

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

### 4.6 The AIS/COF seam (for S5)

AIS v3 is live on Ebony and the Current Outfit folder is the authoritative record of what an agent wears: an
outfit change is `SlamFolder` on the COF and a take-off is `DELETE /item` / `RemoveItem` (AIS ledger A10, which
corrected A7 on exactly this point). The bake does not read the COF. `ServerSideBakingModule` passes
`sp.Appearance.Wearables` to the orchestrator (`ServerSideBakingModule.cs:125`) — a deliberate S1 Part 1a choice,
made when nothing could change the COF behind the region's back. AIS changed that, and S5 owns the consequence.

#### Which store wins

Neither, as stated — the question is malformed, and getting it wrong is how S5 discovers this late.

- **The COF is authoritative about *membership*: which items are worn.** It is what the viewer reads back, what
  `cof_version` counts (§4.3), and what survives a relog.
- **`sp.Appearance.Wearables` is authoritative about *what a bake can be made from*.** The COF holds
  `AssetType.Link` rows (`AisEnvelope.cs:47`), so it names items, not assets; a bake needs asset ids and the
  wearable bodies behind them. The ScenePresence is the only place in the region where membership has already
  been resolved to assets.

So the rule for S5 is an ordering rule, not a precedence rule: **the COF decides *whether* to bake; the
ScenePresence decides *what* to bake; and the bake must not run until the ScenePresence has caught up with the
COF.** A bake that reads one store while the other has moved is not a merge conflict to resolve — it is simply
early.

#### What happens today if they disagree — the trace

**AIS never touches the ScenePresence.** The AIS surface is inventory-backend only by design (Ledger P-2;
`IAisInventoryBackend.cs:9`, `AisInventory.cs:22`): nothing under
`Source/OpenSim.Region.ClientStack.LindenCaps/AIS/` references `ScenePresence`, `AvatarAppearance` or
`AvatarFactory`. A `SlamFolder` that rewrites the COF leaves `sp.Appearance.Wearables` exactly as it was.

**The region learns from the viewer, over UDP, as a separate message.** The only assignment to
`Appearance.Wearables` anywhere in the tree is `AvatarFactoryModule.cs:1289`, inside `Client_OnAvatarNowWearing`
(`:1256`), which is raised by the `AgentIsNowWearing` packet (`LLClientView.cs:8431`, handler `:9229-9244`, event
`:86`). AIS writing the COF and the viewer sending `AgentIsNowWearing` are two independent messages with no
ordering guarantee between them.

**And when it arrives, the asset ids are not there yet.** `MergeNowWearing` (`:1315-1354`) fills a listed item's
`AssetID` from the *existing* contents of that slot (`GetAsset`, `:1345-1349`); an item that was not already in
the slot gets **`UUID.Zero`**. The real asset ids are resolved only in `SetAppearanceAssets` (`:901-947`, its live body; a long commented-out block follows), called
from `SaveAppearance` (`:888`), which runs on a thread pool behind the save queue — `DelayBeforeAppearanceSave`,
default **5 seconds** (`:51`, `:71`), on a 500 ms tick (`:155`).

**So the path is reliable but late, and there is a window.** Between `AgentIsNowWearing` and the queued save, the
newly worn item sits in `sp.Appearance.Wearables` with `AssetID == UUID.Zero`. A bake in that window reads it
through `BakeOrchestrator.ResolveWearables` (`BakeOrchestrator.cs:86-96`) as **worn but assetless** — a genuine
worn instance that contributes its layers' morph masks and no textures at all (the S1c/Q-12 rule, correct in its
own right and exactly wrong here). The avatar is baked wearing the *shape* of the new shirt and none of its
pixels. The window is at least the 5-second save delay, longer when the queue is busy, and unbounded at the front
because nothing bounds the gap between the AIS write and the UDP packet.

Two things already in place soften this and neither is sufficient. The input hash includes the wearable's asset
id (`BakeHash.cs:46`, `:58`), so a stale bake's hash differs from the correct one and a *later* bake will not
reuse it — but the stale bake has already been stored, its face already applied, and the previous good bake
already deleted by supersede. And `SetAppearanceAssets` drops an item whose inventory row is missing
(`AvatarFactoryModule.cs:936-939`), which is a different failure from this one.

#### The options for S5's trigger

**(a) Read the COF directly.** Authoritative, and independent of whether the viewer sends anything. Costs:
`GetFolderForType` plus a folder-content fetch per trigger — a Robust round trip on a grid — then link → item →
asset resolution for every worn item (the descendents cap already does this dance at
`FetchInvDescHandler.cs:436-455`), plus a dependency on the folder `Version` bump (Ledger Q-1: present at the
data layer, `MySQLXInventoryData.cs:162`, `:244`, `:277-278`, `:288`). It also puts a *second* wearable resolver
in the tree alongside `SetAppearanceAssets`, which is the same class of mistake as two lanes deploying from two
branches. Worse, it does not actually fix the disagreement: a bake made from COF-resolved assets writes faces
onto an appearance whose `Wearables` still say `UUID.Zero`, so the next save and the next bake disagree with the
one just stored.

**(b) Keep reading the ScenePresence, and trigger only after the region has applied the change.** Cheap, and it
reuses the one resolver. It depends on the apply path being reliable — and the trace above says it is reliable,
just late, with a precise completion point: `AvatarFactoryModule.cs:890`, immediately after `SetAppearanceAssets`
and `AvatarService.SetAppearance`. An event for exactly this already exists and is unused:
`EventManager.OnAvatarAppearanceChange` / `TriggerAvatarAppearanceChanged` (`EventManager.cs:404-405`,
`:1948-1967`), whose only call site in the tree is **commented out**, on the very next line
(`AvatarFactoryModule.cs:891`), with no subscribers anywhere.

#### What Q-14 means for this, and why it decides the choice

Q-14: an appearance save wipes the bake index, because `AvatarService.SetAvatar` deletes every row for the
principal before rewriting the appearance-derived keys (`AvatarService.cs:93`). **An outfit change is exactly
when an appearance save happens** — `Client_OnAvatarNowWearing` queues one at `AvatarFactoryModule.cs:1292`.

So a bake triggered on the *arrival* of a COF change is wrong twice over: it composites from `UUID.Zero` asset
ids, and about five seconds later the queued save deletes the index it just wrote — so the work is lost as well
as incorrect, and the next login re-bakes from nothing. A bake triggered *after* that save has run reads resolved
asset ids **and** writes its index into a record that has just been rewritten and will not be rewritten again by
this change.

**The stale-wearables problem and the wiped-index problem have the same fix, and it is an ordering fix.** That is
the strongest argument in this section.

#### Recommendation

**(b), hooked to the completion of `SaveAppearance`** — uncomment `TriggerAvatarAppearanceChanged` at
`AvatarFactoryModule.cs:891` (or raise an equivalent event on that line) and make S5's COF-change trigger a
subscriber to it.

In terms of what breaks if the recommendation is wrong:

- **If (b) is wrong** — some outfit change reaches the COF and never produces an `AgentIsNowWearing`, so the save
  never fires — the failure is **a bake that does not happen**. The avatar keeps its previous, *valid* bake and
  looks stale until the next login or one `appearance serverbake`. It is visible, residents report it as "my
  shirt didn't change", it is diagnosable from the absence of an `[SSB]` line, and it is recoverable with one
  console command. Nothing is corrupted.
- **If (a) is wrong** — the COF and the ScenePresence disagree and the bake follows the COF — the failure is **a
  bake that is wrong and stored**: faces written from one truth while `Wearables` holds another, an index whose
  hash describes inputs the ScenePresence never had, and a supersede that has *already deleted the previous good
  asset*. That is the S1d/Q-13 class of defect — a bad bake painted over a good one — and it needs an
  asset-level repair, not a re-trigger.

The asymmetry is the whole argument: **(b) fails late, (a) fails wrong.** A baker that is occasionally late is a
nuisance; a baker that is confidently wrong destroys the previous good bake through supersede. Cost is the
secondary argument and points the same way — (b) is free, (a) is a Robust round trip plus N link resolutions on
every trigger, on a path S2 measured live at 2823 ms cold.

**What (b) requires before S5 can rely on it.** These are S5's work, not caveats:

1. The trigger must fire *after* `AvatarService.SetAppearance`, never before — that is the Q-14 ordering, and it
   is the entire point.
2. `Client_OnAvatarNowWearing` returns early when nothing changed (`:1278-1283`), so no save is queued and no
   event fires. Harmless for the bake (unchanged inputs would be `Reused` anyway), but "no event" must not be
   read as "no change" by anything else that subscribes.
3. `SaveAppearance` drops a queued save when the presence has gone (`:871-880`); `FlushAppearanceSaveOnClose`
   (`:129`) covers the normal close. A bake must not be attempted for a presence that is closing.
4. `BakeCOFVersion` is stored but not compared today (S2, ADR-004 "as built"). §4.3's handshake is what will
   compare it, and it should be read *at the same point* the bake reads the wearables, not earlier.
5. **Ledger Q-6 stands and is the one thing that could overturn this.** If Firestorm on a bit-0 region stops
   sending `AgentIsNowWearing` and only POSTs the cap, (b)'s trigger disappears on precisely the regions SSB is
   enabled for — and then the cap POST becomes the trigger and this section's ordering rule applies to it
   unchanged. That is a measurement, not an argument, and S5 should take it first.

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
