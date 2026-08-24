# Known defects / deferred limitations (engine-wide)

> General log for cross-cutting defects. Phlox-specific items live in
> `PhloxKnownDefects.md`. Add new entries as `## <title>` with Status + symptom.

## ScenePresence finalizer throws NRE under GC (crashes test host; possible production risk)

**Status:** not started — candidate for its own investigation. Blocks presence-based
automated tests today; suspected latent production crash on abnormal presence teardown.

**Symptom.** In the test harness (`SceneHelpers` scenes with one or more
`SceneHelpers.AddScenePresence` / `AddChildScenePresence`), the test host process crashes
during GC with an unhandled `NullReferenceException`:

```
System.NullReferenceException
  at ScenePresence.RemoveClientEvents()   ScenePresence.cs:1306
  at ScenePresence.Dispose(Boolean)       ScenePresence.cs:1219
  at ScenePresence.Finalize()             ScenePresence.cs:1190
```

The crash is GC-timing dependent (fires when the presence is finalized, often after the
test that created it has passed), and because it is unhandled on the finalizer thread it
**aborts the whole test run** — not just the offending test.

**Suspicion.** The disposal path (`Dispose(false)` from the finalizer → `RemoveClientEvents`)
dereferences state that a minimally-constructed / not-fully-torn-down presence leaves null
(e.g. a controlling client or an event-source field). Two angles worth checking:
- The finalizer runs `Dispose(false)` and touches managed/event state that is only valid
  after full initialization or should only be touched in `Dispose(true)`; a finalizer
  should not reach into fields that may be null on an abnormally-constructed instance.
- If `Dispose(true)` (explicit teardown) does not `GC.SuppressFinalize(this)`, every
  properly-removed presence still runs the finalizer later.

**Why it may matter beyond tests.** The same finalizer path runs in production if a
`ScenePresence` is ever collected without a clean `Close`/`Dispose` (abnormal logout,
crossing failure, exception during teardown). An unhandled exception on the finalizer
thread terminates the process — so a fragile finalizer is a potential region-server crash
on abnormal presence teardown, not only a test nuisance.

**Impact / current mitigation.** The Phase-3a `FeederWorldFromScene` integration tests
(`Tests/WebRtcVoiceRegionModule.Tests`) deliberately create **no** presences to avoid this;
the presence→matrix path is covered instead by the deterministic engine `BanScenario` test
and the in-world DEBUG smoke check (see `Docs/voice/phase3a-feeder-acceptance.md`). A fix
here would unblock a genuine presence-based real-`Scene` automated test.

**Suggested first step.** Audit `ScenePresence.Dispose(bool)` /`RemoveClientEvents`: guard
finalizer-path field access against null, split managed-only teardown into the
`disposing == true` branch, and ensure explicit `Dispose`/`Close` calls
`GC.SuppressFinalize`.

## Mixer applies peer_ctl_batch exclusions by display string, silently misapplying on collision

**Status:** not started — mixer-side defect (`legion-voice-mixer`, our code).
**Observed live 2026-08-18 defeating parcel ban enforcement in one direction.**
Candidate for a Mike report as a protocol-application correctness issue.

**Symptom.** A parcel ban is enforced against one party but not the other. Mixer
counters agree with the audio and all read healthy — `excluded_entries=1` on one
handle, `0` on the other, same epoch, same batch.

**Mechanism, evidence, and the observed failure** are recorded in full in
`Docs/voice/parcel-voice-semantics.md` §M (ADDENDUM 3, 2026-08-18). In brief: the
mixer resolves each exclusion entry's listener through a hash keyed on the avatar
UUID string rather than participant identity, and the insert replaces on duplicate
key — so two handles for one avatar collapse to one, non-deterministically, and the
loser silently receives nothing.

**Why it is engine-relevant** despite being mixer-side: it converts the
`OnRemovePresence` teardown defect below from a resource leak into a silent
enforcement failure. Neither defect alone is severe; together a parcel ban can fail
to enforce after any unclean voice teardown.

**Suggested first step.** See §M. The minimum is detecting the collision and logging
it — a silent overwrite in an enforcement path should never be silent.

## WebRTC voice: OnRemovePresence teardown is unwired — orphaned memberships defeat ban enforcement

**Status:** implemented 2026-08-22 — see the resolution at the end of this entry.
**Severity raised 2026-08-18** — previously logged as a resource-leak candidate; then
observed to silently defeat parcel ban enforcement. Candidate for a Mike report.

**Symptom.** Orphaned participants accumulate in Janus rooms: handles with
`ice_state: disconnected` and `rtp_in_count: 0` still listed as room members.
Observed 2026-08-18 surviving a full avatar relog — not a brief teardown window.

**Mechanism.** The presence-side voice teardown hook is commented out:
`scene.EventManager.OnRemovePresence += Event_OnRemovePresence;` at
`Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs:159`, so
its handler `Event_OnRemovePresence` (`:185`) is never invoked. The only live teardown
path is viewer-hangup-driven — `WebRtcJanusService.cs:154`–`:155`
(`OnDisconnect`/`OnHangup`) → `Handle_Hangup` (`:164`) → `DisconnectViewerSession`
(`:183`) → `Shutdown` (`:191`) → `JanusViewerSession.Shutdown` → `LeaveRoom`
(`JanusViewerSession.cs:91`). (Line numbers verified against source 2026-08-18; earlier
revisions cited :206/:242 for the hook/handler, since corrected to :159/:185, and
`WebRtcJanusService.cs:225` for the chain's `LeaveRoom` — but :225 is `LeaveRoom` inside
the logout branch of `ProvisionVoiceAccountRequestBAD`, a different path; the hangup
chain reaches `LeaveRoom` via `Shutdown` at `JanusViewerSession.cs:91`.)

An OpenSim-side presence removal — notably a child agent torn down when a neighbour
region stops being adjacent — issues no `LeaveRoom`. Membership persists until the
viewer drops that WebRTC session or Janus times it out.

**Why it matters — enforcement, not only resources.** An orphaned handle carries the
same display (avatar UUID) as the avatar's live handle. The mixer resolves
per-listener exclusions through a display-keyed index that silently drops one
participant on collision (see the `by_display` entry above, and
`Docs/voice/parcel-voice-semantics.md` §M). An orphaned membership can therefore
capture the avatar's exclusion column, leaving the live handle unexcluded and a parcel
ban unenforced in one direction, non-deterministically.

Observed 2026-08-18 on Ebony: a banned pair failed to hide symmetrically while an
orphaned handle held the exclusion at `excluded_entries=1` with `rtp_in_count=0`.
Clearing mixer session state so each avatar held one handle restored correct symmetric
enforcement with no change to the sim, the feeder, or the matrix.

Neighbour-region voice makes this routine rather than exotic: an avatar joins one room
per adjacent region (`Docs/voice/parcel-voice-semantics.md` §G), so crossings and
draw-distance changes generate exactly the child-agent teardowns this path misses.

**Suggested first step.** Do **not** wire `Event_OnRemovePresence` as-is. Recon
2026-08-18 found four prerequisites, and only three are addon-local:

- **Ignore child removals.** The handler cannot self-distinguish a child teardown from
  a real logout: `isChildAgent` is known at the fire site (`Scene.cs:3832`) but is not
  carried by the delegate (`EventManager.cs:158`), and `TriggerOnRemovePresence` fires
  for both root and child (`Scene.cs:3865`). Propagating it is a **CORE CHANGE** — an
  upstream `EventManager` API break; needs discussion with Mike.
- **Region-scope the teardown.** The session registry is static and simulator-wide
  (`VoiceViewerSession.cs:56`–`:58`) and the handler never consults `RegionId` (`:52`),
  so it would tear down the agent's sessions in *every* region, not just the one that
  fired. **Addon-local.**
- **Locked, idempotent `Shutdown`.** The check-then-null in `JanusViewerSession.Shutdown`
  is unlocked (`JanusViewerSession.cs:87`–`:105`) and the hangup path already runs
  `Shutdown` fire-and-forget (`WebRtcJanusService.cs:187`–`:192`), so a second concurrent
  entry double-leaves / double-destroys. **Addon-local.** *(Implemented since — added
  after this entry was written: `JanusViewerSession.Shutdown` now carries the
  SemaphoreSlim serialization at `:63`–`:65`.)*
- **Handle the non-Janus `Shutdown`.** `VoiceViewerSession.Shutdown` throws
  `NotImplementedException` (`VoiceViewerSession.cs:122`–`:125`); any non-Janus session
  in the registry would throw on removal. **Addon-local.**

**History.** The hook was never active in this repo. It was introduced already commented
inside a `// TODO: figure out what events we care about` scaffold
(`WebRtcVoiceServiceModule.cs:152`) listing six candidate event subscriptions, with an
explicit note (`:183`–`:184`) that hangup-driven teardown was chosen instead. This is a
deferred design decision, not a disabled feature.

**Sharpest risk.** Wired as-is, a single child-agent removal — routine on any border
crossing — would destroy the agent's live voice session in *every* region, and the
viewer's re-provision that follows could manufacture the very §M duplicate-handle
condition this fix was meant to relieve.

**External review, 2026-08-22 — two findings change the approach.**

**1. The core change is not required. Use `OnClientClosed`.**

The entry's Suggested first step lists four prerequisites, one of which — propagating
`isChildAgent` through `TriggerOnRemovePresence` — is a core `EventManager` delegate
change. That is no longer the recommended path.

`OnClientClosed(UUID clientID, Scene scene)` fires immediately before `OnRemovePresence`,
and OpenSim's own documentation states that at the point of firing the scene still
contains the client's ScenePresence. It also passes the `Scene` directly, so the handler
need not rely on a captured module-level scene to know which region fired.

A synchronous handler can therefore resolve the presence and read `IsChildAgent` itself:

    ScenePresence sp = scene.GetScenePresence(agentId);
    if (sp == null || sp.IsChildAgent) return;

**This must happen before the handler returns.** Asynchronous work cannot rely on the
presence remaining resolvable — `Scene.RemoveClient` removes it in its final cleanup,
after the events fire.

Correct the entry's problem 1 wording accordingly: the information is not carried by the
event payload, but it IS available from the Scene while the event is dispatched. "The
information does not exist at the event boundary" is too strong.

`OnClientClosed` is documented as running under the per-agent lock, with a warning that
lengthy work belongs elsewhere. That suits the design: classify synchronously, tear down
asynchronously.

**2. Region scoping is insufficient. Session generation must be part of the identity.**

The entry proposes filtering the registry lookup by `RegionId`. That fixes the
cross-region case — B's teardown reaching into A — but not the case actually observed.

After a relog, an orphaned session and a live session can coexist for the same avatar in
the SAME region. A lookup keyed on `AgentId + RegionId` matches both, so a late-arriving
teardown for the orphan destroys the live session as well. That converts a leak into an
outage.

**The voice session must carry a generation token** captured at creation — the OpenSim
SessionID, circuit identity, or another immutable value — so teardown targets
`AgentId + RegionId + SessionId`. Given that orphans surviving a full relog are already
observed, this is part of the fix, not a refinement.

The asynchronous portion must operate on the exact session captured synchronously. It
must not re-query "all sessions for this avatar."

**Also raised by the review:**

- **Do not make the base `VoiceViewerSession.Shutdown` a silent no-op.**
  `NotImplementedException` is wrong for a path that can legitimately reach those
  instances, but a no-op hides a different leak. If every concrete session must support
  shutdown, make it abstract; otherwise distinguish disposable from non-disposable
  session types explicitly.
- **The registry needs its own synchronisation review**, independent of the
  `SemaphoreSlim` added to `JanusViewerSession.Shutdown`. Enumeration, lookup, insertion
  and removal must be synchronised against one another — a hangup removing an entry while
  the teardown handler enumerates is a separate race.
- **Teardown ordering:** the session should become atomically unavailable to registry and
  policy operations before the Janus calls run, or the policy engine can keep discovering
  a session whose network cleanup is in flight. But a failed remote cleanup must not make
  the orphan permanently invisible — that argues for a Closing state or reconciliation
  logging rather than remove-and-forget.
- **Do not launch unobserved `Task.Run` work.** OpenSim's event dispatch isolates
  subscriber exceptions, but independent work must observe and log its own failures.

**Revised ordering:**

1. **Mixer-side duplicate protection first, independently.** Fixing teardown removes one
   source of duplicate handles; it does not prove there is no other. A `by_display`
   collapse that silently picks one handle is a dangerous invariant for something
   enforcing parcel privacy. Detecting the condition loudly, or enforcing
   one-handle-per-avatar at join, converts an orphan from a policy-enforcement bypass
   into a cleanup bug. This is worth doing before the teardown work, not after.
2. Region scoping plus session-generation identity in the registry.
3. `Shutdown` concurrency and the base-class semantics.
4. Wire `OnClientClosed` with synchronous classification and asynchronous, idempotent
   teardown.

The core `EventManager` change is no longer required by this plan.

*(Citation refresh, 2026-08-22, verified while recording this review. In-tree
confirmation of the review's premise: `ClientClosed(UUID clientID, Scene scene)` is
declared at `EventManager.cs:425`–`:426`, and `Scene.RemoveClient` fires
`TriggerClientClosed(agentID, this)` at `Scene.cs:3863` immediately before
`TriggerOnRemovePresence` at `:3865`, with the presence removed later at `:3899` — so the
presence is resolvable during dispatch as stated. Drift in this entry's earlier cites,
the file having changed since 2026-08-18: `WebRtcJanusService.cs` `:154`–`:155` →
`:160`–`:161` (OnDisconnect/OnHangup), `:164` → `:170` (Handle_Hangup), `:183` → `:189`
(DisconnectViewerSession), and the fire-and-forget `Shutdown` `:187`–`:192` →
`:189`–`:197` (`_ = pViewerSession.Shutdown()` at `:197`); `JanusViewerSession.cs`
`Shutdown` `:87`–`:105` → declared at `:91` with `LeaveRoom` at `:101`, and the method now
carries the SemaphoreSlim serialization the review references (`:63`–`:65`);
`VoiceViewerSession.cs` `Shutdown` `NotImplementedException` `:122`–`:125` →
`:186`–`:188`. Still landing as cited: `WebRtcVoiceServiceModule.cs:152`/`:159`/`:183`–`:185`,
`VoiceViewerSession.cs:52`/`:56`–`:58`, `Scene.cs:3832`/`:3865`, `EventManager.cs:158`.)*

**RESOLUTION (2026-08-22) — the revised plan is implemented.** Commit
`bc86d292b2` (sim side), with the mixer-side items landed first per the revised ordering.

**What shipped:**

- **Mixer-side duplicate protection first** (`legion-voice-mixer`): exclusion fan-out to
  every session matching the listener display (`b1669dc`), join-time duplicate detection
  logging both user_ids plus the existing session's liveness triple, and the
  deterministic dot-batch merge — live handle beats downed, max-power/OR-vad among
  equals (`7bfd4b7`).
- **`OnClientClosed` subscribed** (`WebRtcVoiceServiceModule.cs:163`, unsubscribed
  `:177`), replacing the never-wired `OnRemovePresence` hook. The handler
  (`Event_OnClientClosed`, `:198`) reads root/child synchronously from the
  still-resolvable presence and bails on child closes before any capture.
- **Teardown targets a generation token** — the provisioning client's login SessionId,
  captured onto the session before it enters the registry
  (`CaptureGenerationToken`, `:259`, called at `:329`) — so an orphan and its live
  successor after a relog are distinguishable and only the departing generation is torn
  down (`CaptureSessionsForClose`, `VoiceViewerSession.cs:141`: one-lock select by
  region + agent + token, with a UUID.Zero sweep for failed captures).
- **`TryGetViewerSessionByAgentId` deleted**, its three defects recorded in a tombstone
  (`VoiceViewerSession.cs:204`): deferred query enumerated outside the lock, registry
  mutated inside that enumeration by its only caller, and no region or generation
  filtering.
- **Failed shutdowns parked, not dropped**: `ClosingSessions` (`:84`) holds them with an
  age, out of every policy/provision/matrix read but discoverable; retried at the next
  provision or close for the same agent (`CloseCompleted` `:172`,
  `GetClosingSessions` `:182`; hooks at `WebRtcVoiceServiceModule.cs:214` and in
  `ProvisionVoiceAccountRequest`).

**What did NOT ship, and why:**

- **The core `EventManager` delegate change** (propagating `isChildAgent` through
  `TriggerOnRemovePresence`) — the original prerequisite list called for it; it was not
  made and is not needed. `OnClientClosed` fires while the presence is still in the
  scene (`Scene.cs:3863` vs removal at `:3899`), so the handler reads `IsChildAgent`
  itself. The review predicted this; the implementation confirms it.
- **The Closing state, as reviewed.** `ClosingSessions` implements it for the FAILURE
  case, not the in-flight case the review proposed — because the in-flight window does
  not exist: `RemoveViewerSession` (and the capture path) removes atomically under the
  registry lock, so a session is already unavailable to every policy read before any
  Janus cleanup starts. The parked set addresses the different problem of a cleanup
  that fails.
- **Registry synchronisation, precisely:** the UNLOCKED enumeration defect is gone — it
  was the deleted method's deferred return, removed with it. What remains is LINQ in
  `TryGetViewerSessionByVSSessionId` (`VoiceViewerSession.cs:215`–`:218`) that runs
  entirely inside the lock and materialises before returning — a harmless
  double-enumeration (`Count()` then `First()`), an inefficiency and not a race.
  Recorded as a residual style item, not an open synchronisation defect.

**What remains unverified.** The orphan condition arises from unclean teardowns that
cannot be reliably provoked, so this is verified by unit test (seven capture-semantics
tests pinning orphan-plus-live coexistence, the Zero-token sweep, atomic
registry+membership removal, park-until-complete, and hangup racing capture) and by
absence of regression — not by observing a live orphan being cleaned up. The mixer's
join-time duplicate detection is the instrument that will show it working over time: a
wired teardown should make that WARN progressively rarer, and any occurrence it still
logs carries the liveness data to say why.

**First live run, 2026-08-22 — worked end to end; two findings and one self-inflicted
WARN.** A voice logout captured, shut down, and completed one session in 17ms with the
neighbour-region session correctly cleaned by the hangup path. Findings, mechanisms
verified against source (both were initially described with different mechanisms; the
corrected versions below are what the code shows):

- **`Shutdown` cannot report failure — every Janus fault below it is swallowed.** Not an
  unobserved in-flight task: the chain awaits each step serially, but `JanusPlugin.Detach`
  catches all exceptions internally and returns a bool (`JanusPlugin.cs:143`–`:146`) that
  `Shutdown` ignores (`JanusViewerSession.cs:110`), `LeaveRoom` does the same and returns
  false unconditionally (`JanusRoom.cs:119`–`:133`), and `DestroySession`'s result is
  explicitly discarded (`JanusViewerSession.cs:117`). So a failed Janus cleanup logs at
  ERROR from the plugin layer but reads as "teardown complete" to the caller — the
  parked-session machinery only sees exceptions from elsewhere. **Fix deferred to its own
  scoped change**: propagating those results alters teardown semantics at every logout
  and deserves separate review.
- **The suspected double `OnClientClosed` fire does not exist.** A single trigger site
  exists (`Scene.RemoveClient`, `Scene.cs:3863`); the apparent duplicate in the live run
  was this handler observing its own just-parked capture, fixed by the
  snapshot-before-capture reorder below. What is true and useful: the event fires once
  per scene holding a presence (the root region plus each neighbour holding a child
  agent); the voice handler classifies and bails on child closes, so neighbour-region
  voice sessions are cleaned only by the hangup path — which worked here, and the
  mixer's join-time duplicate WARN is the instrument if it ever does not.
- **The false "prior teardown pending/failed (age 0s)" WARN** on a clean logout: the
  handler read the closing-set AFTER capturing, observing its own just-parked work.
  Fixed by snapshot-before-capture (stateless — no suppression window that could swallow
  a genuine second close of a different generation), together with full-exception
  logging in the teardown catch and a read-only `show voice closing` console command
  exposing the closing-set (agent, session, age, last failure reason).

## Estate CAP save silently flips TaxFree when override_public_access is absent

**Status:** implemented 2026-08-23 (`b5e3472247`) — core Tranquillity defect, was reachable
from modern Firestorm. Candidate for a Mike report. The fix went wider than this entry: a
sweep found five MORE fields in the same CAP handler treating absence as false
(`is_externally_visible`, `allow_direct_teleport`, `deny_anonymous`, `deny_age_unverified`,
`allow_voice_chat` — bare `AsBoolean()` reads on possibly-absent keys). All seven CAP-carried
booleans are now parsed nullable and applied only when present
(`ApplyEstateChangeInfo`, pure and unit-tested with a mutation-verified absent-vs-false
contract, including the carried-false-applies-false case and the region-flags cascade).
The UDP path was always correct and is untouched.

**Symptom.** Any estate save through the CAP path that omits `override_public_access`
inverts the `TaxFree` flag, silently, with no relation to what the operator changed.
Successive saves toggle it back and forth.

**Mechanism.** The request object's default is computed from the *current* value,
negated (`EstateChangeInfo.cs:178`):

```
overridePublicAccess = !TaxFree_current      // default when field absent
...
TaxFree = overridePublicAccess               // EstateManagementModule.cs:2242
```

The assignment is unconditional, so when the viewer omits the field the negated
default survives into the store. The legacy **UDP** path (`:2166`–`:2175`) is correct
and does not exhibit this.

**Why it matters.** `TaxFree` is a misnomer for `!AllowAccessOverride`
(`EstateSettings.cs:205`) and short-circuits *every* parcel-level ban/restrict/voice
check via the common exemption preamble (`LandObject.cs:724`; see
`Docs/voice/parcel-voice-semantics.md` §1.1, §1.4). Per §E of that document, under
`TaxFree` the WebRTC provision gate consults no per-parcel voice or ban deny at all.
So an unrelated estate save can silently disable estate-wide parcel access control
and parcel voice control, and the operator has no indication it happened.

The field name hides this, and the DB column deliberately kept the old name
(`EstateSettings.cs:205`) — so neither the code nor the schema reads as what it does.

**Suggested first step.** Make the CAP handler distinguish *absent* from *false* and
leave `TaxFree` unmodified when the field is not supplied, matching the UDP path.
*(Done, and wider — see the Status line above.)*

## Five estate toggles are packed into region flags but have no write path at all

**Status:** not started — core Tranquillity defect (likely inherited from upstream).
Found by the 2026-08-23 omission-pattern sweep. Distinct from the absent-vs-false
family above: those fields had a write path that applied the wrong value; these have
no write path at all, so the CAP nullable fix does not change their behaviour.

**Symptom — none an operator can trigger, and that is the finding.** These flags reach
viewers and affect behaviour (`AllowSetHome`/`AllowLandmark` gate Set Home / landmark
creation client-side via the packed RegionFlags), but they are pinned on BOTH ends:
no operator action can change them, and no viewer control was found to try —
verified against `D:\phoenix-firestorm` at Firestorm_Beta_7.2.5.81383-7-g26e74c311f: zero hits for any accessor in
`llestateinfomodel.h/.cpp`, zero checkboxes across `panel_region*.xml`. NOT
searched: `floater_region_info.xml` or other XUI outside that glob, and no other
viewer or revision. Dormant dead wiring rather than a lying toggle, on that
evidence.

**Mechanism.** Five `EstateSettings` fields are packed into the region flags viewers
receive (`PackEstateFlags`, `EstateManagementModule.cs:2344`), but NO write path
exists for any of them: `AllowLandmark`, `AllowSetHome`, `ResetHomeOnTeleport`,
`BlockDwell`, `AllowParcelChanges`. The EstateChangeInfo CAP carries no wire key for
them (`EstateChangeInfo.cs:160-196`), the UDP `HandleEstateChangeInfo` bit-writes do
not include them (`EstateManagementModule.cs:2166-2213`), and the only assigning code
— `EstateSettings.SetFromFlags` (`EstateSettings.cs:461-469`) — is dead: zero callers
anywhere in `Source/` or `Addons/`. The fields sit permanently at their constructor
defaults — verified: `AllowLandmark`/`AllowSetHome`/`AllowParcelChanges` true
(`EstateSettings.cs:57`/`:71`/`:64`), `ResetHomeOnTeleport`/`BlockDwell` false
(`:197`/`:183`) — or whatever the DB row carries.

**Why it matters — least of the sweep's findings.** With defaults matching SL's
permissive norm and no control on either end, nothing is currently lied about; the
cost is latent: any future viewer that grows the control, or any operator expecting
`ResetHomeOnTeleport`/`BlockDwell` to be settable, meets a silently immovable flag.

**Deliberately NOT fixed in `b5e3472247`:** the nullable-parse fix cannot reach a
field the wire does not carry. Fixing this means deciding which message should carry
these fields (wiring `SetFromFlags` into the UDP estate-flags path is the natural
candidate — the viewer's full bitmask already contains the bits) — a wire-behaviour
decision worth checking against upstream intent before implementing.

## Parcel access/ban list updates are never persisted

**Status:** implemented — core Tranquillity defect. A working fix existed in the old Legion
fork; ported to this branch (the store added after `UpdateAccessList`). See the parcel
persist-and-preserve commit.

**Symptom.** A parcel ban or access entry added in About Land → Access takes effect
in memory but is lost on restart, or on any crash before some unrelated path happens
to store the parcel.

**Mechanism.** `ClientOnParcelAccessListUpdateRequest`
(`Source/OpenSim.Region.CoreModules/World/Land/LandManagementModule.cs:683`) calls
`land.UpdateAccessList` at `:736` and **never calls `UpdateLandObject` /
`StoreLandObject`**. The `landaccesslist` table and its read/write round-trip are
complete and correct — the write is simply never triggered from this path. Entries
survive only incidentally, when a later dwell store or other parcel write flushes the
object.

**The observed outcome is three-way, not binary.** A ban entry can be *lost* (the common
case above), *persisted correctly*, or *persisted broken*. The incidental store that
flushes the parcel can itself be a properties update (`UpdateLandProperties`), which
persists the ban **entry** but with `UseBanList` **cleared** — a `landaccesslist` row that
reads as an enforced ban yet does not enforce after restart. So "never persisted"
understates it: the entry may reach the database in a non-enforcing half-state. That
flag-recompute has a distinct root cause and its own fix; see *Parcel properties save
clears UseBanList when the viewer omits it, dropping ban enforcement on restart*, below.

**Existing fix.** The old Legion fork added the missing store at
`/d/legion-grid-source/…/LandManagementModule.cs:744`–`:749`, with the comment
*"Without this a crash before the next dwell store loses the change."*

**Suggested first step.** Port the Legion fix, confirming it lands after
`UpdateAccessList` and is not duplicated by any newer Tranquillity store path.

## Parcel properties save clears UseBanList when the viewer omits it, dropping ban enforcement on restart

**Status:** implemented — core Tranquillity defect. Observed live on Legion Grid,
2026-08-18 (not derived from code alone). Fixed alongside the missing-persist defect above
on this branch; see the parcel persist-and-preserve commit.

**Symptom.** A parcel ban enforces normally until the region restarts, then silently
stops. The entry is still visible in About Land and present in `landaccesslist`;
`land.LandFlags` has `UseBanList` clear.

**Mechanism.** `UpdateLandProperties` recomputes the parcel flags as
`preserve | (args.ParcelFlags & allowedDelta)`
(`Source/OpenSim.Region.CoreModules/World/Land/LandObject.cs:671`) with `UseBanList` in
`allowedDelta` (`:649`). The clear is **not** an omitted field: verified against the viewer
(`D:\phoenix-firestorm`), the client transmits the entire 32-bit `ParcelFlags` word on every
properties save (`llparcel.cpp:490`/`:514`, the whole `getParcelFlags()`), and each About-Land
tab toggles only its own bits via a preserving `setParcelFlag` (`llparcel.cpp:355`). `UseBanList`
is the one `allowedDelta` bit with **no authoritative viewer control**: the Access tab hard-codes
it `true` (`llfloaterland.cpp:3160`), every other tab merely retransmits its cached value. The
clear therefore fires when the cached value is already zero — which is exactly the post-restart
state: the DB row holds the flag clear, the server sends zero in `ParcelProperties`, the viewer
caches zero, and the next properties save from any non-Access tab writes zero back. A
self-sustaining loop. `newData` starts as `LandData.Copy()` (`:533`), which deep-copies
`ParcelAccessList`, so the ban entry survives into the same snapshot; `UpdateLandObject` (`:679`)
persists entry-without-flag. On reload, `BuildLandData` restores `Flags` from the row
(`Source/OpenSim.Data.MySQL/MySQLSimulationData.cs:1349`) and `IsBannedFromLand_inner` gates on
the bit (`LandObject.cs:828`), returning false without consulting the entry.

**Coupled with the missing-persist defect** (*Parcel access/ban list updates are never
persisted*, above), not independent of it — the earlier note that fixing persist "would not help
here" was wrong. Persisting the correct state at ban-add time (the `UseBanList` set that
`UpdateAccessList` already applies) breaks the zero loop at its source: with a non-zero flag in
the DB the server no longer seeds the viewer with zero. The two fixes are complementary — persist
stops the loop being seeded, and the recompute preserve stops a save re-introducing zero — so both
land together.

**Why it matters.** A silent failure deferred to restart, leaving a database row that reads as an
enforced ban but does not enforce. It rhymes with *Estate CAP save silently flips TaxFree when
override_public_access is absent*, but the root cause differs: TaxFree is a genuine omitted field
treated as zero, whereas `UseBanList` is transmitted-but-uncontrolled — the server must own it
because the viewer has no way to express it.

**Fix as implemented.** The flag recompute in `UpdateLandProperties` is factored into a pure
`LandObject.ComputeSavedFlags`, which re-asserts `UseBanList` from ban-list membership
(`HasBanEntry`) after taking the client's allowed bits, scoped to `UseBanList` only. `UseAccessList`
is deliberately left viewer-authoritative: a non-empty access list with public access on is a valid
"flag off" state (`llfloaterland.cpp:3130`-`:3138`), so auto-managing it would force a restriction
the owner did not request. Pinned two ways: the full-path
`TestPropertiesSaveOmittingUseBanListPreservesBanFlag` (integration, needs a Scene) and the pure
`LandObjectBanFlagTests` against `ComputeSavedFlags`/`HasBanEntry` (runs without a Scene; verified
mutation-sensitive — removing the re-assert fails `ComputeSavedFlags_ReassertsUseBanList...`).

## Parcel ban-add path is silent on every outcome, success and failure alike

**Status:** implemented — core Tranquillity defect. Diagnosability, not correctness.
See the parcel ban-add instrumentation commit on this branch.

**Symptom (was).** An operator adding a ban entry saw the entry fail to appear, with no
log line, no console output, and no user-facing alert — and saw exactly the same
nothing when it *succeeded*.

**Mechanism (now instrumented).** Four exit points in
`Source/OpenSim.Region.CoreModules/World/Land/LandManagementModule.cs` previously produced
no log and no alert. Three are literal returns; the permission guard silently fell through
when it failed. Each now logs its specific reason at Debug:

| Line | Condition |
|---|---|
| `:689` | flags mask |
| `:692` | `TaxFree` |
| `:713` | `requiredPowers` (see note below) |
| `:716` | permission denied |

The accepted path now logs the written LocalID and the entry count after
`UpdateAccessList`; the LocalID is what makes the two-parcel duplication diagnosable —
two packets produce two "applied" lines with different LocalIDs.

**Note — unreachable branch.** The `requiredPowers == GroupPowers.None` return at `:713`
is unreachable under the current flags mask: `0x1B` at `:689` is exactly the four bits
(access / ban / `8` / `0x10`) that each set a required power, so any request passing `:689`
sets a power. It is instrumented anyway as a drift detector for those two masks, not an
expected log line.

**Why it matters.** Five distinct outcomes are indistinguishable from outside. This
compounds with the missing-persist defect above: "the entry vanished" has at least
two unrelated root causes — rejected-and-unlogged, and accepted-but-never-stored —
that present identically. Diagnosing this cost more time than anything else in the
Phase 3a session.

**Suggested first step.** Log each early return at Debug with its reason, log the
successful update at Debug, and raise an alert to the requesting client on the
permission-denied and requiredPowers branches.

**Instrument's first use, 2026-08-21.** A ban appearing on both Ebony parcels when only one was targeted was suspected to be a viewer double-send or a server fan-out. The new logging resolved it in one step: two `applied to local land` lines 61 seconds apart, one per parcel — operator action across two About Land sessions, not a defect. The server applied exactly what each packet specified.

## About Land access list rendering empty on reopen — RESOLVED, viewer-side, no server change

**Status:** resolved 2026-08-22 — not a server defect. Recorded here because it was
observed live alongside the parcel-ban defects above and consumed a diagnosis pass.

**Observed (old viewer).** With a ban entry present and enforcing, closing About Land
and reopening it showed an empty Banned list; the database was unchanged across the
close/reopen and enforcement continued throughout.

**Resolution.** Does not reproduce on a current Firestorm master-tracking build — the
ban entry renders correctly on reopen. The viewer-side `mAccessList.clear()` /
LocalID-discard analysis was accurate about the source tree; the previously-installed
viewer predated the fix. Same root cause family as the voice-moderation menu
investigation (`Docs/voice/voice-moderation-design-brief.md`, RESOLVED section): the
running binary was months older than the tree being analysed.

**Server behaviour confirmed CORRECT, do not "fix" it.** `SendLandAccessListData`
sends `SequenceID = 0` on both list replies (`LLClientView.cs:6758`). That is correct,
not a workaround: non-zero values would trigger the current viewer's `resetSequence()`
handling and clear the previously-received list when the second reply arrives — making
it worse. The two-packet shape (one Access reply, one Ban reply,
`LandObject.cs:1071`–`:1084`) is likewise correct: the viewer clears once at request
time and merges per packet into per-flag lists. No server change is needed or wanted.

## Parcel ban does not eject an already-present avatar

**Status:** not started — core Tranquillity defect, core parcel enforcement, not
voice. Affects every parcel ban regardless of whether voice is in use. Observed
2026-08-22. Candidate for a Mike report.

**Symptom.** An operator adds a resident to the parcel Banned list; the resident
remains standing on the parcel indefinitely. The ban takes visible effect only when
the banned avatar attempts to move, at which point the movement path's ban-line check
blocks them; a stationary avatar remains indefinitely. Voice exclusion applies
correctly and immediately — this is presence only.

**Mechanism.** The ban-add path (`ClientOnParcelAccessListUpdateRequest` →
`UpdateAccessList` → store, `LandManagementModule.cs:683`–`:749`) never scans for or
ejects present avatars. Enforcement is entry-time only: `EnforceBans`
(`LandManagementModule.cs:358`–`:432`) is invoked solely from the parcel-crossing
event (`EventManagerOnAvatarEnteringNewParcel` at `:464`), so it fires when an avatar
*moves onto* a parcel, never when a ban lands on the parcel they already occupy. Note
also its built-in limits: it is a no-op above `BanLineSafeHeight` (`:361`–`:368`), and
with `m_allowedForcefulBans` off it only alerts "please leave by your own will"
(`:385`–`:392`). (An earlier citation placed a `TODO: FIXME: If agent is in flight,
refuse to land!` at `Scene.cs:5623` — that comment does not exist anywhere in this
tree's `Source/`; the mechanism above is cited against the code that does.)

**Why it matters.** It differs from SL, where a ban ejects. An operator who bans a
griefer and watches them stay put reasonably concludes the ban failed. Voice IS
enforced, so the failure is partial and confusing rather than total.

**The estate-ban path already does this correctly** and is the in-tree template: on an
estate ban add, the handler resolves the present `ScenePresence` and calls
`Scene.TeleportClientHome`, falling back to `Kick` + `CloseAgent` if the teleport
fails (`EstateManagementModule.cs:1135`–`:1146`).

**Suggested first step.** On a successful ban add (the instrumented success branch of
`ClientOnParcelAccessListUpdateRequest`), scan avatars present on the parcel against
the new entry and eject those now banned, matching the estate-ban path's
teleport-home-with-fallback shape. Decide explicitly how it composes with
`m_allowedForcefulBans` and the `BanLineSafeHeight` exemption, and reuse the existing
exemptions (`ForceAvatarToPosition` is already God-exempt, `:436`).

## Voice visibility feeder thread death is not detected by the Watchdog

**Status:** registration implemented (uncommitted) — **Legion-side (our code), not
Tranquillity core.** Partial fix: a blocked or non-heartbeating `RunLoop` is now
reported; thread death and a wedged fire-and-forget sender remain undetected.

**Symptom.** A feeder thread that *dies* (terminates outright) is neither detected
nor reported — the failure is completely silent. A thread that blocks or otherwise
stops heartbeating *without* dying is now caught by the Watchdog alarm after this fix
(see Mechanism); before it, that too was silent.

**On thread death (what registration does *not* cover).** Registration catches a
thread that is alive but no longer calling `UpdateThread` — blocked, wedged, or
spinning — which is the realistic failure here (`RunLoop`'s try/catch keeps the loop
alive across a `Tick()` exception, so outright death is unlikely). It does **not**
catch the thread *dying*: per `Watchdog.cs:357` and `:386-388`, a thread that reaches
`ThreadState.Stopped` is reaped from the tracker silently — the alarm callback on
that branch is commented out — so a feeder thread that terminates outright is still
not reported.

**Mechanism.** `Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceVisibilityService.cs`
now registers the tick thread with `WorkManager.StartThread` (`:100`–`:107`), passing
`alarmIfTimeout: true` (`:105`) and `timeout: 5000` (`:107`); it heartbeats on the
always-executed path via `Watchdog.UpdateThread()` inside `RunLoop` (`:170`, after the
`m_wake` wait/reset so an idle tick still beats) and deregisters with
`Watchdog.RemoveThread()` on loop exit (`:174`). 5000 ms is 20x the 250 ms cadence, and
`Pump` never blocks the tick thread, so that headroom holds.

Note this file is owned by `WebRtcVoiceRegionModule.csproj` and builds into
**`WebRtcVoiceRegionModule.dll`**, not `VoiceVisibility.dll`. The sibling
`Addons/os-webrtc-janus/Visibility/` directory is a separate project
(`Visibility.csproj`, `AssemblyName = VoiceVisibility`) and does not contain the
feeder service. Deploy sets keyed on the wrong dll will ship nothing.

**Consequence.** During the 8.5-hour silent emission stall of 2026-08-16/17 the
feeder produced nothing — no latch, no error, no exception, and no watchdog report —
until a restart cleared it. That stall's proximate cause (a wedged single-flight
send) has since been fixed. Registration would **not** have caught that particular
stall: `Pump` is fire-and-forget (`VoiceVisibilityService.cs:159`), so a wedged
sender runs off the tick thread and leaves `RunLoop` heartbeating normally. What
remains uncovered — and what this fix does address — is a **blocked or
non-heartbeating `RunLoop` itself**, which is currently completely invisible.

**Note on log volume.** Registering does not add log lines. `Watchdog.UpdateThread`
is a heartbeat *call*, not a log statement, and the watchdog is silent until a thread
misses its timeout, then logs once. Normal operation is unchanged.

**Remaining work.** Thread *death* is still undetected: `Watchdog.cs:357` /
`:386-388` reap a `ThreadState.Stopped` thread with the alarm callback commented out,
so a feeder thread that exits outright is removed silently (see *On thread death*
above). Closing that needs a liveness check that does not depend on the
Stopped-thread alarm path. A wedged fire-and-forget sender is likewise outside
`RunLoop`'s heartbeat and would need sender-side instrumentation, not thread
registration. The registration change was scoped to this one file; the deploy
artifact is `WebRtcVoiceRegionModule.dll`.

## ALC split identity — non-shared types crossing a plugin AssemblyLoadContext boundary fail Type-keyed lookup silently

**Status:** documented rule, no fix owed. **Second observed instance** — recorded here
so the third person to hit it finds it.

**Symptom.** A module interface registers successfully and then fails to resolve, in
the same scene, milliseconds apart. It reads as an initialisation ordering race and
is not one — reordering will never fix it.

**Mechanism.** The DotNetCorePlugins backend creates **one isolated
`AssemblyLoadContext` per plugin dll** (`IPluginDiscovery.cs:258`–`:266`).
`PreferSharedTypes` can only unify an assembly already present in the **default**
context. `WebRtcVoice.dll` is dragged into default by `ServerUtils.LoadPlugin` →
`Assembly.LoadFrom` (`ServerUtils.cs:239`), which is why `IWebRtcVoiceService` has
always resolved. `VoiceVisibility.dll` sits on no default-context path, so each plugin
ALC loaded its own private copy, producing two distinct `IPeerCtlBatchSink` `Type`
objects. `SceneBase.ModuleInterfaces` is keyed on `Type` (`:486`, `:495`), so one ALC
wrote a key the other could never read.

**The durable rule: do not cross an ALC boundary with a non-shared type.** Where two
components must share an instance, give one of them *ownership* and pass the instance
in-process, rather than routing it through a `Type`-keyed registry.

**Known instances.**
1. `Phlox.ScriptEngine/AsyncCommand/Plugins/HttpRequest.cs:67`–`:75` (first,
   already documented in-tree).
2. Phase 3a `IPeerCtlBatchSink`. Resolved by moving sink ownership into the region
   module so construction and use share an ALC.

**Rejected non-fix.** Adding the type to the loader's shared-type allowlist is a
**silent no-op**: the string form resolves via `Type.GetType` in the default context,
finds nothing, and adds nothing. It fails without complaint.

**Filed here, not in `PhloxKnownDefects.md`,** because this is a property of the
plugin loader and applies engine-wide, despite the first instance being in Phlox.

## WebRTC voice: OnListenerProvisioned runs on failed provisions, queuing a doomed re-send

**Status:** not started — benign, low priority. Observation, not a live failure; newly
reachable by the ROOM_FULL / HTTP 409 capacity-rejection route.

**Symptom.** A viewer whose voice provision FAILS is still registered with the region's
visibility sender as a pending listener, which schedules a bounded exclusion-column
re-send that never lands — the listener is not, and will not be, in the mixer room — and
exhausts its retries before giving up.

**Mechanism.** The provisioning CAP handler hands the agent to the visibility sender's
pending-join path (`Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:408`–`:411`,
`svc?.OnListenerProvisioned(agentID)`) inside `if (resp is not null)`, which is true for
the failure maps as well as the success map. The sender then treats the agent as a
pending listener whose full exclusion column should be (re)sent once it is present in the
mixer room; since it never becomes present, the bounded re-send exhausts.

**Why it matters.** Benign per the pending-join path's own comment ("the bounded re-send
simply gives up loudly") — no correctness impact, no unbounded work. But it is now
reachable by a NEW route: a ROOM_FULL capacity rejection returns a non-null failure map
(and HTTP 409), so every capacity-rejected joiner also queues one doomed re-send. This is
pre-existing for all five provision-failure paths; the 409 change did not create it, only
added a route to it.

**Suggested first step.** Gate `OnListenerProvisioned` on a *successful* provision — call
it only when the response carries a `jsep` answer (or `viewer_session`), not merely when
`resp is not null`. One condition, in the CAP handler.

## Parcel access-list persistence is non-atomic - delete-then-reinsert under a process lock

**Status:** not started - pre-existing, exposure increased by the access-list persist fix.
Found by the 2026-08-23 upstream-audit sweep.

**Symptom.** A crash, process kill, or concurrent read during a parcel access-list save
can leave the parcel with an empty or partial `landaccesslist` - every allow and ban
entry gone - with no error and no log line. On restart the parcel enforces nothing.

**Mechanism.** `StoreLandObject` deletes the parcel's `landaccesslist` rows and reinserts
them. The two statements are guarded by a process-level lock, NOT a database transaction,
so the delete is committed before the reinsert runs. Anything that observes the DB inside
that window - another process, a replica, a backup, or a restart - sees the parcel with no
access list. The lock only excludes other threads in THIS process.

**Why it matters.** The window has always existed, but it used to open rarely because
`UpdateAccessList` never persisted at all (see the entry above). Fixing that means the
delete-then-reinsert now fires on EVERY access-list edit, so the window opens routinely
rather than almost never. The fix was correct and should stay; this is the second-order
consequence of making the path work.

**Also relevant to voice moderation slice 2**, which plans a `landvoicemoderation` table
mirroring `landaccesslist`. Mirroring the schema is fine; mirroring the write pattern
would inherit this defect. Worth settling before that table is written, not after.

**Suggested first step.** Wrap the delete and the reinsert in a single DB transaction and
assign it to both commands' `cmd.Transaction`. Note the code currently relies on implicit
per-command transaction behaviour, which differs between providers - so this needs
checking on MySQL, PGSQL and SQLite rather than just the one in front of you.

## Region crossing leaves a live voice handle in the previous region's room

**Status:** not started — observed 2026-08-24 on net10 with two avatars.

**Symptom.** After crossing a region boundary, an avatar holds voice handles in BOTH
regions' estate rooms simultaneously. Both are fully alive: ICE and DTLS connected,
datachannel open, and both decoding incoming audio (`rtp_in_count` climbing on each). Two
avatars produced four handles across two rooms, and each room reported
`room_participants: 2`.

**Mechanism.** Crossing triggers a fresh `ProvisionVoiceAccountRequest` → `SelectRoom` →
`JoinRoom` for the new region. Nothing leaves the old room. The mixer has no close or kick
primitive, so it cannot drop the orphan on its own, and the sim does not appear to issue a
leave.

**Why it matters.** The visibility feeder for a region emits only to that region's room. A
handle parked in a room whose scene no longer contains the avatar receives no further
batches — observed frozen at a stale epoch with `last_batch_age_ms` climbing past 20
minutes while the current room advanced normally. Any exclusion computed for that avatar is
never applied there. Since the orphan is still decoding audio, this is a room where audio
is present and moderation rules are not.

**Not observed:** whether audio from an orphan handle is audible to anyone. `rtp_out_count`
was 0 on all handles throughout, and no listener in the stale room was tested. That
distinction decides whether this is bookkeeping or a moderation bypass, and it is the first
thing to establish.

## Pending-join confirmation gives up for every listener, even when the batch lands

**Status:** not started — observed 2026-08-24; noise, not a correctness failure.

**Symptom.** Every listener that joins a voice room produces a `[VISIBILITY SENDER]`
warning stating the full column was re-sent 6x, was never confirmed in the room, and is
being given up on. Four of four listeners across two sessions, twice each per join, roughly
300 ms after `webrtcup`. No exceptions observed.

**Mechanism.** Not established. What IS established is that the warning's premise is false:
exclusions demonstrably do reach the mixer for listeners that produced it. A moderation
mute applied to an avatar whose listener had already "given up" was delivered, appeared as
`excluded_entries: 1` in `handle_info`, silenced the source audibly, and tracked the
target's parcel position live across six boundary crossings. So either the confirmation
check reads the wrong signal, or the retry budget expires before confirmation can arrive —
six attempts complete within ~300 ms of the WebRTC connection coming up, which is a narrow
window for a join to be observed and acknowledged.

**Why it matters.** The guard was built to make a silent drop loud, and it works — but it
fires on every join regardless of outcome. A warning that always fires carries no
information, and a real silent-drop would be indistinguishable from the background. This is
the instrument, not the failure; a broken instrument is worth fixing before it is needed.

**Suggested first step.** Establish what the sender treats as confirmation and where that
signal comes from. If it is presence in a room roster, compare against what the mixer
publishes and when. The `visibility.have_batch` field in `handle_info` reports true for a
room that has A batch, not that a given handle received one — if the sender relies on
anything with that shape, it is asking a question the answer cannot satisfy.
