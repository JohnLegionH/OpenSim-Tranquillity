# joltc - the Jolt module's native, stock and patched

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) runs on joltc, the C API over
Jolt Physics that JoltPhysicsSharp binds to. By default it uses the stock joltc of the
`JoltPhysics.Native` package that JoltPhysicsSharp 2.19.1 depends on: a build puts the package's
files in the output under `runtimes/<rid>/native/`, and the module checks the file's hash against
its record (`JoltNative.Known`) before loading it. On the stock joltc the module runs **one job
pool** (`[Jolt] JobPools`), whatever the setting asks for.

This folder also holds the recipe for a patched build of joltc, which is safe for more than one
job pool. The compiled files are kept in the repository under the module's
`runtimes/<rid>/native/` folders; no build copies them into an output. They are built by a GitHub
Actions workflow, `.github/workflows/joltc-native.yml`, from pinned sources plus the patches in
this folder, so anyone can rebuild them and check the result against the hashes below.

## One job pool on the stock native, more on the patched one

Every physics update runs on a job pool, and each pool admits one update at a time. Regions on one
pool take turns; regions on different pools step at the same time.

1. **One TempAllocator per physics system** (`per-system-tempallocator.patch`). Stock joltc gives
   every physics system the same process-global `TempAllocatorImpl`. That allocator is a LIFO
   stack and is not thread-safe. With two regions stepping at the same time their frames
   interleave on it and Jolt stops the process with `TempAllocator: Freeing in the wrong order`
   and `std::abort()`. On one pool that cannot happen: every use of the allocator is inside a
   physics update, under the pool's gate (`ShapeAndAllocatorRuleTests` checks this). With two or
   more pools it can, so on the stock native the module runs one pool and logs a warning at start
   when `[Jolt] JobPools` asks for more. The patched build gives each `JPH_PhysicsSystem` its own
   allocator, so every pool may step at once.
2. **A lock on the global map of systems** (`physics-systems-map-lock.patch`). joltc keeps every
   system in `s_PhysicsSystems`, a global map written by `JPH_PhysicsSystem_Create` and
   `JPH_PhysicsSystem_Destroy` and read by the step-listener callback, with no lock. The module
   uses no step listener and serialises create and destroy itself
   (`JoltPhysicsBackend.s_systemMapGate`), so this patch matters only to code that adds one.

Neither patch changes an export name or signature: the patched native is a drop-in for the stock
one under JoltPhysicsSharp 2.19.1.

## Using the patched build

An operator who wants more than one job pool replaces the stock file in the output with the
patched build for the platform, under the same name:

| Platform | Replace | With |
|---|---|---|
| win-x64 | `runtimes/win-x64/native/joltc.dll` | `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/win-x64/native/joltc.dll` |
| linux-x64 | `runtimes/linux-x64/native/libjoltc.so` | `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/linux-x64/native/libjoltc.so` |

(For a build or publish for one runtime identifier, the file sits beside the application's
assemblies instead of under `runtimes/`; replace it there.) The module recognises the patched build
by its hash: its start line says `patched build (native/joltc); safe for more than one job pool`,
and `[Jolt] JobPools` takes effect. A rebuild of the output puts the stock file back, so replace
the file again after each build. `assert-joltc-native.ps1 -PublishDir <output> -RequirePatched`
checks that the replacement is in place. There is no patched build for other platforms; they run
one pool.

## Files

| File | What |
|---|---|
| `native/joltc/per-system-tempallocator.patch` | Patch 1 against joltc (a git diff; applies with `git apply`) |
| `native/joltc/physics-systems-map-lock.patch` | Patch 2, applied after patch 1 |
| `native/joltc/exports.txt` | The 1086 names the native must export, the same set as the stock natives of `JoltPhysics.Native 1.0.4` |
| `native/joltc/list-exports.py` | Lists the exported names of a PE (`.dll`) or ELF (`.so`) file; with `--expect exports.txt` it checks them |
| `.github/workflows/joltc-native.yml` | Builds the natives on GitHub's runners |
| `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/win-x64/native/joltc.dll` | win-x64 patched build (not copied by any build) |
| `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/linux-x64/native/libjoltc.so` | linux-x64 patched build (not copied by any build) |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.cs` | Picks the file for the running platform, checks its hash against the tables below and loads it |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.targets` | Keeps the package's unused files (`joltc_double.dll`, Android) out of an application's output |
| `Source/OpenSim.Region.PhysicsModules.Jolt/assert-joltc-native.ps1` | Checks that an output or publish directory holds recorded joltc builds under `runtimes/<rid>/native/` and no other joltc; with `-RequirePatched`, the patched builds |

The `JoltPhysics.Native` package reaches every application that carries the module (the region
server, the Jolt tests, the Jolt harness) through the backend's package reference. A build with
no runtime identifier puts each platform's file under `runtimes/<rid>/native/` (macOS:
`runtimes/osx/native/`, one file for both architectures); a build or publish for one runtime
identifier puts that platform's file beside the application's assemblies. At start the module
loads the file for the platform it runs on (`JoltNative`), from `runtimes/` first, so a portable
build works on each supported platform.

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
   leave the library itself unchanged, and `-DCROSS_PLATFORM_DETERMINISTIC=ON` (below):

   | Target | Runner | Configure | Output |
   |---|---|---|---|
   | win-x64 | `windows-2022` (MSVC 19.44) | `-G "Visual Studio 17 2022" -A x64 -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String=SDK`, with `CXXFLAGS=-Brepro LDFLAGS=-Brepro` | `build/bin/Distribution/joltc.dll` |
   | linux-x64 | `ubuntu-22.04` (GCC 11.4) | `-G "Unix Makefiles" -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String=SDK`, then `strip --strip-unneeded` | `build/lib/libjoltc.so` |

   Build: `cmake --build build --config Distribution --target joltc --parallel`.
4. Hash the file, list its exports, and fail unless they equal `exports.txt`.
5. Upload the file, `SHA256SUMS`, `exports.txt` and `build-info.txt` (the commits, the runner
   image, the CMake and compiler versions) as the artifact `joltc-<rid>`.

`CROSS_PLATFORM_DETERMINISTIC` is Jolt's option to compute the same results on every platform
(on MSVC it builds with `/fp:precise` instead of `/fp:fast`). With it, the Jolt harness's traces on
the Windows and Linux runners agree to the last printed digit over whole runs, crash sweeps
included; without it they part within the first seconds of contact, and a crash could end
differently on the two. The busiest harness scenario took no longer with it. On Linux (GCC) the
library it gives is byte for byte the same as without it.

`-Brepro` makes MSVC write content hashes instead of time stamps into the DLL. The sources are
built under the runner's temporary folder rather than the checkout, so the paths compiled into the
file do not depend on the repository's name.

The workflow runs on pushes that touch `native/joltc/` or the workflow itself. Its permissions are
read-only, it uses no secrets, and each action it uses is pinned by commit. A new target (for
example linux-arm64 or osx) is one more entry in its matrix.

## The files in the repository

These hashes are also recorded in `JoltNative.Known` (as patched builds, safe for more than one
job pool) and in `assert-joltc-native.ps1`. Unit tests in `JoltNativeTests` check the files, this
table and the script against `JoltNative.Known`. Replacing a file means updating all three.

| File | SHA-256 |
|---|---|
| `runtimes/win-x64/native/joltc.dll` | `961002617000c9f2da76b31b816b4185e04361114fb46a1ddc4c95d07fbef844` |
| `runtimes/linux-x64/native/libjoltc.so` | `eead7c1aa7fdfac07132e26913e03b268dfd825ca72ffe6cec3a181da2ec95bb` |

## The stock files

The files of the `JoltPhysics.Native` package the projects use, read from the package. All are
recorded as not safe for more than one job pool. This project's tests and harness run on win-x64
and linux-x64; the other platforms are recorded but not tested here, and the module logs a notice
saying so when it starts on one. `JoltNativePackageTests` reads the restored package and fails
when its files differ from this record, for example after a version bump, listing what to check
before the new hashes are recorded.

| Package | File | SHA-256 |
|---|---|---|
| `JoltPhysics.Native 1.0.4` | `win-x64/joltc.dll` | `67becfc70cfbda643ab9b75aba895042900c3e339b001080ba4107e4929b0910` |
| `JoltPhysics.Native 1.0.4` | `linux-x64/libjoltc.so` | `5fc051708bdd05031a816796612a2f87e17ed32cc198f195a43b2305b3d990fd` |
| `JoltPhysics.Native 1.0.4` | `win-arm64/joltc.dll` | `b0a7d05151a8e504765a39e23b2ec196b88b3bf2ce3e8b884161e6925e7578e4` |
| `JoltPhysics.Native 1.0.4` | `linux-arm64/libjoltc.so` | `fde70508c826370b5cf6bb54c6ea9ceeab0c37c826bcd71579ebb9b4ac3e0d61` |
| `JoltPhysics.Native 1.0.4` | `osx/libjoltc.dylib` | `39e4a8728307e48026d965d8ab661348a8808a9a2afddde6ce58272b37d83e91` |

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
`AccessViolation` are the checks. The module loads such a file only with
`[Jolt] AllowUnrecordedNative = true`, and runs one job pool on it, since it cannot tell from the
hash that the file has the per-system allocator; recording its hash in `JoltNative.Known` lifts
both.

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
