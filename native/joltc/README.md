# Patched joltc.dll — per-system TempAllocator

Legion's Jolt physics REQUIRES this patched native. The stock NuGet
`JoltPhysics.Native 1.0.4` joltc.dll supplies ONE process-global
`TempAllocatorImpl` (a LIFO stack, not thread-safe) to every physics system.
With multiple regions stepping in parallel that shared scratch produces
`TempAllocator: Freeing in the wrong order` → `std::abort()` (proven on
Legion, 2026-08-02). The managed backend (`JoltPhysicsBackend._simLock`,
per-INSTANCE since 2026-08-03) relies on each `JPH_PhysicsSystem` owning its
own allocator — **stock joltc.dll + instance locks = crashes return.**

`deploy-jolt-legion.ps1` asserts the live bin's joltc.dll is this build and
fails loudly otherwise. If a build/restore ever clobbers a bin copy with the
stock NuGet DLL, restore from `win-x64/joltc.dll` in this directory.

## Contents

| File | What |
|---|---|
| `per-system-tempallocator.patch` | The complete source patch (git diff, applies with `git apply`) |
| `win-x64/joltc.dll` | The patched build, ready to deploy |

Patched `win-x64/joltc.dll` SHA-256:

    16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5

Stock 1.0.4 (the one that must NOT be deployed): `67BECFC7 0CFBDA64 3AB9B75A
BA895042 900C3E33 9B001080 BA4107E4 929B0910`.

## Provenance

- Upstream: https://github.com/amerkoleci/joltc
- Commit: `1715c5aab834a5bb0c344dc4a11d573ad6f9736d`
  (2025-10-10, "Improve and add more bindings for HeightFieldShapeSettings")
  — the exact source of the shipped JoltPhysics.Native **1.0.4** win-x64
  binary. Established from the packaging repo (amerkoleci/JoltPhysicsSharp @
  `ba2f3068`, whose vendored `native/win-x64/joltc.dll` is byte-identical to
  the nupkg's) plus the committed debug PDB (CI workspace `D:\a\joltc\joltc`,
  Windows SDK 10.0.26100.0) and timestamp correlation; then CONFIRMED by an
  unpatched rebuild exporting the identical 1086-symbol set.
- Jolt Physics: **v5.4.0**, pulled automatically by CMake FetchContent
  (`GIT_TAG v5.4.0` in joltc's CMakeLists.txt — no submodule to manage).

## What the patch changes (all 7 shared-scratch sites)

Replaces the process-global `s_TempAllocator` (removed entirely) with a
`TempAllocatorImplWithMallocFallback(8MB)` owned by each `JPH_PhysicsSystem`
(created in `JPH_PhysicsSystem_Create`, freed in `JPH_PhysicsSystem_Destroy`),
wired through every consuming site:

1. `JPH_PhysicsSystem_Update`
2. `JPH_CharacterVirtual_Update`
3. `JPH_CharacterVirtual_ExtendedUpdate`
4. `JPH_CharacterVirtual_RefreshContacts`
5. `JPH_CharacterVirtual_WalkStairs`
6. `JPH_CharacterVirtual_StickToFloor`
7. `JPH_CharacterVirtual_SetShape`

(Each CharacterVirtual entry point already takes the owning
`JPH_PhysicsSystem*`, so no extra bookkeeping was needed.)

**No ABI change**: export names/signatures are untouched — the patched DLL is
a drop-in for the stock one under JoltPhysicsSharp 2.19.1. This matches
BulletSim's per-world and InWorldz PhysX's per-PxScene scratch model.

## Rebuild from scratch

Toolchain: Visual Studio 2022 (MSVC v143, C++17), CMake ≥ 3.16, git,
network access to github.com.

```powershell
git clone https://github.com/amerkoleci/joltc D:\joltc-build
git -C D:\joltc-build checkout 1715c5aab834a5bb0c344dc4a11d573ad6f9736d
git -C D:\joltc-build apply path\to\per-system-tempallocator.patch

cmake -S "D:\joltc-build" -B "D:\joltc-build\build_win_64" `
      -G "Visual Studio 17 2022" -A x64 `
      -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String="SDK"
cmake --build "D:\joltc-build\build_win_64" --config Distribution
# -> D:\joltc-build\build_win_64\bin\Distribution\joltc.dll
```

(The recipe is exactly upstream CI's `.github/workflows/build.yml` win-x64
step. Expect benign warnings only: C4530 in `__msvc_ostream.hpp`, LINK C4743
vftable-size — both present in the unpatched CI build too.)

## Verify a rebuild

The hash will differ across compiler versions; the ABI check is the export
set. Dump exports and compare against stock — must be **1086 = 1086 with zero
differences in either direction**:

```powershell
$dumpbin = "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe"
& (Resolve-Path $dumpbin)[0] /exports your\joltc.dll
& (Resolve-Path $dumpbin)[0] /exports "$env:USERPROFILE\.nuget\packages\joltphysics.native\1.0.4\runtimes\win-x64\native\joltc.dll"
```

Then boot a multi-region grid UNPATCHED-baseline-first if bisecting, or run
the full stress sequence (multi-region + vehicle + llCastRay load test,
`docs/jolt-llcastray-loadtest.lsl`) and grep the logs for
`Freeing in the wrong order|AccessViolation` — expect 0.

## History / related

- 2026-08-02: shared-allocator crash root-caused; `_simLock` made static as
  stopgap (serialised all regions).
- 2026-08-02/03: this patch built + verified (exports 1086/1086, boot, stress
  test); `_simLock` reverted to per-instance — regions step in parallel.
- The full investigation trail (commit identification, PDB forensics,
  verification runs) is in the comment block above `_simLock` in
  `OpenSim/Addons/LegionPhysics/Legion.Physics/JoltPhysicsBackend.cs`.
- Upstream later added a TempAllocator C-API + `JPH_PhysicsSystem_Update2`
  (joltc PR #74, first shipped in JoltPhysics.Native 1.1.0 / Jolt 5.6.0) —
  but it does NOT cover the six CharacterVirtual sites. If Legion ever
  upgrades packages, re-audit against this list of 7.

## ★ TODO — next time this native is patched: lock `s_PhysicsSystems`

`s_PhysicsSystems` (the global `UnorderedMap<PhysicsSystem*, JPH_PhysicsSystem*>`)
is **inserted in `JPH_PhysicsSystem_Create` and erased in `JPH_PhysicsSystem_Destroy`
with no lock.** In a real grid, region bring-up and teardown are **concurrent**
(region restarts are routine, and multiple regions boot/stop in parallel), so two
threads can mutate that map at once — the **same failure family** as the shared-
TempAllocator bug this patch fixed (unsynchronised shared native state under
concurrent regions). It has not bitten yet only because Legion ran few regions and
rarely restarted them under load.

**Fix when next touching the native:** guard the `s_PhysicsSystems` insert/erase
(and any lookup that races them) with a mutex — e.g. a `static std::mutex` taken in
`JPH_PhysicsSystem_Create`/`_Destroy`. Fold it into `per-system-tempallocator.patch`
(or a follow-on patch) and bump the verify step. No ABI change. This is a **known,
unfixed concurrency gap**, recorded here so it is actioned rather than rediscovered.
(Managed note: the port's shared-thread-pool fix — see `tranq-migration-plan.md`
Jolt track, design item #1 — is a separate managed change; this one is native.)
