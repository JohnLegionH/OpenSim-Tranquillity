**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **The stale Tier-2 header was corrected** in `fab1f5376b`, which rewrote the header comment
  in `Source/InWorldz.Phlox/SLua/SLuaCompiler.cs` to record that Tier-2 is implemented, not
  rejected. An identical pre-rebase commit `3a176480a3` exists as an unreachable object —
  cite `fab1f5376b`.
- This file's own header cites branch tip `5a25c65583`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `6743ac4e7c`.

---

# SLua — upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `5a25c65583`
**Span audited:** `81e5c2449d` → `cbdfba2811` (11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip = branch intent)
**Merge base:** `cbdfba2811` (upstream is an ancestor of HEAD)
**Status:** read-only audit. Nothing changed, fixed or committed. Uncommitted working-tree output.

## Method and its limits

The brief's working assumption — that SLua "sits on Phlox" and inherits the Phlox result — was
treated as a hypothesis to test, not a premise. I located the SLua implementation from scratch by
filename, project reference and package search; read its sources and its callers; diffed every
SLua-bearing file across the span; and traced its persistence and native-resolution chains to
their ends.

No region was booted and no script was compiled or run. Anything not established is marked
**NOT ESTABLISHED**.

**Result up front: the assumption holds, and holds more strongly than stated.** SLua is not merely
built *on* Phlox — it is *inside* Phlox, and its coupling to anything outside Phlox is nil. Two of
the brief's own premises about SLua turned out to be wrong, and are corrected in §6.

---

## 1. Where SLua actually lives

**Verified by:** `find -iname "*slua*"` across the tree (excluding `.git`, `bin`, `obj`);
`grep -rli slua --include=*.csproj`; and reading each file found.

The entire SLua implementation is four files and one doc:

| path | project | role |
|---|---|---|
| `Source/InWorldz.Phlox/SLua/SLuaCompiler.cs` | **InWorldz.Phlox** | the compiler — 2,176 lines, namespace `InWorldz.Phlox.SLua` |
| `Source/InWorldz.Phlox/Glue/CompilerFrontend.cs` (`CompileLua`, line 294) | **InWorldz.Phlox** | the entry point that routes source into it |
| `Source/Phlox.ScriptEngine/SluaBackHalfProof.cs` | **Phlox.ScriptEngine** | back-half proof harness |
| `Tests/SluaProofRunner/` | SluaProofRunner | offline conformance runner (console tool) |
| `Docs/PhloxSLua.md` | — | documentation |

**It is inside the Phlox projects, not alongside them and not separate.** There is no SLua
project, no SLua assembly, and no SLua namespace outside `InWorldz.Phlox.SLua`. Only two csprojs
mention SLua at all: `Phlox.ScriptEngine.csproj` (a single `<Compile Include="SluaBackHalfProof.cs" />`)
and `SluaProofRunner.csproj`.

### What it shares with Phlox — everything

`SLuaCompiler.cs`'s own header states the architecture, and reading the code bears it out:

> Compiles the TRIVIAL SLua subset (SL's Luau dialect) to Phlox **ASSEMBLY TEXT**, which the proven
> back-half (`CompilerFrontend.AssembleText` → assembler → VM → serialization) consumes
> **unchanged**. … **NO VM/opcode change: this is pure front-end codegen**, mirroring what the LSL
> GenVisitor emits.

So, point by point:

| subsystem | SLua's relationship |
|---|---|
| compiler pipeline | **shared** — SLua is a *parallel front-end* to the LSL front-end; both emit Phlox assembly text into the same `AssembleText` path |
| assembler | **shared, unchanged** |
| VM | **shared, unchanged** — no opcode is added or altered |
| scheduler | **shared** — an SLua script becomes an ordinary Phlox `Interpreter` |
| state persistence | **shared** — see §5 |
| `ll` function table | **shared** — `ll.Name(args)` maps to Phlox `"ll"+Name` against the existing 674-function `TableIndex` |

It is wired into the live grid path, not proof-only: `PhloxScriptLoader.cs` routes on
`SLuaCompiler.IsLuaScript(...)` at **line 400** (`CompileAndStart`) and **line 441** (the
asset-server path), selecting `frontend.CompileLua(...)` over `frontend.Compile(...)`. Detection is
a source heuristic — a leading `--!slua`, `--!lua`, or any leading `--`.

---

## 2. Does the Phlox result transfer

**Verified by:** re-running each Phlox-audit check with SLua paths substituted, and enumerating
SLua's external references directly.

| Phlox-audit finding | holds for SLua? | evidence |
|---|---|---|
| No upstream/branch overlap in Phlox source | **Yes, identically** | branch changed only `Docs/PhloxKnownDefects.md`; no SLua source on either side (§3) |
| Branch touches the script engine at only two call sites (`GetTopObjectStats`, `CreateScriptInstance`) | **Yes — and neither is SLua's** | those two are the *branch's* calls, from `EstateManagementModule` and `LegionJoltScene`. SLua has no core call sites of its own at all (§4) |
| The one real runtime risk is `StateManager`'s SQLite provider swap | **Yes — same risk, same code, one exposure** | SLua persists through the identical `StateManager` (§5) |

### What SLua binds to that Phlox does not

**Nothing.** This is the strongest single result in this audit, and it is a negative one.

`SLuaCompiler.cs` imports exactly five namespaces:

```
System, System.Collections.Generic, System.Globalization, System.Text, InWorldz.Phlox.Types
```

`SluaBackHalfProof.cs` imports:

```
System, System.IO, System.Text,
InWorldz.Phlox.Glue, InWorldz.Phlox.Types, InWorldz.Phlox.VM, InWorldz.Phlox.Serialization, ProtoBuf
```

A grep across all three SLua source files for `OpenSim`, `IScriptModule`, `HasScript`,
`OnGetScriptRunning`, `RegionReady`, `StartProcessing` and `StateManager` returns **nothing**.

SLua's surface is strictly a *subset* of Phlox's — BCL plus Phlox's own types, plus ProtoBuf in the
proof harness. It therefore cannot have exposure that Phlox does not, and the Phlox audit's
conclusions transfer without qualification.

---

## 3. Direct overlap

**Verified by:** diffing each SLua-bearing file across the span and on the branch side.

**The intersection is empty.** The branch modified no SLua file — its only Phlox-area change in the
entire span is `Docs/PhloxKnownDefects.md`.

Upstream's side of the SLua files:

| file | upstream +/− |
|---|---|
| `InWorldz.Phlox/SLua/SLuaCompiler.cs` | **0/0 — untouched** |
| `InWorldz.Phlox/Glue/CompilerFrontend.cs` | **0/0 — untouched** |
| `Phlox.ScriptEngine/SluaBackHalfProof.cs` | **0/0 — untouched** |
| `Tests/SluaProofRunner/Program.cs` | **0/0 — untouched** |
| `Tests/SluaProofRunner/SluaProofRunner.csproj` | 0+/1− |
| `Docs/PhloxSLua.md` | 1+/1− |

The two non-zero rows are trivial and were read in full:

- **`SluaProofRunner.csproj`** — one line removed: `<TargetFramework>net8.0</TargetFramework>`, so
  the runner now inherits net10.0 from `Directory.Build.props`. Nothing else changed; the project
  has no package references, only a `ProjectReference` to `InWorldz.Phlox` and a local
  `Library/C5.dll` reference (deliberately a file reference, per its comment, so the runner loads
  the same C5 identity Phlox is compiled against).
- **`Docs/PhloxSLua.md`** — one line: "SQLite database (via `Microsoft.Data.Sqlite`…" →
  "via `System.Data.SQLite`…", from the SQLite normalization commit. Documentation only.

So the honest statement is stronger than "log-only": **upstream did not touch SLua's code at all.**
Not even the ILogger migration reached it, because no SLua file logs.

---

## 4. API surface

**Verified by:** reading every `using` in the SLua sources; grepping for core types; diffing the
one upstream-modified file that contains SLua's runtime entry point.

### 4.1 What SLua calls from OpenSim core

**Nothing.** There is no OpenSim reference in any SLua source file (§2). `InWorldz.Phlox` is a
compiler/VM assembly; the OpenSim-facing surface lives in `Phlox.ScriptEngine`, and SLua's only
file there (`SluaBackHalfProof.cs`) is a proof harness importing Phlox internals and ProtoBuf.

Because the surface is empty, the "compare signature AND semantics at both revisions" exercise has
no members to compare, and **there are no signature-stable behaviour-changed cases** — there is no
binding that could exhibit one.

### 4.2 What SLua calls from Phlox

| callee | at | stability across span |
|---|---|---|
| `InWorldz.Phlox.Types` (`TableIndex`, opcode/type model) | throughout `SLuaCompiler.cs` | `Gen.cs` had 1 blank-line change; Types untouched |
| `CompilerFrontend.AssembleText` → assembler → VM | via `CompileLua` (`CompilerFrontend.cs:294`) | `CompilerFrontend.cs` **0/0** |
| `InWorldz.Phlox.VM` / `.Serialization` + ProtoBuf | `SluaBackHalfProof.cs` | untouched |

All of it is inside `InWorldz.Phlox`, which upstream touched only in `Compiler/Gen.cs` (a single
added blank line) and its csproj. Nothing SLua depends on moved.

### 4.3 #194 and #195 — explicitly

- **#194 (`HasScript` stub → real ownership check, `OnGetScriptRunning` guard):** SLua touches
  **neither**. Both are `IScriptModule` members on `PhloxEngine`; SLua has no reference to
  `IScriptModule`, `HasScript` or `OnGetScriptRunning` anywhere. **Not reachable from SLua.**
- **#195 (RegionReady now fired from `StartProcessing`):** SLua references neither `RegionReady`,
  `StartProcessing`, `TriggerEmptyScriptCompileQueue` nor any login-lock concept. **No ordering
  dependency.** SLua compilation happens on the `PhloxScriptLoader` worker thread, which is
  driven by the loader's own queue, not by the RegionReady signal.

### 4.4 #196 — the one place SLua and an upstream change share a file

This is worth recording because it is the only genuine adjacency, and it is benign.

SLua's runtime entry points sit at `PhloxScriptLoader.cs:400` and `:441` — and
`PhloxScriptLoader.cs` is precisely the file **#196 rewrote** (68 insertions / 18 deletions, 85
semantic lines). So a naive read would call this an overlap.

It is not:

- Filtering #196's diff for `slua` or `CompileLua` returns **nothing** — #196 touched no SLua line.
- The SLua routing is present and identical at `81e5c2449d`, `cbdfba2811` and HEAD (2 occurrences
  of `SLuaCompiler.IsLuaScript` at each).
- #196's changes are a top-level `try/catch` in `DoWork` around
  `ProcessNextUnload/Load/Compile`, plus a per-request guard in `PerformLoad`.

The interaction, stated precisely: `CompileAndStart` (which contains the SLua routing) **already
had its own `try` … `catch (Exception e)`** that logs and returns — verified at HEAD, the `catch`
sits ~23 lines below the `try`. So an SLua compile that throws (for instance `SLuaException` on an
out-of-subset construct) was already contained *before* #196. #196's guards operate at outer
layers and neither change nor rescue the SLua compile path specifically. They do make the worker
more robust generally, which is mildly in SLua's favour.

---

## 5. State persistence

**Verified by:** reading `StateManager`'s persisted type and searching for any SLua-specific
persistence path.

**SLua persists through Phlox's `StateManager`. There is no separate path, and there could not be
one.**

`StateManager` operates on `InWorldz.Phlox.VM.Interpreter` instances throughout:
`Dictionary<UUID, Interpreter> m_Live` (line 43), `ScriptChanged(Interpreter interp)` (89),
`ScriptUnloaded(Interpreter interp)` (111), `SaveSingle(Interpreter interp)` (218),
`SaveSingleInTransaction(SQLiteConnection conn, Interpreter interp)` (233), writing to
`ScriptEngines/Phlox/state/script_state.db` (line 37).

Because SLua compiles to Phlox assembly text that the standard back-half assembles into a normal
`CompiledScript`, **a running SLua script *is* an `Interpreter`** — indistinguishable from an LSL
one at the persistence layer. A grep of `StateManager.cs` and the Phlox serialization sources for
`slua` returns nothing: there is no SLua branch, no SLua table, no SLua discriminator.

**Its exposure to the SQLite provider swap is therefore identical to Phlox's, not additional.**
The correct treatment is to **test it once, together with Phlox** — a single state save/restore
check covers both script languages. §7 folds it accordingly, and does not duplicate the check.

One nuance worth naming: because SLua scripts serialize through the same ProtoBuf path, an SLua
script's persisted state is subject to the same provider-swap questions the Phlox audit left open
(transaction association, `Mode=ReadWriteCreate` removal, native resolution). Nothing about SLua
makes those worse — but nothing makes them better either, and an SLua script is as good a probe
for them as an LSL one.

---

## 6. Tier-2 tables and the Lua runtime

**Verified by:** package search across every csproj; reading the tier-2 code paths in
`SLuaCompiler.cs`; and tracing the SQLite native chain end to end.

### 6.1 There is no Lua runtime — the premise is wrong

The brief refers to "a Lua runtime binding". **No Lua runtime exists in this tree.** Verified by
searching every `.csproj` for any Lua package: there is no NLua, no MoonSharp, no KeraLua, no
`lua5x`, no embedded interpreter. The only `.csproj` line matching "lua" in the whole repo is
`<Compile Include="SluaBackHalfProof.cs" />`.

SLua does not *host* Luau — it *compiles* a Luau subset ahead of time into Phlox assembly text,
which the existing Phlox VM executes. It is a source-to-source/codegen front-end written in plain
managed C#, using only BCL string, collection and globalization APIs.

**Consequences for the questions asked:**

- **Does net8→net10 affect the Lua runtime?** There is no Lua runtime to affect.
- **Any native or interop layer?** None in SLua. `SLuaCompiler.cs` contains no `DllImport`, no
  `unsafe`, no marshalling.
- **Marshalling?** None. Values cross no managed/native boundary in the SLua path; Luau `number`
  is mapped to Phlox `Float` (double) at compile time using existing `icast`/`fcast` opcodes.

So the Jolt-style treatment the brief anticipated does not apply to SLua itself. It applies to
exactly one thing in the stack beneath it — the SQLite native under `StateManager` — analysed
below.

### 6.2 Tier-2 tables are implemented, not rejected

A second correction, this time to the source's own documentation. `SLuaCompiler.cs`'s header block
states that tables, closures, metatables and user functions "is Tier-2 and intentionally rejected
with a clear error rather than mis-compiled".

**That header is stale.** The body implements Tier-2:

- `TableField` / `TableLit` (lines 251–252) — table literals, with `Key == null` meaning array element
- `ForIn` (line 277) — `for … in pairs(...) / ipairs(...) / string.gmatch(...)`
- `TableInsert` (line 278) — `table.insert(t, v)`
- `MetaCall` (line 262) — `setmetatable` / `getmetatable`

and its error messages are scoped *within* Tier-2 rather than rejecting it, e.g.
`"table." + fn + " is not supported in the Tier-2 subset (only table.insert)"` (line 434) and
`"for-in iterator must be pairs/ipairs or string.gmatch in the Tier-2 subset"` (line 414).

There is **no tier gate or feature flag** — a search for `EnableTier`, `Tier1`-style switches finds
only comments and error strings. Tier-2 constructs compile unconditionally whenever the source
routes to SLua.

This is a documentation defect, not a code defect. It matters only because it misleads a reader
about what is live: tier-2 tables *are* in the runtime path, so they belong in any conformance run.

### 6.3 The one native in the stack, and whether net10 changes its resolution

Beneath SLua, via `StateManager`, sits the SQLite native. Traced in full:

1. **Managed:** `System.Data.SQLite` **2.0.4**. Its package contains **only** managed assemblies —
   `lib/net471`, `lib/netstandard2.0`, `lib/netstandard2.1` — and **no `runtimes/` folder and no
   native at all**. Under net10.0 the restore graph selects
   `lib/netstandard2.1/System.Data.SQLite.dll` (confirmed in `OpenSim.Data.SQLite`'s
   `project.assets.json`). Note this is *not* the official SQLite.org package, which is versioned
   1.0.x and ships `SQLite.Interop.dll`; **no `SQLite.Interop.dll` exists anywhere in this tree.**
2. **Redirect:** `DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly)` — called
   from `StateManager.cs:260` and from six places in `OpenSim.Data.SQLite`. The type is **not
   defined in this tree**; it comes from the `System.Data.SQLite` package namespace. It installs a
   Mono-style DllMap so the managed shim's P/Invoke resolves to the SQLitePCLRaw native.
3. **Native:** `e_sqlite3.dll`, supplied by the separately-referenced **`SQLite` 3.53.4** package
   (`OpenSim.Data.SQLite.csproj:38`). Confirmed present at
   `Source/OpenSim.Server.RegionServer/bin/Release/net10.0/runtimes/win-x64/native/e_sqlite3.dll`.

**Does net10 change that resolution? No.** `SQLite` 3.53.4 ships a `runtimes/` folder, and its
natives appear in the published `deps.json` as `runtimeTargets` under the `"SQLite/3.53.4"` package
block (line 857 ff.). That mechanism is TFM-independent — the host builds
`NATIVE_DLL_SEARCH_DIRECTORIES` from `deps.json` regardless of target framework, exactly as for
Jolt. The managed asset selection did not change either: `netstandard2.1` was already the only
compatible asset under net8.

One thing to watch, flagged rather than asserted: `SQLite` 3.53.4's `buildTransitive` folders cover
`net471`, `net8.0` and `net9.0` — **there is no `net10.0` folder**. If any part of the native
staging depended on those MSBuild targets rather than on `runtimes/`, it would silently stop
applying under net10.0. The native *is* present in the current output, so the `runtimes/` path is
evidently doing the work — but a clean-machine restore is the honest test.

Incidentally, `Docs/PhloxSLua.md`'s statement — "via `System.Data.SQLite`; the native `e_sqlite3`
library" — is **correct** for this unusual combination, despite reading like a mismatch. I checked
expecting a stale-doc defect and found the doc accurate.

**NOT ESTABLISHED:** whether the `System.Data.SQLite` 2.0.4 + `SQLite` 3.53.4 + DllMap chain
actually resolves at runtime on the deploy target. This is the same open item as the Phlox audit's
§4 and is runtime-only: a failure appears at first state save/restore, not at startup.

---

## 7. Runtime verification plan

Sections 1–6 found: SLua is inside Phlox, binds to nothing outside it, was not touched by upstream
at all, has no native or interop layer, no Lua runtime, and shares `StateManager` exactly.

**So there is almost nothing SLua-specific to check — and the state-persistence check must not be
duplicated.** What follows is deliberately short.

### 7.1 Fold into the existing Phlox check — do not duplicate

The Phlox audit's outstanding runtime item is the `StateManager` SQLite provider swap. SLua's
exposure is *the same code on the same database*, so it needs **one** check, not two. The only
worthwhile refinement is to make that single check exercise both front-ends:

Run the existing Phlox state save/restore verification, but use **two scripts** in the region:

1. one ordinary **LSL** script with live state (a variable, a running timer, an active listen);
2. one **SLua** script (source beginning `--!slua`) with equivalent state, ideally using a
   **tier-2 table** (a table literal plus a `for … in pairs(...)` loop and a `table.insert`), since
   §6.2 establishes tier-2 is live and it is the least-exercised codegen.

Then: restart the region server fully and confirm **both** scripts resume with their variables,
current state, pending timers and active listens intact — not re-running `state_entry`.

- **Pass:** both resume identically.
- **Fail — both:** the `StateManager` provider chain is broken (the §6.3 open item). That is a
  Phlox finding, not an SLua one.
- **Fail — SLua only:** that would be a genuinely new finding, indicating something in the SLua
  codegen produces state the serializer cannot round-trip. Nothing in this audit predicts it, and
  it would be worth reporting.

This single run closes the Phlox item and the SLua question together.

### 7.2 One cheap SLua-specific smoke check

Because SLua is wired into the live loader path (`PhloxScriptLoader.cs:400/441`), confirm the
routing itself still works on net10 — this costs one script rez:

- Rez a script whose source begins with `--!slua` and which calls something visible, e.g.
  `ll.Say(0, "slua ok")`.
- **Pass:** the message appears in-world, and the log shows no
  `[PhloxLoader]: Exception compiling …` or `Compilation failed for …` for that item.
- **Fail:** compilation failure logged, or silence — meaning the SLua front-end or the
  `IsLuaScript` heuristic is not being reached.

Also worth one negative case, since detection is a *heuristic on the leading characters*: rez an
ordinary LSL script whose **first line is a comment**, and confirm it still compiles as LSL. The
heuristic routes any source starting with `--` to SLua, so this is the one place a misclassification
could plausibly occur. (This risk is pre-existing and unrelated to the upgrade — worth a single
check because it is nearly free.)

### 7.3 Optional — the offline runner

`Tests/SluaProofRunner` is a console conformance runner that compiles Luau snippets, executes them
on the Phlox VM with a recording syscall shim, and buckets results into PASS / DIVERGENCE / GAP. It
is explicitly "not part of the grid runtime". Running it after the net10 move is a cheap way to
confirm the compiler and VM still agree, with no region required.

**NOT ESTABLISHED:** whether it currently builds and runs — it now inherits net10.0 (its `net8.0`
pin was removed upstream, §3) and it takes a local `Library/C5.dll` file reference specifically so
it loads the same C5 identity Phlox is compiled against. Whether that still resolves under net10 is
unverified.

### 7.4 What does not need checking

- **Anything about SLua's core API surface** — it has none (§4.1).
- **#194 / #195 against SLua** — unreachable (§4.3).
- **#196 against SLua** — touched no SLua line, and `CompileAndStart` already had its own guard
  (§4.4).
- **A separate SLua state test** — same code, same database as Phlox (§5); folded into §7.1.
- **Lua runtime, native binding, interop, marshalling** — none exist (§6.1).

---

## Summary of items needing attention

| # | Item | Severity | Action |
|---|---|---|---|
| 1 | `StateManager` SQLite chain (`System.Data.SQLite` 2.0.4 netstandard2.1 shim → DllMap → `e_sqlite3` from `SQLite` 3.53.4) unverified at runtime — **shared with Phlox, not additional** (§6.3) | **Needs runtime check** | §7.1, once, covering both front-ends |
| 2 | `SLuaCompiler.cs` header says Tier-2 is "intentionally rejected"; Tier-2 tables, `pairs`/`ipairs`/`gmatch`, `table.insert` and metatables are in fact implemented and compile unconditionally (§6.2) | **Doc defect** | Correct the header; include tier-2 in any conformance run |
| 3 | `SQLite` 3.53.4 has no `net10.0` `buildTransitive` folder; natives currently land via `runtimes/`+deps.json, but a clean-machine restore is the honest test (§6.3) | Low, unverified | Note for a clean-checkout build |
| 4 | `SluaProofRunner` now inherits net10.0 and uses a file reference to `Library/C5.dll`; build/run status unverified (§7.3) | Low | §7.3 if convenient |

**The working assumption is confirmed: SLua sits inside Phlox and inherits the Phlox result
entirely.** Upstream did not touch a single line of SLua code. SLua binds to nothing outside
Phlox — no OpenSim core surface, no `IScriptModule`, no `HasScript`, no `RegionReady`. There is no
Lua runtime, no native component and no interop in SLua itself. Its only runtime exposure is
Phlox's `StateManager`, which is the same code on the same database and should be tested once, not
twice. No fixes were made.
