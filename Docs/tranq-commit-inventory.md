# Tranquillity — local commit inventory (since `81e5c244`)

Baseline: **`81e5c244`** (upstream/develop, .NET 8) — everything before it is upstream history.
This inventory covers every local commit on `tranq-baseline` after that point, grouped by upstream
disposition. **Groupings were verified against the actual diffs, not commit labels** — see
§"Cross-category flags" for the cases where a label and the diff disagree (those are what make a
future cherry-pick fail).

> **Push discipline:** this branch pushes to **`origin`** (`JohnLegionH/OpenSim-Tranquillity`) only.
> `upstream` (OpenSim-NGC) has its push URL disabled; never push there.
>
> **Provenance note:** `origin/tranq-baseline` already existed at **`7db9e3d84f`** before the
> 2026-08-11 push (that first LibOMV-vendor commit had been pushed earlier), so the "never pushed"
> premise held for 12 of the 13 commits, not all 13. All were secret-audited CLEAN regardless.

---

## UPSTREAM-BOUND — PR candidates for Mike (clean-checkout build fixes)
These fix what a clean checkout / CI / Docker build hits; they touch only upstream-shared files.

| SHA | Summary | Files |
|---|---|---|
| `70e41d3374` | Dockerfiles for hosted Region/Grid servers (aspnet base, SkiaSharp natives, non-root writes) | `Source/OpenSim.Server.GridServer/Dockerfile`, `Source/OpenSim.Server.RegionServer/Dockerfile` |
| `ff19111f86` | `--skip-restore` → `--no-restore` (Region/Grid Dockerfiles) | same 2 Dockerfiles |
| `061c45040a` | RegionServer must reference the Groups addon (dropped in the Source/ restructure) | `Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj` (+1 line) |

**Removed from this group (mislabel — see flags):** `3fd2a96ece` → it is Legion-specific, not upstream-bound.

## LEGION-SPECIFIC — never upstream
Deviations and Legion-exclusive features; do not PR to NGC.

| SHA | Summary | Files |
|---|---|---|
| `7db9e3d84f` | LOCAL DEVIATION: vendor LibOMV 1.2.13 (upstream pins nonexistent 1.0.6) | `Directory.Build.props`, `Library/OpenMetaverse{,.Types,.StructuredData}.dll` |
| `cf85d87dc1` | Runtime orchestration compose (explicitly NOT an upstream candidate) | `compose.runtime.yaml` (new) |
| `3fd2a96ece` | Legion deployment: compose runtime fixes from Gate-3 bring-up **(re-classified here)** | `compose.runtime.yaml` |
| `dac33d3178` | Legion Market: port DirectDelivery Robust ServiceConnector (Legion-exclusive) | `Source/OpenSim.Server.Handlers/DirectDelivery/DirectDeliveryConnector.cs` (new), `…/DirectDeliveryPostHandler.cs` (new), **`.gitignore` (+4)** |
| `f2a150a6a9` | Mount `osslEnable.ini` into the region container | `compose.runtime.yaml` |

## JOLT — own stack (possible future upstream track, but self-contained)
The 4-slice Jolt port + the one measured tuning change. Self-contained under
`Source/OpenSim.Region.PhysicsModules.LegionJolt/` + `Addons/LegionPhysics/` + `native/joltc/`, with
thin wiring into two shared files (see flags).

| SHA | Summary | Files |
|---|---|---|
| `c5c032bd13` | Jolt slices 1-2: RC module skeleton + `Legion.Physics` backend + patched native (coupled) | `Addons/LegionPhysics/Legion.Physics/*` (interface, backend, csproj), `Source/OpenSim.Region.PhysicsModules.LegionJolt/*` (scene, PluginRegistration, csproj, hash-guard, `runtimes/win-x64/native/joltc.dll`), `native/joltc/{README.md,per-system-tempallocator.patch}`, **`Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj` (+1)**, **`Tranquillity.sln`** |
| `282cb71276` | Jolt slice 3: `Legion.Vehicles` (backend-agnostic Halcyon vehicle controller) | `Addons/LegionPhysics/Legion.Vehicles/*` (6 files), `…/OpenSim.Region.PhysicsModules.LegionJolt.csproj` (+3), **`Tranquillity.sln`** |
| `91ee099737` | Jolt slice 4: real region module (JoltPrim/JoltCharacter/JoltVehicleBody + full scene) + instrumentation | `Source/OpenSim.Region.PhysicsModules.LegionJolt/{JoltCharacter,JoltMetrics,JoltPrim,JoltVehicleBody,LegionJoltScene}.cs` |
| `c0ba4088ae` | Jolt design item #1: one shared, process-capped JobSystemThreadPool | `Addons/LegionPhysics/Legion.Physics/JoltPhysicsBackend.cs` |

## REPO HYGIENE
| SHA | Summary | Files |
|---|---|---|
| `f9c741dfb2` | Harden `.gitignore`: pattern-based secret coverage (extension-scoped) | `.gitignore` |

---

## ★ Cross-category flags (verified against diffs — these are the cherry-pick hazards)

1. **`3fd2a96ece` was labeled UPSTREAM-BOUND but is LEGION-SPECIFIC.** Its diff touches **only
   `compose.runtime.yaml`** — the Legion runtime-orchestration file that `cf85d87dc1` explicitly marks
   "NOT an upstream candidate." Do **not** PR it to NGC. Re-classified under Legion-specific above.

2. **`Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj` is edited by TWO
   categories:** the upstream-bound `061c45040a` (Groups ProjectReference) **and** the Jolt
   `c5c032bd13` (LegionJolt ProjectReference). Cherry-picking either onto a tree without the other will
   conflict in that csproj. When PRing `061c45040a` upstream, take **only** the Groups line, not the
   LegionJolt line.

3. **`Tranquillity.sln` is edited by two Jolt commits** (`c5c032bd13`, `282cb71276`) — solution-file
   merges are noisy; expect trivial-but-manual conflicts if these are reordered or partially applied.

4. **`dac33d3178` (Legion DirectDelivery) also carries 4 lines of `.gitignore`** (the
   DirectDeliverySecret.ini ignores). Harmless, but the `.gitignore` hunk is separable from the
   Legion-exclusive source if that commit is ever split.

5. **`7db9e3d84f` (LibOMV vendor) commits binary DLLs** under `Library/` — a deliberate local deviation
   (remove when NGC publishes real OMV packages). Not a cherry-pick candidate; noted so it isn't mistaken
   for upstream content.
