# PHYS-2b — reading the 2026-09-06 crash dump

**Analysis only. No fix, no deploy.** Region `1.1.246-alpha+05e915df4f`, crashed on the second
Ebony↔Transylvania crossing with Truly's full attachment set, 05:54:40 UTC. Dump
`D:\legiongrid\_dumps\OpenSim.Server.RegionServer.exe.43868.dmp` (WER, DumpType 1 — stacks, no heap),
copied to the session scratch and analysed there; the original was not modified.

---

## Tooling: what was and was not available

**No joltc PDB exists.** `D:\joltc-build` was searched in full; the Distribution build output
(`build_win_64\bin\Distribution\joltc.dll`) ships without one, and the only `.pdb` files in the tree belong to
CMake's compiler-probe scratch projects.

**A rebuild with symbols was not needed.** The build output is byte-identical to the live, faulting DLL:

```
built  D:\joltc-build\build_win_64\bin\Distribution\joltc.dll
live   D:\legiongrid\regionserver\runtimes\win-x64\native\joltc.dll
both   16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5
```

So the exact binary that faulted could be analysed directly, without touching the live file.

**WinDbg was already installed** — `winget install --id Microsoft.WinDbg` answered *"Found an existing package
already installed… No available upgrade found."* But only `WinDbgX.exe` (the GUI) and `dbgsrv*.exe` are
present; there is **no `cdb.exe`, `kd.exe` or `ntsd.exe`** on this machine, and a GUI debugger cannot be driven
from here. **So `!analyze -v` and a native `k` were not run, and no native frame chain was obtained.** Part 1
below is therefore static analysis of the faulting binary plus the managed stack, not an unwound native stack.
`dotnet-dump` was available and did the managed work.

---

## Part 1 — the fault offset is the abort thunk, not an allocator site

Event 1000 reports faulting module `joltc.dll`, exception `0xc0000409`, offset `0x1108bd`, identical across all
three crashes (2026-09-05 ×2, 2026-09-06 ×1). That identity has been read until now as evidence that the same
call site fired each time. **It is not.**

From the byte-identical binary:

| Fact | Evidence |
|---|---|
| `joltc.dll` imports **only `KERNEL32.dll`** | PE import directory: one entry. No `ucrtbase`, no `api-ms-win-crt-*`, no `vcruntime`. The CRT is statically linked, so `abort()` lives inside `joltc.dll`. |
| Fault RVA `0x1108bd` lies in the function `0x110878–0x1108ce` (86 bytes) | `.pdata` exception-directory range table, 5131 entries |
| That function contains a `__fastfail` at `0x1108ad` | opcode `CD 29` (`int 29h`), 16 bytes before the fault offset |
| The bytes immediately preceding the fault are that instruction | `… cd 29 41 b8 01 00 00 00 ba 15 00 00 40 …` then the fault offset |

`0xc0000409` is `STATUS_STACK_BUFFER_OVERRUN`, which is what `__fastfail` raises; MSVC's `abort()` ends in
`__fastfail(FAST_FAIL_FATAL_APP_EXIT)`. **`0x1108bd` is the instruction after that `__fastfail`** — the
process-terminator thunk, reached by *any* `abort()`.

**Consequence:** the identical offset means only that all three crashes went through `abort()`. It carries no
information about which assertion fired or from where. Any Jolt `Trace()`-then-`std::abort()` path — *Freeing in
the wrong order*, *Out of memory trying to allocate*, or any `JPH_ASSERT` in an assert-enabled build — produces
exactly this offset.

**Where the allocator's own abort actually lives.** The string `"TempAllocator: Freeing in the wrong order"` is
at RVA `0x15b7d0`, referenced by exactly two RIP-relative `lea`s, at RVA `0x2a8f` (function `0x2a60–0x2aa2`) and
`0x2be0` (function `0x2b90–0x2bf3`) — two instantiations of `TempAllocatorImpl::Free`, consistent with the
template being emitted once for direct use and once inside `TempAllocatorImplWithMallocFallback`. Both are
**1.1 MB away** from the fault offset.

**Which of the seven `tempAllocator` sites the stack passes through:** `joltc.cpp:8135`,
`JPH_CharacterVirtual_ExtendedUpdate`. This comes from the **managed** stack (Part 2), not from a native unwind.

**Confidence.** High for the abort-thunk identification: it rests on the byte-identical binary, the `.pdata`
function table, the `CD 29` opcode inside the same function range, and the absence of any dynamic CRT import.
The one thing static analysis cannot supply is the symbol name, so the function is identified by what it does,
not by what it is called.

---

## Part 2 — the managed side

### The faulting thread: OS `0x9fb0`

```
JPH_CharacterVirtual_ExtendedUpdate            IP 00007ff958ad08bd
JoltPhysicsBackend.StepCharacter               JoltPhysicsBackend.cs @ 1930
JoltPhysicsBackend.Step                        JoltPhysicsBackend.cs @ 2315
LegionJoltScene.StepOnce
LegionJoltScene.Simulate                       LegionJoltScene.cs @ 3474
Scene.Update
Scene.Heartbeat                                Scene.cs @ 1665
Thread+StartHelper.Callback
```

The innermost stored IP is `00007ff958ad08bd`. joltc's load base is
`0x7ff958ad08bd − 0x1108bd = 0x7ff9589C0000`, so that IP is **`joltc + 0x1108bd` — the abort thunk**. This
identifies the aborting thread from the dump alone, independently of the Event 1000 record.

It is a **region heartbeat thread**, in `ExtendedUpdate` via `StepCharacter`, i.e. phase 1 of `Step`.

### The other Jolt thread: OS `0x5740`

The same stack shape, in `ExtendedUpdate` via `StepCharacter` → `Step` → `Simulate` → `Scene.Heartbeat`. Its
innermost IP (`00007ff988bb7234`) is outside joltc's image, so it was parked in a kernel transition when the
dump was taken.

**Exactly two threads are in `JoltPhysicsBackend.Step`.** Three regions run in this process; the third was not
stepping.

### They are different backends

| | faulting `0x9fb0` | other `0x5740` |
|---|---|---|
| `JoltPhysicsBackend` | `0x1a6bbd15fe8` | `0x1a6bb69e080` |
| `LegionJoltScene` | `0x1a6b8fe0388` | `0x1a6bb9a3508` |
| `Scene` | `0x1a6b8fa23b0` | — |

Distinct instances, so distinct `PhysicsSystem`s (`_system` is an instance field, `JoltPhysicsBackend.cs:55`)
and therefore distinct `TempAllocatorImplWithMallocFallback`s (`joltc.cpp:956`, one per
`JPH_PhysicsSystem`). `_charVsChar` is also per-instance (`:239`); the only static is `s_jobSystem` (`:63`),
which `ExtendedUpdate` does not take.

### `_simLock` ownership, and which region

**Neither could be read from this dump.** `syncblk` returned no rows and `dumpobj` on all four instance
addresses answers *"this object has an invalid CLASS field / Invalid object"* — the GC heap is not present in a
DumpType 1 minidump, so object fields, and with them `RegionInfo.RegionName`, cannot be resolved. **The dump
therefore cannot say whether the faulting thread was Ebony's (departing) or Transylvania's (arriving).**

What the stacks do establish: each thread was inside its own backend's `Step`, which takes that backend's
`_simLock` at entry and holds it for the whole step, so each held its own instance's lock. They are different
lock objects, so neither excluded the other — which is the intended design, given per-system allocators.

---

## Part 3 — the 23 "TempAllocator" log lines are not what they look like

All 23 in `OpenSim.Server.RegionServer20260906.log` are `[LEGION JOLT METRICS]` heartbeat lines, each carrying
the literal text `TempAllocator high-water/malloc-fallback: N/A (needs native counter)`. **None is Jolt
`Trace()` output.**

| | timestamp | relative to the `:39.957` spawn |
|---|---|---|
| first | `05:43:13,357` | 11 min 26 s **before** |
| last | `05:54:14,343` | **25.6 s before** |

`grep -c "TempAllocator high-water"` = 23; non-metrics matches = **0**; the string `wrong order` appears in **no
log file at all**. Yesterday's log has 866 of the same metrics lines.

So the premise that the log captured the allocator message this time is a miscount of the metrics heartbeat: as
on 2026-09-05, the abort message went to the **console only**. Jolt's default `Trace` writes to
stdout/`OutputDebugString`, not through the log framework, and nothing routes it there.

**The log tail around the crash** (region-side, for the record):

```
05:54:39,956  [SCENE PRESENCE] Complete movement of "Truly Bazar" into "Transylvania"
05:54:39,957  [LEGION JOLT]    avatar 'Truly.Bazar' id=558891172 spawned at (144,160)
05:54:39,961  [CAPS]           SEED caps request in Transylvania
05:54:39,982  [PhloxEngine]    OnRezScript ... in prim 558891520
05:54:39,983  [CompleteMovement] end: 31ms
05:54:39,985  [AVFACTORY]      Received texture update for "Truly Bazar"
05:54:40,069  [PhloxExe]       Restored state for "db5032d3-..."
05:54:40,070  [PhloxListen]    Registered listen handle 4 ...
              (log ends)
```

---

## Part 4 — conclusion

**What the dump establishes.** The aborting thread was a region heartbeat thread inside its own backend's
`JoltPhysicsBackend.Step` → `StepCharacter` → `CharacterVirtual::ExtendedUpdate` (`joltc.cpp:8135`), holding
that backend's `_simLock`, while a second heartbeat thread was inside a **different** backend's
`ExtendedUpdate` at the same instant. Both backends have their own `PhysicsSystem` and therefore their own
`TempAllocator`, so this is not two threads sharing one allocator, and it is not a missing lock on a managed
call site — the three managed call sites that reach the seven `tempAllocator` entry points are all inside
`_simLock` since PHYS-1, and the PHYS-2 harness confirmed that over a thousand contended crossings.

**What the dump cannot say, and why.** Which free was out of order, and in which region. The fault offset is
the CRT abort thunk and carries no site information; there is no joltc PDB and no console debugger on this
machine, so no native frame chain between `ExtendedUpdate` and `abort()` was obtainable; and the minidump is
stacks-only, so neither the allocator's internal state, nor `_simLock` ownership, nor the region names on the
heap can be read. Everything above the managed `ExtendedUpdate` frame is inference from the binary, not
observation.

**The one harness ingredient the evidence points at: contact-listener re-entry.** `ExtendedUpdate` is the only
one of the seven allocator entry points that calls **back into managed code while an allocator sequence is
open** — `character.OnContactAdded/Persisted/Removed` (`JoltPhysicsBackend.cs:1594-1598`) and
`OnCharacterContactAdded/Persisted` (`:1608-1611`), the latter running `PushCharacterCharacterContact`
(`:1646`), which dereferences another `CharacterVirtual` (`other.UserData`) from inside the callback. The
PHYS-2 harness exercised `ExtendedUpdate` several million times and never reproduced anything — but it ran
characters alone in empty regions, so **not one of those callbacks ever fired**. The live crash needs a full
attachment set and lands immediately after an attachment script rez, which is precisely the condition that
produces contacts. That is the gap between the harness and the failure, and it is the next thing to add.

*No code change is proposed here.*

---

# Addendum, 2026-09-06 (PHYS-2c) - the contact callbacks fire, and nothing re-enters

PHYS-2b named contact-listener re-entry as the one harness ingredient the evidence pointed at. It has now been
added, the callbacks demonstrably run, and **the hypothesis is not confirmed**. No fix was made.

## The guard the owner check could not be

`RequireSimLock` asks only "does this thread hold `_simLock`". Monitor is re-entrant, so a contact handler that
called back into one of the seven allocator entry points would hold the lock and pass that check while
corrupting the allocator underneath the call it is nested inside. `JoltPhysicsBackend` therefore gained a
second, orthogonal guard: a `[ThreadStatic]` record of which allocator site is currently open **on this
thread**, checked and set by an `AllocatorSite` scope around all three managed call sites. Entering one while
another is open throws and names both. It is per-thread, not per-backend, so cross-backend re-entry on one
thread is caught too. Same gating as the owner check: on in DEBUG, off in RELEASE unless a harness enables it.

## Making the callbacks fire

The PHYS-2 harness ran characters alone in an empty world, so `CharacterVirtual::OnContact*` never fired once.
Three changes fixed that, and each was necessary:

- **a floor** - a static box at z=20, characters standing on it, so contacts begin and persist;
- **attachments ON the avatar** - the 14 rezzed bodies overlap the wearer rather than sitting beside it;
- **a resident avatar re-grounded every crossing** - two `CharacterVirtual`s in one backend's
  `CharacterVsCharacterCollisionSimple`, overlapping. The first run without the re-grounding produced only 22
  avatar-vs-avatar callbacks in a thousand crossings, because the controller shoves the pair apart within a
  step or two; that number is recorded here because a thinly-exercised callback would have made the negative
  worthless.

The handlers are the production ones - `PushCharacterBodyContact` (`JoltPhysicsBackend.cs:1677`) and
`PushCharacterCharacterContact` (`:1699`), wired at `:1594-1598` and `:1608-1611` - not stubs. The harness
counts what they push, drained from `Step`.

## Result: green, with the callbacks proven to have run

| | Ebony | Transylvania |
|---|---|---|
| steps | 16,090,814 | 16,124,337 |
| `OnContactAdded` | 24,146 | 24,351 |
| `OnContactPersisted` | 37,916,883 | 37,893,374 |
| `OnCharacterContactAdded/Persisted` | 2,089,153 | 1,927,231 |

1000 crossings, both regions stepping throughout, ~32 M steps, **~76 M contact callbacks and 4 M
avatar-vs-avatar callbacks executed inside `ExtendedUpdate`** - and neither the owner check nor the re-entry
guard fired, and no fault of any kind occurred.

**Why, from the code.** Both handlers are pure managed bookkeeping: a `ConcurrentDictionary` lookup and a push
into a pre-allocated ring buffer. The only native call either makes is `other.UserData`
(`JPH_CharacterVirtual_GetUserData`) in the avatar-vs-avatar handler, and that is **not** one of the seven
`tempAllocator` entry points. There is no path from a contact callback back into the allocator.

**So contact-listener re-entry is eliminated**, on the same footing as the earlier eliminations: by a harness
that demonstrably exercises the thing, not by reading alone. The remaining untested ingredients from PHYS-2b
are mesh/convex-hull attachment cooking, terrain replacement, and region shutdown racing a step; constraints
remain a separate, differently-presenting hazard.

**Tooling note.** `cdb` still could not be obtained: `winget install Microsoft.WindowsSDK.10.0.26100` with
`/features OptionId.WindowsDesktopDebuggers /quiet` downloaded and verified the installer and then failed with
exit `2147944002` = `HRESULT_FROM_WIN32(1602)`, `ERROR_INSTALL_USEREXIT` - the SDK installer needs elevation
and cannot get it non-interactively. **No native stack was obtained, then or now.** The gap PHYS-2b identified
between `ExtendedUpdate` and the abort thunk is still unobserved.

---

# Addendum, 2026-09-06 (PHYS-2d) - the native stack, and the patch

`cdb.exe` is now present at
`C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe`. Report only; nothing was fixed or deployed.

## Part 1 - the native stack

The first thing cdb gave, before any stack walk, closes a PHYS-2b unknown: **thread names**. The faulting
thread `0x9fb0` is `Heartbeat-(Ebony)` and the other Jolt thread `0x5740` is `Heartbeat-(Transylvania)`.
**The abort happened on the DEPARTING region's heartbeat, not the arriving one.**

`joltc.dll` is loaded at `00007ff9'589c0000`-`00007ff9'58b9b000`. Frames between `ExtendedUpdate` and the
abort, with each return address mapped to its `.pdata` function range in the byte-identical binary:

| # | return address | RVA | containing function | size | what it is |
|---|---|---|---|---|---|
| 0 | `7ff9'58ad08bd` | `0x1108bd` | `0x110878-0x1108ce` | 86 | the abort thunk - `int 29h` at `0x1108ad`, and `rcx = 7` = `FAST_FAIL_FATAL_APP_EXIT` |
| 1 | `7ff9'589c2bf2` | `0x2bf2` | **`0x2b90-0x2bf3`** | 99 | **`TempAllocatorImpl::Free` - one of the two "Freeing in the wrong order" paths found in PHYS-2b** |
| 2 | `7ff9'58a3e608` | `0x7e608` | `0x7e086-0x7e68a` | 1540 | **the Jolt function that called `Free`** |
| 3 | `7ff9'58a3eb94` | `0x7eb94` | `0x7eb18-0x7ebbb` | 163 | |
| 4 | `7ff9'58a403da` | `0x803da` | `0x80274-0x8046e` | 506 | |
| 5 | `7ff9'58af06ef` | `0x1306cf` | `0x1304c0-0x1306f9` | 569 | `JPH_CharacterVirtual_ExtendedUpdate+0x20f` (export RVA `0x1304c0`) |

**The chain passes through `0x2b90-0x2bf3`, not `0x2a60-0x2aa2`.** That is the larger of the two Free
instantiations (99 bytes against 66), which fits the `TempAllocatorImplWithMallocFallback::Free` wrapper with
`TempAllocatorImpl::Free` inlined into it - and the binding constructs exactly that class, so the virtual call
lands there. **PHYS-2b's inference that the fault offset carries no site information is now confirmed
directly**: the abort thunk is frame 0, and the allocator's own check is frame 1.

**Which Jolt function called `Free` is NOT resolvable.** No PDB exists, so frames 2-4 can only be given as
address ranges. Frame 2 (`0x7e086-0x7e68a`, 1540 bytes) is the caller of `Free`; from Jolt v5.4.0's structure
the candidates three levels below `ExtendedUpdate` that build temp-allocated contact arrays are
`CharacterVirtual::GetContactsAtPosition`, `MoveShape` and `GetFirstContactForSweep`, but **naming it would be
a guess and it is not named here.**

Registers at the abort are readable; the native heap is not. `dq` on the allocator pointers held in the Free
frame's callee-saved registers (`rbx=1c6'ecda2ae0`, `r12=1c6'ecda2b10`, `r14=1c6'ecda29e0` - three addresses
0x30 and 0x100 apart, consistent with one fallback allocator and its embedded `TempAllocatorImpl`) returns
`????????`. **So `mBase`, `mTop` and `mSize` could not be read, and the exact out-of-order address cannot be
shown.** That is the DumpType 1 limit again.

## Part 2 - what D:\joltc-build actually changes

**One commit, one file, 14 insertions and 13 deletions.**

- Base: `amerkoleci/joltc` at `1715c5a` ("Improve and add more bindings for HeightFieldShapeSettings").
- Legion commit `907819f`, "Legion: per-system TempAllocator (base amerkoleci/joltc 1715c5a; builds joltc.dll
  SHA256 16AF7638)", touching `src/joltc.cpp` only. Working tree clean; no other local change.
- The same patch is vendored in this repo as `native/joltc/per-system-tempallocator.patch` with
  `native/joltc/README.md`.

The diff is a pure ownership substitution, not a change of strategy: the process-global
`static TempAllocator* s_TempAllocator`, created in `JPH_Init` and deleted in `JPH_Shutdown`, is removed and
replaced by a `JPH::TempAllocator* tempAllocator` member on `struct JPH_PhysicsSystem`, created in
`JPH_PhysicsSystem_Create` and deleted in `JPH_PhysicsSystem_Destroy`. Every one of the eight
`*s_TempAllocator` uses becomes `*system->tempAllocator` / `system->tempAllocator`. **The allocator class and
size are unchanged from upstream.**

| | |
|---|---|
| Jolt version | **v5.4.0** (`_deps/joltphysics-src` at `036ea7b1` "Bump version to v5.4.0") |
| `CMAKE_BUILD_TYPE` | **`Distribution`** |
| `USE_ASSERTS` | **`OFF`** - so `JPH_ENABLE_ASSERTS` is not defined |
| `DOUBLE_PRECISION` | `OFF` |
| `CROSS_PLATFORM_DETERMINISTIC` | `OFF` |
| allocator class | **`TempAllocatorImplWithMallocFallback`**, `8 * 1024 * 1024` (`joltc.cpp:956`), one per `JPH_PhysicsSystem` |

## Part 3 - conclusion

This is an **ordering violation, not a size mismatch, and not an assert artefact**. The message comes from
`TempAllocatorImpl::Free`'s `mBase + mTop != inAddress` test (`Jolt/Core/TempAllocator.h:81-85`) and the stack
lands in that exact function, one frame below the abort; a size problem would instead surface from `Allocate`
as *"Out of memory trying to allocate"*, and with the malloc-fallback class in use it cannot even do that,
because an overflow of the 8 MB stack silently spills to `malloc` - so the 8 MB figure is not implicated.
**Stock, assert-off joltc would have aborted identically**: this build already has `USE_ASSERTS=OFF`, and the
`Trace()` + `std::abort()` pair is plain code, not a `JPH_ASSERT` - only the `inAddress == nullptr` branch
beside it uses the assert macro - so the check is compiled into every configuration including `Distribution`
and cannot be turned off. What remains unknown is *which* Jolt function freed out of order, and that is now a
symbols problem rather than a debugging one. **The single next step: rebuild `D:\joltc-build` from the pinned
source (base `1715c5a` + `907819f`, Jolt v5.4.0, `Distribution`, `USE_ASSERTS=OFF`) with debug info emitted so
a PDB is produced, leave the live DLL alone, and re-open this same dump with that PDB on the symbol path -
which names frames 2-4 and, with them, the call that freed out of order.**

---

# Addendum, 2026-09-06 (PHYS-2e) - frames 2-4 named, and the LIFO violation found

Report only; nothing fixed, nothing deployed. The live
`D:\legiongrid\regionserver\runtimes\win-x64\native\joltc.dll` was **not touched**: SHA-256
`16AF7638...43B5` before and after, unchanged.

## Part 1 - the symbol rebuild

Built from the pinned source into a **separate** directory, `D:\joltc-build\build_sym`, leaving the verified
`build_win_64` output alone: VS 2022 x64, `Distribution`, `USE_ASSERTS=OFF`, `DOUBLE_PRECISION=OFF`,
`CROSS_PLATFORM_DETERMINISTIC=OFF`, plus `/Zi` and `/DEBUG /OPT:REF /OPT:ICF` so a PDB is emitted while keeping
the linker's Release folding behaviour.

**The result is not byte-identical, and that does not matter here.**

| | |
|---|---|
| live / original build | `16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5` |
| symbol build | `5B8336BF41E91BD9B57527B3B0666519834702FDA0D38B5D245A717B1528EFB0` |
| file size | **identical**, 1,865,728 bytes both |
| whole-file bytes differing | 116,232 (6.23%), first at `0x118` - the PE headers |

The fallback proposed in PHYS-2d was `.pdata` range matching, and it turned out to be far stronger than
expected:

- **`.pdata` is byte-for-byte identical** - 5131 entries, every function beginning and ending at the same RVA;
- **`.text` is 99.98% identical** (256 bytes differ out of 1,360,896) at the same RVA and the same size;
- only `.rdata` grew, by `0x60`, for the debug directory and PDB path.

So the code layout is the same binary in both, and resolving the dump's addresses through the symbol build's
PDB is sound. cdb was pointed at it with `.reload /i` to accept the signature mismatch deliberately.
**Confidence: high**, and it rests on the identical `.pdata` table rather than on the hash.

## Part 2 - the named stack

```
joltc!abort+0x45
  (inline) JPH::TempAllocatorImpl::Free+0x44
joltc!JPH::TempAllocatorImplWithMallocFallback::Free+0x62
  (inline) JPH::STLTempAllocator<JPH::CharacterVirtual::Constraint>::deallocate+0x22
  (inline) JPH::Array<Constraint, STLTempAllocator<Constraint>>::free / destroy / {dtor}
joltc!JPH::CharacterVirtual::MoveShape+0x5f8            <- frame 2, the caller of Free
joltc!JPH::CharacterVirtual::Update+0xb4                <- frame 3
joltc!JPH::CharacterVirtual::ExtendedUpdate+0x19a       <- frame 4
joltc!JPH_CharacterVirtual_ExtendedUpdate+0x22f
```

Every range predicted from `.pdata` in PHYS-2d matches: frame 2 `0x7e086-0x7e68a` is `MoveShape` (1540 bytes,
the big one), frame 3 `0x7eb18-0x7ebbb` is `Update`, frame 4 `0x80274-0x8046e` is `ExtendedUpdate`.

**The array being freed is `ConstraintList constraints`** - `Array<CharacterVirtual::Constraint,
STLTempAllocator<Constraint>>`, declared at `Jolt/Physics/Character/CharacterVirtual.cpp:1243` inside
`MoveShape`'s collision loop and destructed at the end of each iteration.

**Which allocation should have been freed first.** `MoveShape` declares three temp arrays inside the loop body,
in this order (`:1228`, `:1238`, `:1243`):

```cpp
TempContactList    contacts(inAllocator);          contacts.reserve(mMaxNumHits);              // :1228-1229
IgnoredContactList ignored_contacts(inAllocator);  ignored_contacts.reserve(contacts.size());  // :1238-1239
ConstraintList     constraints(inAllocator);       constraints.reserve(contacts.size() * 2);   // :1243-1244
```

C++ destroys in reverse declaration order, so `constraints` correctly frees first. **The destructor order is
right; the stack underneath it was wrong.** For `constraints`'s buffer not to be the top of the allocator, some
allocation made *after* it must still have been live.

**The structural reason that can happen.** `STLTempAllocator` implements only `allocate` and `deallocate`
(`Jolt/Core/STLTempAllocator.h:41-50`) - it has **no `reallocate`**. So `Array::reallocate`
(`Jolt/Core/Array.h:155-177`) takes the else branch: **allocate the new buffer, move, then free the old one**.
On a stack allocator that is an inherent LIFO violation - the new block is pushed on top and the old block,
now beneath it, is freed - and `TempAllocatorImpl::Free` (`Jolt/Core/TempAllocator.h:71-90`) answers
`mBase + mTop != inAddress` with `Trace()` and `std::abort()`. Any temp Array that outgrows its `reserve`
while another temp allocation sits above its buffer aborts the process. This is upstream Jolt behaviour,
present with or without the Legion patch and with `USE_ASSERTS=OFF`.

**Which of the three grew, and when, cannot be read from this dump.** `mBase`, `mTop`, `mSize` and the address
passed to `Free` are on the native heap, which a DumpType 1 minidump does not carry. Naming a specific array
here would be a guess and is not done.

## Part 3 - conclusion

The abort is `constraints`'s destructor freeing a buffer that is no longer the top of its region's stack
allocator, inside `CharacterVirtual::MoveShape` on Ebony's heartbeat; the destructor order in `MoveShape` is
correct by construction, so the inversion was created earlier by a temp array growing past its `reserve` -
`STLTempAllocator` has no `reallocate`, so a growth allocates above and then frees below, which on a LIFO stack
allocator is fatal by design. **The departing region's removal of Truly's character and her 14 attachment bodies
cannot be the cause, and this is now a positive statement rather than an absence of evidence**: the allocator is
per-`JPH_PhysicsSystem` (`joltc.cpp:956`) so no other region can touch it; `RemoveCharacter`
(`JoltPhysicsBackend.cs:1737-1742`) and every body op take `_simLock`, which the heartbeat holds for the whole
of `Step` including `MoveShape`, so they cannot interleave with it; and `rec.Character.Dispose()` at `:1762` is
deterministic and inside both locks, so there is no finalizer race on the `CharacterVirtual` either. That also
explains why PHYS-2c's 1000 contended crossings and 32 M steps found nothing: **concurrency is not an
ingredient of this bug at all.** The smallest harness change that should make it red is therefore not a
concurrency change but a load one - drive a single character's contact count past `mMaxNumHits`
(default **256**, `CharacterVirtual.h:52`) so the temp arrays in `MoveShape` outgrow their reservations: the
PHYS-2c harness surrounded the avatar with **14** bodies, which never came close. Stated as a hypothesis, not a
known reproduction: it follows from the named frames and the allocator contract, and it has not yet been run.

**Still missing:** which array grew and the address it freed. WER is now DumpType 2, so the next occurrence
carries the heap and answers both directly.
