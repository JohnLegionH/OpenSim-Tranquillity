**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **No finding in this audit has changed.**
- **The upstream endpoint has moved on.** The span audited ends at `cbdfba2811`; upstream has
  since advanced to `93765a999e` (2026-08-24, moving the libOMV packages to NuGet) — exactly
  one commit. Nothing in this audit was re-checked against it.
- This file's own header cites branch tip `5a25c65583`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `6743ac4e7c`.

---

# Experience — upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `5a25c65583`
**Span audited:** `81e5c2449d` → `cbdfba2811` (11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip = branch intent)
**Status:** read-only audit. Nothing changed, fixed or committed. Uncommitted working-tree output.

## Method and its limits

`git diff` between revisions; per-file classification of changed lines into ILogger-migration
noise versus semantic (blank lines excluded); reading interface declarations and call sites at
both revisions; SHA-256 of file contents to prove identity. No region was booted and no database
was touched. Anything not established is marked **NOT ESTABLISHED**.

---

## 1. Position

**Verified by:** `git merge-base`, `git log -1` on the base commit, and a path-filtered diff of the
11-commit span.

The framing in the brief is *substantively* right but needs **two factual corrections**.

### 1.1 `81e5c2449d` is the Experience commit — confirmed

```
81e5c2449d5808f517ea5ef45758cdbba3d5c879
Experience: SL conformance for script surface, consent, admission, and caps (#184)
```

Confirmed exactly as stated.

### 1.2 It is no longer the merge base — `cbdfba2811` is

`git merge-base HEAD cbdfba2811` returns **`cbdfba2811`**, not `81e5c2449d`.

This is a consequence of the rebase, not a contradiction of the brief: `81e5c2449d` *was* the merge
base while the branch sat at `de94534257`. Now that the 80 commits have been replayed onto
`cbdfba2811`, upstream is an ancestor of HEAD and is therefore the merge base itself.

The correction matters only for how one phrases the position. The substance is *more* favourable
than the brief assumes: Experience (#184) sits below the branch, and so do all 11 upstream commits.
Nothing above Experience is unmerged.

### 1.3 Seven Experience files *were* modified by the 11 commits — but mechanically

The brief's expectation that "no commit in the 11 above it modifies Experience code" is **false as
literally stated**. Seven Experience-pathed files were touched:

| file | +/− | non-log, non-blank |
|---|---|---|
| `LindenCaps/ExperienceModule.cs` | 26/25 | **0** |
| `CoreModules/ServiceConnectorsOut/Experience/LocalExperienceServiceConnector.cs` | 9/8 | **0** |
| `CoreModules/ServiceConnectorsOut/Experience/RemoteExperienceServiceConnector.cs` | 6/4 | **0** |
| `Server.Handlers/Experience/ExperienceServerPostHandler.cs` | 6/5 | **0** |
| `Services.Connectors/Experience/ExperienceServicesConnector.cs` | 13/12 | **0** |
| `Services.ExperienceService/ExperienceService.cs` | 4/4 | **0** |
| `Services.ExperienceService/OpenSim.Services.ExperienceService.csproj` | 1/2 | 3 |

**Every one of the six `.cs` files is logger-migration only — zero semantic lines.** The csproj's
three lines are the removal of the project-local `net8.0` TargetFramework and a
`System.Configuration.ConfigurationManager` 10.0.8 → 10.0.10 bump.

Only **two** of the 11 commits touched Experience at all, confirmed by
`git log --oneline 81e5c2449d..cbdfba2811 -- '*Experience*'`:

- `cbdfba2811` — Feature/ilogger migration (#198)
- `0914c8104a` — Updated SDK and Runtime to dotnet10

So the *intent* of the brief's claim holds: **no upstream commit changed Experience behaviour.**
The claim is worth restating precisely, because "untouched" and "touched only mechanically" have
different implications for a future merge — the files *do* carry upstream edits, so a future
cherry-pick or revert in this area is not conflict-free by default.

---

## 2. What this branch touches

**Verified by:** path-filtering the branch's 123 changed files for `experience`; then grepping
every changed `.cs` for the word and inspecting each hit; then checking whether the branch's own
diff hunks touch any Experience line.

### The branch touches no Experience code. At all.

`git diff --name-only 81e5c2449d..de94534257 | grep -i experience` returns **nothing**. No file
under any Experience path — service, connectors, handlers, caps module, data layer, interfaces —
is modified by this branch.

Five branch-modified files *mention* the word. All five were inspected and none is a dependency:

| file | nature of the mention |
|---|---|
| `Services.MembershipService/MembershipServiceBase.cs` | comment: "Byte-for-byte the ExperienceServiceBase pattern." |
| `CoreModules/…/Membership/LocalMembershipServiceConnector.cs` | comment: "Mirrors LocalExperienceServicesConnector…" |
| `CoreModules/…/Membership/RemoteMembershipServiceConnector.cs` | same pattern reference |
| `PhysicsModules.LegionJolt/LegionJoltScene.cs` | comment: "Mirrors the house idiom (ExperienceModule.WrongConsoleScene)." |
| `CoreModules/PluginRegistration.cs` | contains the two pre-existing Experience connector registrations |
| `CoreModules/World/Estate/EstateManagementModule.cs` | contains pre-existing `EstateExperienceDeltaRequest` / `handleEstateExperienceDeltaRequest` |

For the last two — the only ones containing real Experience code — I checked the branch's diff
hunks directly. Filtering the branch-side diff of each file for lines matching `experience`
returns **nothing** in both cases. `PluginRegistration.cs` gains three *Membership* registrations
placed alongside the untouched Experience ones; `EstateManagementModule.cs`'s 100 semantic branch
lines are the estate-CAP nullable work and do not reach the Experience delta-request code.

The Membership service is, by its own comments, modelled on the Experience service — that is a
design lineage, not a runtime coupling.

### What that means for the rest of this audit

The usual question ("did the merge preserve both intents?") **does not arise**: there is only one
intent in Experience, and it is upstream's. Sections 3–5 therefore ask a narrower question —
whether Experience, which the branch inherits unmodified, still works correctly on the new base.
Section 6 is scoped accordingly.

---

## 3. Indirect exposure

**Verified by:** reading `ExperienceModule`'s lifecycle methods and every `IScriptModule` usage;
diffing `IScriptModule.cs`; checking whether upstream touched `PostScriptEvent`; and tracing the
module-controller dispatch order.

### 3.1 Experience does bind to the script engine

`ExperienceModule.cs:31` declares `private IScriptModule[] m_ScriptModules`, populated at
**line 106** inside `RegionLoaded` via `scene.RequestModuleInterfaces<IScriptModule>()`.

**The only member it ever calls is `PostScriptEvent`** — at line 685, delivering
`"experience_permissions_denied"` to the requesting script. Grepping the module for other calls
on those instances turns up nothing else.

Exposure to the three Phlox PRs:

- **#194 (`HasScript` / `OnGetScriptRunning`)** — Experience calls neither. The two methods #194
  changed are not in its surface.
- **#195 (RegionReady from `StartProcessing`)** — grepping every Experience file for
  `RegionReady`, `StartProcessing`, `EmptyScriptCompileQueue` and `TriggerEmptyScript` returns
  **nothing**. Experience does not observe, wait on, or depend on that signal.
- **#196 (boot rez guard in `PhloxScriptLoader`)** — Experience does not reference the loader.

And the one member it does use is stable: `IScriptModule.cs` is **0/0 across the span** (literally
unchanged), and filtering `PhloxEngine.cs`'s upstream diff for `PostScriptEvent` returns nothing —
upstream's 37 semantic lines in that file (#194 and #195) do not touch it. `IScriptModule.PostScriptEvent`
is declared at `IScriptModule.cs:70` as `bool PostScriptEvent(UUID itemID, string name, Object[] args)`
at both revisions.

### 3.2 Module init order — safe by construction, and unchanged

There is one genuine ordering coupling: Experience captures the script-engine array in
`RegionLoaded`, so the engines must have registered themselves by then. Verified that this holds
structurally rather than by luck:

- Both engines register in **`AddRegion`** — `PhloxEngine.cs:102` (method at line 98) and
  `XMREngine.cs:289` (method at line 280), each calling
  `m_Scene.RegisterModuleInterface<IScriptModule>(this)`.
- `RegionModulesControllerPlugin.cs` dispatches **all** `AddRegion` passes (lines 337, 412, 435,
  472) before **any** `RegionLoaded` pass (lines 488, 493).

So every engine is registered before Experience's `RegionLoaded` runs. Upstream changed neither
registration site (filtering the upstream diff of both engine files for `RegionModuleInterface`
returns nothing) nor the dispatcher.

### 3.3 Caps registration

`ExperienceModule` subscribes `scene.EventManager.OnRegisterCaps += RegisterCaps` at line 110, also
in `RegionLoaded`, and unsubscribes in `RemoveRegion` (line 89). `EventManager.cs` has **zero**
semantic changes across the span, and `Caps.cs` is 0/0. Since `RegionLoaded` precedes
`scene.Start()` → `StartScripts()` → `StartProcessing()`, #195's earlier login unlock cannot
precede Experience's caps registration.

**Conclusion: none of #194–196 disturbs Experience.**

---

## 4. Persistence

**Verified by:** reading `ExperienceServiceBase`'s data-layer acquisition; reading
`MySqlExperienceData`'s provider imports; searching for a SQLite Experience DAL; reading
`OpenSimCoreContextFactory`'s declared interface; and listing the six Pomelo→Microting files.

### 4.1 Experience persists through raw ADO, not EF

`ExperienceServiceBase.cs:45` — `m_Database = LoadPlugin<IExperienceData>(dllName, …)`. The
implementation is `Source/OpenSim.Data.MySQL/MySQLExperienceData.cs`, declared
`public class MySqlExperienceData : MySqlFramework, IExperienceData`, using **`MySqlConnector`**
with hand-built `MySqlCommand` objects against tables like `experience_permissions`.

**There is no SQLite Experience DAL** — `find Source/OpenSim.Data.SQLite -iname "*experience*"`
returns nothing. Experience is **MySQL-only**.

**Therefore the SQLite provider swap does not reach Experience at all.** (It reaches Phlox's state
store and NPC persistence; see `phlox-upstream-audit.md`.)

### 4.2 The EF model exists but is design-time only

There *is* an EF model: `OpenSim.Data.Model/Core/` holds `Experience.cs`, `ExperienceKVP.cs`,
`ExperiencePermission.cs`, `EstateAllowedExperience.cs` and `EstateKeyExperience.cs`, and
`OpenSimCoreContext` declares `DbSet<>`s for all five (lines 34–38, with `modelBuilder.Entity<>`
configuration from line 394).

So **yes, Experience entities are inside the context whose factory is among the six changed
files** — `OpenSimCoreContextFactory.cs` is one of them. But:

- That factory is declared
  `public class OpenSimCoreContextFactory : IDesignTimeDbContextFactory<OpenSimCoreContext>` —
  the EF Core **design-time** interface, used by `dotnet ef` tooling, not by the running server.
- Grepping the whole `Source` tree for `OpenSimCoreContext` outside `OpenSim.Data.Model/` returns
  **no runtime consumers**.
- The actual `.cs` change in that commit was a **comment**: "…use MySQL with Pomelo provider" →
  "…with the Microting (Pomelo fork) provider". The substantive change is in the csproj:
  `Pomelo.EntityFrameworkCore.MySql 9.0.0` → `Microting.EntityFrameworkCore.MySql 10.0.10`, plus
  dropping `Microsoft.VisualStudio.Web.CodeGeneration.Design` (the "unused scaffolding" of the
  commit message).

**So Pomelo→Microting affects Experience's design-time migration tooling only, not its runtime
persistence.**

### 4.3 What does reach Experience

The one shared exposure with the rest of the MySQL DAL: `OpenSim.Data.MySQL` bumped
**`MySqlConnector` 2.5.0 → 2.6.1** (and `ConfigurationManager` 10.0.8 → 10.0.10, and moved to
net10). `MySqlExperienceData` inherits `MySqlFramework` and rides that driver.

Also verified: **upstream changed no migration resources anywhere in the span**, so
`Source/OpenSim.Data.MySQL/Resources/Experience.migrations` and the `experience_*` schema are
byte-identical.

**NOT ESTABLISHED:** whether `MySqlConnector` 2.6.1 returns identical CLR types for the
`experience_*` columns as 2.5.0 did. This is the same open item recorded in
`land-estate-upstream-audit.md` §4.3 and has the same low expected risk, but it is unverified.

---

## 5. The Phlox↔Experience adapter

**Verified by:** locating the adapter, diffing it and both interfaces across the span, and reading
each of the six points at `cbdfba2811`.

`Source/Phlox.ScriptEngine/PhloxExperienceAdapter.cs` arrived with `02cf1370df` ("Add Phlox: LSL/SLua
compiler, VM, and region script engine (#182)") — **below** the merge base. It is **0/0 across the
span** (upstream never touched it) and byte-identical at `cbdfba2811` and HEAD
(`1f28b51da7cdc4d4`). The branch does not touch it either.

The adapter holds two fields: `private readonly IExperienceService m_service` (line 36) and
`private readonly IExperienceModule m_module` (line 37), and uses both. **Both interfaces are 0/0
across the span** — `IExperienceService.cs` and `IExperienceModule.cs` are literally unchanged.

### Point-by-point re-verification at `cbdfba2811`

| # | recon claim (June 2026) | status at `cbdfba2811` | evidence |
|---|---|---|---|
| 1 | `AttachObject` with the experience param | **still matches** | `IAttachmentsModule.cs` is 0/0; signature is `bool AttachObject(IScenePresence sp, SceneObjectGroup grp, uint AttachmentPt, bool silent, bool addToInventory, bool append, UUID experience)`. Phlox calls it with 7 args at `LSLSystemAPI.cs:1615` and `:1633`, both passing `GetScriptExperienceId()` last. Upstream touched neither line. |
| 2 | the KV functions | **still match** | `GetKeyValue`, `CreateKeyValue`, `UpdateKeyValue`, `DeleteKey`, `GetKeyCount`, `GetKeys`, `GetSize` all present on `IExperienceService` (lines 103–109) and `IExperienceModule` (lines 38–44). Adapter binds `GetKeyValue` (:74), `CreateKeyValue` (:77), `UpdateKeyValue` (:85), `DeleteKey` (:89), `GetKeyCount` (:92), `GetKeys` (:96), `GetSize` (:101). |
| 3 | `UpdateKeyValue`'s 5-param shape | **still matches** | Declared `string UpdateKeyValue(UUID experience, string key, string val, bool check, string original)` on both interfaces. The adapter's own method is 4-param and derives `doCheck`, calling the develop method with exactly 5 arguments at line 85. |
| 4 | the permission methods | **still match** | `GetExperiencePermission` / `SetExperiencePermissions` on `IExperienceModule` (used at adapter :108, :124, :141); `FetchExperiencePermissions` / `UpdateExperiencePermissions` on `IExperienceService` (:110, :130, :143). All present, both interfaces unchanged. |
| 5 | `ExperiencePermission.Allowed` / `.Blocked` | **still matches** | `enum ExperiencePermission { None, Allowed, Blocked }` at `IExperienceService.cs:5–10`, unchanged. Adapter uses `.Allowed` (:108, :143) and `.Blocked` (:125). |
| 6 | `GetSize` int-vs-long wrinkle | **still present, still cosmetic** | see below |

### The `GetSize` wrinkle

Still exactly as the recon described. `IExperienceService.cs:109` declares
`int GetSize(UUID experience)`. The adapter declares
`public long DataSizeKeyValue(UUID experienceId) => m_service?.GetSize(experienceId) ?? 0L;`

`m_service?.GetSize(...)` yields `int?`; `?? 0L` makes the coalesce operands `int?` and `long`, so
the underlying `int` is implicitly promoted to `long` and the expression types as `long`, matching
the declared return. **Merely cosmetic — confirmed.**

Two supporting observations. First, the promotion is compiler-guaranteed, not incidental: there is
an implicit `int → long` numeric conversion and no narrowing anywhere in the expression, so there
is no truncation or overflow path. Second, this is corroborated empirically — the full solution
builds with **0 errors** on the rebased tree, which it could not do if the conversion had stopped
being implicit.

The only way this could stop being cosmetic is if develop widened `GetSize` to `long` (harmless —
the adapter is already `long`) or narrowed the adapter to `int` (which would then need an explicit
cast). Neither happened in this span.

---

## 6. Runtime verification plan

Sections 1–5 found **no behavioural change to Experience, no branch modification of it, no
disturbance from #194–196, and an adapter that still matches on all six points.** So the honest
answer is close to "nothing needs checking" — but not exactly, because two mechanical changes do
reach Experience's runtime and neither has been exercised.

### What genuinely warrants a check

Only two things, and both are cheap:

1. **The ILogger migration rewrote every log call in six Experience files.** Those files now log
   through `LoggerProvider`. Note the finding in `webrtc-upstream-audit.md` §6.0: log4net has no
   configured appenders in this tree, so anything still on `ILog` is silent. `ExperienceModule.cs`
   uses `m_log.LogInformation` (ILogger — verified at line 100), so it should reach the file sink,
   but that is inference from the code, not observation.
2. **`MySqlConnector` 2.5.0 → 2.6.1 under `MySqlExperienceData`** (§4.3), plus net10.

### Step 1 — Experience logging reaches the log (1 minute)

Boot a region with Experience enabled, then:

```
findstr /C:"[EXPERIENCE]" <logPath>\OpenSim.Server.RegionServer.log
```

- **Pass:** entries appear (at minimum the module's own startup lines).
- **Fail:** nothing — meaning Experience's migrated logging is going nowhere, which would also be
  the first sign that `LoggerProvider.LoggerFactory` is not reaching that assembly.
- If the module is *disabled*, expect
  `[EXPERIENCE]: Module disabled becuase IExperienceService was not found!` (sic — the typo is
  upstream's and is a useful exact-match string).

### Step 2 — Experience persistence round-trips on the new driver (5 minutes)

Back up the `experience_*` tables first (`experience`, `experience_permissions`, plus the
`estate_allowed_experiences` / `estate_key_experiences` / `estate_blocked_experiences` tables if
present).

1. With a viewer, grant an experience permission (accept an experience permission request), then
   query `SELECT * FROM experience_permissions WHERE avatar = '<uuid>';` — the row must be present
   with the expected value.
2. Block the same experience; confirm the row updates rather than duplicating.
3. Forget/clear it; confirm the row is removed.
4. Restart the region server and confirm the state persisted.

- **Failure:** any `MySqlConnector` exception in the log — particularly an
  `InvalidCastException` from a `Convert.To*`, which is the shape a driver type-mapping change
  would take — or state that does not survive the restart.

### Step 3 — The adapter's script surface (5 minutes)

The adapter is the one place where two subsystems this branch inherits meet, so exercise it once
end to end with a Phlox script in an experience:

1. `llRequestExperiencePermissions` → accept. Confirm the grant is visible immediately on the read
   path (the adapter comments note the write and read paths are deliberately the same source).
2. Deny a request and confirm the script receives `experience_permissions_denied` — this is the
   **only** `IScriptModule.PostScriptEvent` path Experience uses (§3.1), so it is the single check
   that covers that binding.
3. Exercise the KV surface: `llCreateKeyValue`, `llReadKeyValue`, `llUpdateKeyValue` (with and
   without the check-value form, since that is the 5-param path), `llDeleteKeyValue`,
   `llKeyCountKeyValue`, `llKeysKeyValue`, `llDataSizeKeyValue`.
4. `llAttachToAvatarTemp` / experience-based attach, to exercise the 7-param `AttachObject`.

- **Failure:** any `MissingMethodException` at runtime would indicate a signature drift the static
  check missed. None is expected — the build already proves compile-time compatibility — but
  `llDataSizeKeyValue` returning a wrong or truncated number is the one place to look, since it is
  the `GetSize` promotion path (§5).

### What does **not** need checking, and why

- **The branch's interaction with Experience** — there is none (§2). No test can exercise a
  coupling that does not exist.
- **#194, #195, #196 against Experience** — Experience calls neither `HasScript` nor
  `OnGetScriptRunning`, references `RegionReady` nowhere, and does not touch the script loader
  (§3.1). Ordering is safe by construction (§3.2).
- **The SQLite provider swap** — there is no SQLite Experience DAL (§4.1).
- **Pomelo→Microting** — design-time tooling only, no runtime consumers (§4.2). It would only
  matter if someone regenerates EF migrations, which is a tooling task, not a runtime one.
- **Adapter signature compatibility** — statically re-verified on all six points (§5) and
  corroborated by a clean build.

If time is short, **Step 1 alone** is the highest value: it is a one-line check that also happens
to test the logging finding from the WebRTC audit against a second, independent subsystem.

---

## Summary of items needing attention

| # | Item | Severity | Action |
|---|---|---|---|
| 1 | Brief's premise needs correcting: `cbdfba2811` is now the merge base, and 7 Experience files *were* touched upstream — though all six `.cs` are logger-only (§1.2, §1.3) | Informational | None; record the precise position |
| 2 | `MySqlConnector` 2.5.0→2.6.1 reaches `MySqlExperienceData` (MySQL-only DAL); type mapping unverified (§4.3) | **Needs runtime check** | §6 Step 2 |
| 3 | Experience's six migrated files now log via `ILogger`; unverified that output reaches the sink (§6) | **Needs runtime check** | §6 Step 1 |
| 4 | `GetSize` int-vs-long wrinkle persists | Cosmetic, confirmed | None |

**The branch touches no Experience code, upstream changed no Experience behaviour, #194–196 do
not disturb it, the SQLite swap does not reach it, Pomelo→Microting is design-time only, and the
adapter still matches on all six points.** The residual risk is two mechanical changes — a MySQL
driver bump and the logging migration — neither of which has been run. No fixes were made.
