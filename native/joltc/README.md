# Patched joltc - how the Jolt module's native is made

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) needs a patched build of
joltc, the C API over Jolt Physics that JoltPhysicsSharp binds to. The compiled files live in the
module's `runtimes/<rid>/native/` folders. They are built by a GitHub Actions workflow,
`.github/workflows/joltc-native.yml`, from pinned sources plus the patches in this folder, so
anyone can rebuild them and check the result against the hashes below.

## Why the native is patched

1. **One TempAllocator per physics system** (`per-system-tempallocator.patch`). Stock joltc gives
   every physics system the same process-global `TempAllocatorImpl`. That allocator is a LIFO
   stack and is not thread-safe. With several regions stepping in parallel their frames interleave
   on it and Jolt stops the process with `TempAllocator: Freeing in the wrong order` and
   `std::abort()`. The managed backend (`JoltPhysicsBackend._simLock`) locks per region, so it
   relies on each `JPH_PhysicsSystem` owning its own allocator. **Stock joltc with per-region locks
   aborts as soon as two regions step at once.**
2. **A lock on the global map of systems** (`physics-systems-map-lock.patch`). joltc keeps every
   system in `s_PhysicsSystems`, a global map written by `JPH_PhysicsSystem_Create` and
   `JPH_PhysicsSystem_Destroy` and read by the step-listener callback, with no lock. Regions start
   and stop on different threads while others run, so two threads could change the map at once.

Neither patch changes an export name or signature: the patched native is a drop-in for the stock
one under JoltPhysicsSharp 2.19.1.

## Files

| File | What |
|---|---|
| `native/joltc/per-system-tempallocator.patch` | Patch 1 against joltc (a git diff; applies with `git apply`) |
| `native/joltc/physics-systems-map-lock.patch` | Patch 2, applied after patch 1 |
| `native/joltc/exports.txt` | The 1086 names the native must export, the same set as the stock natives of `JoltPhysics.Native 1.0.4` |
| `native/joltc/list-exports.py` | Lists the exported names of a PE (`.dll`) or ELF (`.so`) file; with `--expect exports.txt` it checks them |
| `.github/workflows/joltc-native.yml` | Builds the natives on GitHub's runners |
| `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/win-x64/native/joltc.dll` | win-x64 build, loaded by the module |
| `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/linux-x64/native/libjoltc.so` | linux-x64 build, shipped but not loaded yet |
| `Source/OpenSim.Region.PhysicsModules.Jolt/assert-patched-joltc.ps1` | Checks that every `joltc*.dll` in an output or publish directory is the patched build |

The module copies the win-x64 `joltc.dll` to the output root, which is the copy the region server
loads, and under `runtimes/win-x64/native/`.

## The recipe

Sources, each pinned by commit:

- joltc: https://github.com/amerkoleci/joltc at `1715c5aab834a5bb0c344dc4a11d573ad6f9736d`
  ("Improve and add more bindings for HeightFieldShapeSettings"), the source behind the
  `JoltPhysics.Native 1.0.4` package that `JoltPhysicsSharp 2.19.1` depends on.
- Jolt Physics: https://github.com/jrouwe/JoltPhysics at `036ea7b1d717b3e713ac9d8cbd47118fb9cd5d60`,
  the commit of tag `v5.4.0`, which joltc's CMakeLists fetches. The workflow places it in
  `<joltc>/JoltPhysics`, which joltc's CMakeLists uses instead of fetching, so no tag is resolved
  at build time.

Steps, per target (the workflow does exactly this):

1. Check out joltc and Jolt Physics at those commits.
2. `git apply` `per-system-tempallocator.patch`, then `physics-systems-map-lock.patch`.
3. Configure and build with the options of joltc's own CI at that commit
   (`.github/workflows/build.yml` there), plus `-DJPH_SAMPLES=OFF` and `--target joltc`, which
   leave the library itself unchanged:

   | Target | Runner | Configure | Output |
   |---|---|---|---|
   | win-x64 | `windows-2022` (MSVC 19.44) | `-G "Visual Studio 17 2022" -A x64 -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String=SDK`, with `CXXFLAGS=-Brepro LDFLAGS=-Brepro` | `build/bin/Distribution/joltc.dll` |
   | linux-x64 | `ubuntu-22.04` (GCC 11.4) | `-G "Unix Makefiles" -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String=SDK`, then `strip --strip-unneeded` | `build/lib/libjoltc.so` |

   Build: `cmake --build build --config Distribution --target joltc --parallel`.
4. Hash the file, list its exports, and fail unless they equal `exports.txt`.
5. Upload the file, `SHA256SUMS`, `exports.txt` and `build-info.txt` (the commits, the runner
   image, the CMake and compiler versions) as the artifact `joltc-<rid>`.

`-Brepro` makes MSVC write content hashes instead of time stamps into the DLL. The sources are
built under the runner's temporary folder rather than the checkout, so the paths compiled into the
file do not depend on the repository's name.

The workflow runs on pushes that touch `native/joltc/` or the workflow itself. Its permissions are
read-only, it uses no secrets, and each action it uses is pinned by commit. A new target (for
example linux-arm64 or osx) is one more entry in its matrix.

## The files in the repository

| File | SHA-256 |
|---|---|
| `runtimes/win-x64/native/joltc.dll` | `1f855744227482146708ab9af683f4975cfc4c262030e22daace855f9d7479b6` |
| `runtimes/linux-x64/native/libjoltc.so` | `eead7c1aa7fdfac07132e26913e03b268dfd825ca72ffe6cec3a181da2ec95bb` |

Stock `JoltPhysics.Native 1.0.4` win-x64 `joltc.dll` (must not be used with this module) has
SHA-256 `67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910`.

## Check a file

Against the table above, or against the `SHA256SUMS` of a workflow run:

```
sha256sum joltc.dll
```

PowerShell: `Get-FileHash joltc.dll -Algorithm SHA256`.

The exports, which must match whatever compiler built the file:

```
python native/joltc/list-exports.py joltc.dll --expect native/joltc/exports.txt
```

It prints `1086 exported, 1086 expected, 0 missing, 0 extra` and exits 0 for a good file.

A rebuild with a different compiler version gives a different hash. Then the export check, the Jolt
test suite (`Tests/OpenSim.Region.PhysicsModules.Jolt.Tests`) with the new file in place, and a
simulator with several regions whose log shows no `Freeing in the wrong order` or
`AccessViolation` are the checks.

## What the patches change

`per-system-tempallocator.patch` removes the process-global `s_TempAllocator` and gives each
`JPH_PhysicsSystem` its own `TempAllocatorImplWithMallocFallback(8 MB)`, created in
`JPH_PhysicsSystem_Create` and freed in `JPH_PhysicsSystem_Destroy`. Every site that used the
global allocator now uses the system's own:

1. `JPH_PhysicsSystem_Update`
2. `JPH_CharacterVirtual_Update`
3. `JPH_CharacterVirtual_ExtendedUpdate`
4. `JPH_CharacterVirtual_RefreshContacts`
5. `JPH_CharacterVirtual_WalkStairs`
6. `JPH_CharacterVirtual_StickToFloor`
7. `JPH_CharacterVirtual_SetShape`

Each CharacterVirtual entry point already takes the owning `JPH_PhysicsSystem*`, so no other
bookkeeping is needed. This is the same model as BulletSim's per-world scratch memory.

`physics-systems-map-lock.patch` adds a mutex beside `s_PhysicsSystems` and holds it around every
access to the map: the insert in `JPH_PhysicsSystem_Create`, the erase in
`JPH_PhysicsSystem_Destroy`, and the lookup in `ManagedPhysicsStepListener::OnStep`, which now uses
`find` instead of `operator[]` so that a lookup never inserts. Nothing else is called while the
mutex is held. The managed backend also serialises system create and destroy
(`JoltPhysicsBackend.s_systemMapGate`), so it stays safe with a joltc that lacks this patch.

Before moving to a newer joltc, check that all seven allocator sites use a per-system allocator
and that every access to the map of systems is locked.

## Licences

- joltc: MIT, Copyright (c) Amer Koleci and Contributors. `ThirdPartyLicenses/joltc.txt`.
- Jolt Physics (compiled into the native): MIT, Copyright 2021 Jorrit Rouwe.
  `ThirdPartyLicenses/JoltPhysics.txt`.
- JoltPhysicsSharp, the managed binding: MIT, Copyright (c) Amer Koleci and Contributors.
  `ThirdPartyLicenses/JoltPhysicsSharp.txt`.
