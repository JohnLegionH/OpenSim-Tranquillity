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
