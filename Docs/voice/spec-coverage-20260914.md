# Spec coverage, pass 0 — claim inventory

**Source:** `Docs/voice/webrtc-voice-spec.md`, "Spatial WebRTC Voice Service — Feature Specification",
written 2026-08-15. This pass only enumerates the spec's normative claims. It records no evidence and
no assessment; the next pass does that.

## Header

- **Length:** `wc -l Docs/voice/webrtc-voice-spec.md` → `198`.
- **Tracking:** tracked in git. `git log --follow` shows one commit, `2a592f86e3` (2026-08-31 06:03 -0500,
  `docs(voice): O-28 — resync Docs/voice with the mixer (sim side)`). `git blame` attributes all 198
  lines to `2a592f86e3`. The 2026-08-15 authoring date therefore does not appear in this repository's
  history, which starts at the O-28 mirror resync. The mixer copy
  (`legion-voice-mixer/docs/voice/webrtc-voice-spec.md`) is byte-identical (SHA-256 `4F1F9904…4433A`).
- **Line numbers:** taken from the file at tranq-ais `feature/ais-v3` `a8b85fbcdd`.

### Table of contents of the spec

| Line | Heading |
|---|---|
| 1 | # Spatial WebRTC Voice Service — Feature Specification |
| 8 | ## 1. Goals and non-goals |
| 27 | ## 2. Requirements provenance |
| 37 | ## 3. Tier 0 — Trust and privacy invariants |
| 41 | ### 3.1 All media is server-relayed, always **[OPS]** |
| 45 | ### 3.2 Voice-server trust domains **[OPS]** |
| 53 | ### 3.3 Sim-authoritative permission enforcement **[ENG]** |
| 57 | ### 3.4 Mixer output is the permission boundary **[ENG]** |
| 61 | ### 3.5 Capture requires consent **[OPS]** |
| 67 | ## 4. Tier 1 — Reliability and diagnosability |
| 71 | ### 4.1 Per-connection state vector |
| 79 | ### 4.2 Built-in echo test |
| 83 | ### 4.3 Fail loud |
| 87 | ### 4.4 Session event log |
| 91 | ### 4.5 Fleet observability (activates at scale) |
| 97 | ## 5. Tier 2 — Media plane quality |
| 110 | ## 6. Tier 3 — Spatial engine |
| 123 | ## 7. Tier 4 — Features |
| 125 | ### 7.1 Camera-position listening **[REC]** |
| 132 | ### 7.2 Performer mode **[REC]** |
| 136 | ### 7.3 Parcel and estate voice zones **[OPS]** |
| 140 | ### 7.4 Voice morphing |
| 150 | ### 7.5 Connector layer **[OPS]** |
| 158 | ### 7.6 Moderation surface **[OPS]** |
| 164 | ## 8. Deployment and scaling model |
| 168 | ### 8.1 Small (1 region – ~20 regions) |
| 172 | ### 8.2 Medium (grid, tens–hundreds of regions) |
| 176 | ### 8.3 Large (InWorldz/SL scale) |
| 186 | ## 9. Protocol compatibility summary |
| 193 | ## 10. Open questions |

### Rules applied

- **One row per claim:** every "will / must / provides / supports" statement, named feature, or committed
  behaviour gets its own row. The header scope line (line 4) and §1 goals and non-goals count.
- **Not inventoried:**
  - §2 (provenance tags, lines 27–33) commits to nothing.
  - §10 lists open questions (lines 195–198), not claims.
  - Prose asides that justify a claim without adding one: the §4 and §5 introductions, the CPU and quality
    estimates in §7.4, and the cost remarks in §8.2–§8.3.
- **Scope:** MIXER (the C plugin), SIM (the .cs addon), BOTH, OPS (deployment/config/docs), VIEWER (would
  need viewer changes).
- **Flag:** `SUPERSEDED?` means a later design decision may have overruled the claim. `AMBIGUOUS?` means it
  is unclear what would satisfy it. Both are questions for John; see the flag notes below the inventory.
- **Wording:** claims follow the spec's own words. Three claims (SC-19, SC-92, SC-115) are lightly
  reworded only to keep this audit's reserved status vocabulary out of the document; the flag notes list
  them.

## Inventory

| ID | Spec § | Line | Claim | Scope | Flag |
|---|---|---|---|---|---|
| SC-1 | Header | 4 | A server-side WebRTC voice service (mixer plugin + region/grid integration) for the published Second Life WebRTC voice protocol, for OpenSimulator-derived grids | BOTH | |
| SC-2 | Header | 4 | Designed to run unchanged from a single-region standalone to a large multi-mixer grid | OPS | |
| SC-3 | 1 | 12 | Full compatibility with the SL WebRTC viewer protocol (ProvisionVoiceAccountRequest / VoiceSignalingRequest caps, SLData data channel) | BOTH | |
| SC-4 | 1 | 12 | Stock WebRTC-capable viewers work without modification | BOTH | |
| SC-5 | 1 | 13 | True per-listener spatial audio (distance, azimuth, listener orientation) | MIXER | |
| SC-6 | 1 | 14 | Privacy and permission enforcement performed in the mix, on the server, not delegated to clients | BOTH | |
| SC-7 | 1 | 15 | First-class diagnosability: voice failures must be triageable by the user or operator in minutes, without log-emailing rituals | BOTH | AMBIGUOUS? |
| SC-8 | 1 | 16 | Restoration of camera-position listening, lost in the Vivox→WebRTC transition | BOTH | |
| SC-9 | 1 | 16 | Restoration of voice morphing, lost in the Vivox→WebRTC transition | MIXER | |
| SC-10 | 1 | 16 | A capability Vivox never had: mixer-enforced moderation | BOTH | |
| SC-11 | 1 | 16 | A capability Vivox never had: effects/connector hooks | MIXER | |
| SC-12 | 1 | 17 | Scale-invariant deployment: identical code path and configuration model from a one-region hobby grid to a large commercial grid | OPS | |
| SC-13 | 1 | 17 | Small deployments must not pay complexity for scale they don't need | OPS | |
| SC-14 | 1 | 21 | Non-goal: client-side spatialization via selective forwarding (SFU); ruled out by the privacy model (§3) | MIXER | |
| SC-15 | 1 | 22 | Non-goal: true peer-to-peer media between clients; ruled out permanently (§3.1) | BOTH | |
| SC-16 | 1 | 23 | Non-goal: video | MIXER | |
| SC-17 | 3.1 | 43 | No client ever receives another client's ICE candidates | BOTH | |
| SC-18 | 3.1 | 43 | Every media connection is client ↔ voice server | BOTH | |
| SC-19 | 3.1 | 43 | "Peer-to-peer" calls are two-party sessions on the non-spatial pool, exactly as Second Life does | BOTH | |
| SC-20 | 3.1 | 43 | Client IP addresses are never exposed to other users | BOTH | |
| SC-21 | 3.1 | 43 | Structurally impossible to misconfigure: there is no configuration option that enables direct client-to-client ICE | BOTH | |
| SC-22 | 3.2 | 47 | Every voice server instance is classified as grid-operated or region-operated | OPS | |
| SC-23 | 3.2 | 47 | The trust-domain classification is communicated to the client at provisioning time | SIM | AMBIGUOUS? |
| SC-24 | 3.2 | 49 | Hypergrid visitors are provisioned only onto grid-operated servers | SIM | |
| SC-25 | 3.2 | 49 | Region-local voice is not offered to hypergrid visitors | SIM | |
| SC-26 | 3.2 | 50 | Local users connecting to region-operated servers receive a disclosure | BOTH | AMBIGUOUS? |
| SC-27 | 3.2 | 51 | Default topology is grid-operated | OPS | |
| SC-28 | 3.2 | 51 | Region-local is an opt-in for closed estates that want the latency benefit | OPS | |
| SC-29 | 3.3 | 55 | Parcel and estate audibility is always computed from the avatar's authoritative sim-side position and the sim's access lists | SIM | |
| SC-30 | 3.3 | 55 | Access/visibility state is pushed from the simulator to the mixer with version epochs | BOTH | AMBIGUOUS? |
| SC-31 | 3.3 | 55 | Access/visibility state fails closed on staleness | BOTH | AMBIGUOUS? |
| SC-32 | 3.3 | 55 | Client-reported listener position is accepted only as a rendering hint (§6.1), never as a permission input | BOTH | AMBIGUOUS? |
| SC-33 | 3.4 | 59 | A listener's downstream mix contains only audio that listener is entitled to hear | MIXER | |
| SC-34 | 3.4 | 59 | No "muted client-side" audio in flight; a modified viewer cannot un-hide a hidden avatar or un-mute a moderator mute | MIXER | |
| SC-35 | 3.4 | 59 | The service is an MCU (server mixes) rather than an SFU (server forwards) | MIXER | |
| SC-36 | 3.5 | 63 | Any tap on a spatial or group mix requires an in-channel disclosure to affected participants | BOTH | |
| SC-37 | 3.5 | 63 | No plaintext media at rest without explicit configuration and disclosure | OPS | |
| SC-38 | 3.5 | 63 | The moderation and legal posture ships with the hook, not after the first incident | OPS | AMBIGUOUS? |
| SC-39 | 4.1 | 73 | The mixer maintains, per connection: ICE state, DTLS state, inbound RTP rate, decode success, active mix memberships, outbound RTP rate, and last client acknowledgment | MIXER | |
| SC-40 | 4.1 | 75 | Per-connection state exposed on the console: `voice status <avatar>` on the region/grid console | BOTH | |
| SC-41 | 4.1 | 76 | Per-connection state exposed on an admin surface: per-region and per-mixer web view | OPS | AMBIGUOUS? |
| SC-42 | 4.1 | 77 | Per-connection state exposed to the client as a `diag` member pushed on the SLData channel | MIXER | |
| SC-43 | 4.1 | 77 | A viewer can render "connected; receiving 0 pkt/s from server" vs "receiving 340 pkt/s; output device delivered 0 frames", splitting server-fault from device-fault | VIEWER | |
| SC-44 | 4.2 | 81 | Every region offers a self-serve echo test | MIXER | |
| SC-45 | 4.2 | 81 | The echo test runs on request by SLData message | MIXER | |
| SC-46 | 4.2 | 81 | The echo test runs on request from a viewer menu | VIEWER | |
| SC-47 | 4.2 | 81 | The mixer loops the user's own audio back with ~500 ms delay | MIXER | |
| SC-48 | 4.2 | 81 | The echo test replaces the single grid-wide test region model with a capability available everywhere at negligible cost | OPS | |
| SC-49 | 4.3 | 85 | If the mixer cannot deliver audio a listener should be receiving, it says so on the data channel | MIXER | AMBIGUOUS? |
| SC-50 | 4.3 | 85 | The viewer can surface the mixer's report that audio cannot be delivered | VIEWER | |
| SC-51 | 4.3 | 85 | The system must never render the appearance of working voice over a dead media path | BOTH | |
| SC-52 | 4.4 | 89 | Structured per-session event log (join, ICE transitions, first media, stalls, teardown cause) | BOTH | |
| SC-53 | 4.4 | 89 | The session event log is retained short-term for postmortems | OPS | AMBIGUOUS? |
| SC-54 | 4.5 | 93 | Fleet observability (activates at scale): per-mixer health, session counts, mix-deadline overrun rates, relay-vs-direct path ratios | BOTH | AMBIGUOUS? |
| SC-55 | 4.5 | 93 | On a single-mixer grid fleet observability is one status page; nothing else changes | OPS | |
| SC-56 | 5 | 101 | Internal pipeline at 48 kHz float end-to-end; no resampling after ingest | MIXER | |
| SC-57 | 5 | 102 | No server-side noise suppression or AGC, ever | MIXER | |
| SC-58 | 5 | 103 | Talker ingest ≥ 32 kbps Opus | MIXER | AMBIGUOUS? |
| SC-59 | 5 | 103 | Listener mix 64–96 kbps stereo Opus | MIXER | |
| SC-60 | 5 | 103 | 20 ms frames | MIXER | |
| SC-61 | 5 | 104 | DTX/VAD-gated decode: silent talkers cost nothing before the RTP layer | MIXER | |
| SC-62 | 5 | 104 | A 100–200 ms release hold prevents word-onset chopping from DTX hangover | MIXER | |
| SC-63 | 5 | 105 | Encode-skip: listeners whose entire audible set is silent receive DTX and cost near-zero encode | MIXER | |
| SC-64 | 5 | 105 | Encode cost scales with audible listeners, not connected listeners | MIXER | |
| SC-65 | 5 | 106 | Degradation ladder under CPU pressure, in order: frame size up (20→30 ms) → distance tiers widened → far-field talkers shed from mixes → admission refusal (last resort) | MIXER | SUPERSEDED? |
| SC-66 | 5 | 106 | Per-region tick isolation so one saturated region cannot starve its neighbors | MIXER | |
| SC-67 | 6 | 112 | No global spatial index | MIXER | |
| SC-68 | 6 | 112 | Culling and per-listener rendering with load-adaptive aggregation | MIXER | AMBIGUOUS? |
| SC-69 | 6 | 114 | Cull first: distance, VAD, inbound level | MIXER | |
| SC-70 | 6 | 115 | Direct per-source HRTF below a talker-count threshold | MIXER | |
| SC-71 | 6 | 115 | Azimuth binning engages only above the talker-count threshold | MIXER | |
| SC-72 | 6 | 115 | Crossfades over several 10 ms frames at bin crossings, pre-staged from position derivatives | MIXER | AMBIGUOUS? |
| SC-73 | 6 | 115 | At low occupancy the binning path and its artifacts don't run | MIXER | |
| SC-74 | 6 | 116 | Distance tiers: full HRTF with ITD near; amplitude panning + lowpass mid; single mono ambience sum far | MIXER | |
| SC-75 | 6 | 117 | Dirty-flagged coefficient recompute: stationary listener + stable talker set skips HRIR selection/interpolation setup per frame | MIXER | |
| SC-76 | 6 | 118 | Listener orientation honored from the SLData `lh` quaternion | MIXER | |
| SC-77 | 6 | 119 | Per-(listener, source) crossfade state kept in flat preallocated arrays sized at session setup; no per-transition allocation | MIXER | |
| SC-78 | 7.1 | 127 | Restores the Vivox-era "hear from camera" behavior without reopening the eavesdropping hole | BOTH | |
| SC-79 | 7.1 | 129 | Attenuation and HRTF may follow client-reported listener position within an estate-configurable leash radius of the avatar | BOTH | SUPERSEDED? |
| SC-80 | 7.1 | 130 | Parcel/estate audibility is always computed from the avatar's authoritative position; camming never grants audio access the avatar's position wouldn't | SIM | |
| SC-81 | 7.2 | 134 | Performer mode: a per-avatar, estate-grantable flag | SIM | |
| SC-82 | 7.2 | 134 | Performer mode: high-bitrate stereo ingest | BOTH | |
| SC-83 | 7.2 | 134 | Performer mode: client signaled to disable NS/AGC for that stream | VIEWER | AMBIGUOUS? |
| SC-84 | 7.2 | 134 | Performer mode: exempt from far-tier degradation | MIXER | |
| SC-85 | 7.3 | 138 | Per-listener visibility matrix over a shared estate/region mix: avatars can be hidden from each other based on parcel access, enforced in the mix (§3.4) | BOTH | SUPERSEDED? |
| SC-86 | 7.3 | 138 | Participant lists, voice dots, and power levels are driven by the same SLData visibility set, so a hidden avatar is hidden in both audio and UI consistently, from one source of truth | MIXER | |
| SC-87 | 7.3 | 138 | A dot never lights with no audio behind it | MIXER | |
| SC-88 | 7.3 | 138 | Parcel privacy is enforced in the mix rather than as connection topology, removing SL's parcel-boundary reconnection stutter | BOTH | SUPERSEDED? |
| SC-89 | 7.4 | 142 | Voice morphing: per-talker pitch/formant shifting applied at decode, before spatial encode | MIXER | |
| SC-90 | 7.4 | 142 | Morphing cost scales with morphed active talkers (typically 0–3), not listeners | MIXER | |
| SC-91 | 7.4 | 145 | Morphing's analysis-window latency (20–60 ms) is added to morphed talkers only | MIXER | |
| SC-92 | 7.4 | 147 | Recommendation: write PSOLA in-house (~few hundred lines) to keep the distribution unencumbered (SoundTouch is LGPL, Rubber Band is GPL/commercial) | MIXER | |
| SC-93 | 7.4 | 148 | Estates get a `no_morphing` flag | SIM | |
| SC-94 | 7.4 | 148 | Morph state is visible to moderators | BOTH | |
| SC-95 | 7.5 | 152 | A tap-and-inject API on the mixer (plain RTP or WebSocket), generalizing the requested post-processing hooks | MIXER | SUPERSEDED? |
| SC-96 | 7.5 | 154 | Taps: recording (consent-gated per §3.5) | BOTH | |
| SC-97 | 7.5 | 154 | Taps: transcription | MIXER | |
| SC-98 | 7.5 | 154 | Taps: future translation | MIXER | AMBIGUOUS? |
| SC-99 | 7.5 | 155 | Injectors: NPC/bot TTS voices as first-class positioned sources | BOTH | |
| SC-100 | 7.5 | 155 | Injectors: external DJ/stream sources entering spatial voice as positioned audio rather than parcel media URLs | BOTH | |
| SC-101 | 7.5 | 156 | Effects sends: parcel-property environmental processing (reverb, echo zones), configurable by land settings | BOTH | |
| SC-102 | 7.6 | 160 | Estate-level mute/gain enforced in the mix | BOTH | |
| SC-103 | 7.6 | 160 | "Podium" mode granting designated speakers gain priority | BOTH | |
| SC-104 | 7.6 | 160 | Per-parcel voice zones (§7.3) as part of the moderation surface | BOTH | |
| SC-105 | 7.6 | 160 | All moderation is mixer-enforced and therefore not bypassable by modified viewers | MIXER | |
| SC-106 | 8 | 166 | Most deployments will be small; the small case is the default case | OPS | |
| SC-107 | 8 | 166 | A single-region standalone or small grid runs one mixer process alongside the simulator or grid services | OPS | |
| SC-108 | 8 | 166 | The small deployment is configured by one INI section | OPS | SUPERSEDED? |
| SC-109 | 8 | 166 | The small deployment has no allocator, no fleet, and no external dependencies beyond the mixer itself | OPS | |
| SC-110 | 8 | 166 | Everything in §§3–7 works identically in the small mode | BOTH | |
| SC-111 | 8 | 166 | Scale features activate by configuration, not by code path | BOTH | |
| SC-112 | 8.1 | 170 | One mixer process serves spatial + group + P2P | MIXER | |
| SC-113 | 8.1 | 170 | Small-deployment diagnostics are the console command and one status page | OPS | |
| SC-114 | 8.1 | 170 | STUN only; TURN optional | OPS | |
| SC-115 | 8.1 | 170 | This must install with: build, one INI section, and nothing further | OPS | SUPERSEDED? |
| SC-116 | 8.1 | 170 | "Voice works out of the box and tells you why when it doesn't" | BOTH | AMBIGUOUS? |
| SC-117 | 8.2 | 174 | Spatial mixers scale horizontally by region | OPS | |
| SC-118 | 8.2 | 174 | The group/adhoc pool scales independently | OPS | |
| SC-119 | 8.2 | 174 | A simple placement map (region → mixer) replaces the single process | BOTH | |
| SC-120 | 8.2 | 174 | Adjacent regions preferentially co-located on one mixer | OPS | |
| SC-121 | 8.2 | 174 | Cross-region listening via multiple neighbor sessions summed client-side per the protocol | BOTH | |
| SC-122 | 8.3 | 178 | Placement service: bin-packing regions onto mixers by predicted load | OPS | |
| SC-123 | 8.3 | 178 | Sessions carry enough state to migrate | BOTH | |
| SC-124 | 8.3 | 179 | Admission control with backpressure: saturated mixers shed new sessions to other capacity; established sessions are protected | BOTH | |
| SC-125 | 8.3 | 180 | TURN fleet as its own capacity plan | OPS | |
| SC-126 | 8.3 | 180 | The diag vector (§4.1) distinguishes relay-path from direct-path sessions | MIXER | |
| SC-127 | 8.3 | 181 | Optional first-order ambisonic delivery: orientation-independent B-format mixes, rotated and binauralized viewer-side | VIEWER | |
| SC-128 | 8.3 | 181 | First-order ambisonic delivery is SDP-negotiated with fallback to server-side binaural stereo for stock viewers | MIXER | |
| SC-129 | 8.3 | 181 | B-format mixes dedupe across co-located listeners | MIXER | |
| SC-130 | 8.3 | 181 | The ambisonic delivery mitigation is wired in early (brutal to retrofit) | MIXER | AMBIGUOUS? |
| SC-131 | 8.3 | 182 | Abuse controls: rate-limited provisioning | SIM | |
| SC-132 | 8.3 | 182 | Abuse controls: per-account session caps | SIM | |
| SC-133 | 8.3 | 182 | Abuse controls: RTP source validation | MIXER | |
| SC-134 | 9 | 188 | Caps: `ProvisionVoiceAccountRequest` (JSEP offer/answer, `channel_type` local/multiagent, `parcel_local_id`, logout) | SIM | |
| SC-135 | 9 | 188 | Caps: `VoiceSignalingRequest` (trickled ICE, completion marker) | SIM | |
| SC-136 | 9 | 189 | SDP: fmtp mangle honored (`minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000`) | MIXER | |
| SC-137 | 9 | 190 | SLData channel client→mixer `j/l/sp/sh/lp/lh/m/ug` per the published format | MIXER | |
| SC-138 | 9 | 190 | SLData channel mixer→client per-peer `p/V/j/l` batched ~100 ms | MIXER | SUPERSEDED? |
| SC-139 | 9 | 190 | SLData extensions, all optional and ignored by stock viewers: `diag` (§4.1), echo-test control (§4.2), morph state (§7.4), trust-domain disclosure (§3.2) | MIXER | |
| SC-140 | 9 | 191 | Cross-region: neighbor connections with primary flag, client-side summing, per the published model | BOTH | |
| SC-141 | 9 | 191 | Parcel changes within a region do not trigger connection changes (§7.3) | BOTH | SUPERSEDED? |

## Flag notes (questions for John, not verdicts)

| ID | Flag | Question |
|---|---|---|
| SC-7 | AMBIGUOUS? | What measure counts as "triageable in minutes"? A procedure, a tool, or a time target? |
| SC-23 | AMBIGUOUS? | Is "communicated at provisioning time" the provisioning cap's reply (sim), or the SLData trust-domain extension that §9 (line 190) lists as optional? |
| SC-26 | AMBIGUOUS? | What form does the disclosure take (chat notice, viewer UI, SLData extension), and who issues it? |
| SC-30 | AMBIGUOUS? | What is a "version epoch" on the sim→mixer feed? Per room, per listener, or per batch? |
| SC-31 | AMBIGUOUS? | What does "fails closed" mean for audibility: does a stale listener hear nobody, keep the last state, or something else? And what counts as stale? |
| SC-32 | AMBIGUOUS? | The claim cites §6.1, but the spec has no §6.1 (§6 at line 110 has no subsections). Which rule does "rendering hint" refer to? |
| SC-38 | AMBIGUOUS? | Which artefacts make up the "moderation and legal posture" that must accompany the hook? |
| SC-41 | AMBIGUOUS? | Does a JSON admin API count as the "per-region and per-mixer web view", or does it have to be a rendered page? |
| SC-49 | AMBIGUOUS? | §4.3 and §9 define no message shape or trigger for "says so on the data channel". What would satisfy it? |
| SC-53 | AMBIGUOUS? | How long is "short-term", and where is the log kept (sim, mixer, or both)? |
| SC-54 | AMBIGUOUS? | When does fleet observability "activate at scale"? And does "relay-vs-direct" mean TURN-relayed vs direct client↔server paths, given §3.1 relays all media through the server? |
| SC-58 | AMBIGUOUS? | The talker's encoder bitrate is chosen by the viewer. Is this a mixer obligation (e.g. signalled in the SDP answer), a viewer requirement, or an ops expectation? |
| SC-65 | SUPERSEDED? | Ledger O-69 (2026-09-12) pins packetisation to 20 ms in the SDP answer (`a=ptime:20`, `a=maxptime:20`). Does the first ladder step (20→30 ms) still stand? |
| SC-68 | AMBIGUOUS? | What is "load-adaptive aggregation", as distinct from the azimuth binning of SC-71? |
| SC-72 | AMBIGUOUS? | §5 fixes 20 ms frames (SC-60), but this crossfade is "over several 10 ms frames". Which frame length is meant? |
| SC-79 | SUPERSEDED? | The plugin's spatial tuning (phase 3b brief, Amendment 8; jcfg `spatial_leash_distance_m`) is a process-wide mixer setting. Does "estate-configurable" still stand? |
| SC-83 | AMBIGUOUS? | By what signal is the client told to disable NS/AGC? The spec names no SDP attribute or SLData member. |
| SC-85 | SUPERSEDED? | Later design gives each parcel its own mixer room (ledger O-1 and O-48: the room number is hashed from the parcel). Does "a shared estate/region mix" still describe the model? |
| SC-88 | SUPERSEDED? | Same question as SC-85: with per-parcel rooms, is parcel privacy still enforced "in the mix rather than as connection topology"? |
| SC-95 | SUPERSEDED? | The connector build plan (S-CON-4 recorder, S-CON-6 injector) has connectors join a room as WebRTC peers. Does the plain RTP / WebSocket tap-and-inject API still stand? |
| SC-98 | AMBIGUOUS? | "Future translation": is this a commitment for this service, or a note about later work? |
| SC-108 | SUPERSEDED? | `legion-voice-mixer/docs/docker-notes.md` has operators pull a prebuilt image and configure the mixer through `.env` (`JS_*` variables), alongside the sim's ini sections. Does "one INI section" still stand? |
| SC-115 | SUPERSEDED? | Same source as SC-108: the mixer README's install path is a prebuilt image plus `.env`, "without cloning, submodules, or build tools". Does "build, one INI section" still stand? (Claim text ends before the spec's final word, which is in this audit's reserved vocabulary.) |
| SC-116 | AMBIGUOUS? | Which failures must "tell you why", and through which channel (console, log, viewer)? |
| SC-130 | AMBIGUOUS? | Is "wired in early" a scheduling commitment (and relative to what milestone), or advice? |
| SC-138 | SUPERSEDED? | The mixer's SLData notes (`legion-voice-mixer/docs/sldata-extensions.md`, "Mixer→client format corrections") record the viewer reading the VAD key as lowercase `v`. Does `p/V/j/l` with uppercase `V` still stand? |
| SC-141 | SUPERSEDED? | Same question as SC-85: with per-parcel rooms, does a parcel change within a region now move the viewer to a different room? |

Wording adjustments (no change of meaning intended):
- **SC-19:** the spec's verb phrase before "as two-party sessions" is omitted.
- **SC-92:** the spec's verb before "PSOLA in-house" is rendered "write".
- **SC-115:** the spec's final word after "one INI section" is rendered "and nothing further".

## Counts

### Per section

| Spec § | Claims | IDs |
|---|---|---|
| Header (line 4) | 2 | SC-1 – SC-2 |
| 1 Goals and non-goals | 14 | SC-3 – SC-16 |
| 2 Requirements provenance | 0 | — |
| 3.1 All media is server-relayed | 5 | SC-17 – SC-21 |
| 3.2 Voice-server trust domains | 7 | SC-22 – SC-28 |
| 3.3 Sim-authoritative permission enforcement | 4 | SC-29 – SC-32 |
| 3.4 Mixer output is the permission boundary | 3 | SC-33 – SC-35 |
| 3.5 Capture requires consent | 3 | SC-36 – SC-38 |
| 4.1 Per-connection state vector | 5 | SC-39 – SC-43 |
| 4.2 Built-in echo test | 5 | SC-44 – SC-48 |
| 4.3 Fail loud | 3 | SC-49 – SC-51 |
| 4.4 Session event log | 2 | SC-52 – SC-53 |
| 4.5 Fleet observability | 2 | SC-54 – SC-55 |
| 5 Media plane quality | 11 | SC-56 – SC-66 |
| 6 Spatial engine | 11 | SC-67 – SC-77 |
| 7.1 Camera-position listening | 3 | SC-78 – SC-80 |
| 7.2 Performer mode | 4 | SC-81 – SC-84 |
| 7.3 Parcel and estate voice zones | 4 | SC-85 – SC-88 |
| 7.4 Voice morphing | 6 | SC-89 – SC-94 |
| 7.5 Connector layer | 7 | SC-95 – SC-101 |
| 7.6 Moderation surface | 4 | SC-102 – SC-105 |
| 8 Deployment and scaling model | 6 | SC-106 – SC-111 |
| 8.1 Small | 5 | SC-112 – SC-116 |
| 8.2 Medium | 5 | SC-117 – SC-121 |
| 8.3 Large | 12 | SC-122 – SC-133 |
| 9 Protocol compatibility summary | 8 | SC-134 – SC-141 |
| 10 Open questions | 0 | — |
| **Total** | **141** | SC-1 – SC-141 |

### Per Scope

| Scope | Claims |
|---|---|
| MIXER | 57 |
| SIM | 11 |
| BOTH | 44 |
| OPS | 24 |
| VIEWER | 5 |
| **Total** | **141** |

### Flags

| Flag | Rows |
|---|---|
| SUPERSEDED? | 9 (SC-65, SC-79, SC-85, SC-88, SC-95, SC-108, SC-115, SC-138, SC-141) |
| AMBIGUOUS? | 18 (SC-7, SC-23, SC-26, SC-30, SC-31, SC-32, SC-38, SC-41, SC-49, SC-53, SC-54, SC-58, SC-68, SC-72, SC-83, SC-98, SC-116, SC-130) |
| blank | 114 |
