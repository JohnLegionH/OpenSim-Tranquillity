# Ledger — WebRTC Voice Programme

**Artifact type:** Ledger — **LIVING**. Never frozen. Amend in place; date every change.
**Last reconciled:** 2026-08-26, against `tranquillity-develop` at *feat(voice): return the
joined room in the provision success response* (`3c95ddea0e`, branch
`feature/voice-visibility-matrix`) and `legion-voice-mixer` at *fix(voice): clear stale
exclusions on leave, error on unknown room and request* (`872f0d9`, branch `main`).
**Scope:** the whole voice programme — the `os-webrtc-janus` addon in this tree, the
`janus.plugin.slvoice` mixer, and the documents about both. Adjacent parcel/estate enforcement
defects are listed only where a voice document depends on them.
**Convention note:** neither repository has a `ledger/`, `adr/` or `rfc/` directory or any written
artifact-type convention; the Discovery Note / Recon Report / Design Brief / Build Plan / Ledger /
ADR / RFC vocabulary exists only in conversation. This file lives in `Docs/voice/` because every
voice artifact does and because that directory is the cross-repo-synced one
(`Docs/voice/.gitattributes`: synced verbatim with `legion-voice-mixer:docs/voice/`, LF-pinned).
**A copy is owed to the mixer repo** under the same sync convention; it was not created because
that repo was out of scope for this task.

## How to read the evidence marks

Every status claim carries one of:

- **[SRC]** — verified against source, a commit in the log, a live log line, a live config
  value, or container metadata, read during this reconciliation. The citation follows.
- **[DOC]** — stated in a document. A document's own status header is a *claim*, not evidence;
  several were found stale this week and are listed in §4.3.
- **[INF]** — inferred by the reconciler from [SRC] facts, with the inference stated.
- **UNKNOWN** — could not be established. §6 says what would settle each.

Commits are cited by subject (this branch is rebased; SHAs go stale — `Docs/KnownDefects.md`
preamble). Where a short SHA is given it is paired with the subject.

---

## 1. The authority

**Path:** `legion-voice-mixer/docs/voice/webrtc-voice-spec.md`, 198 lines. **There is no copy in
this tree** — `Docs/voice/` here holds five files, none of them the spec (§4.4). Every phase
plan's `§3.1 / §7.2 / §7.4 / §10` references resolve to this file [SRC: the 3b brief's
`**Authority:**` line cites it by path; `sldata-extensions.md` and `protocol-compat.md` call it
"the vendored spec"].

**Freeze state: `Status: Draft for review`** [SRC: line 3]. It has **never been frozen**, carries
no date, no basis commit, and no amendment log. Its section map: §1 goals, §2 provenance, §3
Tier 0 trust/privacy (3.1 server-relayed, 3.2 trust domains, 3.3 sim-authoritative enforcement
with "version epochs" that "fail closed", 3.4 mixer output is the permission boundary, 3.5
capture consent), §4 Tier 1 diagnosability (4.1 per-connection state vector + `diag` SLData
member + `voice status <avatar>` console, 4.2 echo test, 4.3 fail loud, 4.4 session event log,
4.5 fleet observability), §5 Tier 2 media quality, §6 Tier 3 spatial engine, §7 Tier 4 features
(7.1 camera leash, 7.2 performer mode, 7.3 parcel/estate zones, 7.4 voice morphing/PSOLA, 7.5
connector layer, 7.6 moderation surface incl. estate mute/gain and podium), §8 deployment
tiers, §9 protocol compatibility, §10 four open questions. There is no `§10.1` heading — §10 is
a numbered list, so "§10.1" in conversation means its item 1 (hypergrid group/P2P policy).

**Does it still match the code?** Section by section, against `src/janus_slvoice.c` and the
addon at the basis commits:

| Spec | Code | Mark |
|---|---|---|
| §3.1 all media server-relayed | Mixer is an MCU; no client-to-client ICE path exists in either repo | [SRC] structural: only `relay_rtp` to the owning handle |
| §3.2 trust domains, HG-only-on-grid-servers, disclosure | Nothing. `grep -ci trust src/` = 0; no provisioning-time classification | [SRC] absent |
| §3.3 sim-authoritative enforcement | Yes for audibility: the 3a matrix is built from sim state only (`Visibility/FeederWorld.cs`, `VisibilityRules.cs`) | [SRC] |
| §3.3 "version epochs … fails closed on staleness" | **No.** The `peer_ctl_batch` wire carries `op`/`room`/`excl` only (`src/visbatch.c:76`–`:107`); the mixer keeps its last state indefinitely — fail-**open**. The `vis_epoch` counter (`janus_slvoice.c`) is a diagnostic tally, not a protocol epoch | [SRC] contradicts spec |
| §3.4 mixer output is the boundary | Yes: exclusion is a hard drop before the sum (`janus_slvoice.c`, pass 2 `mutes[j]=1` on `slv_roster_excludes`; `mix.c` skips muted) | [SRC] |
| §3.5 capture consent | No capture exists (§7.5 unbuilt), so nothing to gate | [SRC] absent |
| §4.1 per-connection state vector / `diag` / `voice status` | `query_session` exposes rtp in/out rates, decode, mix memberships, RMS, tick histogram, visibility counters [SRC]. **No** `diag` SLData member (`grep '"diag"'` = 0) and **no** `voice status` console command — the sim registers only `janus info`, `janus list rooms`, `show voice closing` [SRC: `AddCommand` sites] | partial |
| §4.2 echo test | Yes: `{"echo":true}` SLData toggle + `SLV_ECHO_AUTOSTART` [SRC: `SLV_FIELD_ECHO`, `echo_start_locked`] | [SRC] |
| §4.3 fail loud | No data-channel failure signalling exists | [SRC] absent |
| §4.4 session event log | None (`grep 'event log'` = 0) | [SRC] absent |
| §4.5 fleet observability | None beyond per-handle `query_session` | [SRC] absent |
| §5 48 kHz float, no NS/AGC, ≥32k ingest / 64–96k stereo mix, DTX/VAD 100–200 ms release, encode-skip | Yes: `SLV_RATE 48000`, `SLV_CHANNELS 2`, `SLV_OPUS_BITRATE 64000`, `SLV_VAD_RELEASE_MS 150`, encode skipped on silent mix | [SRC] |
| §5 degradation ladder | None (`grep degrad` = 0) | [SRC] absent |
| §6 cull first (distance/VAD/level) | Yes | [SRC] |
| §6 HRTF, ITD, distance tiers, azimuth binning, dirty-flag recompute | None — the plugin's own description says "no HRTF, ITD, distance tiers or azimuth binning yet" [SRC: `JANUS_SLVOICE_DESCRIPTION`]; what ships is cull + attenuation + constant-power azimuth pan | partial |
| §6 listener orientation from `lh` | Yes: `slv_azimuth(s->snap_lp, s->snap_lh, …)` | [SRC] |
| §7.1 camera leash | Yes, but **per-process jcfg**, not estate-configurable — recorded as a known deviation in the 3b brief | [SRC] partial |
| §7.2 performer mode | None (`grep performer src/` = 0; no commit) | [SRC] absent |
| §7.3 parcel/estate zones in the mix | Estate channel: yes. Per-parcel channels: **no** — the delivery gap (§3, item 3a-D) | [SRC] partial |
| §7.4 voice morphing / PSOLA | None (`grep morph src/` = 0; the four `morph` hits in this tree's history are unrelated BulletSim/HTTP commits) | [SRC] absent |
| §7.5 connector layer | None in code; a DRAFT brief exists (§3 item C) | [SRC] absent |
| §7.6 moderation: estate mute/gain, podium, parcel zones | **Parcel** voice moderation slice 1 exists (sim CAP + store + matrix rule) [SRC]. Estate-level mute/gain and podium: none (`grep podium` = 0; `grep estate src/` = 1, a comment) | partial |
| §8.1 small deployment: one INI section, docker compose | Yes for the shipped shape [SRC: `docker-compose.yml`, `[WebRtcVoice]`/`[JanusWebRtcVoice]`] | [SRC] |
| §8.2/§8.3 placement, admission backpressure, TURN fleet, ambisonics, migration | None (`grep ambisonic` = 0; one mixer per grid) | [SRC] absent |
| §9 caps, fmtp mangle, SLData fields | Yes: both caps; fmtp `minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000` at `janus_slvoice.c:1457`; `j/l/sp/sh/lp/lh/m/ug` parsed | [SRC] |
| §9 "parcel changes within a region do not trigger connection changes" | True in the sense that nothing pushes one — and that is now a recorded gap, not a feature (§4.1 item O-11) | [SRC] |

**Net:** the spec is an aspirational Draft. Tiers 0/1 are partially built, Tier 2 built, Tier 3
built to the "mid tier" only, Tier 4 has one of six features (7.3, and only on the estate
channel) plus half of a seventh (7.6 parcel moderation). Nothing in either repo claims otherwise
in code; the only document that overstates is the mixer README (§4.3).

---

## 2. Phase structure as the repositories record it

The mixer's git log is the cleanest phase record in either repo [SRC: 66 commits, subjects
below]. This tree's log records the sim halves of 3a onward and the moderation/teardown/ban
work; it does not use phase numbers for its own commits except "Phase 3a".

**As recorded:**

| # | Phase (recorded name) | Recording commit(s) | Version |
|---|---|---|---|
| 0 | *Phase 0 scaffold: janus.plugin.slvoice spatial voice mixer* (2026-08-12) | mixer `e1abecf` | — |
| 1 | *Phase 1: single-participant echo test (JSEP + Opus + data channel)*; *Phase 1A: hold a WebRTC voice session (answer the SLData data channel)*; *Phase 1B: echo audio on the held session* | mixer `e483799`, `cc39f2b`, `2d474f9` | 0.3.0 → 0.5.0 |
| 2 | *Phase 2: flat N-minus-one conference mixing* | mixer `1381077` | 0.6.0 |
| 3a | *Phase 3a: consume the per-listener visibility matrix (peer_ctl_batch)* (mixer); *VoiceStateFeeder — per-listener visibility matrix producer (Phase 3a)*, *FeederWorldFromScene adapter + region-module wiring (Phase 3a)*, *JanusAdminClient … (Phase 3a prereq)*, *peer_ctl_batch sender — wire the visibility matrix to the mixer (option C)* (this tree) | mixer `fbb0b7b`; tree `3e29a7c6f7`, `e044ee1670`, `caf5bc7b49`, `fc1454ea3e` | 0.7.0 |
| 3b | *Phase 3b slice 1 — geometry snapshot pass with §7.1 camera leash*; *slice 2 — distance cull with hysteresis*; *slice 3 — distance attenuation*; *item 4 — horizontal azimuth* + *stereo mix core and constant-power pan law* + *wire azimuth pan into the mix*; brief Amendments 1–8 | mixer `ac6a12a`, `ccddc93`, `8e40959`, `25787a3`, `6411c16`, `3b7cd94` | 0.8.0 → 0.9.0 |

**Not phases, but recorded work streams** (no phase number in any commit or document):
voice moderation slice 1 (this tree, 2026-08-21/22); presence-close teardown (this tree,
2026-08-22); capacity cap / HTTP 409 (both repos, 2026-08-20); scaling assessment (mixer doc,
2026-08-18/20); duplicate-display detection + fan-out (mixer, 2026-08-19/22); runtime spatial
config (mixer, 2026-08-21); connector design brief DRAFT (mixer doc, 2026-08-21/22); estate-channel
ban fix and the per-room emission plan S1–S5 (this tree, 2026-08-25/26).

**Correcting the chat-history claim.** The list "recon, echo-test bring-up, mixing, 3a, 3b,
ACL/position push with epoch fail-closed, trust domains and HG policy, features (performer,
estate mute/gain, podium, echo console trigger), connector with PSOLA morphing" is **not how
either repository records the programme**, and it is nine items, not seven:

- "recon" is not a phase; it is a set of documents (`current-architecture.md`,
  `parcel-voice-semantics.md` baseline, `Docs/audit/webrtc-upstream-audit.md`) [SRC].
- "echo-test bring-up", "mixing", "3a", "3b" map to Phases 1, 2, 3a, 3b [SRC].
- "ACL/position push with epoch fail-closed" exists **only as spec §3.3 language**. No commit,
  document, or code names it as a phase; no epoch is on the wire; the sim position feed was
  explicitly deferred by the 3b brief ("ship on viewer-supplied geometry") [SRC].
- "trust domains and HG policy" exists only as spec §3.2 and §10 item 1, plus connector-brief
  Q5 [SRC]. No code, no phase.
- "features (performer mode, estate mute/gain, podium, echo console trigger)" exist only as
  spec §7.2/§7.6. The echo test exists as an SLData toggle and an env knob, **not** a console
  trigger [SRC: no console command]. None of the four is a phase anywhere.
- "connector layer with PSOLA morphing" conflates spec §7.5 (connector; DRAFT brief exists)
  with §7.4 (morphing; PSOLA is a licensing recommendation in the spec, nothing more). No code.

So the repositories record **six phases (0, 1A/1B, 2, 3a, 3b)** plus unnumbered work streams,
and treat everything from spec §3.2/§3.3-epochs/§7.2/§7.4/§7.5/§7.6-estate onward as
**unscheduled**.

---

## 3. Status by phase and work item

Statuses: **done** / **partial** / **not started** / UNKNOWN. "Done" means the code is in the
basis commit and exercised; live verification is stated separately because it is separately
evidenced.

### Phase 0 — scaffold — done
[SRC] mixer `e1abecf`; `docs/protocol-compat.md` records the audiobridge-superset constraint.
Nothing remains. See §4.3 for that document's stale status.

### Phase 1A/1B — hold a session, echo — done
[SRC] `janus_slvoice_negotiate` answers both m-lines; `incoming_data` parses SLData; echo ring
+ `build_echo`; `SLV_ECHO_AUTOSTART` in `docker-entrypoint.sh`. Live verification: [DOC]
`phase1-bringup.md` CHECK 1/2 described against Firestorm 7.2.2; no dated pass is recorded in
that file. Remaining: none as a phase. Echo ring is now allocated lazily (*slvoice: allocate the
240KB echo delay ring lazily on first enable*) [SRC: `janus_slvoice.c:2148`].

### Phase 2 — flat N-minus-one mix — done
[SRC] `janus_slvoice_room_tick` pass 1 decode / pass 2 per-listener sum via
`slv_mix_nminus1_stereo`; per-source `m`/`ug` honoured; encode-skip; tick histogram. Live
verification: **no dated CHECK 3 pass is recorded anywhere** [SRC: grep across both docs trees].
[INF] the Phase 3a acceptance runs of 2026-08-17/18 (which require a working mix to observe
exclusion) exercised it live. `docs/sldata-extensions.md` says `m`/`ug` were "verified against
the viewer" [DOC].

### Phase 3a — sim-authoritative per-listener visibility — done for the estate channel; partial overall
Sim: feeder (`Visibility/VoiceStateFeeder.cs`), matrix (`VisibilityMatrix.cs`), rules
(`VisibilityRules.cs`: voice-enable, estate ban, moderation, parcel ban/restrict symmetric,
SeeAVs symmetric), Scene adapter (`FeederWorldFromScene.cs`) with the TaxFree-ignoring ban
delegate (`LandBan.cs`), sender (`VisibilityBatchSender.cs`), Admin-API transport
(`JanusAdminClient.cs`), sink (`JanusPeerCtlBatchSink.cs`), Watchdog-registered tick thread
(`VoiceVisibilityService.cs:112`) [SRC]. Mixer: `visbatch.c` parser, `apply_visbatch` with
display fan-out, `slv_roster_excludes` used by mix, dot batch, presence and join backlog, drop
counters [SRC]. Live verification: [DOC] `parcel-voice-semantics.md` ADDENDUM 2/3 — acceptance
run §O on 2026-08-18 against this tree `8e52212b0f` and **mixer plugin 0.7.0**; [SRC] the live
config has `VisibilityFeederEnabled = true`, `VisibilityEmitEnabled = true`; the region log of
2026-08-25 20:29 shows all three sinks constructed and feeders started `(emit=True)`, and zero
emission errors, latches or give-ups through the 2026-08-26 04:54 shutdown.

**What remains (recorded):** 3a-D the per-parcel delivery gap (§4.1 O-1); 3a-E the estate-room
fallback interplay with the missing channel-change push (O-11); §G neighbour-region rooms
(O-12); §M duplicate-display residue (O-13); the 64 KB dense-batch rejection (O-2); the
pending-join give-up noise (O-8); two sender unit tests failing since the ILogger migration
(O-20).

### Phase 3b — spatial DSP — done to the spec's "mid tier"; the near/far tiers not started
[SRC] slice 1 geometry snapshot + leash (`snapshot_geometry_locked`), slice 2 cull with
hysteresis (`distance_cull_locked`, `cull_hyst[]`), slice 3 attenuation (`pow(t, falloff_exp)`),
item 4 azimuth + constant-power pan (`azimuth.h`, `pan.h`, `mix.c` stereo), runtime jcfg for the
five constants (`load_spatial_settings`), per-channel RMS diagnostics. Live verification:
UNKNOWN — no dated in-world listening record exists in either repo; the 3b brief states
"verification is numeric (no listening tests available)" and the unit tests are the acceptance
criteria [DOC]. **Remaining, all recorded as deferred in the 3b brief:** HRTF+ITD (near tier),
azimuth binning + crossfades, far-tier mono ambience, dirty-flag coefficient recompute,
sim-authoritative position feed (child agents; cross-region frame transform), estate-level
leash configuration (ships per-process), per-region spatial config (Amendment 8: "deferred, not
rejected"). Non-blocking open question: do neighbour-room handles carry `sp`?

### Voice moderation — slice 1 done; parity gap open
[SRC] `SpatialVoiceModerationRequest` CAP registered (`WebRtcVoiceRegionModule.cs:271`),
`VoiceModerationStore.cs`, `VoiceModerationAuth.cs`, matrix rule 2b (`VisibilityRules.cs:37`).
Live verification: [DOC] brief `Status: … VERIFIED end to end (2026-08-22)`, re-verified on
net10 2026-08-24, requiring a Firestorm master-tracking build. **Open:** moderation state is
reported to no client — SL parity gap, worked around **viewer-side** on `phoenix-firestorm`
branch `fix/voice-webrtc-fixes` [DOC]; OQ1 (`mute_all` scope confirmation in SL) and OQ2
(exemption set) unanswered [DOC]. The moderation store is in-memory, non-persistent [SRC].

### Presence-close teardown — done
[SRC] *webrtc-voice: presence-close teardown with generation-token capture* +
*teardown diagnostics*; `VoiceViewerSession.CaptureSessionsForClose`, `ClosingSessions`,
`show voice closing`. KnownDefects entry status "implemented 2026-08-22" agrees [DOC].

### Capacity cap and HTTP 409 — done
[SRC] mixer join-time `ROOM_FULL` at `SLV_MAX_MIX` = 110 (`janus_slvoice.c:1681`); sim maps
495 → 409 (`WebRtcVoiceRegionModule.cs:540`). Remaining: the all-audible load case is guarded
by nothing (mixer comment at the cap) [SRC]; scaling-assessment open items (O-16).

### Duplicate-display handling — partial
[SRC] *slvoice: fan visibility exclusions out to every session matching the listener display*;
*join-time duplicate-display detection + deterministic dot-batch merge*. Remaining [DOC §M
addendum]: orphan capacity burn, leave-dot ghosting; eviction deliberately not done
(cannot notify an evicted viewer). **This tree's KnownDefects entry still says "not started"**
(§4.3-a).

### Estate-channel ban/restrict at provisioning — done
[SRC] *fix(voice): enforce parcel ban/restrict on the estate voice channel*
(`WebRtcVoiceRegionModule.cs:513`). Deployed 2026-08-25 (§5). Closes one of three parts of
OPEN item #13 (§4.1 O-3).

### Per-room visibility emission (build plan S1–S5, M1) — S1 done; S2–S5 not started
[SRC] S1: *feat(voice): return the joined room in the provision success response*
(`WebRtcJanusService.cs:278`–`:283`), **committed, not deployed** (§5). S2 (region records
room), S3a (partitioner), S3b (sink partitions + bounded-parallel sends — first in-world-testable
step), S4 (`NotApplied` inner-reply reading), S5 (docs): not started. M1 (mixer version bump):
optional, not started. Decisions OQ1–OQ7 recorded in the brief [DOC, this tree
`per-room-visibility-emission-design-brief.md` §7].

### Connector layer — not started (design DRAFT)
[DOC] `connector-design-brief.md` `Status: DRAFT. Not frozen.`; Q1 (identity) resolved by
Amendment 1 2026-08-22 as "NPC-backed presence plus policy record"; Q2–Q6 open (§4.1 O-17).
[SRC] no connector code; the brief itself establishes the plain-RTP participant does not exist
in this mixer.

### Unscheduled — spec sections with no phase, no brief, no code
Trust domains and HG policy (§3.2, §10 item 1); wire epochs / fail-closed (§3.3); capture
consent (§3.5); `diag` member, `voice status` console, fail-loud signalling, session event log,
fleet observability (§4.1/4.3/4.4/4.5); degradation ladder (§5); HRTF/ITD/tiers/binning (§6 —
recorded as deferred by 3b, so "deferred" not merely unscheduled); performer mode (§7.2);
voice morphing / PSOLA (§7.4); estate-level mute/gain and podium (§7.6); §8.2/§8.3 scaling
machinery; FOA (§10 item 2); recording consent defaults (§10 item 3); session migration (§10
item 4). All [SRC] absent by grep of both source trees and both git logs.

---

## 4. Consolidated open items

### 4.1 One list, deduplicated

| ID | Item | Status | Recorded in |
|---|---|---|---|
| O-1 | Visibility feed addressed only to the estate room; per-parcel agents get no exclusions | filed; fix planned S1–S5, S1 done | `KnownDefects.md` (this tree); per-room brief; `mixer-feed-protocol.md` §3.4 correction; `parcel-voice-semantics.md` §P Part 2 |
| O-2 | Dense exclusion batch > 64 KB rejected whole, read as applied; sender marks synced | filed 2026-08-26; chunking deferred (OQ6); visibility via S4 | `KnownDefects.md`; per-room brief §3 |
| O-3 | #13 estate-channel ban — three parts: provisioning bypass **closed**; mixer-side closed for estate room only (= O-1); TaxFree void **open** | split | `parcel-voice-semantics.md` OPEN #13 + §E + §P |
| O-4 | TaxFree short-circuit voids parcel ban/restrict at provisioning on both channels; matrix overrides it — the two layers disagree under TaxFree | open, undecided | `parcel-voice-semantics.md` §E, §P Part 3 |
| O-5 | Parcel local IDs hashed as `float` (`Add(int)` → `Add(float)`) | filed; deliberately not fixed (grid-wide renumbering) | `KnownDefects.md`; per-room brief §2c |
| O-6 | OnListenerProvisioned queues a doomed re-send on failed provisions | not started | `KnownDefects.md` (cites stale lines `:408`–`:411`; now `:546`–`:553`) |
| O-7 | Region crossing leaves a live voice handle in the previous region's room | not started, observed 2026-08-24 | `KnownDefects.md` |
| O-8 | Pending-join confirmation gives up for every listener even when the batch lands | not started, noise | `KnownDefects.md` |
| O-9 | Feeder thread *death* undetected (blocked/wedged now caught) | partial | `KnownDefects.md` — status says "(uncommitted)"; **stale**, see §4.3-b |
| O-10 | Parcel ban does not eject an already-present avatar | not started (core) | `KnownDefects.md` |
| O-11 | No channel-change push on intra-region parcel crossing (no `ParcelVoiceInfoRequest` CAP); agent stays in old room until viewer re-provisions | open, **unfiled** — appears only in a commit message and the per-room brief §6 | commit *fix(voice): enforce parcel ban/restrict…*; per-room brief |
| O-12 | Neighbour-region voice rooms: child agents' room semantics | open | `parcel-voice-semantics.md` §G, §L "REMAINS OPEN" |
| O-13 | Duplicate-display residue: orphan capacity burn, leave-dot ghosting; eviction policy | partial | `parcel-voice-semantics.md` §M addendum; `KnownDefects.md` "Mixer applies…" (stale status, §4.3-a) |
| O-14 | Coarse-location (map dot) vs voice hiding policy divergence | open/undecided | `parcel-voice-semantics.md` OPEN #14; `mixer-feed-protocol.md` §1 |
| O-15 | Moderation state reported to no client (SL parity gap); viewer-side workaround only | open | `voice-moderation-design-brief.md` 2026-08-24 section |
| O-16 | Moderation OQ1 (`mute_all` scope vs SL) and OQ2 (exemption set) | unanswered | `voice-moderation-design-brief.md` |
| O-17 | Connector brief Q2 disclosure, Q3 authorisation, Q4 injection identity, Q5 hypergrid interaction, Q6 disclosure sufficiency | open; Q1 resolved | `connector-design-brief.md` |
| O-18 | 3b deferred DSP: HRTF+ITD, binning, far-tier, dirty-flag; sim position feed; estate-level leash; per-region spatial config | deferred | `phase3b-design-brief.md` + Amendments 3/8 |
| O-19 | Scaling: pass-2 parallelism (open for all-audible case); tick composition at N≈110 decomposed; "exactly one inbound track?" unverified inference | open | `scaling-assessment.md` §Open questions + Amendment 1 |
| O-20 | Two `VisibilityBatchSenderTests` fail (stall-log assertions count a log4net appender; the sender logs via ILogger since 2026-08-23) — pre-existing at `6586838e43`, proven 2026-08-26 | open, **unfiled** | this ledger; S1 report |
| O-21 | `os-webrtc-janus.ini.example` carries none of `VisibilityFeederEnabled` / `VisibilityEmitEnabled` / `VisibilityTickMs`; the live config uses all three | open, **unfiled** | this ledger [SRC: grep = 0] |
| O-22 | `mixer-feed-protocol.md` "room-level flag in v1.1" for voice-denied vs no-exclusions | idea, unscheduled | `mixer-feed-protocol.md` §3.2 |
| O-23 | `protocol-compat.md` constraint status "ACTIVE (Phase 0)" with expiry at flat-mix parity — parity reached in code (Phase 2); whether the audiobridge-superset constraint still binds is undecided | UNKNOWN | `protocol-compat.md` |
| O-24 | Spec §10 questions 1–4 (HG pool policy, FOA viewer decode, consent defaults, session migration) | untouched | `webrtc-voice-spec.md` |
| O-25 | Parcel access-list persistence non-atomic (delete-then-reinsert) | not started (core; adjacent) | `KnownDefects.md` |
| O-26 | Five estate toggles packed into region flags with no write path | not started (estate; adjacent) | `KnownDefects.md` |
| O-27 | Mixer version string not bumped by the `unknown_room` commit, so a deployed plugin cannot self-identify as carrying it (M1) | optional | per-room brief §8 |
| O-28 | `Docs/voice` cross-repo sync drift: `parcel-voice-semantics.md` differs by 56 lines (this tree's §P not in the mixer); this tree lacks the spec, 3b brief, connector brief, scaling assessment, current-architecture; the mixer lacks the moderation brief and the per-room brief | open | this ledger [SRC: diff 2026-08-26] |

Closed items, kept so nobody re-files them: OnRemovePresence teardown (implemented 2026-08-22);
estate CAP TaxFree flip on absent `override_public_access` (implemented 2026-08-23); parcel
access/ban list not persisted and UseBanList clobber (implemented, *fix(land): persist access/ban
list edits and preserve UseBanList on properties save*); ban-add silence (implemented,
instrumented); About Land access list rendering empty (resolved viewer-side); REGION_FLAGS_ALLOW_VOICE
bit-28 (not a defect); OPEN #12 estate-change event (exists — `OnEstateInfoChange`, subscribed at
`VoiceVisibilityService.cs:94`–`:96` [SRC]); scaling items 1 (non-deterministic truncation →
join-time cap) and 4 (lazy echo ring) [SRC]; connector Q1 [DOC]; per-room OQ1–OQ7 [DOC]; ALC
split-identity rule (documented, no fix owed).

### 4.2 KnownDefects in the mixer repo
**There is none** [SRC: `find` for `*knowndefect*` returns nothing]. Mixer-side defects are
filed in this tree's `Docs/KnownDefects.md` (O-2, O-5, O-13) and in `parcel-voice-semantics.md`
§M, which is synced to the mixer.

### 4.3 Where documents disagree with code or with each other

- **(a)** `KnownDefects.md` "Mixer applies peer_ctl_batch exclusions by display string… **Status:
  not started**" vs. mixer commits *fan visibility exclusions out to every session matching the
  listener display* and *join-time duplicate-display detection + deterministic dot-batch merge*
  [SRC], and vs. `parcel-voice-semantics.md` §M addendum which treats those as landed. The
  KnownDefects status is stale.
- **(b)** `KnownDefects.md` feeder-thread entry "registration implemented **(uncommitted)**" vs.
  commit *feat(voice): register visibility feeder tick thread with the Watchdog* (2026-08-17)
  [SRC]. Stale.
- **(c)** mixer `README.md` "**Status: Phase 1B**" vs. plugin description "Phase 3b" and the git
  log [SRC]. Stale by five phases.
- **(d)** `phase3a-feeder-acceptance.md` "the feeder is off by default — no consumer emits its
  output to Janus yet" vs. the sender (*peer_ctl_batch sender — wire the visibility matrix to the
  mixer*) and the live config `VisibilityEmitEnabled = true` [SRC]. Stale.
- **(e)** `protocol-compat.md` "Status: ACTIVE (Phase 0 / bring-up)… lifted at the flat-mix
  parity milestone" vs. Phase 2 landed at v0.6.0. Never updated; O-23.
- **(f)** `webrtc-voice-spec.md` §3.3 "version epochs … fails closed" vs. the wire and the
  mixer's keep-last-state behaviour [SRC]. Spec ahead of code, unacknowledged in the spec.
- **(g)** `webrtc-voice-spec.md` §7.1 "estate-configurable leash" vs. per-process jcfg [SRC];
  acknowledged in the 3b brief as a deviation, not in the spec.
- **(h)** `current-architecture.md` is a survey of this tree at `0bdeb0bf08` on branch
  `feature/membership-tiers` (2026-08-12), before 3a existed; its "no per-listener filtering"
  finding is now false [SRC]. Historical, not corrected.
- **(i)** `mixer-feed-protocol.md` §3.2 "per-parcel rooms use the `CalcRoomNumber` hash" reads
  as served, while §3.4's correction and O-1 establish per-parcel rooms receive nothing.
- **(j)** `KnownDefects.md` OnListenerProvisioned entry cites `WebRtcVoiceRegionModule.cs:408`–`:411`;
  the hook is at `:546`–`:553` [SRC]. Citation drift.
- **(k)** `mixer-feed-protocol.md` §3.3.1 was corrected 2026-08-25 in both repos; the mixer's
  `docs/voice/parcel-voice-semantics.md` was **not** re-synced after this tree's §P (O-28).
- **(l)** The 3b brief carries a 2026-08-25 header stating its body is pre-implementation; its
  body still says `SLV_MAX_MIX` = 64 and "no vector helpers" [SRC: 110; `vec3.h` etc. exist].
  Flagged by the header, not corrected in the body, by design.

### 4.4 Documents in scope and their freeze states (claims, not evidence)

| Document | Repo | Stated status | Note |
|---|---|---|---|
| `webrtc-voice-spec.md` | mixer only | Draft for review | never frozen, undated |
| `current-architecture.md` | mixer only | inventory at `0bdeb0bf08` | stale baseline (h) |
| `parcel-voice-semantics.md` | both | living, append-only addenda through §P | copies drifted (k) |
| `mixer-feed-protocol.md` | both | living; §3.3.1 version-scoped | in sync |
| `phase3a-feeder-acceptance.md` | both | acceptance notes | stale claim (d) |
| `phase3b-design-brief.md` | mixer only | FROZEN 2026-08-18 + Amendments 1–8 + staleness header | body deliberately unedited |
| `scaling-assessment.md` | mixer only | DRAFT + Amendments 1–2 | open items O-19 |
| `connector-design-brief.md` | mixer only | DRAFT, not frozen | Q1 resolved, Q2–6 open |
| `voice-moderation-design-brief.md` | this tree only | slice 1 verified; OQ1/2 open | parity gap section 2026-08-24 |
| `per-room-visibility-emission-design-brief.md` | this tree only | DECIDED 2026-08-26 + build plan | S1 done |
| `protocol-compat.md` | mixer only | ACTIVE (Phase 0) | expiry condition met, not updated (e) |
| `voice-mute-wiring.md`, `sldata-extensions.md`, `phase1-bringup.md`, `docker-notes.md` | mixer only | recon / runbook | no status headers |
| `Docs/audit/webrtc-upstream-audit.md` | this tree only | point-in-time 2026-08-23 vs upstream `cbdfba2811` | self-declares as as-of |
| `Docs/KnownDefects.md` | this tree only | living | statuses (a), (b), (j) stale |

---

## 5. Deployed versus committed, as of 2026-08-26 06:19 local

### Region side — `D:\legiongrid\regionserver`
- **Not running at reconciliation time.** No `OpenSim.Server.RegionServer.exe` process; nothing
  listening on 9000/9001/9002/8003; last line of today's log is `Hosting stopped` at 04:54:31
  [SRC: process/port query; log].
- **Last run:** started 2026-08-25 20:29:28, host build `OpenSim-NGC Tranquillity Release
  1.1.114-alpha+119fea881e` [SRC: log `[STARTUP]: Version`], ran until 04:54 today.
- **Voice binaries in the deploy root** [SRC: file timestamps and SHA-256 recorded at deploy]:
  `WebRtcVoiceRegionModule.dll` built 2026-08-25 20:11:58 from *fix(voice): enforce parcel
  ban/restrict on the estate voice channel* (`ec3ad9b2f2`), hash `F61BD8D13C90…`, copied 20:19,
  loaded at the 20:29 start; `WebRtcJanusService.dll`, `WebRtcVoice.dll`,
  `WebRtcVoiceServiceModule.dll`, `VoiceVisibility.dll` from a 2026-08-25 15:47 build. [INF] that
  15:47 build corresponds to `119fea881e` for these four assemblies, because no voice source
  outside the region module changed between *voice: convert the last four log4net call sites to
  ILogger* (2026-08-23) and `ec3ad9b2f2`.
- **Proof the visibility path ran on that build** [SRC: log 2026-08-25 20:29:33–35]:
  `[JANUS PEERCTL SINK]` constructed for Ebony (estate room 226001844), Transylvania
  (1578726032), Elm (1967062692); `[VOICE VISIBILITY] feeder started … @ 250ms (emit=True)` for
  each; zero `GIVING UP` / `ProtocolError` / latch / stuck-in-flight / derivation-error lines
  through shutdown. Elm's number matches the float-hash replication in `KnownDefects.md`.
- **Live config** [SRC: `config\OpenSim.ini`]: `[WebRtcVoice] Enabled = true`,
  `SpatialVoiceService` and `NonSpatialVoiceService` = `WebRtcJanusService.dll:WebRtcJanusService`
  (local-Janus topology, not the connector), `VisibilityFeederEnabled = true`,
  `VisibilityEmitEnabled = true`, `VisibilityTickMs = 250`; `[JanusWebRtcVoice]` gateway
  `192.168.1.225:24223/voice`, admin `:24225/voiceAdmin`, `PluginName = janus.plugin.slvoice`.
- **Committed but NOT deployed:** S1, *feat(voice): return the joined room in the provision
  success response* (`3c95ddea0e`). Today's 05:55 build hashes differ from every deploy-root
  voice DLL, all of which are dated 2026-08-25 [SRC]. Documentation commits deploy nothing.

### Mixer side — container `legion-voice-mixer-janus-1`
- **Running.** Image `ghcr.io/johnlegionh/legion-voice-mixer:latest`, id `sha256:86d6ec82…`,
  container started 2026-08-25 20:01:56 local, "Up 10 hours" at reconciliation [SRC: `docker ps`,
  `docker inspect`]. Plugin log: `Legion SLVoice mixer initialized! (API v106, 0.9.0)` [SRC:
  `docker logs`]. The region log's burst of Janus session errors at 20:01:38 is the mixer restart
  seen from the sim [SRC].
- **What it was built from** [SRC: `docker history`]: `COPY src` at 2026-08-25 19:49:41 local,
  then `make test && make && make install` at 19:49:43 — so the mixer unit tests **passed at
  image build** for that source. Commit `872f0d9` is timestamped 19:51:55 local — **two minutes
  after the image**. No image label carries a commit; the Dockerfile has no revision ARG.
- [INF] The image contains the `872f0d9` change set. Basis: the working tree at 19:49 carried
  the same 35-insertion/11-deletion diff that was committed unchanged at 19:51, and the tree was
  clean immediately after. This is inference, not proof; §6 says what would prove it.
- **Committed but not deployed (mixer):** nothing, if the inference holds. The optional M1
  version bump does not exist yet.

### On the statement "both halves are deployed as of today"
Both halves were deployed on **2026-08-25** — the region module fix at 20:19–20:29 and the
mixer image at 19:49–20:01 — and the region ran on them overnight. As of this reconciliation the
region is **stopped**, and the one code commit made today (S1) is **not** deployed. If "today"
meant the 08-25 evening deploy, the statement holds; if it meant the current HEAD, it does not.

---

## 6. Could not determine, and what would settle each

| # | Unknown | What settles it |
|---|---|---|
| U-1 | Whether the running mixer image contains `872f0d9` (inferred from timestamps only) | Any of: `strings janus_slvoice.so \| grep unknown_request` inside the container; an admin `message_plugin` with an unknown `request` (expect `{"slvoice":"error","reason":"unknown_request"}`); or M1's version bump on the next image |
| U-2 | Which commit the four 15:47 voice DLLs were built from | No build stamp in the deploy root; a `git describe` or `AssemblyInformationalVersion` stamped into addon DLLs would settle it going forward |
| U-3 | Whether Phase 2's two-party mix was ever formally accepted (CHECK 3) | No dated record exists; the 3a acceptance runs imply it; a one-line dated note in `phase1-bringup.md` would close it |
| U-4 | Whether Phase 3b spatial rendering has been verified by ear in-world | No record; the brief says numeric-only. A dated listening check (two avatars, azimuth left/right, distance fade) |
| U-5 | Whether a stock viewer re-provisions on intra-region parcel crossing (drives O-11's severity) | A crossing with `MessageDetails = true` and watching for a second `ProvisionVoiceAccountRequest` |
| U-6 | Whether the protocol-compat audiobridge-superset constraint is still meant to bind (O-23) | An owner decision recorded in `protocol-compat.md` |
| U-7 | Whether ILogger output reaches log4net in production (bears on O-20's scope: are stall logs visible live?) | Read the region's logging bootstrap; or force a stall and look for the line |
| U-8 | The mixer admin round-trip under load (the per-room brief's crossover uses a 2.5–3.3 ms loopback floor for a trivial request) | Timing a real `peer_ctl_batch` from the sink |
| U-9 | Exact membership of every parcel's `UseEstateVoiceChan` on this grid (four parcels sampled; Elm clear) | `SELECT RegionUUID, LocalLandID, Name, LandFlags & 0x40000000 FROM land` |
| U-10 | The OpenMetaverse `ParcelFlags` enum text (binary package only; values taken from the SL header, corroborated twice) | A reflection dump of `UtopiaSkye.OpenMetaverse` or its source tag |

---

## Maintenance

Amend this file on every voice commit that changes a status above, and on every deploy. Bump
**Last reconciled** and name the basis commits. When a [DOC] claim is later verified, upgrade it
to [SRC] with the citation; when a [SRC] citation drifts, fix the citation, not the claim. Keep
the mixer copy in sync per `Docs/voice/.gitattributes`.
