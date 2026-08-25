**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **The log4net-unconfigured finding was acted on.** `5d43d3e1d3` converted the last four
  call sites to `ILogger`: `JanusAdminClient.cs`, `JanusPeerCtlBatchSink.cs`,
  `VisibilityBatchSender.cs`, `VoiceVisibilityService.cs`.
- **The duplicate-handle and pending-join findings are filed** in `Docs/KnownDefects.md`
  (added by `d295323cf8`), as "Region crossing leaves a live voice handle in the previous
  region's room" and "Pending-join confirmation gives up for every listener, even when the
  batch lands". That commit also recorded the 2026-08-24 net10 verification outcomes in
  `Docs/voice/voice-moderation-design-brief.md`.
- This file's own header cites branch tip `5a25c65583`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `6743ac4e7c`.

---

# WebRTC voice / upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `5a25c65583`
**Span audited:** `81e5c2449d` (old merge base) → `cbdfba2811` (upstream/develop, 11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip = branch intent)
**Status:** read-only audit. Nothing changed, fixed or committed. This file is uncommitted
working-tree output.

## Method and its limits

`git diff` between the two revisions; per-file classification of changed lines into
ILogger-migration noise (`m_log`, `log4net`, `ILogger`, `LogManager`, `using` churn) versus
semantic, with blank lines excluded; then reading the surviving semantic lines and the
surrounding code at both revisions. API comparison is by reading declarations at each revision
and diffing implementation bodies.

**No region was booted and no voice session was established.** Section 6 exists because the
decisive questions are runtime ones. Anything not established is marked **NOT ESTABLISHED**.

The 11 upstream commits, for reference:

```
cbdfba2811  Feature/ilogger migration (#198)
a115734ff3  Feature/xunit tests (#197)
6180f40260  Fix HGInventoryAccessModule OutboundPermission default … Export bit (#187)
3aaf0aeecf  Fix parcel-for-sale classification … ForSale flag instead of SalePrice > 0 (#189)
1a742c8c01  Fix dead DenyIdentified/DenyTransacted estate-access enforcement chain (#188)
bee925f310  Phlox: guard the boot rez path (#196)
1ed957e7e7  Phlox: fire the RegionReady signal from StartProcessing (#195)
b01e85562e  Phlox: implement HasScript / guard OnGetScriptRunning (#194)
9baaf80c9b  Data.Model: Pomelo → Microting EF MySQL provider
27f222b84f  Normalize SQLite access on System.Data.SQLite
0914c8104a  Updated SDK and Runtime to dotnet10
```

---

## 1. Direct overlap

**Verified by:** `git diff --name-only` on both sides, sorted and intersected with `comm -12`;
then per-file classification of the **upstream** side of each intersecting file.

Upstream changed 797 files, the branch 123. The intersection is **19** files, of which **14** are
voice-relevant (the remaining five are `Directory.Build.props`, `Tranquillity.sln`, and three
unrelated csprojs).

| overlapping file | upstream +/− | upstream non-log, non-blank lines |
|---|---|---|
| `Janus/JanusAudioBridge.cs` | 14/13 | **0** |
| `Janus/JanusMessages.cs` | 4/3 | **0** |
| `Janus/JanusPlugin.cs` | 12/11 | **0** |
| `Janus/JanusRoom.cs` | 9/8 | **0** |
| `Janus/JanusViewerSession.cs` | 6/5 | **0** |
| `Janus/WebRtcJanusService.cs` | 27/27 | **0** |
| `WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs` | 30/29 | **0** |
| `WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.csproj` | 1/3 | 3 (TFM + package pins) |
| `WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs` | 17/15 | **0** |
| `LindenCaps/EstateChangeInfo.cs` | 4/4 | **0** |
| `CoreModules/World/Estate/EstateManagementModule.cs` | 17/17 | **0** |
| `CoreModules/World/Land/LandManagementModule.cs` | 31/24 | **9** (PR #189) |
| `CoreModules/World/Land/LandObject.cs` | 27/27 | **0** |
| `Services/LLLoginService/LLLoginService.cs` | 53/52 | **0** |

### Stated plainly

**For twelve of the fourteen, upstream's entire contribution is log lines.** Every file under
`Addons/os-webrtc-janus/` that both sides touched — all nine of them — was touched by upstream
*only* to migrate `m_log`/`_log` from log4net to `ILogger`. Upstream made no behavioural change
anywhere in the voice addon. The same is true of `LandObject.cs`, `EstateManagementModule.cs`,
`EstateChangeInfo.cs` and `LLLoginService.cs`.

The merged result preserves both intents by construction in those twelve: the branch supplied
the behaviour, upstream supplied the call-shape, and the rebase resolution (audited separately in
this directory) took branch behaviour with upstream's converted call. There is no case among them
where the two sides expressed competing intent about behaviour.

The two exceptions:

- **`WebRtcVoiceRegionModule.csproj`** — upstream dropped the project-local `net8.0` TFM and moved
  package pins. Branch intent (its own package/reference set) and upstream intent (net10 + pins)
  are orthogonal and both survive; the project builds at net10.0 today.
- **`LandManagementModule.cs`** — the only file in the entire voice surface where upstream changed
  behaviour. That is PR #189 and it is the subject of §3.

---

## 2. API surface

**Verified by:** extracting the OpenSim namespaces and members the voice code references
(`grep` over all 34 `.cs` files under `Addons/os-webrtc-janus/`), then diffing every core type
named, across the span, with the log filter applied.

### 2.1 What the voice code binds to

Namespace usage across the voice tree: `OpenSim.Framework` (16 files),
`OpenSim.Region.Framework.Scenes` (5), `OpenSim.Region.Framework.Interfaces` (5),
`OpenSim.Server.Base` (2), `OpenSim.Framework.Servers.HttpServer` (2),
`OpenSim.Services.Base` (1), `OpenSim.Server.Handlers.Base` (1),
`OpenSim.Framework.Monitoring` (1).

| category | concrete members used |
|---|---|
| scene | `Scene.RegionInfo`, `.EventManager`, `.Permissions.IsAdministrator`, `.GetScenePresences`, `LandChannel.GetLandObject` |
| presence | `ScenePresence` position/agent identity; `ControllingClient.SessionId` (generation token) |
| parcel/land | `ILandObject.LandData`, `.GlobalID`, `.Name`, `.OwnerID`, `.GroupID`, `.IsGroupOwned`, `.Flags`, `.ParcelAccessList`, `.IsBannedFromLand`, `.IsRestrictedFromLand` |
| estate | `EstateSettings.AllowVoice`, `.IsBanned`, `.IsEstateManagerOrOwner`, `.TaxFree` |
| client | `IClientAPI` (identity + `SessionId`); **no** `LLClientView` reference anywhere |
| caps | `EventManager.OnRegisterCaps`, `Caps`, `ISimulatorFeaturesModule.AddFeature` |
| lifecycle | `ISharedRegionModule` / `INonSharedRegionModule` (`Initialise`, `PostInitialise`, `AddRegion`, `RegionLoaded`, `RemoveRegion`, `Close`) |
| threading | `WorkManager.StartThread`, `Watchdog.UpdateThread` / `.RemoveThread` |

### 2.2 Signature and semantics across the span

| core file | +/− | non-log, non-blank | verdict |
|---|---|---|---|
| `Framework/EstateSettings.cs` | 3/1 | **0** | ILogger only |
| `Framework/LandData.cs` | 0/0 | 0 | **unchanged** |
| `Framework/IClientAPI.cs` | 0/0 | 0 | **unchanged** |
| `Framework/Capabilities/Caps.cs` | 0/0 | 0 | **unchanged** |
| `Scenes/Scene.cs` | 137/138 | 2 (both `string.Format(…)` continuation lines of log calls) | ILogger only |
| `Scenes/EventManager.cs` | 113/113 | **0** | ILogger only |
| `Scenes/ScenePermissions.cs` | 0/0 | 0 | **unchanged** |
| `Scenes/ScenePresence.cs` | 108/100 | **6** | **behaviour changed — see §4** |
| `CoreModules/World/Land/LandObject.cs` | 27/27 | **0** | ILogger only |
| `CoreModules/World/Land/LandChannel.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/ILandObject.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/ILandChannel.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/IRegionModuleBase.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/ISharedRegionModule.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/INonSharedRegionModule.cs` | 0/0 | 0 | **unchanged** |
| `Interfaces/ISimulatorFeaturesModule.cs` | 0/0 | 0 | **unchanged** |
| `Monitoring/Watchdog.cs` | 0/0 | 0 | **unchanged** |
| `Monitoring/JobEngine.cs` | 0/0 | 0 | **unchanged** |
| `WorkManager.cs` | 0/0 | 0 | **unchanged** |
| `Framework/Util.cs` | 23/21 | **4** | behaviour changed, not used by voice |

### 2.3 Signature-stable, behaviour-changed cases — flagged explicitly

Two exist in the whole span. **Neither is in the voice call path**, but both are exactly the class
the brief asked to surface.

**(a) `ScenePresence.Dispose(bool disposing)`** — identical signature, changed behaviour.
Upstream added an early return on the finalizer path:

> `if (!disposing) return;` — placed *after* `disposed = true`, so on finalization the method now
> skips `IsDeleted = true`, `RemoveFromPhysicalScene()`, `KnownRegions = null`,
> `m_scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd`, `RemoveClientEvents()`, and
> the `Animator`/`Appearance`/`ControllingClient` null-outs.

The rationale is sound (touching managed objects from a finalizer can throw, and an escaping
finalizer exception kills the process). The consequence is that a `ScenePresence` reaching the
finalizer *without* a prior `Dispose()` no longer unsubscribes its scene events. The public
`Dispose()` path calls `Dispose(true)` and is unaffected. See §4 for why this does not reach the
voice teardown.

**(b) `Util.Decompress(string)` and `Util.GetOSDMap(Stream, int)`** — identical signatures;
`stream.Read(…)` replaced by `stream.ReadExactly(…)` in both. This is a genuine correctness fix
(`Read` may return fewer bytes than requested; `ReadExactly` loops), and it changes behaviour for
short reads. **Verified the voice code calls neither** — a grep for `Util.Decompress`,
`Util.GetOSDMap` and related helpers across `Addons/os-webrtc-janus/` returns nothing.

Everything else the voice code binds to is either literally unchanged (13 files at 0/0) or
logger-migration only.

---

## 3. Land and parcel specifically

### 3.1 PR #189 — parcel for-sale classification

**What changed.** One condition, in `LandManagementModule.cs`:
`currentParcelLandData.SalePrice > 0` became
`(currentParcelLandData.Flags & (uint)ParcelFlags.ForSale) != 0`.
Rationale (verbatim from the in-code comment): every parcel defaults to `SalePrice = 0`, so the
old test could not distinguish "not for sale" from "for sale at no cost", and a genuinely free
parcel with `ForSale` set never rendered as purchasable.

**Where it landed.** Verified by locating the changed line and walking back to the enclosing
method: it is inside **`SendParcelOverlay(IClientAPI remote_client)`**, in the loop that builds
the per-`LandUnit` overlay byte, selecting `LandChannel.LAND_TYPE_IS_FOR_SALE` for the minimap
colour. `git show --stat 3aaf0aeecf` confirms the commit touches that one file and nothing else.

**Does it change parcel state or lookup?** **No.** It is a read-only classification computed
into a byte array sent to the viewer. It mutates no `LandData`, registers no parcel, and does not
touch `GetLandObject`, the parcel index, or any access decision.

**Does the visibility matrix depend on it?** **No**, and this is verified rather than assumed.
Every `ParcelFlags` bit the voice code reads was enumerated:

- `Visibility/FeederWorld.cs:52` — `AllowVoiceChat`
- `WebRtcVoiceRegionModule/FeederWorldFromScene.cs:94` — `AllowVoiceChat`
- `WebRtcVoiceRegionModule/LandBan.cs:42` — `UseBanList`
- `WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:502` — `UseEstateVoiceChan`

The voice tree contains **no** reference to `ParcelFlags.ForSale`, `SalePrice`, `AuthBuyerID`,
`LAND_TYPE_IS_FOR_SALE`, or `SendParcelOverlay`. The matrix resolves parcels via
`LandChannel.GetLandObject` and then reads voice/ban flags. #189 and the matrix are disjoint.

### 3.2 PR #188 — estate access enforcement

**What changed.** Verified from `git show 1a742c8c01`: the commit touches **one file,
`LLClientView.cs`**, not the estate modules. Two changes, both about what the *viewer* is told:

1. In `GetRegionFlags()`, `RegionFlags.DenyIdentified` and `RegionFlags.DenyTransacted` are now
   folded in from `EstateSettings` — previously commented out as "unused", so the bits were never
   set regardless of estate configuration.
2. In the RegionInfo capability packet, `RegionDenyIdentified` / `RegionDenyTransacted` now report
   the real bits instead of being hardcoded `false`.

**Does it change estate state the matrix uses?** **No.** It changes neither `EstateSettings`
storage nor any server-side gate. The estate-derived inputs the matrix consults are
`EstateSettings.AllowVoice`, `.IsBanned`, `.IsEstateManagerOrOwner`, `.TaxFree`, plus
`Scene.Permissions.IsAdministrator` — enumerated from
`FeederWorld.cs:88/90`, `FeederWorldFromScene.cs:115/116`, `VoiceModerationAuth.cs:28`,
`WebRtcVoiceRegionModule.cs:453/494`. None is `DenyIdentified` or `DenyTransacted`.

The voice tree contains **no** reference to `LLClientView`, `GetRegionFlags`, `RegionFlags`,
`DenyIdentified` or `DenyTransacted`. Additionally `EstateSettings.cs` itself is logger-only
across the span (3/1, zero semantic lines), so the properties the matrix does read are untouched.

**One second-order note, offered as information not as a finding.** #188 makes two
previously-dead estate restrictions actually reach viewers. If an estate has `DenyIdentified` or
`DenyTransacted` configured, the *population admitted to the region* may change after this
upgrade — and the matrix derives from whoever is present. That is a change in inputs, not in
matrix logic, and it is the intended effect of the security fix.

### 3.3 The ban path

`LandBan.cs` deliberately re-implements `LandObject.IsBannedFromLand`'s exemption chain and its
private `IsBannedFromLand_inner` ban-list scan (its header comments cite `LandObject.cs:826`),
minus the TaxFree line. That is a copy of core logic, so it is exposed to drift by construction.
**Verified `LandObject.cs` is logger-only across this span (27/27, zero semantic lines)** — the
mirrored logic did not move. Worth re-checking on any future upstream merge that does touch
`LandObject.cs`.

---

## 4. Presence and teardown

**Verified by:** locating every `TriggerClientClosed` call site and diffing it across the span;
diffing `EventManager.cs` and `ScenePresence.cs`.

### 4.1 Client-close ordering — unchanged

`EventManager.OnClientClosed` is fired from exactly one place:
`Scene.cs:3864` — `m_eventManager.TriggerClientClosed(agentID, this);` inside the client-removal
path, dispatched by `EventManager.cs:2032`.

Diffing both files across the span for any line mentioning `TriggerClientClosed`,
`RemoveClient`, `ClientClosed` or `CloseAgent` yields **only commented-out log lines**
(`// m_log.Debug(…)` → `// m_log.LogDebug(…)`, and two commented `ErrorFormat` → `LogError`).
`EventManager.cs` has **zero** semantic changed lines overall; `Scene.cs` has two, both
continuation arguments of log calls.

**Conclusion: upstream did not change client-close ordering, the event's firing point, or its
dispatch semantics.** The branch's `OnClientClosed` wiring, generation-token capture and
`ClosingSessions` park-and-retry rest on an unchanged foundation.

### 4.2 Presence lifecycle — one real change, outside the voice path

The `ScenePresence.Dispose(bool)` finalizer change described in §2.3(a) is the only presence
lifecycle change in the span. Its relevance to the branch's teardown:

- The branch hooks `EventManager.OnClientClosed`, which fires from `Scene.RemoveClient`, **not**
  from `ScenePresence` finalization. That path is unchanged.
- Normal presence teardown calls the public `Dispose()`, which invokes `Dispose(true)` and
  `GC.SuppressFinalize(this)` — the `!disposing` branch is not taken.
- Reaching the finalizer means nobody called `Dispose()`, which is already an abnormal path.

**No dependency of the branch's teardown is affected.** The change is nonetheless worth recording
because it is silent and lifecycle-shaped: after it, a leaked `ScenePresence` retains its
`OnRegionHeartbeatEnd` subscription and client event handlers instead of shedding them at
finalization. That is an upstream concern, not a voice one.

### 4.3 Scene teardown

`Scene.cs` carries no semantic change (§2.2). `RemoveRegion`/`Close` on the module interfaces are
literally unchanged (`IRegionModuleBase.cs`, `ISharedRegionModule.cs`,
`INonSharedRegionModule.cs` all 0/0). The branch's `RemoveRegion` (which stops the per-region
visibility service) and `Close` (which disposes sinks) bind to an unchanged contract.

---

## 5. Threading and lifecycle

**Verified by:** diffing the threading infrastructure across the span; reading the feeder's guard
and its driver; tracing module init order through the source at HEAD.

### 5.1 Threading infrastructure — literally unchanged

`Watchdog.cs`, `JobEngine.cs` and `WorkManager.cs` are all **0/0** across the span — not one line
changed, not even a log line.

### 5.2 The feeder's single-thread invariant holds

`VoiceStateFeeder` captures `_tickThreadId` on first tick (`RecordAndCheckTickThread`,
`VoiceStateFeeder.cs:106`) and asserts every later mutation is on that thread. The header
comment is explicit that `Debug.Assert` compiles out in Release and that the id capture itself is
cheap and always runs, so `TickThreadId` remains observable in Release even though the assertion
does not fire.

The invariant is upheld structurally by the driver, not by luck.
`VoiceVisibilityService.cs:5–7` records the design decision verbatim: it drives `Tick()` on **one
dedicated named background thread**, and is "deliberately NOT a `System.Threading.Timer`" because
pool callbacks hop threads and would trip the guard. The thread is created via
`WorkManager.StartThread` (`:110`), registers with `Watchdog.UpdateThread()` each pass (`:180`)
and `Watchdog.RemoveThread()` on exit (`:184`).

Since `WorkManager` and `Watchdog` are byte-identical across the span, **nothing upstream can have
perturbed the feeder's threading assumptions.** The Release-mode compile-out is a pre-existing
property of the branch's own design, not something the migration introduced.

### 5.3 Module init order and caps registration

Traced through the source at HEAD:

1. `OpenSimBase.cs:437` → `controller.AddRegionToModules(scene)`
2. → `RegionModulesControllerPlugin.cs:488` / `:493` → `module.RegionLoaded(scene)`
3. `OpenSimBase.cs:453` → `scene.SetModuleInterfaces()`
4. later, `OpenSimBase.cs:803` / `OpenSim.cs:745` → `scene.Start()`
5. → `Scene.cs:1625` → `StartScripts()` → `Scene.Inventory.cs:94` → `engine.StartProcessing()`

`WebRtcVoiceRegionModule` does its per-region setup in `RegionLoaded` (`:146`): it subscribes
`OnRegisterCaps` at `:150` and starts the visibility service at `:177`. That is **step 2**,
strictly before **step 5**.

This matters because of PR **#195**, which is the only sequencing change in the span: Phlox's
`StartProcessing` now fires `TriggerEmptyScriptCompileQueue` unconditionally, releasing the
`RegionReady` login lock that previously (on a Phlox-only region) was never released. Logins can
therefore open earlier than before.

**Nothing in the voice path depends on that ordering.** Caps registration and the feeder are both
live at step 2; the RegionReady signal cannot fire before step 5. Firing it *earlier than never*
cannot expose voice to an uninitialised state.

The module interfaces themselves (`IRegionModuleBase`, `ISharedRegionModule`,
`INonSharedRegionModule`) are unchanged, so `Initialise`/`PostInitialise`/`AddRegion`/
`RegionLoaded` retain their contract.

---

## 6. Runtime verification plan

### 6.0 The finding that must be read first: log4net call sites are silent

This is a static conclusion, reached while preparing this section, and it changes what the
runtime checks can tell you.

**The RegionServer logging pipeline is ILogger → console + Serilog file, and nothing else.**
`Program.cs:169–172`: `loggingBuilder.ClearProviders()`, then
`AddOpenSimLogging("OpenSim.Server.RegionServer", logPath)`, then
`LoggerProvider.LoggerFactory = …`. `AddOpenSimLogging`
(`OpenSimLoggingBuilderExtensions.cs:30–52`) adds `AddOpenSimConsole()` and
`AddSerilog(fileLogger)`, where the file sink is:

- path `Path.Combine(logPath, "OpenSim.Server.RegionServer.log")`
- `rollingInterval: RollingInterval.Day`, `shared: true`
- `restrictedToMinimumLevel: LogEventLevel.Debug` — so **`LogDebug` does reach the file**;
  anything below Debug (`LogTrace`) does not
- formatted by `OpenSimLog4NetStyleFormatter`, deliberately emitting log4net-style lines so
  existing log parsing keeps working

**But log4net itself is never configured.** Verified exhaustively:

- The only `XmlConfigurator.Configure` call in the tree is inside
  `Log4NetBootstrapper.Configure` (`OpenSim.Server.Base/Hosting/Log4NetBootstrapper.cs:29`).
- `Log4NetBootstrapper` / `ILog4NetBootstrapper` have **no callers, no DI registration and no
  injection sites** — a repo-wide grep returns only their own two declaration files.
- There is **no** assembly-level `[assembly: log4net.Config.XmlConfigurator]` attribute anywhere.
- There is **no** `log4net.config` or `*.exe.config` in the tree (all `*log4net*` hits are DLLs
  or the three source files).
- There is **no** programmatic `BasicConfigurator` / `AddAppender` bootstrap.
- `Microsoft.Extensions.Logging.Log4Net.AspNetCore` *is* referenced by the RegionServer csproj
  (`:93`), but **`AddLog4Net` is never called** — so no ILogger→log4net bridge is installed either.

`ServerBase.RegisterCommonAppenders` enumerates `LogManager.GetRepository().GetAppenders()`
looking for one named `"Console"`; with no configuration that enumeration is empty.

**Consequence.** Four voice files still declare a log4net `ILog` rather than an `ILogger`, and
their output goes to a repository with no appenders — i.e. nowhere:

```
WebRtcVoiceRegionModule/VoiceVisibilityService.cs   (feeder thread lifecycle, tick errors)
WebRtcVoiceRegionModule/VisibilityBatchSender.cs    (the emit path)
WebRtcVoiceRegionModule/JanusPeerCtlBatchSink.cs    (the Janus admin sink)
Janus/JanusAdminClient.cs                           (admin transport + auth outcomes)
```

That is precisely the `peer_ctl_batch` emit chain — the newest and least-proven voice code —
and it is exactly where the never-throw guards report failures. The remaining eleven voice files
use `ILogger` and will log normally.

**NOT ESTABLISHED:** this is inferred from the absence of any configuration path, not observed at
runtime. Step 1 below is designed to confirm or refute it directly, and should be treated as the
test of this claim rather than an assumption built on it.

### 6.1 Step 1 — Does voice logging reach the file, and are the four files silent?

Boot one region with voice enabled. Then:

```
# ILogger side — expect hits
findstr /C:"[REGION WEBRTC VOICE]" <logPath>\OpenSim.Server.RegionServer.log
findstr /C:"[WEBRTC VOICE SERVICE MODULE]" <logPath>\OpenSim.Server.RegionServer.log
findstr /C:"[JANUS WEBRTC SERVICE]" <logPath>\OpenSim.Server.RegionServer.log

# log4net side — expect NOTHING if §6.0 is correct
findstr /C:"[VOICE VISIBILITY]" <logPath>\OpenSim.Server.RegionServer.log
findstr /C:"[VISIBILITY BATCH SENDER]" <logPath>\OpenSim.Server.RegionServer.log
findstr /C:"[JANUS ADMIN]" <logPath>\OpenSim.Server.RegionServer.log
```

(Confirm the exact header strings from each file's `LogHeader` constant before grepping.)

- **Expected if §6.0 holds:** the first three return lines including `[REGION WEBRTC VOICE]: enabled`;
  the last three return nothing at all, on both console and file.
- **If the last three DO return lines:** §6.0 is wrong — something configures log4net that I did
  not find. Say so; the rest of this plan is unaffected.
- **If the first three return nothing:** a much bigger problem — `LoggerProvider.LoggerFactory`
  is not reaching addon assemblies. Check that the `DeferredLogger` rebind
  (`DeferredLogger.cs:40–46`, which rebinds when the factory reference changes) actually fired.

This step is cheap, needs no viewer, and settles the single most consequential open question.

### 6.2 Step 2 — Module load and caps registration

With `[WebRtcVoice] Enabled = true`:

- **Observe:** `[REGION WEBRTC VOICE]: enabled` and
  `[WEBRTC VOICE SERVICE MODULE] WebRtcVoiceService enabled` in the log; then, on a viewer login,
  `OnRegisterCaps called with agentID … in scene …` (`WebRtcVoiceRegionModule.cs:248`).
- **Fail:** module never announces enable (config not read, or plugin discovery failed), or caps
  never register (the `RegionLoaded` subscription at `:150` did not run).

### 6.3 Step 3 — Provision a voice session end to end

Log a viewer in and join voice.

- **Observe:** `ProvisionVoiceAccountRequest` progressing through to a `jsep` answer and a
  `viewer_session` in the response; the Janus room being created or reused (`CreateRoom … ReturnCode`,
  or the 486 "already exists. Reusing!" path).
- **Fail:** `voice service not loaded`; `voice_server_type is not 'webrtc'`;
  `JoinRoom failed (error_code=…)`; or a 409 `ERROR_CHANNEL_FULL` when the room is not actually full.

### 6.4 Step 4 — Close/teardown, the branch's own new machinery

Log the viewer out (and separately, kill the client abruptly).

- **Observe:** `Event_OnClientClosed: captured N voice session(s) for <agent> in <scene>`
  followed by `voice-session teardown complete (client close <agent>): session … agent …`.
- **Watch for:** `prior teardown for … still pending/failed (session …, age Ns) - retrying` —
  that is the `ClosingSessions` park-and-retry working as designed; it is informative, not a
  failure, unless the age climbs without ever completing.
- **Watch for:** `provision for …: could not capture client SessionId (scene …, presence …)` —
  the generation token failed to capture, which makes that session sweepable by any close for the
  agent. The branch's own comment says this should not happen in practice.
- **Note:** the teardown-failure logger uses `LogWarning(exception, message)`, so on failure you
  should see the full exception (type, message, stack, inners) attached to the entry, not just
  `e.Message`.

### 6.5 Step 5 — The mixer admin API at `http://localhost:24225/voiceAdmin`

This is the Janus **Admin API** endpoint, configured as `[JanusWebRtcVoice] JanusGatewayAdminURI`,
with `AdminAPIToken` as the secret and `AdminTimeoutMs` (default 5000) as the per-send deadline.
It is used by `JanusAdminClient` to POST `message_plugin` envelopes carrying the
`peer_ctl_batch` request produced by `PeerCtlBatchSerializer`.

Two protocol facts, both verified in-code against Janus 0.7.0-debug per the source comments, that
determine what you should look for:

- **Authentication is `admin_secret` in the message BODY.** `apisecret` is rejected with 403
  (`JanusAdminClient.cs:55–56`).
- **Janus returns API-level errors as HTTP 200 with `janus:"error"`.** So a *wrong secret*
  surfaces as `ProtocolError`, not `TransportError`; only a genuine non-2xx (proxy down, Janus
  down) is `TransportError` (`Interpret`, `JanusAdminClient.cs:147–152`).

**Read-only probe.** Send a no-op batch — an empty `excl` map changes no mixer state — to prove
reachability, auth and plugin dispatch in one call:

```
curl -sS -X POST http://localhost:24225/voiceAdmin \
  -H "Content-Type: application/json" \
  -d '{
        "janus": "message_plugin",
        "transaction": "audit-probe-1",
        "admin_secret": "<AdminAPIToken>",
        "plugin": "janus.plugin.slvoice",
        "request": { "request": "peer_ctl_batch", "op": "replace", "excl": {} }
      }'
```

(`op` must be one of `add` / `remove` / `replace` — `PeerCtlBatchSerializer.OpString`. Use the
plugin name configured as `[JanusWebRtcVoice] PluginName`; the mixer is `janus.plugin.slvoice`,
the stock default is `janus.plugin.audiobridge`.)

- **Pass:** HTTP 200 with `"janus": "success"`, and the plugin's acknowledgement
  `{"slvoice":"applied"}` nested in the response.
- **Auth failure:** HTTP 200 with `"janus": "error"` and a 403 in the body — this is the wrong-secret
  signature, and it is what a misconfigured `AdminAPIToken` looks like.
- **Transport failure:** any non-2xx, or a connection refusal — Janus not running, or
  `JanusGatewayAdminURI` pointing at the wrong port. Note 24225 is the *admin* port; the client
  API port is different, so a working voice session does not prove the admin port is reachable.

**Then exercise the real emit path.** With `[WebRtcVoice] VisibilityFeederEnabled = true` and
`VisibilityEmitEnabled = true`, put two avatars in the region on parcels with differing
`AllowVoiceChat` / ban state, and confirm the mixer receives non-empty `peer_ctl_batch` requests
(observe on the Janus side — `janus.plugin.slvoice` logging, or an admin-API session dump).

**Critically, given §6.0:** if the sink fails, `VisibilityBatchSender` and `JanusPeerCtlBatchSink`
will report it through log4net and **you will see nothing in the region log**. Until that is
fixed, the mixer side is the only place the emit path is observable. Do not read an empty region
log as evidence that emission succeeded.

### 6.6 Step 6 — Feeder thread health

- **Observe:** the feeder thread present in `show threads` on the region console, heart-beating
  (it calls `Watchdog.UpdateThread()` each pass).
- **Fail:** a Watchdog alarm for the feeder thread means the tick loop stopped — but note the
  loop's own error reporting is in `VoiceVisibilityService`, i.e. log4net, i.e. currently silent.
  The Watchdog alarm itself comes from core (`ILogger`) and will appear.
- Release builds compile out the `Debug.Assert` single-thread guard. If you want that invariant
  actually checked, run a Debug build for this step; `TickThreadId` is observable in both.

### What this plan does not cover

- Whether the four log4net files are genuinely silent is *tested* by Step 1, not assumed — but if
  Step 1 shows them silent, this plan cannot then tell you what those files would have reported.
- The SQLite provider swap and Pomelo→Microting migration are outside the voice path (voice
  persists nothing) and are not exercised here.
- Multi-region and cross-region voice handoff are not covered.

---

## Summary of items needing attention

| # | Item | Severity | Affects this branch? |
|---|---|---|---|
| 1 | log4net has **no configured appenders** (bootstrapper never called, no attribute, no config file, no bridge). Four voice files log via `ILog` and appear to be silent — including the whole `peer_ctl_batch` emit chain and its failure reporting | **High** | Yes — directly. Confirm with §6.1, then convert those four to `ILogger` |
| 2 | Runtime verification never performed; §6.1 is cheap and settles item 1 | **Do before deploy** | Yes |
| 3 | `ScenePresence.Dispose(bool)` — signature-stable behaviour change on the finalizer path (§2.3a) | Informational | No — branch hooks `OnClientClosed`, not finalization |
| 4 | `Util.Decompress` / `GetOSDMap` `Read`→`ReadExactly` (§2.3b) | Informational | No — voice calls neither |
| 5 | `LandBan.cs` re-implements `LandObject`'s ban logic by hand; safe this span (`LandObject.cs` logger-only) but exposed to future drift (§3.3) | Low, latent | Re-check on any merge touching `LandObject.cs` |
| 6 | #188 makes previously-dead `DenyIdentified`/`DenyTransacted` reach viewers, which can change who is admitted and therefore the matrix's input population (§3.2) | Informational | Indirect only |

**PR #189 does not affect the visibility matrix, and neither does PR #188.** Upstream's
contribution to every voice file in the overlap is log lines only. The physics of the merge are
sound; the open risk is the logging pipeline in item 1 and the fact that none of it has been run.
No fixes were made.
