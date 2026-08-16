# Phase 3a — VoiceStateFeeder adapter & wiring: acceptance notes

Scope of this commit: the `FeederWorldFromScene` Scene adapter, the `VoiceVisibilityService`
(per-region owner + tick thread + event wiring), and the `WebRtcVoiceRegionModule` hooks. The pure
matrix engine (`Addons/os-webrtc-janus/Visibility`) landed earlier. No Janus emission yet.

## Config knobs (`[WebRtcVoice]`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | false | module master switch (pre-existing) |
| `VisibilityFeederEnabled` | **false** | start the per-listener visibility feeder per region |
| `VisibilityTickMs` | 250 | feeder tick cadence |

The feeder is **off by default** — no consumer emits its output to Janus yet, so production regions
should leave it off until the sender lands. Enable it for the smoke check below.

## Automated tests (all green)

- Engine — `Tests/WebRtcJanusService.Tests` (36): rules, matrix, delta, snapshot, fan-out, hardening,
  thread-capture. Includes the two-parcel symmetric-exclusion end-to-end via the `FakeWorld`.
- Adapter — `Tests/WebRtcVoiceRegionModule.Tests` (12):
  - `LandBanTests` (8): the TaxFree-bypass ban scan against real `LandData`/`LandAccessEntry`
    (permanent/live/expired bans, `UseBanList` gate, admin / EM-or-owner / parcel-owner exemptions).
  - `FeederWorldFromSceneTests` (4): real-`Scene` ban delegate reads live `LandObject` state,
    `SeeAVs`/`AllowVoiceChat` flag mapping, benign unknown-parcel, and event→dirty wiring.

## Tick-thread single-thread invariant — validate in a DEBUG session

`VoiceStateFeeder` asserts (via `Debug.Assert` in `RecordAndCheckTickThread`) that the matrix is
mutated only on one tick thread. **`Debug.Assert` is compiled out in Release**, so this invariant is
*not* checked in a Release build. It must be validated **in a DEBUG build session**:

1. Build the addon **Debug**.
2. Set `[WebRtcVoice] Enabled = true`, `VisibilityFeederEnabled = true` (optionally lower
   `VisibilityTickMs`).
3. Run a region; connect an avatar (or two) and move across parcel boundaries; edit parcel
   flags / bans; change estate voice settings.
4. **Pass:** no assertion failure fires, the dedicated thread `VoiceVisibilityFeeder:<region>` runs,
   and the log shows `[VOICE VISIBILITY] <region>: +N listeners / -M listeners` as avatars move.
   On region close the thread stops and joins within the 2s timeout.

The dedicated named background thread (never a `ThreadPool` timer) is what keeps the guard quiet;
event handlers only flip the dirty flag on sim threads and never touch the matrix.

## Known test-harness limitation

The real-`Scene` tests deliberately create **no `ScenePresence`s**: this tree's
`ScenePresence.Finalize()` throws an NRE during GC and crashes the test host (a pre-existing harness
fragility, not adapter code). The full presence→matrix path (symmetric exclusion across avatars) is
therefore covered by the deterministic engine `BanScenario` test and by the in-world DEBUG smoke
check above, rather than an automated real-`Scene` presence test.

## Decisions in force (see `parcel-voice-semantics.md` §E/§F)

- SeeAVs hiding is **symmetric, pending SL verification**.
- The parcel-ban TaxFree void is **fixed, ban-only**; access-**restriction** keeps the sim's TaxFree
  self-nullify (deliberate divergence, commented in `FeederWorldFromScene`).
