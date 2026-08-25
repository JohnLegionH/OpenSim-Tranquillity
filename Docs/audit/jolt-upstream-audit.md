**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **The OMV layout question is closed.** `Legion.Physics` contains zero `OpenMetaverse`
  references, and the OMV ↔ `System.Numerics` conversion happens at the module boundary in
  `OpenSim.Region.PhysicsModules.LegionJolt` (`JoltPrim.cs:282`, `JoltCharacter.cs:186`).
  The OMV package version therefore cannot reach the native boundary.
- **The csproj pin rationale is documented** in `6743ac4e7c`.
- **The OMV package version did change, and that conclusion held.** `93765a999e` (2026-08-24)
  moved the repo from `OpenMetaverse` 1.0.6 (assembly identity 1.2.0.0) to
  `UtopiaSkye.OpenMetaverse` 1.1.6-ga897ffefd7 (identity 1.1.0.0), pinned centrally in
  `Directory.Build.props`. Both live trees were restaged onto it on 2026-08-24 and
  `joltc.dll` was byte-identical before and after (SHA-256 `16AF7638…`).
- **Jolt is now the live physics engine** on all three regions (Ebony, Transylvania, Elm;
  `physics = Jolt`), terrain verified during that bring-up. Section 5 was written when no
  region had been booted and nothing was runtime-verified; that is no longer the case.
- This file's own header cites branch tip `22b9a489c4`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `05b31ce748`.

---

# Jolt / upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `22b9a489c4`
**Span audited:** `81e5c2449d` (old merge base) → `cbdfba2811` (upstream/develop, 11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip)
**Status:** read-only audit. Nothing changed, fixed, upgraded or committed. No package version
was altered. This file is uncommitted working-tree output.

## Method and its limits

Findings come from: `git diff` between the two revisions with changed lines classified into
ILogger-migration noise vs semantic; reading csproj/source at both revisions; SHA-256 hashing
every `joltc*.dll` on disk; reading the resolved `project.assets.json` and published
`deps.json` to determine which package asset and which native path the runtime actually uses;
and inspecting the local NuGet package cache.

**No region was booted and no physics step was executed.** Jolt is a native binding, so its
real failure modes are load-time and first-use, and nothing below is runtime-verified. Section 5
exists precisely because the decisive questions cannot be settled statically. Anything not
established is marked **NOT ESTABLISHED** rather than inferred.

I did **not** query the NuGet feed (read-only constraint). Where feed data appears below it
comes from a *locally cached* listing file, and its staleness is stated.

---

## 1. The dependency decision

### 1.1 Is the net8.0-targeted package consuming correctly under net10.0?

**The premise of the question is wrong, and so is the comment in the csproj.**

`Legion.Physics.csproj:38` says:

> `Managed Jolt binding. 2.19.1 is the newest net8.0-compatible release (2.19.2+ = net9/net10).`

Verified against the local package cache — `JoltPhysicsSharp` **2.19.1 ships two lib assets**:

```
~/.nuget/packages/joltphysicssharp/2.19.1/lib/net8.0/
~/.nuget/packages/joltphysicssharp/2.19.1/lib/net9.0/
```

So 2.19.1 is not a net8.0-only package. And under net10.0 NuGet picks the *nearest* compatible
TFM, which is net9.0, not net8.0. Confirmed from the resolved restore graph
(`Addons/LegionPhysics/Legion.Physics/obj/project.assets.json`, `net10.0` target):

```
"compile": { "lib/net9.0/JoltPhysicsSharp.dll": {} },
"runtime": { "lib/net9.0/JoltPhysicsSharp.dll": {} }
```

The rebase therefore **silently changed which managed assembly is consumed**. The evidence is
still on disk in the two build outputs:

| output | asset selected |
|---|---|
| `Legion.Physics/bin/Release/net8.0/Legion.Physics.deps.json` | `lib/net8.0/JoltPhysicsSharp.dll` |
| `Legion.Physics/bin/Release/net10.0/Legion.Physics.deps.json` | `lib/net9.0/JoltPhysicsSharp.dll` |

**Is this a compatibility shim that could shift? No.** This is ordinary nearest-TFM asset
selection, a first-class supported scenario — a net9.0-built assembly running on the net10.0
runtime, not a netstandard/binding-redirect shim. It is stable and will not "shift" on its own.
It would only change if the package added a `lib/net10.0` asset in a future version, and the
version here is pinned.

The change is nonetheless worth knowing: **a different binary is loaded now than before the
rebase**, and any behavioural difference between the package's net8.0 and net9.0 builds is now
in play. Whether those two builds differ was **NOT ESTABLISHED** — it would require decompiling
or diffing both assemblies.

### 1.2 What patched native is vendored, where, and how is it resolved?

**What.** A patched `joltc.dll` whose patch removes the process-global `TempAllocator` so each
`JPH_PhysicsSystem` owns its own. Source patch is checked in at
`native/joltc/per-system-tempallocator.patch`; it deletes the static `s_TempAllocator` and its
`JPH_Init`/`JPH_Shutdown` lifecycle from `src/joltc.cpp`. Provenance is documented to an
unusually high standard in `native/joltc/README.md`: upstream `amerkoleci/joltc` commit
`1715c5aab834a5bb0c344dc4a11d573ad6f9736d` (2025-10-10), identified as the exact source of the
shipped **JoltPhysics.Native 1.0.4** win-x64 binary and confirmed by an unpatched rebuild
exporting an identical 1086-symbol set.

**Why it is load-bearing.** `JoltPhysicsBackend._simLock` is per-instance. Per-instance locks +
stock joltc's shared LIFO scratch = `TempAllocator: Freeing in the wrong order` → `std::abort()`
the moment two regions step concurrently. The native and the lock design are one decision.

**Where.** Checked in at
`Source/OpenSim.Region.PhysicsModules.LegionJolt/runtimes/win-x64/native/joltc.dll`, shipped by
the `<Content Include="runtimes\**\*.*">` glob in the LegionJolt csproj. Only `win-x64` exists;
no Linux `.so`.

**Verified by hashing.** Patched = `16af7638…`; stock 1.0.4 win-x64 = `67becfc7…`.
Every `joltc.dll` on a **win-x64** path in the tree — LegionJolt output, RegionServer output
(both net8.0 and net10.0), and the test output — hashes `16af7638`. A sweep for `67becfc7`
across all outputs returns **nothing**. On the RID that actually runs, the patched native wins.

**How it is resolved at runtime — and this is the subtle part.** Not by a Dllmap, not by an
explicit path, and not by a custom resolver. Confirmed:

- The managed binding imports the plain name **`joltc`** (10 literal occurrences in
  `lib/net9.0/JoltPhysicsSharp.dll`; no `joltc_double` occurrences).
- No `NativeLibrary`, `SetDllImportResolver`, or `DllImportResolver` appears anywhere in the
  branch's Jolt sources — so it is default .NET native probing.
- Default probing consults `NATIVE_DLL_SEARCH_DIRECTORIES`, which the host builds from
  `deps.json` `runtimeTargets` entries for the current RID.

In the published `OpenSim.Server.RegionServer.deps.json`, the entry mapping
`runtimes/win-x64/native/joltc.dll` sits under the package block **`JoltPhysics.Native/1.0.4`**
(line 156). LegionJolt's own `deps.json` entry declares **no native asset at all** — its patched
DLL is a plain content file copied to disk.

> **The patched native is discoverable only because the *excluded* stock package's manifest
> entry maps that exact path, and the Content copy overwrote the file sitting there.**

That is a real and fragile coupling. `ExcludeAssets="all"` suppresses the stock *files* from
Legion.Physics's own asset consumption while leaving the package in the dependency graph, so the
`runtimeTargets` mapping survives and keeps `runtimes/win-x64/native/` on the probe path. If
someone later "tightens" this to `PrivateAssets="all"` — which looks like an obvious cleanup for
stopping stock natives from shipping — the mapping disappears, the directory drops off the native
search path, and `joltc` fails to load. **That failure lands at first physics use, not at
process start**, and no compile step catches it. This is the single most important structural
finding in this audit.

### 1.3 Does the net8→net10 move change native resolution or the RID path?

**No.** Verified two ways:

- The resolution mechanism (`deps.json` `runtimeTargets` → `NATIVE_DLL_SEARCH_DIRECTORIES`) is
  TFM-independent; nothing in it keys off the target framework.
- The two build outputs are structurally identical: `bin/Release/net8.0/runtimes` and
  `bin/Release/net10.0/runtimes` each contain exactly 4 `joltc*.dll` files, with matching
  hashes per RID. No flattened root `joltc.dll` exists in either.

The one thing the move *did* change is §1.1 — the managed asset went net8.0 → net9.0. That
surfaces at JIT/first-call, not at assembly load, so it belongs in the "first use, not load"
category the brief asked about — but it is a managed-side change, not a native-resolution one.

### 1.4 Collateral: the exclusion is leakier than the comment claims

`ExcludeAssets="all"` does **not** stop the stock package's assets reaching downstream projects.
Hashing the RegionServer output proves stock binaries did ship:

| file in RegionServer output | hash | origin |
|---|---|---|
| `runtimes/win-x64/native/joltc.dll` | `16af7638` | **patched** ✓ |
| `runtimes/win-arm64/native/joltc.dll` | `b0a7d051` | stock 1.0.4 |
| `runtimes/win-x64/native/joltc_double.dll` | `8c68788d` | stock 1.0.4 |
| `runtimes/win-arm64/native/joltc_double.dll` | `a57a9a14` | stock 1.0.4 |

All three stock hashes match `~/.nuget/packages/joltphysics.native/1.0.4/` byte-for-byte.

Functionally harmless **on win-x64**: the binding imports `joltc`, never `joltc_double`, and the
arm64 tree is never probed on an x64 host. But two consequences follow:

1. **On a win-arm64 host the stock native would load**, reinstating exactly the
   `TempAllocator` abort the patch exists to prevent. There is no arm64 patched build.
2. `assert-patched-joltc.ps1` checks **only** `runtimes\win-x64\native\joltc.dll` (or a
   flattened root `joltc.dll`). It does not inspect arm64 or either `joltc_double`. Its coverage
   is narrower than its stated purpose ("fails loudly if a stray NuGet restore … reintroduced
   the stock DLL") implies. On the deploy target that is sufficient; as a general guard it is not.

### 1.5 Is 2.19.1 the newest release? — Stated plainly: **no.**

A locally cached NuGet version listing exists at
`~/AppData/Local/NuGet/v3-cache/…$ps_api.nuget.org_v3_index.json/list_joltphysicssharp.dat`
(file mtime 2026-08-23 16:18, i.e. written by a recent restore during this rebase work). It lists,
in order: … 2.19.0, **2.19.1**, 2.19.2, 2.19.3, 2.19.4, 2.19.5, 2.20.0, 2.20.1, 2.21.0, **2.22.0**.

So 2.19.1 is ten releases behind the newest the cache knows about.

- **NOT ESTABLISHED:** which TFMs 2.20/2.21/2.22 target, and specifically whether any ships a
  `lib/net10.0` asset. Only 2.19.1 is present in the package cache; answering this needs a feed
  query, which I did not perform.
- **NOT ESTABLISHED:** whether the cached listing is current as of today.

**Reporting only, per instruction — but the recommendation is to keep the pin, for a reason the
comment does not give.** The stated rationale (net8.0 compatibility) is obsolete now that the
project is net10.0, and was inaccurate even before. The *real* reason to stay at 2.19.x is ABI
coupling: the patched native is built from the joltc commit that produced **JoltPhysics.Native
1.0.4**, which is what the 2.19.x managed binding targets. Bumping JoltPhysicsSharp would pull a
newer `JoltPhysics.Native` expecting a different joltc ABI, and the patch would have to be
rebuilt against that newer joltc source before the managed bump is safe. The version comment
should be rewritten to say that; as written it justifies the right pin with the wrong reason,
which invites someone to "fix" it by upgrading.

---

## 2. Project targeting

All five projects were confirmed at `net10.0` by reading each csproj at HEAD.

| project | TFM | packages | project refs | coherent? |
|---|---|---|---|---|
| `Legion.Physics` | net10.0 | JoltPhysicsSharp 2.19.1; JoltPhysics.Native 1.0.4 `ExcludeAssets=all` | none | yes, but see §1 and the stale comments below |
| `Legion.Vehicles` | net10.0 | none | none | **yes — fully clean** |
| `OpenSim.Region.PhysicsModules.LegionJolt` | net10.0 | log4net 3.3.2 | Framework, Region.Framework, SharedBase, Legion.Physics, Legion.Vehicles | yes |
| `WebRtcVoiceRegionModule.Tests` | net10.0 | Test.Sdk **18.8.1**, NUnit 4.3.2, NUnit3TestAdapter 5.0.0, coverlet 6.0.4 | 19 refs incl. OpenSim.Tests.Common | yes |
| `WebRtcJanusService.Tests` | net10.0 | Test.Sdk **17.14.1**, NUnit 4.3.2, NUnit3TestAdapter 5.0.0, coverlet 6.0.4 | 4 voice projects | **inconsistent — see below** |

### 2.1 Test SDK divergence

`WebRtcVoiceRegionModule.Tests` was moved to `Microsoft.NET.Test.Sdk` **18.8.1** during the
rebase (it had to be — it references `OpenSim.Tests.Common`, which requires ≥18.8.1, and the
mismatch was an NU1605 downgrade-as-error). `WebRtcJanusService.Tests` still pins **17.14.1**.

It restores and builds, because it does not reference `OpenSim.Tests.Common` and so nothing
forces the floor. But two sibling test projects in the same solution now run on test SDKs a major
version apart, on net10.0. Whether 17.14.1 hosts net10.0 test runs correctly under `dotnet test`
was **NOT ESTABLISHED** — the solution build passes, which only proves compilation, not that the
runner works. This is a coherence gap worth closing.

### 2.2 net8-era assumptions still in the tree

Two stale comments survive, and they contradict each other:

- `Legion.Physics.csproj:38` — "2.19.1 is the newest net8.0-compatible release (2.19.2+ =
  net9/net10)". Wrong on both clauses (§1.1), and its premise is moot now the project is net10.0.
- `Legion.Physics/JoltPhysicsBackend.cs:10` — "Jolt binding: JoltPhysicsSharp **2.18.6** (newest
  still shipping lib/net8.0/)". Names a different version than the actual pin, and repeats the
  same obsolete net8.0 rationale.

Both are comments only; no code depends on them. But together they misdescribe the dependency to
the next reader, which is how a wrong upgrade gets made.

One `AllowUnsafeBlocks` comment (`<!-- 2.19.x HeightFieldShapeSettings takes float* -->`) remains
accurate and version-appropriate.

### 2.3 Stale build leftover

`Source/Legion.Physics/` exists on disk containing only `bin/` and is **untracked**
(`git ls-files` returns nothing for it) — a leftover from an earlier layout, since the real
project lives at `Addons/LegionPhysics/Legion.Physics/`. It holds `bin/Debug/net8.0/` outputs.
Harmless, invisible to git, but it will confuse a future grep for Jolt project files.

---

## 3. Upstream overlap

**Verified by:** `git diff --name-only` on both sides of the span, sorted, intersected with
`comm -12`.

- Upstream changed **797** files; the branch changed **123**.
- The intersection is **19** files.
- **The Jolt-path intersection is empty.** Filtering the intersection for `jolt`,
  `legionphysics` or `legion.` returns nothing.

This is expected and worth stating plainly: `LegionJolt`, `Legion.Physics` and `Legion.Vehicles`
are **entirely branch-added**. Upstream has never seen these files, so there was no second intent
for an auto-merge to reconcile, and no merge-shaped risk exists anywhere in the Jolt code.

For completeness the 19 overlapping files are the voice modules
(`JanusAudioBridge`, `JanusMessages`, `JanusPlugin`, `JanusRoom`, `JanusViewerSession`,
`WebRtcJanusService`, `WebRtcVoiceRegionModule` + its csproj, `WebRtcVoiceServiceModule`),
four core files (`EstateChangeInfo`, `EstateManagementModule`, `LandManagementModule`,
`LandObject`, `LLLoginService`) and build metadata (`Directory.Build.props`, three csprojs,
`Tranquillity.sln`). All were reconciled during the rebase and are outside this audit's scope.

The only Jolt-adjacent file upstream touched at all is
`Source/OpenSim.Region.PhysicsModules.SharedBase/OpenSim.Region.PhysicsModules.SharedBase.csproj`
— which the branch did not touch, so still no overlap. Its change was dropping the local
`net8.0` TFM and bumping `System.Configuration.ConfigurationManager` 10.0.8 → 10.0.10.

---

## 4. Physics API surface

**Verified by:** identifying the base types and imported core namespaces in the three Jolt
projects, then diffing each core type across the span and filtering out logger-migration lines.

### 4.1 What the Jolt code binds to

| binding | where |
|---|---|
| `PhysicsActor` (base class) | `JoltPrim.cs` — 55 overrides; `JoltCharacter.cs` — 55 overrides |
| `PhysicsScene` (base class) | `LegionJoltScene.cs` — 15 overrides |
| `INonSharedRegionModule` | `LegionJoltScene.cs` |
| `OpenSim.Framework` | 4 files |
| `OpenSim.Region.PhysicsModules.SharedBase` | 3 files |
| `OpenSim.Region.Framework.Scenes` | 1 file (`LegionJoltScene`) |
| `OpenSim.Region.Framework.Interfaces` | 1 file |

Concrete core members `LegionJoltScene` calls, extracted from source:
`_scene.Heightmap`, `_scene.RayCastFiltered`, `_scene.GetScenePresences`,
`_scene.AddNewSceneObject`, `_scene.DeleteSceneObject`, `_scene.RegionInfo`, `_scene.Frame`,
`_scene.AssetService`, `_scene.DefaultScriptEngine`, `_scene.EventManager`
(`.ScriptColliding`, `.ChatFromWorldEvent`), and on the part:
`RootPart.PhysActor`, `.LocalId`, `.UUID`, `.OwnerID`, `.Scale`, `.GetWorldRotation`,
`.SitTargetPosition`, `.SitTargetOrientation`, `.SetScriptEvents`, `.RemoveScriptEvents`,
`.Inventory` (including `CreateScriptInstance`).

### 4.2 Signature and semantics across the span

Every type in that surface was diffed at both revisions, with changed lines classified:

| core file | raw diff | non-log, non-blank lines | verdict |
|---|---|---|---|
| `SharedBase/PhysicsActor.cs` | 3+/1− | **0** | ILogger migration only |
| `SharedBase/PhysicsScene.cs` | 3+/1− | **0** | ILogger migration only |
| `SharedBase/NullPhysicsScene.cs` | 7+/6− | 0 | ILogger migration only |
| `Interfaces/IRegionModuleBase.cs` | 0+/0− | 0 | **unchanged** |
| `Interfaces/INonSharedRegionModule.cs` | 0+/0− | 0 | **unchanged** |
| `Interfaces/IMesher.cs` | 0+/0− | 0 | **unchanged** |
| `Scenes/Scene.cs` | 137+/138− | 2 (both `string.Format(…)` continuation lines of log calls) | ILogger migration only |
| `Scenes/EventManager.cs` | 113+/113− | **0** | ILogger migration only |
| `Scenes/SceneObjectPart.cs` | 28+/29− | **0** | ILogger migration only |
| `Scenes/TerrainChannel.cs` | 7+/7− | **0** | ILogger migration only |
| `Scenes/SceneObjectPartInventory.cs` | 18+/18− | **0** | ILogger migration only |

### 4.3 Signature-stable behaviour-changed cases — explicitly: **none found**

The brief asked for these to be flagged. In the physics API surface there are **zero**.

The reasoning is deductive and worth stating because it is stronger than a spot check: since
`PhysicsActor.cs` and `PhysicsScene.cs` are byte-identical between `81e5c2449d` and
`cbdfba2811` apart from logger lines and one blank line, **no member the Jolt code overrides
changed in signature, virtuality, or body**. There is no room for an override to have silently
become a hide, nor for a base implementation to have shifted underneath. The same holds for the
three module interfaces, which are literally unchanged, and for the scene/part/terrain types the
scene class calls into.

Upstream's 11 commits did not touch physics semantics at all. The .NET 10 move, SQLite swap,
Pomelo→Microting, xunit and the Phlox PRs are all orthogonal to this surface.

### 4.4 One divergence I could not clear: OpenMetaverse

Physics marshals `Vector3`/`Quaternion` across the native boundary, and those types come from
`OpenMetaverse.Types`, not from OpenSim. The branch and upstream disagree about that package:

- upstream `cbdfba2811` `Directory.Build.props`: `PackageReference OpenMetaverse* 1.0.6`
- branch HEAD: those three references commented out, replaced by `<Reference>` elements pointing
  at vendored `Library/OpenMetaverse*.dll` (**1.2.13**, stock/unpatched), because per the
  in-file comment 1.0.6 "is not on the OpenSim-NGC feed" and restores fail NU1101.

The whole tree compiles against the vendored 1.2.13 consistently — `Directory.Build.props`
applies repo-wide — so there is no intra-tree mismatch and nothing is broken by it. But upstream's
core was authored against a different OMV than the one this branch links, and the affected types
are exactly the ones crossing into `joltc` through `unsafe`/`float*` paths in
`JoltPhysicsBackend`.

**NOT ESTABLISHED:** whether `Vector3`/`Quaternion` field layout is identical between OMV 1.0.6
and 1.2.13. I could not compare them — 1.0.6 is not in the local package cache and is reportedly
unobtainable from the feed. Layout change in these structs would be a silent marshalling
corruption, not a compile error. I rate this **low likelihood** (these structs have been three
and four contiguous floats for the lifetime of the library) but explicitly **unverified**, and it
is physics-relevant enough to name.

---

## 5. Runtime verification plan

Nothing in §1 or §4 can be closed statically. The following is the smallest sequence that
exercises every unproven link: native resolution, ABI match, the heightfield/body/step path, and
the patched-allocator coupling that is the entire reason for the vendored DLL.

Run on the win-x64 deploy target. Each step states what to run, what to observe, and what
constitutes failure. Stop at the first failure — later steps assume earlier ones passed.

### Step 0 — Guard the native before booting (seconds)

```
powershell -File Source\OpenSim.Region.PhysicsModules.LegionJolt\assert-patched-joltc.ps1 -PublishDir "D:\legiongrid\regionserver"
```

- **Pass:** `joltc.dll PATCHED build OK (16AF7638...)`, exit 0.
- **Fail:** exit 1 with `STOCK NuGet 1.0.4` or `UNKNOWN build`. Do not boot — §1.4's clobber
  scenario has occurred.

Because the guard only covers win-x64 (§1.4), also confirm by hand that the deployed
`runtimes\win-x64\native\joltc.dll` is the only `joltc.dll` on the probe path for this RID.

### Step 1 — Does the native load at all? (first-use proof)

Configure one region with `[Startup] physics = Jolt` and boot the region server.

- **Observe:** `[LEGION JOLT] enabled (physics = Jolt).` in the log, then the region reaching a
  steady heartbeat.
- **Failure mode this catches:** `DllNotFoundException: joltc` — the §1.2 resolution break. Note
  this will **not** appear at process start; it appears when the scene first constructs the
  physics backend. A region that starts and then throws on first physics touch *is* this failure.
- **Also watch for:** `BadImageFormatException` or `EntryPointNotFoundException`, which would
  indicate an ABI mismatch between the patched native and the managed binding (§1.5's concern
  if anyone bumped the package).

### Step 2 — Does the managed↔native round trip actually work?

At the region console:

```
jolt terraintest
jolt probe 128 128
```

- **Observe:** reported hit Z values consistent with the region heightmap. `terraintest` sweeps
  interior and far-edge probes and exists specifically to prove the cooked collision surface
  rather than "it booted".
- **Fail:** no hits, zero/NaN Z, or an exception. That means the heightfield never cooked —
  a real native-path failure even though the process is up.

### Step 3 — Body creation, stepping, and collision dispatch

```
jolt rezprims
jolt rayprims
jolt droptest
jolt collidetest
```

- **Observe:** `rayprims` reporting hits on the rezzed bodies; `droptest` showing bodies falling
  and coming to rest; `collidetest` printing `PASS` (it verifies a subscribed prim receives
  collision dispatch through the real Phlox VM path via `llSay` → `OnChatFromWorld`).
- **Fail:** `CHECK`/`FAIL` from `collidetest`, no ray hits, or bodies that never settle.

Clean up with `jolt clearprims`.

### Step 4 — The decisive one: two regions stepping concurrently

This is the only step that proves the **patched** allocator is in play rather than merely
present on disk. Everything above passes with the stock native too.

Boot **two** regions in the same process, both with `physics = Jolt`, both with active physics
(rez prims in each via `jolt rezprims`, or use `jolt droptest` in both), and let them step
together for several minutes.

- **Pass:** both regions continue stepping; no abort; no allocator diagnostics.
- **Fail:** `TempAllocator: Freeing in the wrong order` followed by `std::abort()` — the process
  dies. That is the stock-native signature (`native/joltc/README.md`, reproduced on Legion
  2026-08-02) and means the patched DLL is not the one actually loaded, regardless of what the
  hash on disk says.

Single-region testing cannot distinguish patched from stock. **If you run only one step from this
plan, run this one** — with Step 0 first.

### Step 5 — Optional A/B sanity

The tree ships a `parity` console command (`parity terrain | ramp | drop | core | boat`) designed
for booting the same scenario under `physics = BulletSim` and `physics = Jolt` and comparing.
Useful for confirming the net10 build behaves as the net8 build did, if a baseline exists.

### What this plan does not cover

- The OMV 1.0.6-vs-1.2.13 layout question (§4.4) is only indirectly exercised. Steps 2–3 passing
  is good evidence that `Vector3` marshals correctly, since wrong layout would produce garbage
  positions and failed raycasts — but it is evidence, not proof.
- win-arm64 is not covered and cannot be made safe by testing: no patched arm64 native exists
  (§1.4). Treat arm64 as unsupported.
- Whether `WebRtcJanusService.Tests` runs under `dotnet test` with Test SDK 17.14.1 on net10.0
  (§2.1) is independent of the physics path; verify separately by running that one test project.

---

## Summary of items needing attention

| # | Item | Severity | Action |
|---|---|---|---|
| 1 | Patched native is found only via the *excluded* stock package's `deps.json` runtimeTarget. Changing `ExcludeAssets="all"` to `PrivateAssets="all"` would break native load at first use (§1.2) | **High — latent trap** | Add a comment recording the coupling so nobody "cleans it up" |
| 2 | `Legion.Physics.csproj:38` and `JoltPhysicsBackend.cs:10` both justify the pin with an obsolete and factually wrong net8.0 rationale, and disagree on the version (§1.1, §2.2) | **Medium** | Rewrite to cite the real reason: joltc ABI coupling to JoltPhysics.Native 1.0.4 |
| 3 | 2.19.1 is 10 releases behind (cache lists to 2.22.0). Keep the pin — but for the ABI reason, not net8.0 (§1.5) | Informational | No upgrade without rebuilding the patch against newer joltc |
| 4 | Stock arm64 joltc + both `joltc_double` ship into output; guard checks win-x64 only (§1.4) | **Medium** | Harmless on x64; treat arm64 as unsupported, consider widening the guard |
| 5 | `WebRtcJanusService.Tests` still on Test SDK 17.14.1 vs sibling's 18.8.1, both net10.0 (§2.1) | **Medium** | Align, or verify `dotnet test` works on the older SDK |
| 6 | Runtime verification never performed; §5 Step 4 is the decisive check | **Do before deploy** | Execute §5 |
| 7 | OMV 1.0.6 vs vendored 1.2.13 struct layout unverified (§4.4) | Low, unverified | Steps 2–3 give indirect evidence |
| 8 | Untracked stale `Source/Legion.Physics/bin/` leftover (§2.3) | Cosmetic | Delete when convenient |

**Nothing in this audit indicates the merged result is wrong.** The physics API surface is
semantically untouched by upstream (§4.3) and there is no Jolt overlap at all (§3). The open risk
is entirely in the native packaging and the untested runtime path, not in the merge. No fixes
were made.
