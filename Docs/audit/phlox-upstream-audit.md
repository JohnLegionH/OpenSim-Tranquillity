**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **The SQLite native chain was traced end to end and is TFM-independent** — the net8.0 vs
  net10.0 asset-selection question this audit left open does not affect it.
- **Two findings are now filed** in `Docs/KnownDefects.md` (added by `8dbb35a579`): the
  script-state save failing under concurrent script/physics load, and `script_state` rows
  that are never reaped and are written for scripts with no live instance.
- This file's own header cites branch tip `22b9a489c4`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `05b31ce748`.

---

# Phlox / upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `22b9a489c4`
**Span audited:** `81e5c2449d` (old merge base) → `cbdfba2811` (upstream/develop, 11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip)
**Status:** read-only audit. Nothing was changed, fixed, or committed. This file is
uncommitted working-tree output.

## Method and its limits

Everything below is **static comparison of the two trees** — `git diff` between the two
revisions, per-file classification of changed lines into "ILogger-migration noise" vs
"semantic", and reading the resulting source at both revisions. Signatures were compared
by reading the interface declarations at each revision; semantics by diffing implementation
bodies.

Nothing here was verified by running the region, rezzing a script, or exercising script
state save/restore. Where a conclusion needs runtime evidence, it is marked
**NOT ESTABLISHED** rather than inferred.

---

## 1. Direct overlap

**Verified by:** `git diff --name-only` for `*Phlox*` on both sides of the span, then `comm -12`
on the two sorted file lists.

**There is no overlap at all in Phlox source.**

- Upstream modified **15** Phlox-related paths across the span.
- This branch modified exactly **one** Phlox-related path: `Docs/PhloxKnownDefects.md`.
- The intersection is **empty**.

Upstream's 15:

```
Docs/PhloxSLua.md
Source/InWorldz.Phlox/Compiler/Gen.cs
Source/InWorldz.Phlox/InWorldz.Phlox.csproj
Source/Phlox.ScriptEngine/AsyncCommand/AsyncCommandManager.cs
Source/Phlox.ScriptEngine/AsyncCommand/Plugins/HttpRequest.cs
Source/Phlox.ScriptEngine/AsyncCommand/Plugins/SensorRepeat.cs
Source/Phlox.ScriptEngine/AsyncCommand/Plugins/XmlRequest.cs
Source/Phlox.ScriptEngine/LSLSystemAPI.cs
Source/Phlox.ScriptEngine/Phlox.ScriptEngine.csproj
Source/Phlox.ScriptEngine/PhloxEngine.cs
Source/Phlox.ScriptEngine/PhloxExecutionScheduler.cs
Source/Phlox.ScriptEngine/PhloxListenManager.cs
Source/Phlox.ScriptEngine/PhloxMasterScheduler.cs
Source/Phlox.ScriptEngine/PhloxScriptLoader.cs
Source/Phlox.ScriptEngine/StateManager.cs
```

Because the branch never edited Phlox source, **no auto-merge in Phlox could have hidden a
conflicting intent** — there was no second intent to reconcile. The rebase took upstream's
Phlox wholesale. This is the strongest single result in this audit: the "silent auto-merge"
risk the audit was commissioned to find does not exist inside Phlox itself.

The residual risk is therefore *not* merge-shaped. It is **dependency-shaped**: upstream
changed Phlox behaviour underneath branch code that calls into it. That is sections 2–4.

### Which of upstream's 15 are actually semantic

Changed lines were classified by filtering out logger-migration lines (`m_log`, `log4net`,
`ILogger`, `LogManager`, `using` churn). Counts are of remaining changed lines:

| File | total +/- | non-log lines | verdict |
|---|---|---|---|
| `PhloxExecutionScheduler.cs` | 11 / 11 | 0 | ILogger migration only |
| `PhloxMasterScheduler.cs` | 4 / 4 | 0 | ILogger migration only |
| `PhloxListenManager.cs` | 8 / 7 | 1 (blank line) | ILogger migration only |
| `LSLSystemAPI.cs` | 49 / 48 | 1 (blank line) | ILogger migration only |
| `AsyncCommandManager.cs` | 5 / 3 | 3 (comment `dotnet8`→`dotnet10`) | cosmetic |
| `HttpRequest.cs` | 6 / 4 | 3 (same comment) | cosmetic |
| `SensorRepeat.cs` | 1 / 1 | 2 (same comment) | cosmetic |
| `XmlRequest.cs` | 1 / 1 | 2 (same comment) | cosmetic |
| `Gen.cs` | 1 / 0 | 1 (blank line) | cosmetic |
| `PhloxEngine.cs` | 47 / 17 | **37** | **semantic — PRs #194, #195** |
| `PhloxScriptLoader.cs` | 98 / 47 | **85** | **semantic — PR #196** |
| `StateManager.cs` | 33 / 32 | **~10** | **semantic — SQLite provider swap** |
| `Phlox.ScriptEngine.csproj` | — | — | **semantic — net10 + provider swap** |

So the entire semantic surface of upstream's Phlox work is **three source files plus one
csproj**. Notably `LSLSystemAPI.cs`, despite a 49/48 diff, contains zero behavioural change —
it is pure logger migration. Same for both schedulers.

---

## 2. API surface

**Verified by:** grepping every branch-modified `.cs` (123 files) for references to Phlox,
`IScriptModule`, `IScriptEngine`, script-running APIs and task inventory; then, for each hit,
reading the call site and comparing the declaration and implementation body at both revisions.

The branch calls into the script-engine surface at exactly **two** places.

### 2.1 `IScriptModule.GetTopObjectStats`

**Call site:** `Source/OpenSim.Region.CoreModules/World/Estate/EstateManagementModule.cs:1889`
— the land-stat report path. It deliberately uses `RequestModuleInterfaces<IScriptModule>()`
(plural) to aggregate across YEngine *and* Phlox, then calls
`sm.GetTopObjectStats(0.001f, 1024, out _, out _)` on each and merges per-`localID`.

- **Signature:** `ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out …, out …)`
  — declared at `IScriptModule.cs:128` at **both** revisions. `IScriptModule.cs` has **zero**
  changed lines across the entire span (verified: `git diff` on the interface file is empty).
- **Semantics:** Phlox's implementation body was extracted at both revisions
  (`PhloxEngine.cs:877` at old, `:907` at new — the shift is from lines added above it) and
  diffed line-for-line. **Byte-identical.**

**Verdict: no risk.** Signature unchanged, body unchanged, interface unchanged.

### 2.2 `IEntityInventory.CreateScriptInstance`

**Call site:** `Source/OpenSim.Region.PhysicsModules.LegionJolt/LegionJoltScene.cs:1709`
— a self-test harness that rezzes a real script onto a prim to observe what
`llDetectedLinkNumber` returns through the Phlox VM. Calls
`ls.RootPart.Inventory.CreateScriptInstance(taskItem, 0, false, _scene.DefaultScriptEngine, 1)`.

- **Signature:** the four `CreateScriptInstance*` overloads sit at
  `IEntityInventory.cs:88/122/136/138` at **both** revisions, unchanged.
- **Semantics:** `SceneObjectPartInventory.cs` changed 18 lines across the span; after
  filtering logger lines, **zero** non-log changed lines remain. Implementation unchanged.
- `Scene.DefaultScriptEngine` still defaults to `"YEngine"` at both revisions (verified by
  extracting the `GetString("DefaultScriptEngine", …)` literal at each).

**Verdict: no risk.**

### 2.3 The dangerous case — signature stable, behaviour changed

The audit brief asked specifically for APIs where the signature is unchanged but behaviour
differs. **Two exist in the span, and the branch calls neither:**

- `IScriptModule.HasScript(UUID, out bool)` — declared identically at
  `IScriptModule.cs:87` at both revisions, but Phlox's implementation went from a hard-coded
  stub (`running = false; return false;` — `PhloxEngine.cs:818` at old) to a real ownership
  check (`m_ExeScheduler.FindScript(itemID)` plus the running flag — `:827` at new). This is
  the textbook shape of the hazard: same signature, inverted behaviour.
- `IScriptModule.StartProcessing()` — declared identically at `IScriptModule.cs:100` at both
  revisions, but went from an empty no-op to firing a region-wide event. See §3.2.

Both were checked against the branch: a grep of all 123 branch-modified `.cs` files for
`HasScript`, `OnGetScriptRunning`, `GetScriptRunning`, `StartProcessing`, `RegionReady`,
`LoginLock`, `EmptyScriptCompileQueue` and `TriggerEmptyScript` returns **no matches at all**.
The branch does not reference any of them.

---

## 3. The three PRs specifically

### 3.1 `b01e85562e` — #194, HasScript + OnGetScriptRunning ownership guard

**What changed.** `HasScript` was a stub always returning false; it now reports ownership via
`m_ExeScheduler.FindScript(itemID) != null` plus the running flag. `OnGetScriptRunning` was a
no-op behind a false-premise TODO; it now replies via `SendScriptRunningReply`, but *only for
scripts Phlox owns*. The ownership guard matters because `OnGetScriptRunning` is a broadcast
`EventManager` event delivered to every registered engine — without the guard, with YEngine
and Phlox both enabled, Phlox would race a `SendScriptRunningReply(false)` for a YEngine-owned
script and the viewer's Running checkbox could show a running script as stopped. Confined to
`PhloxEngine.cs`, 18 insertions / 3 deletions.

**Does this branch depend on the old behaviour?** **No.** The branch contains no reference to
`HasScript`, `OnGetScriptRunning` or `GetScriptRunning` (verified by the grep in §2.3). The
user-visible effect is the viewer script tab's Running checkbox for Phlox scripts, which no
branch code reads or asserts on.

**Second-order note.** Anything that previously relied on `HasScript` returning false for
*every* Phlox script — i.e. treating "Phlox owns nothing" as an invariant — would now behave
differently. No such code exists on this branch. Whether such code exists elsewhere in the
tree was **not** surveyed; the audit scope was this branch's dependencies.

### 3.2 `1ed957e7e7` — #195, RegionReady fired from StartProcessing

**What changed.** `PhloxEngine.StartProcessing()` was an empty no-op. It now unconditionally
calls `TriggerEmptyScriptCompileQueue(0, "")`. `RegionReadyModule` holds the login lock until
some engine fires that event; YEngine already did so from its own `StartProcessing`, so on a
region where Phlox is the *only* registered engine the lock was never released and logins hung
forever (masked in practice only because YEngine is normally also enabled). The signal is fired
unconditionally rather than gated on a compile-queue drain, because Phlox compiles
asynchronously and a drain barrier risks never firing — so logins may now open marginally
before the last boot script finishes compiling. 15 insertions / 1 deletion.

**Is this a sequencing change? Yes. Does anything on this branch depend on that ordering?**
**No — and this was checked structurally, not just by grep.**

The startup order was traced through the source at HEAD:

1. `OpenSimBase.cs:437` → `controller.AddRegionToModules(scene)`
2. → `RegionModulesControllerPlugin.cs:488` and `:493` → `module.RegionLoaded(scene)`
3. `OpenSimBase.cs:453` → `scene.SetModuleInterfaces()`
4. later, `OpenSimBase.cs:803` / `OpenSim.cs:745` → `scene.Start()`
5. → `Scene.cs:1625` → `StartScripts()`
6. → `Scene.Inventory.cs:94` → `engine.StartProcessing()` → **the new RegionReady signal**

The voice region module does all of its per-region setup in `RegionLoaded`
(`WebRtcVoiceRegionModule.cs:146`) — it subscribes `OnRegisterCaps` at `:150` and starts the
per-region visibility feeder at `:177`. That is **step 2**, strictly before **step 6**.

So the voice CAP handlers and the visibility feeder are registered and running before the
RegionReady signal can fire, under both the old and new behaviour. Firing the signal *earlier
than never* cannot expose the voice path to an uninitialised state. **No ordering dependency
on this branch is affected.**

The residual theoretical exposure — logins opening slightly before the last script finishes
compiling — concerns script availability, not voice, estate, land or physics. Nothing on this
branch reads script-compile completion.

### 3.3 `bee925f310` — #196, boot rez path guarded

**What changed.** `PerformLoad` called `BeginScriptRun` on the `TryStartSharedScript` and
`TryStartFromUnloadedCache` paths with no exception guard, so one script throwing during load
could kill the `PhloxScriptLoader` work item and abort the remainder of the boot rez batch.
Now each request is wrapped: on failure it logs item name + UUID, prim name + localID, and the
exception with stack via `LogLoadFailure`, then continues, so every other script still loads.
A top-level backstop was added in `DoWork` so a throw from the unload/compile paths cannot stop
the worker draining its queue. Confined to `PhloxScriptLoader.cs`, 68 insertions / 18 deletions.

**Does this branch depend on the old behaviour?** **No.** The branch does not reference
`PhloxScriptLoader` and contains no code that relies on a failed script load aborting the
batch. This is a strictly-more-robust change: the previous behaviour (whole batch aborted) has
no plausible dependant.

The one behavioural nuance worth recording: a script that used to take the whole batch down
now fails silently-but-logged, so a region that previously failed loudly at boot may now come
up with a subset of its scripts running. That is a diagnosis change, not a regression, and it
affects script operators rather than this branch.

---

## 4. Runtime/provider fallout

**Verified by:** grepping all Phlox source for data-provider types; diffing
`Phlox.ScriptEngine.csproj` and `StateManager.cs` across the span; reading `StateManager.cs`
at HEAD for its transaction, parameter-binding and connection-lifetime usage.

### Does Phlox touch SQLite or MySQL directly? — Yes, SQLite. Directly.

`Source/Phlox.ScriptEngine/StateManager.cs` is a hand-rolled ADO.NET persistence layer for
script state, against its own database file `ScriptEngines/Phlox/state/script_state.db`. It is
the only such place; the single MySQL mention in Phlox (`LSLSystemAPI.cs:11365`) is a comment
about grid-backend sizing, not a call.

**This is the most consequential finding in the audit**, and it is not one of the three PRs.

### The provider was swapped

`Phlox.ScriptEngine.csproj` across the span:

- **removed** `Microsoft.Data.Sqlite` 10.0.7
- **added** `System.Data.SQLite` 2.0.4 **and** `SQLite` 3.53.4
- also removed the explicit `log4net` 3.3.1 pin and dropped the local `net8.0` TargetFramework
  (now inherited as net10.0)

`StateManager.cs` was changed to match:

| | before (`81e5c2449d`) | after (`cbdfba2811`) |
|---|---|---|
| namespace | `Microsoft.Data.Sqlite` | `System.Data.SQLite` |
| connection type | `SqliteConnection` | `SQLiteConnection` |
| native init | `SQLitePCL.Batteries_V2.Init()` | `DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly)` |
| connection string | `Data Source={DB_FILE};Mode=ReadWriteCreate` | `Data Source={DB_FILE}` |

These are two different ADO.NET providers with different native-library resolution, different
defaults, and different strictness. The C# compiles either way, which is exactly why this is
worth flagging.

### What `StateManager` actually relies on

Read at HEAD:

- **Transactions** — `OpenConnection()` then `using var tx = conn.BeginTransaction()` at
  `:200` and `:223`, with `tx.Commit()` at `:209`/`:225`. The commands executed inside are
  created via `conn.CreateCommand()` in `SaveSingleInTransaction` (`:243`) and
  **`cmd.Transaction` is never explicitly assigned**. Under `Microsoft.Data.Sqlite`,
  `CreateCommand()` pre-populates `Transaction` from the connection's active transaction and
  the provider *enforces* a match on execute. `System.Data.SQLite` is more permissive and
  associates by connection. Both are expected to work, by different mechanisms — but the
  enforcement contract genuinely differs, and this code leans on the implicit behaviour.
- **Type mapping** — BLOB round-trip: `AddWithValue("@data", blob)` with a `byte[]` at `:253`,
  read back as `(byte[])reader[1]` at `:145`. Plus a Unix-seconds `long` timestamp at `:254`
  and `Guid.ToString()` keys. All are unremarkable in both providers.
- **Connection lifetime** — a fresh connection per operation (`using var conn = OpenConnection()`
  at `:129`, `:160`, `:199`, `:222`, `:264`), each of which executes
  `PRAGMA journal_mode=WAL` on open (`:286`). Connection **pooling defaults differ between the
  two providers**, and pooling interacts with WAL through file-handle lifetime and the `-wal`/`-shm`
  sidecar files. This is the concrete mechanism by which the swap could change observable
  behaviour despite identical source logic.
- **File creation** — the new connection string dropped `Mode=ReadWriteCreate`. Create-if-missing
  is the default for `System.Data.SQLite` (`FailIfMissing` defaults false), so this is *expected*
  to be equivalent. Neither provider creates the parent directory.

### NOT ESTABLISHED

The following require runtime evidence and were **not** obtained — no region was started and
no state save/restore was exercised:

1. Whether the untyped `cmd.Transaction` usage behaves identically under
   `System.Data.SQLite` in the save path (§ transactions above).
2. Whether the dropped `Mode=ReadWriteCreate` in fact still creates a missing
   `script_state.db` under the new provider.
3. Whether `DllmapConfigHelper.RegisterAssembly` resolves the native SQLite interop in the
   actual deploy target (`D:\legiongrid\regionserver`). This is a runtime-only failure mode:
   if the native cannot be resolved, the first state save/restore throws, and nothing at
   compile time catches it. The previous `SQLitePCL.Batteries_V2.Init()` used a completely
   different resolution strategy, so success under the old provider is not evidence for the new one.
4. Whether `System.Data.SQLite` **2.0.4** and `SQLite` **3.53.4** are the packages intended.
   The canonical System.Data.SQLite package versions in the 1.0.x series, so a 2.0.4 pin under
   that name is worth confirming against the feed before trusting it.

### Bearing on this branch

Direct impact on `feature/voice-visibility-matrix`: **none identified.** The branch does not
persist or restore script state and does not reference `StateManager`. This is upstream risk
carried in the rebase, not a merge hazard introduced by it — but it now sits under this branch,
and items 1–4 above are the ones worth a runtime smoke check before deploy.

---

## 5. Async-syscall interaction

**Verified by:** inspecting `D:\tranq-port-async` (branch `fix/phlox-async-syscall-resume`,
HEAD `65ca25fbcf`), confirming its merge base, and diffing its three commits against
`cbdfba2811`.

**Base:** `git merge-base HEAD cbdfba2811` returns `cbdfba2811` exactly. The async branch is
already rebased **on top of** upstream's span. All 11 upstream commits, including the three
Phlox PRs and the SQLite swap, are already beneath it. **There is no pending merge and
therefore no latent conflict to resolve.**

**Files the async branch touches** (3 commits, 443 insertions / 152 deletions):

```
Source/Phlox.ScriptEngine/LSLSystemAPI.cs           410 +/-
Source/Phlox.ScriptEngine/PhloxEngine.cs             57 +
Source/Phlox.ScriptEngine/PhloxExecutionScheduler.cs 128 +/-
```

### Conflict analysis

- **`LSLSystemAPI.cs`** — the async branch rewrites API bodies to thread `SysReturn`. Upstream's
  change to this file across the span is **pure ILogger migration, zero semantic lines** (§1).
  No semantic collision.
- **`PhloxExecutionScheduler.cs`** — the async branch adds the syscall-timeout sweep,
  `SnapshotRunStates`, and the parked-script warning. Upstream's change is **pure ILogger
  migration, zero semantic lines** (§1). No semantic collision.
- **`PhloxEngine.cs`** — the only file where upstream made semantic changes (#194, #195) *and*
  the async branch adds code. Its addition was read in full: it is **purely additive** — a new
  `"phlox scripts [syscall]"` console command registration plus its `HandleScriptsCommand`
  handler, which reads `m_ExeScheduler.SnapshotRunStates()` and resolves prim/script names for
  display. A targeted grep of that diff for `StartProcessing`, `HasScript`,
  `OnGetScriptRunning` and `TriggerEmptyScript` returns **nothing**. It does not touch either
  method upstream changed.

**Verdict: no conflict, no duplication.**

### Complementarity, not overlap

#196 and the async branch both add "make a stall visible" diagnostics, which invites a
duplication concern. They operate at different layers and do not overlap:

- **#196** guards *load time*, in `PhloxScriptLoader.cs`, against a script throwing during the
  boot rez batch. The async branch **does not touch `PhloxScriptLoader.cs` at all.**
- **The async branch** guards *run time*, in `PhloxExecutionScheduler.cs`, against a script
  parked in `Syscall` awaiting a `SysReturn` that never arrives.

A script that fails to load never reaches the syscall path; a script wedged in a syscall
loaded successfully. The two diagnostics are disjoint by construction.

### One interaction worth recording

#196's rationale notes that the loader defect, *combined with the StartProcessing defect*,
could strand the RegionReady login lock. With #195 in place, `StartProcessing` now fires the
signal unconditionally, so **login availability is no longer coupled to script health at all**.
For the async work this is mildly helpful: a region carrying syscall-wedged scripts will now
open logins regardless, so the wedge presents as scripts that stopped responding rather than as
a region that never admits anyone — which is precisely the failure mode the async branch's
`phlox scripts syscall` dumper is built to surface. No action required; noted so the changed
symptom is not misread later.

---

## Summary of items needing attention

| # | Item | Severity | Affects this branch? |
|---|---|---|---|
| 1 | SQLite provider swap under `StateManager` — pooling/WAL, native resolution, transaction association (§4, items 1–4 NOT ESTABLISHED) | **Needs runtime check** | No direct dependency; carried risk |
| 2 | `System.Data.SQLite` 2.0.4 / `SQLite` 3.53.4 package identity unconfirmed (§4) | Worth confirming | No |
| 3 | `HasScript` stub→real and `StartProcessing` no-op→fires: signature-stable behaviour changes (§2.3) | Informational | No — branch references neither |
| 4 | #196 changes boot failure from loud batch-abort to logged-and-continue (§3.3) | Informational | No |

Nothing in this audit blocks the branch. No fixes were made.
