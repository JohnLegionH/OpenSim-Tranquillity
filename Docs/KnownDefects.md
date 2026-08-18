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

## WebRTC voice: OnRemovePresence teardown is unwired — room stays joined on child-agent removal

**Status:** not started — resource-leak candidate, independent of Phase 3a. Candidate for a Mike
report.

**Symptom.** The presence-side voice teardown hook is commented out:
`scene.EventManager.OnRemovePresence += Event_OnRemovePresence;` at
`Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs:206`, and its handler
`Event_OnRemovePresence` (`:242`) is therefore never invoked. The only live teardown path is
viewer-hangup-driven — `WebRtcJanusService.cs:154`–`155` (`OnDisconnect`/`OnHangup`) →
`Handle_Hangup` (`:164`) → `DisconnectViewerSession` (`:183`) → `Room.LeaveRoom` (`:225`).

**Consequence.** An **OpenSim-side** presence removal — notably a **child agent** being torn down
when a neighbour region stops being adjacent — does not leave the Janus room. Nothing on the sim
side proactively hangs up or leaves; the room membership persists until the *viewer* drops that
WebRTC session (logout or its own connection teardown) or Janus times it out. Stale/leaked room
memberships are the expected accumulation.

**Why it may matter.** Neighbour-region voice means an avatar joins a room per adjacent region (see
`Docs/voice/parcel-voice-semantics.md` §G). If those rooms are only ever cleaned by viewer hangup,
churn (crossings, draw-distance changes) can leave orphaned participants in rooms the sim believes
the avatar has left — a slow resource leak and a possible source of phantom roster/mix entries.

**Suggested first step.** Decide whether `Event_OnRemovePresence` should be wired (and made
idempotent/root-child-aware) so an OpenSim presence removal issues the corresponding `LeaveRoom`,
rather than relying solely on viewer hangup.

## Estate CAP save silently flips TaxFree when override_public_access is absent

**Status:** not started — core Tranquillity defect, reachable from modern Firestorm.
Candidate for a Mike report.

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

## Parcel access/ban list updates are never persisted

**Status:** not started — core Tranquillity defect. **A working fix exists in the old
Legion fork and is unported.** Candidate for a Mike report.

**Symptom.** A parcel ban or access entry added in About Land → Access takes effect
in memory but is lost on restart, or on any crash before some unrelated path happens
to store the parcel.

**Mechanism.** `ClientOnParcelAccessListUpdateRequest`
(`Source/OpenSim.Region.CoreModules/World/Land/LandManagementModule.cs:683`) calls
`land.UpdateAccessList` at `:719` and **never calls `UpdateLandObject` /
`StoreLandObject`**. The `landaccesslist` table and its read/write round-trip are
complete and correct — the write is simply never triggered from this path. Entries
survive only incidentally, when a later dwell store or other parcel write flushes the
object.

**Existing fix.** The old Legion fork added the missing store at
`/d/legion-grid-source/…/LandManagementModule.cs:744`–`:749`, with the comment
*"Without this a crash before the next dwell store loses the change."*

**Suggested first step.** Port the Legion fix, confirming it lands after
`UpdateAccessList` and is not duplicated by any newer Tranquillity store path.

## Parcel ban-add path is silent on every outcome, success and failure alike

**Status:** not started — core Tranquillity defect. Diagnosability, not correctness.
Candidate for a Mike report.

**Symptom.** An operator adding a ban entry sees the entry fail to appear, with no
log line, no console output, and no user-facing alert — and sees exactly the same
nothing when it *succeeds*.

**Mechanism.** Four exit points in
`Source/OpenSim.Region.CoreModules/World/Land/LandManagementModule.cs` produce no log
and no alert. Three are literal returns; :716 is a permission guard that silently
falls through when it fails.

| Line | Condition |
|---|---|
| `:690` | flags mask |
| `:693` | `TaxFree` |
| `:714` | `requiredPowers` |
| `:716` | permission denied |

The **successful** path is equally silent: `UpdateAccessList` contains no logging at
all.

**Why it matters.** Five distinct outcomes are indistinguishable from outside. This
compounds with the missing-persist defect above: "the entry vanished" has at least
two unrelated root causes — rejected-and-unlogged, and accepted-but-never-stored —
that present identically. Diagnosing this cost more time than anything else in the
Phase 3a session.

**Suggested first step.** Log each early return at Debug with its reason, log the
successful update at Debug, and raise an alert to the requesting client on the
permission-denied and requiredPowers branches.

## Voice visibility feeder threads are not registered with the Watchdog

**Status:** not started — **Legion-side (our code), not Tranquillity core.** Fix is
scoped and queued.

**Symptom.** If a feeder thread dies or blocks, OpenSim neither detects nor reports
it. The failure is completely silent.

**Mechanism.** `Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceVisibilityService.cs:95`
constructs `m_thread = new Thread(RunLoop)` and `:100` starts it, with no
`Watchdog.StartThread` registration and no `Watchdog.UpdateThread` heartbeat anywhere
in `RunLoop` (`:136`).

Note this file is owned by `WebRtcVoiceRegionModule.csproj` and builds into
**`WebRtcVoiceRegionModule.dll`**, not `VoiceVisibility.dll`. The sibling
`Addons/os-webrtc-janus/Visibility/` directory is a separate project
(`Visibility.csproj`, `AssemblyName = VoiceVisibility`) and does not contain the
feeder service. Deploy sets keyed on the wrong dll will ship nothing.

**Consequence.** This directly enabled the 8.5-hour silent emission stall of
2026-08-16/17: the feeder produced nothing, with no latch, no error, no exception,
and no watchdog report, until a restart cleared it. The stall's proximate cause (a
wedged single-flight send) has since been fixed, but the **invisibility** of a dead
or blocked feeder thread has not.

**Note on log volume.** Registering does not add log lines. `Watchdog.UpdateThread`
is a heartbeat *call*, not a log statement, and the watchdog is silent until a thread
misses its timeout, then logs once. Normal operation is unchanged.

**Suggested first step.** `Watchdog.StartThread` at creation, `Watchdog.UpdateThread`
inside `RunLoop`, and confirm the 250 ms tick cadence sits comfortably inside the
watchdog timeout. Scope is this file only; the deploy artifact is
`WebRtcVoiceRegionModule.dll`.

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
