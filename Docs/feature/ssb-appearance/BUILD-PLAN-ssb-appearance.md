# Build Plan — Server-Side Baking (L-2)

**Status:** DRAFT, gated on Ledger D-1/D-3 + ADR-003. **Date:** 2026-09-03
**Estimating convention:** wall-clock Claude Code minutes per session, anchored to measured web-viewer sessions (S4 21 min, S10 20 min, S6 28 min, S11 39 min, S9 55 min). A session past ~2× its estimate is stuck: stop it, report, re-scope. One feature per session.
**Branch:** `feature/ssb-appearance` off `develop` HEAD (`cb141dd61d` + maptile fix). Commit locally at each session's DoD; push is John's call.
**Repos:** T = `/d/tranquillity-develop` (worktree for this branch — see §0), W = `D:\web-viewer`.

## 0. Setup (John, ~3 min, no CC)

```bash
cd /d/tranquillity-develop
git fetch --all
git worktree add /d/tranq-ssb -b feature/ssb-appearance develop
cd /d/tranq-ssb
mkdir -p Docs/feature/ssb-appearance
cp /d/_TO_REVIEW/ssb-appearance/*.md Docs/feature/ssb-appearance/
# then copy the five docs from this delivery into the same folder
git add Docs/feature/ssb-appearance
git commit -m "docs(ssb): recon + addendum, design brief, ADR set, build plan, ledger"
```

**Needs your attention:** the `cp` from `_TO_REVIEW` assumes the CC recon's filenames are `.md` at the top level of that folder — check `ls /d/_TO_REVIEW/ssb-appearance/` first.

## 1. Session table

| S | Repo | Feature | DoD (harness green, not "looks right") | Est. min | Verify loop (yours) |
|---|---|---|---|---|---|
| S0a | T | **Verification grep pass + library project skeleton.** Resolve every `[UNVERIFIED]` in the RECON addendum §2/§6 with file:line at HEAD; create `Source/OpenSimNGC.Appearance.Baking` (net10.0, SkiaSharp, tree's J2K encoder, embedded `avatar_lad.xml` per ADR-007) with an empty public API + test project. | Report table with file:line for all 6 items; solution builds; `dotnet test` runs 0 tests green. | 25 | none |
| S0b | W→T | **Extract compositor.** Move `gateway/src/Gateway/Baking/` into the library; port its existing unit tests; golden-fixture harness: given a wearables+params fixture and Firestorm's bake assets for Truly's stock-Library outfit, pixel-diff per channel with a threshold (report SSIM/abs-diff per channel). Gateway switches to `ProjectReference` (NuGet later, ADR-003) and its `Baking/` dir is deleted. | Library tests green; gateway builds and its 9 S11 tests + S12 tests still green against the library; diff numbers printed for 6 legacy channels. | 40 | **You:** produce the golden fixtures — log Truly in via Firestorm on the stock outfit, note the 6 bake UUIDs from Appearance debug (or from `AvatarAppearance` via the harness), pull the assets. This is the step that can't be automated and blocks S0b's last third. |
| S1 | T | **Bake orchestrator + console trigger.** `AppearanceBakeModule` (Addons or OptionalModules per S0a finding): COF → wearables → textures → 11-channel composite via library → store assets → update ScenePresence TE → `SendAppearance` to all. Trigger: console `appearance bake <first> <last>`. No cap, no flag, no persistence keys yet. | Console command on Ebony bakes Truly; Firestorm observer (you) sees the sim's bake replace hers; harness diff of the stored assets vs goldens ≤ threshold. | 35 | Firestorm side-by-side, 1 loop. |
| S2 | T | **Persistence + supersede + reaper** (ADR-004). Avatar-service keys, per-channel input hash, skip-compute on match, synchronous supersede-delete, Robust reaper with `BakeTTLDays`, off by default. | Unit tests: hash stability, supersede deletes old UUID, reaper deletes only past-TTL-and-not-logged-in; console bake twice → second run logs "reused 11/11". | 30 | none |
| S3 | T | **Wire: flag, bit 0, `AppearanceData`, cap.** `[Appearance] ServerSideBaking` per region; `RegionProtocols |= 1` when set; `SendAppearance` emits `AppearanceData{1, CofVersion}` for sim-baked avatars only; `UpdateAvatarAppearance` cap with the §4.3 handshake + anti-livelock; login-time bake trigger on `MakeRootAgent`. Flag stays **false** in every shipped ini. | Unit tests for the handshake (equal / less / greater / livelock cap); with the flag on for a **test region only**, LL viewer logs in and is textured to itself; Firestorm on the same region POSTs and is textured. | 40 | **You:** stock LL viewer + Firestorm on the test region, 1 loop each. First moment the LL viewer isn't a cloud. |
| S4 | T | **Appearance service on Robust** (ADR-002). `agent_appearance_service` in the login response; `GET texture/<agent>/<channel>/<uuid>` resolving via avatar-service keys and streaming the asset; standalone registration. | curl the URL for Truly's `head` returns the J2K bytes; LL viewer sees **other** avatars textured on the test region. | 30 | LL viewer observing Firestorm-and-sim-baked Legion, 1 loop. |
| S5 | T | **Change triggers + BoM aux channels.** Rebake on `AvatarNowWearing` (Firestorm on a bit-0 region) and on cap POST with a newer COF version; the 5 BoM aux channels produced when universal wearables are present (needs [[web-viewer]] S13 to *render* them — sim side only here). | Firestorm on the test region changes a shirt → new bake within one POST; fixture with a universal wearable yields 11 stored channels. | 30 | Firestorm outfit change, 1 loop. |
| S6 | W | **Gateway SSB-aware mode** (ADR-009). Detect bit 0 per region; `server` appearance mode; accept `AppearanceData` for self; no bake path reachable on bit-0 regions (structural, like the S11 invariant). | Unit test proving the bake step is unreachable when bit 0 is set; live: Truly logs in via the web viewer on the test region and is textured with **zero** gateway bakes logged; on Transylvania (flag off) the S12 path still runs. | 25 | Web-viewer login on both regions, 1 loop. |
| S7 | T+W | **Soak + fidelity sign-off.** Harness against all three test avatars' outfits; 30-minute soak with LL viewer + Firestorm + web viewer on the test region; region restart with flag on → no rebake (persistence); flag off → Firestorm reverts, LL viewer clouds, nothing deleted. | All harness diffs ≤ threshold; no `AppearanceData` regressions on the flag-off region; report lists every unsupported layer seen. | 30 | Your call on flipping Ebony/Transylvania/Elm. |

**Total:** ~4.75 h CC across 9 sessions; 5 short verify loops of yours. Comparable to two web-viewer working days at the measured pace.

## 2. Order and gates

```
S0a ──► S0b ──► S1 ──► S2 ──► S3 ──► S4 ──► S5 ──► S7
                 ▲                       │
   goldens (you) ┘                       └──► S6 (any time after S3)
```

Gates:
- **Before S0a:** D-1, D-3, ADR-003 ruled (Ledger).
- **Before S1:** golden fixtures exist (your step in S0b). If they lag, S1 can proceed and S0b's diff numbers land in S1's DoD instead.
- **Before S3:** S0b diff ≤ threshold on the stock outfit. This is the rule that keeps a worse-than-Firestorm bake from ever reaching a bit-0 region.
- **Before flipping any production region (after S7):** the wipe-loop check from S0a is resolved on Tranquillity.

## 3. Each session prompt carries

Per the standing prompt structure: the S0a grep results and file:line anchors; the library's public API; what *not* to read (no LibreMetaverse Baker, no Halcyon wire code, no `appearance-utility-bin`); test avatars Truly/Aleric only, never Legion; reporting contract (done / VERIFY-resolved with file:line / decisions needed). Prompts are written here per session, in a code block, when you say go.

## 4. Deploy notes

- Test region = one region only, flag on in its own ini section. Recommended: Transylvania (currently loads 0 objects anyway — separate issue — so nothing to disturb).
- Both servers down before deploy (your practice). Publish path is `bin\Release\net10.0\win-x64\publish\` per project (BUILDING.md is wrong on this; noted in [[repo-audit]]).
- Robust must be redeployed at S4 (appearance service) and S2 if the reaper is enabled; region-only for the rest.
