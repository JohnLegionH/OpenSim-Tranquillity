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
