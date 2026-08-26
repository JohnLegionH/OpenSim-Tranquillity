# Design Brief — Per-Room Visibility Emission

**Status:** DRAFT for decision. Nothing implemented. Open questions at the end need answers
before this is frozen.
**Date:** 2026-08-26.
**Basis:** `tranquillity-develop` at *docs(voice): file the per-parcel visibility delivery gap,
split #13 by status* (branch `feature/voice-visibility-matrix`); `legion-voice-mixer` at
*fix(voice): clear stale exclusions on leave, error on unknown room and request* (plugin
`janus.plugin.slvoice` 0.9.0 — the version string was not bumped by that commit, so "0.9.0"
alone does not identify whether the `unknown_room` reply is present; see §5).
**Authority for the problem:** `Docs/KnownDefects.md`, *"WebRTC voice: the visibility feed is
addressed only to the estate room, so per-parcel agents receive no exclusions"*. Every claim
below about current behaviour carries a `file:line` citation against the basis above.

## Purpose

Make sim-authoritative voice visibility — parcel ban/restrict, parcel voice moderation,
`SeeAVs` hiding — take effect for agents on per-parcel voice channels, not only for agents
on the estate channel. Today the Phase-3a matrix is computed correctly for every voiced agent
in the region and then delivered to one room, so it is enforced for a subset of them.

## Binding constraints — decided, not re-opened here

- **No data migration.** No existing parcel's `LandFlags` changes, on this grid or any other.
  Running grids upgrade by deploying code only.
- **`LandData.cs`'s default flag word is out of scope.** OpenSim's default omits
  `ParcelFlags.UseEstateVoiceChan` where SL's `PF_DEFAULT` includes it (`LandData.cs:60`–`:64`
  composes seven flags, `0x2800204B`; bit 30 is absent). That five-bit divergence is a
  separate parity question on its own timeline. **This fix must make the default
  irrelevant, not correct it** — a default-flagged region must be fully covered after this
  change with its parcels left exactly as they are.
- **Per-parcel rooms stay.** See *Rejected: one shared room per region* below.
- **The estate channel must not regress.** Agents on the estate channel are fully covered
  today; every path they take must be byte-for-byte the same or strictly better.

## 1. Problem statement, from source

**The sink fixes its room once, at construction.** `JanusPeerCtlBatchSink`'s constructor
computes `_room = JanusAudioBridge.CalcRoomNumber(regionId.ToString(), "local",
JanusAudioBridge.REGION_ROOM_ID, string.Empty)` (`Addons/os-webrtc-janus/WebRtcVoiceRegionModule/JanusPeerCtlBatchSink.cs:38`–`:39`;
`REGION_ROOM_ID = -999` at `Addons/os-webrtc-janus/Janus/JanusAudioBridge.cs:176`) and stamps that
one number on every request it ever sends (`request["room"] = new OSDInteger(_room)`,
`JanusPeerCtlBatchSink.cs:49`). It is the sole `CalcRoomNumber` call site in the module.

**Everything upstream of the sink is room-agnostic by design.** `VisibilityBatchSender`'s header
says "no Janus / no room number here — the sink stamps the room"
(`WebRtcVoiceRegionModule/VisibilityBatchSender.cs:3`); `IPeerCtlBatchSink.SendAsync` takes only
an op and a listener→sources map (`Visibility/PeerCtlBatchSink.cs:30`–`:33`);
`PeerCtlBatchSerializer.BuildRequest` produces a room-less body (`Visibility/PeerCtlBatchSerializer.cs:34`–`:60`);
and `VoiceVisibilityService` hands the feeder a placeholder, `EstateRoomPlaceholder = -999`
(`VoiceVisibilityService.cs:32`, passed at `:64`), which nothing downstream reads
(`VisibilityBatch.Room`, `Visibility/VisibilityBatch.cs:22`, is carried but never consulted by
the sink).

**Room membership is chosen per agent, per parcel, at provisioning.**
`ProvisionVoiceAccountRequest` removes `parcel_local_id` from the forwarded request only when
the parcel sets `UseEstateVoiceChan` (`WebRtcVoiceRegionModule.cs:502`–`:505`). With the flag
clear the viewer's `parcel_local_id` survives, and the service hashes it:
`int parcel_local_id = pRequest.TryGetInt("parcel_local_id", out int pli) ? pli :
JanusAudioBridge.REGION_ROOM_ID` (`Janus/WebRtcJanusService.cs:243`), then
`SelectRoom(pSceneID.ToString(), channel_type, isSpatial, parcel_local_id, channel_id)`
(`:261`–`:262`) → `CalcRoomNumber` (`JanusAudioBridge.cs:195`), which for `"local"` hashes region
ID + `"local"` + parcel local ID. So a per-parcel agent joins room
`H(region, "local", parcelLocalID)` while its exclusion column is addressed to
`H(region, "local", -999)`.

**At the mixer the mismatch is a listener the room does not contain.** `apply_visbatch` scans
`room->participants` for a session whose display equals the entry's listener UUID
(`legion-voice-mixer/src/janus_slvoice.c:1199`); zero matches increments
`vis_dropped_listener_entries` (`:1284`) and `vis_last_batch_dropped_listeners` (`:1319`) and logs
at `LOG_VERB`. The admin reply is `{"slvoice":"applied", …}` regardless (`:1349`).

**Why the default matters.** With `LandData.cs`'s default word, a freshly created region's
initial parcel has `AllowVoiceChat` set and `UseEstateVoiceChan` clear — exactly the
triggering state. On this grid Elm's parcel reads `0x2800204B`, the untouched default. This is
a default-configuration defect with grid-wide reach, which is why the constraint above
forbids fixing it by touching flags.

**What already landed and is a prerequisite.** *fix(voice): clear stale exclusions on leave,
error on unknown room and request* (mixer) makes a session that leaves and rejoins start with
an empty exclusion set. Per-room emission moves a listener's column between rooms when the
agent re-provisions; without that fix the old room's session would have kept the stale set.

## Rejected: one shared room per region

Considered and rejected; recorded so it is not revisited. Collapsing every agent in a region
into the `-999` room would make scoping purely an exclusion problem and trivially fix delivery,
but the mixer's limits make it untenable:

- **`SLV_MAX_MIX` = 110 is a per-room admission ceiling** (`janus_slvoice.c:140`, enforced at
  join with `ROOM_FULL` at `:1681`). Today it bounds each per-parcel room separately; one room
  per region makes 110 the region-wide voice population cap.
- **The mix tick is O(N²) in one thread per room.** `janus_slvoice_room_tick` enumerates only its
  own `room->participants` (`:2423`) and, per listener, walks every source. Merging rooms
  multiplies N inside a single 20 ms tick thread and removes the parallelism separate rooms
  give across cores.
- **The visibility-batch caps are sized for sparse exclusions** (§3). One room per region makes
  exclusion the only scoping mechanism, so columns become dense — most of the region for most
  listeners — and the 128-source and 64 KB caps are reached at populations well under 110.
- **Per-listener state scales with room population:** `cull_hyst[SLV_MAX_MIX]` per session runs
  at capacity and LRU-evicts; the sender's shared-full-batch fast path for listeners with no
  exclusions (`janus_slvoice_sender`) effectively disappears.

Per-parcel rooms are therefore load-bearing for capacity, and this brief keeps them.

## 2. The proposed change

**Principle:** the feeder and sender stay exactly as they are. The sink — already the only
room-aware component — becomes room-*resolving* instead of room-*fixed*: it groups each
`SendAsync` call's listeners by their current room and emits one `peer_ctl_batch` per room.
The wire format does not change; only the `room` values do.

### 2a. Where the listener's room is known, and the new seam

The room an agent is actually in is decided at exactly one place: the successful JSEP-offer
branch of `WebRtcJanusService.ProvisionVoiceAccountRequest`, where `viewerSession.Room` is set
from `SelectRoom` (`WebRtcJanusService.cs:261`–`:262`) and its `RoomId` is the joined room
(`Janus/JanusViewerSession.cs:65`; `Janus/JanusRoom.cs:40`). Three places could read it:

1. **Live, from `JanusViewerSession.Room.RoomId`.** Ground truth for mixer membership, but
   (i) `IVoiceViewerSession` does not expose a room (`WebRtcVoice/IVoiceViewerSession.cs:36`–`:61`),
   so the sink would cast to the Janus type; (ii) the per-agent index in `VoiceViewerSession`
   is private — only `IsAgentInRegion` is public (`WebRtcVoice/VoiceViewerSession.cs:75`–`:76`,
   `:94`–`:102`) — so a new query is needed anyway; and (iii) in the grid-service topology
   (`SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector`,
   `Addons/os-webrtc-janus/os-webrtc-janus.ini.example`) the region holds
   a `VoiceViewerSession` with no room at all — the `JanusViewerSession` lives on the ROBUST side.
   The sink runs region-side, so this source is topology-dependent.
2. **Recomputed region-side from the agent's parcel** via `CalcRoomNumber`. Topology-independent
   and needs no new plumbing, but it computes the room the agent *should* be in from where it
   is standing now — which diverges from the room it *is* in whenever the agent crossed a parcel
   without re-provisioning (§2b). Batches addressed on that basis land in the wrong room for
   exactly the agents the crossing gap affects. It also couples the sink to `CalcRoomNumber`'s
   encoding (§2c). Rejected as the primary source.
3. **Recorded region-side at provisioning success, from the service's response.** Proposed.

**The proposed seam.** The provision response gains an additive `room` field alongside `jsep`
and `viewer_session` (built at `WebRtcJanusService.cs:275`–`:279`), carrying
`viewerSession.Room.RoomId`. The region module's provisioning handler, at the point where it
already forwards a success to the visibility service (`svc?.OnListenerProvisioned(agentID)`,
`WebRtcVoiceRegionModule.cs:553`, after the service call at `:526`), reads `room` from the
response and passes it: `OnListenerProvisioned(agentID, room)`. `VoiceVisibilityService`
(`:126`) records it in a per-region **agent → room** table and hands the sink a resolver
delegate, `Func<UUID, int?>`, at construction. The sink's `IPeerCtlBatchSink.SendAsync`
signature is unchanged; internally it partitions `excl` by `roomOf(listener)` and sends one
admin message per distinct room.

Why this seam:

- **Topology-independent.** The connector topology forwards the leaf service's response map
  (`WebRtcVoice/WebRtcVoiceServiceConnector.cs:88`–`:95`), so `room` rides through to the region
  from a remote `WebRtcJanusService` exactly as from a local one.
- **Ground truth, not recomputation.** The recorded room is the one the service actually
  joined. No `CalcRoomNumber` call is added on the sim side; the float-encoding coupling in §2c
  does not widen.
- **Naturally gated on success.** Only the JSEP-offer success branch has a `Room`, so only a
  real join produces a `room` field. This also sidesteps *"WebRTC voice: OnListenerProvisioned
  runs on failed provisions, queuing a doomed re-send"* for the room record (the pending-join
  queueing itself is unchanged and that entry stands).
- **Preserves the separation.** Feeder, matrix, delta, sender and serializer are untouched;
  the sender's `_synced` / `_knownListeners` / `_pending` state remains single-instance because
  the sink, not the sender, does the splitting. The only new coupling is a one-way
  `Func<UUID,int?>` from the service into the sink.

**Clearing the record.** A record need not be actively cleared. Matrix membership is already
gated by `VoiceViewerSession.IsAgentInRegion` (`WebRtcVoiceRegionModule/FeederWorldFromScene.cs:61`–`:67`),
so a departed agent is never a listener and its stale record is never consulted; a returning
agent re-provisions and overwrites it. Clearing on close is an optional tidy-up, not a
correctness requirement.

**Sink send semantics across rooms.** `SendAsync` returns `Ok` only if every per-room send
returned `Ok`; any `TransportError` → `TransportError`; any `ProtocolError` → `ProtocolError`.
This preserves the sender's contract: a partial failure makes the sender re-snapshot next
tick, and `replace` is per-listener idempotent, so re-sending the rooms that succeeded is
harmless.

**Listeners with no record.** A listener the matrix names but the table does not know is an
agent that never reached the success branch this region saw. Whether the sink drops that
listener's entry, or falls back to the estate room as today, is **Open Question 4** — the
fallback is the conservative choice for the no-regression constraint.

### 2b. Tracking room changes — and whether this depends on the channel-change gap

Nothing pushes a channel change on parcel crossing today. This module registers
`ProvisionVoiceAccountRequest`, `VoiceSignalingRequest`, `ChatSessionRequest` and
`SpatialVoiceModerationRequest` (`WebRtcVoiceRegionModule.cs:250`–`:275`) and no
`ParcelVoiceInfoRequest`; only the Vivox and FreeSwitch modules do. So an agent's room changes
only when the *viewer* sends a new provision (the service leaves the old room at
`WebRtcJanusService.cs:230`–`:234` and joins the new one at `:261`–`:272`) or on close. Whether
a stock viewer re-provisions on an intra-region parcel crossing cannot be determined from
either repo.

**This fix does not depend on that gap being closed.** The proposed record is written at
provisioning success, which is the only moment membership actually changes. An agent that
crosses from parcel A to parcel B without re-provisioning stays in room A, the record still
says A, and its exclusions land in A — where it is. Its *column* is computed from parcel B
(the feeder resolves parcels by position/`currentParcelUUID`), which is the semantically
right set for where it stands. Any residual wrongness — being audible in A's room while
standing on B — is the crossing gap's, not this fix's, and it is the same wrongness the
estate channel exhibits today.

If the gap is later closed by a push, the push path will terminate in a re-provision, which
writes the record. No change to this design is needed then.

### 2c. The float-encoding defect in `CalcRoomNumber`

*"Parcel local IDs are hashed as `float`, not as an integer"* (`Docs/KnownDefects.md`):
`hasher.Add(pParcelLocalID)` at `JanusAudioBridge.cs:205` binds to `Add(float)` because
`IBHasher` has no `Add(int)`. Both sides that must agree on a room number — the provision
path and, today, the sink — call the same function, so they agree.

**Recommendation: fix it separately, not in this change, and keep this change from touching
the derivation at all.** Three reasons:

- Under the proposed seam the sink stops calling `CalcRoomNumber` entirely (the constructor
  call at `JanusPeerCtlBatchSink.cs:38`–`:39` is removed; rooms arrive from the service). This
  change *reduces* the number of derivation sites from two to one and adds none.
- That KnownDefects entry states the consequence of changing the encoding: every room number on
  the grid changes, and every region and the mixer must cut over together or a rolling upgrade
  splits voice mid-flight. That is a grid-wide renumbering with its own deploy choreography, and
  bundling it into a delivery fix makes the delivery fix undeployable in isolation.
- The encoding is deterministic and collision-free at present parcel counts, so it does not
  impair this fix's correctness.

If **Option 2** in §2a were chosen instead, the sink would gain a second derivation site and
the two would have to be kept binding-identical — a reason not to choose it.

## 3. Capacity analysis

This is the highest-risk section, because over-cap items on the mixer are **skipped and
counted, not errored** — overrun degrades silently.

### Caps, verified against source

| Cap | Value | Where | On overrun |
|---|---|---|---|
| `SLV_VISBATCH_MAX_BYTES` | 65,536 | `src/visbatch.h:39` | whole batch rejected `TOOBIG` (`src/visbatch.c:61`); reply `{"slvoice":"error","reason":"too_big"}` (`janus_slvoice.c:1367`–`:1369`) |
| `SLV_VIS_MAX_ENTRIES` | 128 listeners/batch | `src/visbatch.h:42` | extra listeners skipped, `n_skipped++` (`visbatch.c:119`–`:122`) |
| `SLV_VIS_MAX_EXCL` | 128 sources/listener | `src/visbatch.h:45` | extra sources skipped, `n_skipped++` (`visbatch.c:143`–`:146`) |
| `SLV_MAX_MIX` | 110 participants/room | `janus_slvoice.c:140`, `:1681` | join rejected `ROOM_FULL` |

The byte cap applies to the **inner** request object: Janus core passes only
`json_object_get(root, "request")` to the plugin (`vendor/janus-gateway/src/janus.c:2457`–`:2458`),
and the handler measures `json_dumps` of that (`janus_slvoice.c:1342`–`:1344`). The outer envelope
(`admin_secret`, `transaction`, `plugin`) does not count.

Two properties of "skipped and counted" matter here. First, `n_skipped` is reported only in the
reply's `skipped` field, which `JanusAdminClient` never reads — it classifies solely on the outer
`janus:"success"` (`Janus/JanusAdminClient.cs:142`–`:165`). Second, the `too_big` case is a
plugin-level *error* that also rides inside `janus:"success"`, so a rejected-whole batch is as
invisible to the sim as a truncated one. **Today no sim-side instrument can see either.**

### Wire arithmetic

Compact JSON, 36-character UUIDs. A listener entry with k sources costs ≈ 42 + 39k bytes; the
header `{"request":"peer_ctl_batch","op":"…","room":<int>,"excl":{` ≈ 73. A batch with N
listeners of k sources each ≈ 73 + N(42 + 39k).

- **Byte cap:** ≈ 1,670 source entries per batch with few listeners; ≈ 1,540 with 128 listeners
  (keys cost ≈ 5.3 KB). So 128 listeners × more than ~12 sources each is already `TOOBIG`.
- **Dense worst case, one room of N with every listener excluding every other source**
  (mute-everyone via voice moderation is exactly this shape, moderators excepted):
  ≈ 73 + 3N + 39N². **N = 40 → 62,593 (fits); N = 41 → 65,755 (rejected whole).**

### What per-room emission changes

Today one batch per op per tick carries **every** listener in the region's matrix — including
the per-parcel agents whose entries are then dropped — addressed to one room. Per-room
emission partitions that same content by room. Consequences:

- **`SLV_VIS_MAX_ENTRIES` (128 listeners) becomes unreachable.** A per-room batch holds at most
  that room's population, ≤ 110 by `SLV_MAX_MIX`. Today it is reachable: a region whose voiced
  population exceeds 128 across all channels overflows the single estate batch on a snapshot,
  and the 129th-plus listeners are silently skipped.
- **`SLV_VIS_MAX_EXCL` (128 sources) becomes unreachable *only with same-room source
  filtering*** (Open Question 2). A listener's matrix column contains every excluded source in
  the region, in any room. Sources in other rooms are inert at the mixer — room membership
  already prevents them being heard, and the dot/presence paths iterate `room->participants`
  too — but they consume cap. The symmetric `SeeAVs` rule makes columns wide: an occupant of a
  `SeeAVs=false` parcel excludes every outsider, so its column is (region population − parcel
  population). **Unfiltered, a `SeeAVs=false` parcel in a region of 130+ voiced agents hits the
  cap; filtered to the listener's own room, a column is ≤ 109 by construction.** Filtering
  requires the sink to resolve sources' rooms as well as listeners' — the same table — and to
  drop sources with no record (not in any room, so inert anyway).
- **Byte cap per batch improves** because each room's batch is a subset of today's single
  batch. It does **not** remove the dense case: mute-everyone in a room of 41+ still produces a
  `TOOBIG` delta, and that case exists today on the estate channel unchanged. See *Guard* below.
- **Batch count per tick rises from ≤ 2 to ≤ 2R**, where R is the number of rooms with at least
  one listener whose column changed this tick; a snapshot is R messages. Steady-state deltas are
  sparse (a crossing touches two rooms), so typical R is 0–2. The worst case is a region-wide
  invalidation — an estate-settings change, or a ban-list edit on a parcel with many
  outsiders — which changes every room at once.
- **Sender single-flight interacts with R.** `Pump` skips a tick if the previous send is still
  in flight and forces a snapshot next (`VisibilityBatchSender.cs:123`–`:135`). All R sends of one
  `SendAsync` run inside one flight. At an admin round-trip of t ms, sequential sends fit the
  250 ms cadence only while 2R·t < 250; beyond that every other tick is skipped, each skip
  forces an R-message snapshot, and the storm self-sustains until R·t drops. **The admin
  round-trip is not measured or recorded anywhere in either tree** (the only figure is the
  5,000 ms timeout), so the crossover R cannot be stated numerically here. Mitigation is
  bounded concurrency: rooms are independent, `apply_visbatch` takes only its own room's
  mutex, so per-room sends can be issued in parallel with a small cap (Open Question 3).
- **Mixer tick cost is unaffected.** Each per-room message takes its room's mutex for one
  sub-millisecond apply; K messages across K rooms contend with nothing but their own room's
  20 ms tick. Sim-side, grouping and filtering are O(total entries), negligible against the
  O(N²) matrix build.

### The parcel/population shape at which a cap is first approached

1. **Dense exclusion inside one room — first cap hit, and pre-existing.** Mute-everyone on a
   room of **41 or more** produces a `TOOBIG` add/replace that is rejected whole and invisible
   to the sim. Same today on the estate channel. Per-room emission neither causes nor cures it.
2. **Wide `SeeAVs` columns without filtering** — `SLV_VIS_MAX_EXCL` at a region of ~130 voiced
   agents with one `SeeAVs=false` parcel. Removed by same-room filtering.
3. **Region voiced population > 128** — `SLV_VIS_MAX_ENTRIES` on today's single batch. Removed
   by per-room emission regardless of filtering.
4. **Many occupied per-parcel rooms changing at once** — the single-flight storm above. Not a
   mixer cap; a sim-side latency budget with an unmeasured constant.

### A heavily subdivided region

Take 64 parcels, all with `UseEstateVoiceChan` clear (the default), each occupied by one or
two agents. R = 64 rooms with listeners. Steady state: almost every tick touches no room or one;
fine. A region-wide invalidation: 64 rooms × up to 2 ops = 128 sequential admin messages in one
flight; at any plausible LAN round-trip this exceeds 250 ms, the next tick is skipped, a
64-message snapshot follows, and the region oscillates until it settles. Each message is tiny
(a room of two has columns of ≤ 1), so no mixer cap is near; the whole cost is round-trips.
Bounded concurrency turns 128 sequential round-trips into ⌈128/c⌉ rounds. Conversely a region
of 64 parcels on the **estate** channel is one room and one message per op, exactly as today —
the subdivided cost is paid only where per-parcel rooms exist, which is the population this
fix is for.

### Guard against silent truncation

Needed, in two places:

- **Sim-side chunking in the sink.** Before sending a per-room batch, split it by listener into
  messages of ≤ 128 listeners and ≤ ~60 KB (leaving headroom under 65,536). Ops are
  per-listener scoped and idempotent, so a listener's entry moves whole into one chunk; never
  split one listener's array across chunks for `replace`. This removes `SLV_VIS_MAX_ENTRIES` and
  the byte cap as silent failure modes and turns the mute-everyone-at-41 case into two
  messages. The mixer's limits become sim-side constants that must track the mixer's — a version
  coupling to record next to them. A per-listener column over 128 sources cannot be chunked
  under `replace`; with same-room filtering it cannot occur, which is a further argument for
  filtering.
- **Sim-side counters and one-shot logs** in the sink: chunks emitted, and any listener whose
  column exceeded the per-listener cap (should be zero with filtering). These are the only
  signal the sim can produce without reading the mixer's reply.
- **Reading the inner reply** (§5) would additionally expose `skipped > 0` and `too_big`
  directly. Recommended, but a separate decision.

## 4. Version skew

The wire format is unchanged: every batch already carries `op`, `room` and `excl`, and the
mixer keys application on `room` per batch (`visbatch.c:94`–`:100`, `janus_slvoice.c:1160`).
Per-room emission only changes the *values* in `room`. The parser ignores unknown keys, but
this change adds none, so that property is not what the skew analysis rests on.

**New sim against old mixer (0.7.0 through 0.9.0 without the `unknown_room` commit).**
Fully functional, not merely graceful. The old mixer applies each batch to the room it names,
which now exists and contains the listener. The one difference is diagnostic: a batch to a
room the mixer does not hold gets `{"slvoice":"applied"}` instead of an error — and since
`JanusAdminClient` reads only the outer envelope, the sim behaves identically either way. No
new mixer is required to deploy this fix. Established from source: the old
`apply_visbatch`/`handle_admin_message` path is the one documented in
`mixer-feed-protocol.md` §3.3.1 and unchanged in shape.

**Old sim against new mixer.** The old sim still addresses everything to the estate room. If
that room exists, the new mixer applies the batch and drops per-parcel listeners with the
`LOG_VERB` line and counters, as before. If it does not exist — a region where no parcel uses
the estate channel, i.e. a default-flagged region — the reply is now
`{"slvoice":"error","reason":"unknown_room"}` (`janus_slvoice.c:1358`–`:1359`) inside
`janus:"success"` (`janus.c:2460`–`:2463`) rather than `applied`. The sim classifies both as `Ok`
(`JanusAdminClient.cs:155`–`:158`). The `WARN` "peer_ctl_batch for unknown room … dropped"
pre-dates the change (it was at the same `LOG_WARN` in 0.9.0), so log volume is unchanged. No
regression.

**Sim-internal skew (connector topology).** A new region module against an old remote
`WebRtcJanusService` receives no `room` in the provision response. The record is never
written and the sink takes the no-record path (Open Question 4). With the estate-room
fallback, behaviour is exactly today's; with drop, per-parcel agents get nothing, as today.
Either way not a regression, but the fallback is the only choice that keeps estate-channel
delivery working in that mixed state.

**When `unknown_room` can legitimately occur after this fix.** Rooms are never destroyed on
empty: the `g_hash_table_size(room->participants) == 0` test at `janus_slvoice.c:1920` is the
sender skipping idle rooms, and no sim-side path calls `JanusAudioBridge.DestroyRoom`. A
recorded room therefore exists until the mixer restarts. After a mixer restart every recorded
room is unknown until its agents re-provision — a pre-existing condition for the estate room,
now visible per room in the mixer log. Any inner-reply reading added in §5 must treat
`unknown_room` as non-latching for this reason.

## 5. Verification plan

Instruments that exist today, and what each should show after the fix.

**Before deploying, establish the baseline on a per-parcel room.** Pick a parcel with
`UseEstateVoiceChan` clear (`SELECT LandFlags FROM land WHERE LandFlags & 0x40000000 = 0`,
read-only). Provision two avatars there and ban one from the parcel *after* it has joined (the
provisioning check now denies a banned avatar at join, *fix(voice): enforce parcel
ban/restrict on the estate voice channel*, so the ban must come second to be a mixer-side
test). Then:

1. **`handle_info` → `excluded_entries`** (`janus_slvoice.c:1014`) on the un-banned listener's
   handle. Before: 0 — the column went to the estate room. After: 1.
2. **`handle_info` → `visibility.dropped_listener_entries` and
   `visibility.last_batch_dropped_listeners`** (`:1079`, `:1081`) on any estate-room handle.
   Before: `dropped_listener_entries` climbs by the number of per-parcel listeners on every
   changed tick and `last_batch_dropped_listeners` is non-zero after each. After: both stop
   moving; `last_batch_dropped_listeners` reads 0 in steady state. **This counter pair is the
   single best regression signal** — it should read zero on every room on a healthy grid.
3. **`visibility.epoch`** on the per-parcel room's handle. Before: 0 forever. After: increments
   on each applied batch. Confirms the room is being addressed at all.
4. **The sink's start-up log** (`JanusPeerCtlBatchSink.cs:40`–`:41`) currently prints one
   estate room number "compare vs handle_info". Replace with a per-send debug line naming the
   room(s) addressed, and a counter of distinct rooms addressed per tick.
5. **Audible check**, the only end-to-end proof: the banned avatar's voice is inaudible to the
   other occupant of the per-parcel room; on the estate channel, unchanged.
6. **The `unknown_room` reply.** With the current `JanusAdminClient` it is invisible — the
   client maps every `janus:"success"` to `Ok` (`JanusAdminClient.cs:155`–`:158`) and discards
   the body. **This fix does not require teaching it to read the inner field**: correctness
   rests on addressing the right room, and §4 shows the fix works against a mixer that does not
   send the error at all. But it **should** be taught, as a separate decision (Open Question 5),
   because it is the only way the sim can see `too_big`, `skipped`, and `unknown_room`, and
   because the sink's chunking guard in §3 otherwise has no far-end confirmation. If it is
   taught, the result class must be new — not `ProtocolError`, whose K-consecutive latch would
   disable emission on the benign mixer-restart case in §4.
7. **Console: `janus list rooms`** (`WebRtcJanusService.cs:431`–`:432`) to see per-parcel rooms
   exist and their populations; `show voice closing` for parked sessions that would explain a
   missing record.

**Existing tests to extend.** `Tests/WebRtcJanusService.Tests/VisibilityBatchSenderTests.cs`
(16 tests) drives the sender through a fake sink; the sender is unchanged so these should pass
untouched. New unit coverage belongs on the sink: partitioning by resolver, no-record policy,
result aggregation across rooms, chunking boundaries at 128 listeners and the byte limit, and
(if adopted) same-room filtering. `PeerCtlBatchSerializerTests.cs` covers the body builder,
which is unchanged.

## 6. What this fix does NOT address

- **The `TaxFree` void.** `LandObject.IsBannedFromLand` and `IsRestrictedFromLand` return
  `false` under `EstateSettings.TaxFree` (`LandObject.cs:847`–`:848`, `:878`–`:879`), so the
  provisioning ban check is a no-op on `TaxFree` estates on both channels. The matrix overrides
  this on its own side via `LandBan.IsBannedIgnoringTaxFree`; the two layers disagree under
  `TaxFree`. Documented in `parcel-voice-semantics.md` addendum §E and §P; untouched here.
- **The missing channel-change push.** No `ParcelVoiceInfoRequest` CAP; an agent crossing
  parcels intra-region stays in its old room until the viewer re-provisions. §2b shows this fix
  is independent of it. Not filed in `KnownDefects.md` as of the basis commit — it is
  referenced only in the commit message of *fix(voice): enforce parcel ban/restrict on the
  estate voice channel* ("tracked separately with the missing channel-change push"). It should
  be filed.
- **The `LandData.cs` default divergence.** Bit 30 absent from OpenSim's default word. Binding
  constraint: out of scope; this fix must work with it in place.
- **The dense-room byte cap.** Mute-everyone in a room of 41+ is `TOOBIG` today and stays so
  unless the chunking guard in §3 is adopted; it is proposed here but is a separate decision
  from the delivery fix itself.
- **`OnListenerProvisioned` on failed provisions.** The pending-join queueing for failed
  provisions is unchanged; only the room record is gated on success.

## 7. Open questions — your decision

**OQ1 — Room source.** (a) *Proposed:* additive `room` in the provision response, recorded
region-side at success. Topology-independent, ground truth, no new derivation site; needs a
one-line service change and a one-line region-module read. (b) Live read of
`JanusViewerSession.Room.RoomId` through a new query on the session registry. Ground truth
without a wire change, but topology-dependent (no room region-side in the connector
deployment) and needs the private per-agent index exposed. (c) Region-side recompute via
`CalcRoomNumber` from the agent's current parcel. No plumbing at all, but addresses the room
the agent *should* be in rather than *is* in (§2b), and adds a second derivation site bound to
the float encoding (§2c).

**OQ2 — Same-room source filtering.** (a) *Recommended:* drop sources whose recorded room
differs from the listener's. Makes `SLV_VIS_MAX_EXCL` unreachable, shrinks batches, and is
semantically lossless because cross-room sources are inert at the mixer. Costs: the sink must
resolve source rooms; a source with no record is dropped (correct — it is in no room — but a
source whose record lags a re-provision is dropped for that interval). (b) No filtering: send
full columns. Simpler; cap reachable at ~130 voiced agents with a `SeeAVs=false` parcel, and
silently.

**OQ3 — Per-room send concurrency.** (a) Sequential within one flight, as the single-flight
model implies today. Simplest; storm risk at large R with an unmeasured round-trip. (b)
Bounded parallel (e.g. 4–8 in flight) per `SendAsync`, aggregated result. Removes the storm at
the cost of concurrent `HttpClient` use on one long-lived client (supported) and a small
change to the sink only. (c) Per-room single-flight in the sender — rejected as re-opening the
sender.

**OQ4 — No-record policy.** (a) Fall back to the estate room, with a counter. Preserves
today's behaviour exactly for any listener the record does not cover, which is the strongest
form of the no-regression guarantee and covers the connector-topology skew in §4. (b) Drop the
entry, with a counter. Cleaner; a listener with no record is not in any room this region
knows about, so the estate room is a guess. The counter under (a) tells you when (b) would
have been safe.

**OQ5 — Teach `JanusAdminClient` to read the inner `slvoice` reply.** (a) Not in this change:
correctness does not need it. (b) In this change, adding a fourth `AdminSendResult` (say
`NotApplied`) for `error`/`empty`/`skipped>0` that logs and counts but **never latches**. Gives
the sink far-end confirmation of chunking and the only sim-side view of `too_big`. Touches the
admin client's tests and the sink's result mapping.

**OQ6 — Chunking guard.** (a) In this change, in the sink, with mixer-cap constants mirrored
sim-side. (b) Separate change; the mute-everyone-at-41 case is pre-existing and per-room
emission does not worsen it. If (a), the mirrored constants need a home and a note that they
must track `visbatch.h`.

**OQ7 — Multiple sessions per agent.** During a relog overlap an agent briefly holds two
sessions, possibly in different rooms. (a) Newest provision wins the record; the older session
receives no exclusions until torn down (transient, bounded by teardown). (b) Record a set of
rooms per agent and address all of them; the mixer fans out by display within a room already
(`janus_slvoice.c:1199`), so this only matters across rooms. (a) is simpler and the window is
the existing teardown window.

## Acceptance

- On a per-parcel room, a ban applied after join produces `excluded_entries = 1` on the other
  occupant's handle and inaudibility in-world; on the estate channel, unchanged.
- `visibility.last_batch_dropped_listeners` reads 0 on every room in steady state;
  `dropped_listener_entries` stops climbing.
- A default-flagged region (all parcels `0x2800204B`, `UseEstateVoiceChan` clear) is fully
  covered with no flag changed.
- The existing 16 `VisibilityBatchSenderTests` pass unmodified.
- Against a mixer without the `unknown_room` commit, behaviour is identical (§4).
