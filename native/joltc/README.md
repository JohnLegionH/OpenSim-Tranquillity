# Patched joltc - one TempAllocator per physics system

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) needs a patched build of
joltc, the C API over Jolt Physics that JoltPhysicsSharp binds to.

The stock `JoltPhysics.Native 1.0.4` joltc gives every physics system the same process-global
`TempAllocatorImpl`. That allocator is a LIFO stack and is not thread-safe. With several regions
stepping in parallel, their frames interleave on it and Jolt stops the process with
`TempAllocator: Freeing in the wrong order` and `std::abort()`. The managed backend
(`JoltPhysicsBackend._simLock`) locks per region, so it relies on each `JPH_PhysicsSystem` owning
its own allocator. **Stock joltc with per-region locks aborts as soon as two regions step at once.**

## Files

| File | What |
|---|---|
| `native/joltc/per-system-tempallocator.patch` | The source patch against joltc (a git diff; applies with `git apply`) |
| `Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/win-x64/native/joltc.dll` | The patched build, win-x64 |
| `Source/OpenSim.Region.PhysicsModules.Jolt/assert-patched-joltc.ps1` | Checks that every `joltc*.dll` in an output or publish directory is the patched build |

The module copies the patched `joltc.dll` to the output root, which is the copy the region server
loads, and under `runtimes/win-x64/native/`.

Patched win-x64 `joltc.dll`, SHA-256:

    16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5

Stock `JoltPhysics.Native 1.0.4` win-x64 `joltc.dll` (must not be used with this module), SHA-256 begins:

    67BECFC70CFBDA64

Only win-x64 is built at present. A Linux or macOS build of the same patch, placed under
`runtimes/<rid>/native/`, is what those platforms need.

## Provenance

- Upstream: https://github.com/amerkoleci/joltc
- Commit: `1715c5aab834a5bb0c344dc4a11d573ad6f9736d` ("Improve and add more bindings for
  HeightFieldShapeSettings"), the joltc source behind the `JoltPhysics.Native 1.0.4` package that
  `JoltPhysicsSharp 2.19.1` depends on.
- Jolt Physics **v5.4.0**, fetched by joltc's CMake (`FetchContent`, `GIT_TAG v5.4.0`).

## What the patch changes

It removes the process-global `s_TempAllocator` and gives each `JPH_PhysicsSystem` its own
`TempAllocatorImplWithMallocFallback(8 MB)`, created in `JPH_PhysicsSystem_Create` and freed in
`JPH_PhysicsSystem_Destroy`. Every site that used the global allocator now uses the system's own:

1. `JPH_PhysicsSystem_Update`
2. `JPH_CharacterVirtual_Update`
3. `JPH_CharacterVirtual_ExtendedUpdate`
4. `JPH_CharacterVirtual_RefreshContacts`
5. `JPH_CharacterVirtual_WalkStairs`
6. `JPH_CharacterVirtual_StickToFloor`
7. `JPH_CharacterVirtual_SetShape`

Each CharacterVirtual entry point already takes the owning `JPH_PhysicsSystem*`, so no other
bookkeeping is needed.

**No ABI change:** export names and signatures are untouched, so the patched DLL is a drop-in for
the stock one under JoltPhysicsSharp 2.19.1. This is the same model as BulletSim's per-world
scratch memory.

Before moving to a newer joltc, check that all seven sites use a per-system allocator there.

## Rebuild

Toolchain: Visual Studio 2022 (MSVC v143, C++17), CMake 3.16 or later, git, and network access to
github.com. In PowerShell, from a working directory of your choice (`joltc-build` below):

```powershell
git clone https://github.com/amerkoleci/joltc joltc-build
git -C joltc-build checkout 1715c5aab834a5bb0c344dc4a11d573ad6f9736d
git -C joltc-build apply <path to this repository>\native\joltc\per-system-tempallocator.patch

cmake -S joltc-build -B joltc-build\build_win_64 `
      -G "Visual Studio 17 2022" -A x64 `
      -DCMAKE_BUILD_TYPE:String=Distribution -DCMAKE_INSTALL_PREFIX:String="SDK"
cmake --build joltc-build\build_win_64 --config Distribution
# -> joltc-build\build_win_64\bin\Distribution\joltc.dll
```

These are the commands of upstream's own win-x64 CI step (`.github/workflows/build.yml` at that
commit).

## Verify a rebuild

The hash changes with the compiler version, so the ABI check is the export set. Dump the exports
of the rebuilt DLL and of the stock one and compare them; they must match exactly (1086 exports
each, no difference in either direction):

```powershell
$dumpbin = "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe"
& (Resolve-Path $dumpbin)[0] /exports your\joltc.dll
& (Resolve-Path $dumpbin)[0] /exports "$env:USERPROFILE\.nuget\packages\joltphysics.native\1.0.4\runtimes\win-x64\native\joltc.dll"
```

Then run the Jolt test suite (`Tests/OpenSim.Region.PhysicsModules.Jolt.Tests`) with the new DLL in
place, and on a simulator with several regions check the log for
`Freeing in the wrong order` or `AccessViolation`: there must be none.

## Known gap: `s_PhysicsSystems` is not locked

joltc's global `s_PhysicsSystems` map (`UnorderedMap<PhysicsSystem*, JPH_PhysicsSystem*>`) is
written in `JPH_PhysicsSystem_Create` and `JPH_PhysicsSystem_Destroy` with no lock. Regions start
and stop concurrently (a region restart while others run, or several regions booting at once), so
two threads can change the map at the same time. It is the same kind of fault as the shared
TempAllocator: native state shared between regions without synchronisation.

The fix, next time the native is patched: guard the insert, the erase and any lookup that can race
them with a mutex (for example a `static std::mutex` taken in `JPH_PhysicsSystem_Create` and
`JPH_PhysicsSystem_Destroy`), in this patch or a follow-on one. It needs no ABI change.

## Licences

- joltc: MIT, Copyright (c) Amer Koleci and Contributors. `ThirdPartyLicenses/joltc.txt`.
- Jolt Physics (compiled into joltc.dll): MIT, Copyright 2021 Jorrit Rouwe.
  `ThirdPartyLicenses/JoltPhysics.txt`.
- JoltPhysicsSharp, the managed binding: MIT, Copyright (c) Amer Koleci and Contributors.
  `ThirdPartyLicenses/JoltPhysicsSharp.txt`.
