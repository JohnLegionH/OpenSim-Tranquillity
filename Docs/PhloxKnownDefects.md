# Phlox — known defects / deferred limitations

> Created because no existing "defects doc" was found in the tree when this note was
> logged. If you keep such a log elsewhere, move this there and delete this file.

## Script state is not carried across a process/DB boundary (Get/SetXMLState + SaveAllState stubs)

**Status:** not blocking today (single `regionserver` process hosting all regions);
becomes blocking the moment regions are split across processes/hosts, or for OAR
export/import and Hypergrid teleport. Logged for later — do **not** implement yet.

### What Phlox actually does (its own path)
Phlox persists script state through its own `StateManager` (constructed in
`PhloxEngine.AddRegion`/`RegionLoaded`), **not** the framework serializer:

- **Store:** per-process SQLite DB at `ScriptEngines/Phlox/state/script_state.db`
  (one file per region-server process, shared by every region that process hosts).
- **Key/validation:** primary key `item_id`; each row carries `asset_id` and load
  validates it (`LoadState(itemId, assetId)` discards rows whose saved `asset_id`
  no longer matches — i.e. the script was replaced).
- **Save:** immediate on unload (`ScriptUnloaded → SaveSingle`) and via a dirty-flush
  loop (2.5 s) on change (`ScriptChanged`). **Delete** on script reset (`DeleteState`).

Because of this, state **survives** anything that stays inside one process with the
`item_id` intact: in-place region/sim restart, in-process region crossings (all live
regions are one process today — this is why crossings and attachments have worked
for months), and local attach/detach and take→rez where the item id is preserved.

### The gap (the stubbed framework serializer)
`IScriptModule.GetXMLState`, `SetXMLState`, and `SaveAllState` are stubs on Phlox
(`GetXMLState => string.Empty`, `SetXMLState => false`, `SaveAllState => {}`). These
are the framework's mechanism for carrying script state **inside the serialized
object**, across a StateManager-DB boundary. Their callers are framework code
(`SceneObjectGroup.Inventory.cs`, `SceneObjectPartInventory.cs`), not the (unused)
XEngine LSL_Api. With them stubbed, state is **lost** whenever it must ride in the
object rather than sit in the local SQLite DB:

- **OAR export carries NO script state** — including the migration OARs already
  taken. On import into a fresh DB, scripts come back at default state.
- **Cross-process / cross-host** region crossing or teleport-with-attachments — the
  destination process has a different `script_state.db` with no row for that item id.
- **Hypergrid teleport** to/from a foreign grid — the foreign sim cannot reach
  Legion's SQLite DB; embedded object state is the only possible transport.
- Operations that assign a **new item id** to the rezzed copy (rez-a-copy,
  give-to-another-avatar) resolve to default state (arguably correct for a fresh copy,
  but noted so it is not mistaken for the cross-process gap).

### If/when this is picked up
Implement `GetXMLState`/`SetXMLState` to (de)serialize `StateManager`'s
`SerializedRuntimeState` into the framework's `<State>` XML envelope, and
`SaveAllState` to flush all dirty state on shutdown. That makes state portable across
processes, OARs, and HG — the only cases the SQLite path cannot cover.

---

## PHLOX-1 — the two live compile failures, and an OSSL surface audit

**Logged:** 2026-09-07, against `1.1.264-alpha+bb4bcd03dc`. Both failures are from the
Ebony startup at 08:35 UTC. **Nothing was fixed in this pass** — see "What this needs"
at the end, and the decision it turns on.

### The two failures, and they are not the same kind of thing

**(1) `9898c41e-8e85-45ed-9235-be1cc6176936` — "Function 'osTeleportAgent' expects 4
arguments, got 3", lines 16:12 and 20:12.** Prim 181231483, item "ManholeAccess",
asset `01d4448d-087e-49fb-9253-4b06ce522811`. The script is 23 lines and calls
`osTeleportAgent(id, opos + <0,0,-3>, <0,1,0>)` at line 16 and the mirror at line 20 —
the **3-argument local-teleport overload**.

**This is Phlox's defect.** `OSSL_Api` has three overloads —
`osTeleportAgent(string, string, Vector3, Vector3)` (`OSSL_Api.cs:971`),
`osTeleportAgent(string, int, int, Vector3, Vector3)` (`:1015`) and
`osTeleportAgent(string, Vector3, Vector3)` (`:1051`) — and Phlox declares exactly one,
the 4-argument form (`InWorldz.Phlox/Types/Defaults.cs:4733-4736`,
`Glue/ISystemAPI.cs:404`). `osTeleportOwner` has the same three overloads in OSSL
(`:1096`, `:1104`, `:1111`).

**(2) `3eb0c62b-f307-41d4-a82c-da59aef5ca05` — "Symbol 'SetVehicleSettings()' already
defined", line 96:0.** Prim 181231469, item "DG Simple airship", asset
`b8079466-322a-47f5-ba8d-cd17d9e0da61`, 446 lines. The script defines the function
**twice, with different signatures**:

```lsl
92:  SetVehicleSettings()
93:  {
94:      SetVehicleSettings("");
95:  }
96:  SetVehicleSettings(string filter)
```

So: **once each at two arities**, not twice at one, and not a name colliding with a
variable or a state. It is user-function overloading.

**YEngine accepts this; Phlox rejects it.** YEngine's symbol table is keyed by *name
plus argument signature* — `MMRScriptVarDict.AddEntry` builds
`master[name][ArgTypes]` and returns false only when that exact pair is already present
(`MMRScriptVarDict.cs:132-151`) — and the duplicate-function error it would raise
prints `funcNameSig`, the signature-qualified name (`MMRScriptReduce.cs:303`, `:326`).
Two `SetVehicleSettings` entries with different `ArgTypes` are two distinct keys, so
YEngine admits both.

> **Caveat on that verdict.** It is read from YEngine's source, not from an executed
> compile: standing up the YEngine compiler needs a scene and there was no harness for
> it in the tree. The structural evidence is unambiguous — the dictionary is keyed by
> signature and the error message carries the signature — but it has not been *run*.

Note also that neither engine's behaviour here is "LSL as SL defines it": **SL has no
user-function overloading**, so the airship script is not valid LSL for SL either.
Phlox's rejection is defensible as strict-LSL; YEngine's acceptance is a deliberate
XMR-lineage extension. This tree therefore has two script engines that disagree about
what the language is, and the same script loads on one and not the other.

### The audit — Phlox's table against `OSSL_Api.cs` and `LSL_Api.cs`

Phlox declares **674** functions; the two Api classes expose **756** names.

| Class | Count | What it is |
|---|---|---|
| Overloaded in the API, and Phlox holds one signature | **2** | `osTeleportAgent` (3 overloads, Phlox has the 4-arg), `llLinkPlaySound` (2 overloads, Phlox has the 4-arg) |
| Arity divergence, single signature each side | **4** | `llDerezObject`, `llSHA256String`, `llTargetedEmail`, `llUpdateKeyValue` |
| Genuine type divergence | **1** | `llMapBeacon` — Phlox `(string,string,vector)`, API `(string,vector,list)` |
| In the API, absent from Phlox entirely | **271** | mostly newer OSSL: `osAES*`, `osApproxEquals`, `osAgentSaveAppearance`, `llCastRayV3`, `llListSortStrided`, `llsRGB2Linear`, … |

**A false alarm worth recording so nobody re-raises it.** A naive diff reports **60**
type mismatches; **59 of them are `key` against `string`** and are not defects. LSL's
`key` and `string` marshal to the same CLR type, so the Api signatures spell as `string`
what Phlox's table spells as `VarType.Key` — and Phlox's is the more faithful of the two
at the language level. Only `llMapBeacon` survives that filter.

**And the 4 arity divergences are internally consistent within Phlox.** Its own
implementations match its own table in every case — `llSHA256String(string,int)`
(`LSLSystemAPI.cs:7388`), `llTargetedEmail(int,string,string,string)` (`:12349`),
`llUpdateKeyValue(string,string,string)` (`:11431`), `llDerezObject(string)`
(`:3402`), `llMapBeacon(string,string,Vector3)` (`:12284`). These are two engines
carrying different *vintages* of the same functions, not a table that has drifted from
its own code. Editing Phlox's table to match OSSL without also changing these
implementations would break working scripts, which is why this pass changed nothing.

### What this needs, and why it was not done here

**`osTeleportAgent`'s missing overloads cannot be added to the table.** Phlox's function
table is `Dictionary<string, FunctionSig>` (`Defaults.cs:8`) — **one signature per
name**, with a single `TableIndex` per entry driving the syscall shim. Holding three
`osTeleportAgent` signatures needs the table to become name → list, plus overload
resolution at the call site, plus a dispatch index per overload. That is a compiler
change, not a data change.

**It is the same change failure (2) needs.** User-function overloading and built-in
overloading are one capability: resolve a call by name *and* argument signature instead
of by name alone. Do it once and both the manhole script and the airship script compile.

So the choice is a real one and it belongs to John:

- **(A)** Give Phlox signature-keyed symbol resolution — fixes both live failures, closes
  the 2 overload rows, and makes Phlox and YEngine agree. Largest change; touches the
  symbol table, call-site resolution and codegen.
- **(B)** Add only the missing *non-overloaded* functions from the 271 — real value, no
  compiler change, does nothing for either live failure.
- **(C)** Fix neither and treat both scripts as content bugs. Defensible for the airship
  (SL would reject it too); **not** defensible for the manhole, whose `osTeleportAgent`
  call is valid OSSL that this simulator advertises.

### Surfacing: today a broken script is a log line and nothing else

`PhloxScriptLoader.LogOutputListener.Error` writes `[PhloxCompile]: <itemId>: <message>`
to the log and increments a counter (`PhloxScriptLoader.cs:577-581`). That is the whole
of it — and the class says so itself: *"Minimal ILSLListener that sends compilation
errors to the log. Replace with a real listener adaptor once LSLSystemAPI is wired"*
(`:565-568`). The owner is never told, and the object gives no sign.

**Proposed SL-parity behaviour (not built):**

1. **The owner gets the error.** SL sends the compile error to the owner as script-error
   chat with the object name, item name and line/column. The listener has `m_ItemId` and
   nothing else, so it would need the part and item to address the owner.
2. **The object shows it.** In SL a script that failed to compile leaves the prim's
   script count showing a broken script; touch and inspect surface it. The minimum
   worth having here is that the failure is visible without console access.
3. **A console query.** `phlox scripts failed` listing item id, prim, region and first
   error — cheap, and the thing that would have surfaced these two without a startup-log
   grep.

None of that is built. (1) is the one with SL parity behind it and the smallest surface.

---

## PHLOX-2 — a compile harness, owner-visible errors, and the overload work not done

**Logged:** 2026-09-07. **Part 2 (signature-keyed built-in resolution) was NOT implemented.**
Parts 1 and 3 were. Read the "Why part 2 stopped" section before picking it up.

### Scope ruling, recorded

**Built-in (`ll*`/`os*`) overloads should resolve by name + signature. User-function
overloading stays rejected** — SL has no user-function overloading, so Phlox's existing
rule is the parity rule and YEngine's acceptance is the divergence. The airship
(`b8079466-322a-47f5-ba8d-cd17d9e0da61`, `SetVehicleSettings()` at line 92 and
`SetVehicleSettings(string)` at 96) is therefore **content to fix, not a compiler defect**.
`UserFunctionOverloadTests` pins that so nobody "fixes" it later by copying YEngine.

### What landed

**A Phlox test project — there was none.** `Tests/InWorldz.Phlox.Tests`, on
`CompilerFrontend.Compile(string)` with a collecting `ILSLListener`. The template path the
frontend takes is stored and never read, so a compile needs no scene, no region and no disk.
This is the harness whose absence made PHLOX-1 report YEngine's verdict from reading source
rather than running a compile.

**It reproduces the live failure verbatim** — `line 8:8 Function 'osTeleportAgent' expects 4
arguments, got 3`, the same message the region logged for `9898c41e-…` at 08:35 UTC.

**Owner-visible compile errors (part 3).** `PhloxCompileErrorReport.Build` composes the SL-shaped
message — `<object> [<script>]: script failed to compile` followed by the compiler's own lines,
positions and all — and `ToOwner` sends it once through `IDialogModule.SendAlertToUser`.
`PhloxScriptLoader.CompileAndStart` keeps its listener and calls it on failure. **One message per
failed compile however many errors it carried**, capped at ten lines with the rest pointed at the
log. The startup log line is unchanged: this is in addition, not instead. Silent when the region
has no dialog module or no part — a missing notification must never take down a script load.

### Why part 2 stopped, and what the next person needs

Four tests are committed **`[Fact(Skip = …)]`** — they are the executable spec and the Skip is the
reason. Remove it when the work lands.

The blocker is not the table, it is that **Phlox resolves calls by name alone at four layers**:

| Layer | Site | What it does today |
|---|---|---|
| Table | `InWorldz.Phlox/Types/Defaults.cs:8` | `Dictionary<string, FunctionSig>` — one signature per name, one `TableIndex` per entry |
| Symbols | `Compiler/SymbolTable.cs:172-182` | `_globals.Define(sysMethod)` keyed by `fn.FunctionName` — a second `osTeleportAgent` collides |
| Types | `Compiler/TypesVisitor.cs:612-616`, `:758` | compares against **the** parameter list; raises the "expects N arguments" error |
| Codegen | `ByteCompiler/BytecodeGenerator.cs:126`, `:174-194` | `_functions` keyed by name; `.Add` throws on a duplicate |

**The one unknown worth having resolved: the annotation channel exists.** `TypesVisitor` already
passes decisions forward to codegen through `_annotations` (`SetPromoteToType`, used at `:631` for
argument promotions). So the types pass can record *which* overload it chose on the call node and
codegen can read it — which is the mechanism a name-mangling implementation needs, and it is
already there.

**Recommended shape** (not built): keep `Dictionary<string, FunctionSig>` and key overloads by a
mangled name (`osTeleportAgent$3`) with `FunctionName` left bare; `SymbolTable` and
`BytecodeGenerator` then define and dispatch per mangled name with no collision and a correct
`TableIndex` each; `TypesVisitor` resolves the bare name, gathers `name` plus `name$N` candidates,
picks by arity then by `CanAssignTo`/`promoteFromTo` — **the implicit-conversion rule already in
the tree** (`TypesVisitor.cs:623-624`), which is LSL's int→float and key↔string — and annotates the
node with the winner. Then `LSLSystemAPI` gains the two `osTeleportAgent` forms and
`llLinkPlaySound`'s 4-argument form against `OSSL_Api.cs:1015`, `:1051` and its `llLinkPlaySound`.

**Why I did not do it in the time available:** it is four coupled layers of a compiler plus the
syscall shim, and the failure mode of getting it wrong is not a build error — it is a *silently
wrong dispatch index*, which corrupts running content instead of failing loudly. It wants a session
of its own with the harness now in place.

### Parked

- **The 4 arity divergences** (`llDerezObject`, `llSHA256String`, `llTargetedEmail`,
  `llUpdateKeyValue`) — **check these against the SL wiki, not against `OSSL_Api.cs`.** Phlox's
  table matches Phlox's own implementations, so OSSL is not the authority for whether Phlox is
  wrong; SL is. Input to PHLOX-3.
- **The 271 functions absent from Phlox** — PHLOX-3, to be ranked by SL-wiki usage rather than
  added wholesale. Re-derive with `Docs/audit/phlox-ossl-surface-audit.py`.
- **Script `96c2d98a`'s recurring "Slow timeslice" (250–2500 ms)** — perf lane, not compiler.
  Not investigated here.

---

## PHLOX-2b — signature-keyed built-in resolution: **UNFINISHED**

**Logged:** 2026-09-07. Four of the five layers are in and the solution is green, but **the
overloads are not yet reachable from a script**, so the manhole is *not* fixed. Three tests are
committed skipped with the exact next step in the Skip reason.

### What is in, and verified

- **The guard, committed first and green before anything moved** (`1742252656`).
  `DispatchIndexGuardTests` pins all 674 built-ins' `TableIndex` against a baseline captured at
  `34fb6d201b`, asserts indices stay unique and contiguous from zero, and asserts every index has a
  non-null shim. `SyscallShim._shimMap` is positional (`SyscallShim.cs:81`), so this is the check
  that a wrong dispatch is a red test rather than a live surprise. **It has stayed green throughout.**
- **`Defaults.cs` is name → `List<FunctionSig>`.** The 674 literal entries are *untouched* — the
  public table is built from them by `BuildByName()`, and the three new overloads append with keys
  `osTeleportAgent$3`, `osTeleportAgent$5`, `llLinkPlaySound$3` at indices 674-676. `AllMethods`
  and `TryGetMethod` serve the callers that predate overloading (CompilerFrontend, SLuaCompiler,
  the interpreter map, SluaProofRunner) with exactly their previous behaviour.
- **Shims and API.** Three shims appended at the matching positions; `ISystemAPI` and
  `LSLSystemAPI` gained `osTeleportAgent(agent,pos,lookat)` (OSSL_Api.cs:1051, routed through
  `iwTeleportAgent` with an empty region, which is this tree's own "same region" convention),
  `osTeleportAgent(agent,gridX,gridY,pos,lookat)` (OSSL_Api.cs:1015, handle built as
  `llTeleportAgentGlobalCoords` builds its own, behind the same `IsTeleportAuthorized` gate) and
  `llLinkPlaySound(link,sound,volume)` (LSL_Api.cs:2939, the four-argument form with flags 0).
  **`LSLSystemAPI` is the only live `ISystemAPI` implementation** — the brief asked; there is one.
- **Both compiler passes resolve by arity.** `TypesVisitor.ResolveCall` and
  `GenVisitor.ResolveCallForGen` try the bare name and fall back to `name$<arity>`; both derive
  their symbol names from `Defaults.SymbolNameFor`, as does the assembler
  (`BytecodeGenerator.cs:126`), so the three cannot disagree about which shim a call reaches.
  Argument types are still checked by the tree's own implicit-conversion rule — `promoteFromTo`
  plus `CanAssignTo` (`TypesVisitor.cs:623-624`), which is LSL's integer→float widening and its
  interchangeable key and string.
- **The error message already lists every accepted signature**, which is one of the four tests and
  it passes: *"Function 'osTeleportAgent' got 3 arguments; it accepts osTeleportAgent(string,
  string, vector, vector); osTeleportAgent(string, vector, vector); osTeleportAgent(string,
  integer, integer, vector, vector)"*.

### What is not in

**The mangled symbol does not resolve.** `Globals.Resolve("osTeleportAgent$3()")` returns null, so
`ResolveCall` falls back to the first signature and the call is still rejected. Everything upstream
of the symbol table is demonstrably correct — the error text above proves `Defaults` and the type
pass both see all three signatures.

**The next step, precisely:** `BaseScope.Define` keys on `sym.Name` verbatim (`BaseScope.cs:63`)
while every lookup is `Resolve(name + "()")`, so `MethodSymbol` is evidently not storing the string
it was constructed with. Confirm what `MethodSymbol.Name` actually returns after construction and
make `Defaults.SymbolNameFor` produce that exact form. It is expected to be a one-line change; it
was not made because the session hit its stop time mid-diagnosis, and guessing at a symbol-table
key is precisely the class of mistake this work exists to avoid.

### Standing

- **The manhole (`9898c41e-…`) is NOT fixed** and will still fail at every region start.
- **The airship stays rejected** by ruling; `UserFunctionOverloadTests` passes.
- Nothing is deployed. Solution 0 errors; `InWorldz.Phlox.Tests` 12 passed, 3 skipped.

---

## PHLOX-2c — overload resolution finished; the manhole is **fixed, pending deploy**

**Logged:** 2026-09-07. Closes PHLOX-2b. Built-in `ll*`/`os*` calls now resolve by name **and**
signature, all the way through to the shim.

### The two faults 2b stopped on

**(1) Only one of the two call visitors resolved by arity.** A statement-level call —
`osTeleportAgent(id, pos, lookat);` on its own line, which is exactly what the manhole writes —
reaches `TypesVisitor.VisitFuncCall`, not `VisitMethodCallPostfix`. 2b changed the latter only.
That is why the symptom was so confusing: the error message already listed all three signatures,
proving `Defaults` and the type pass both saw the overloads, while resolution still fell back to
the first. **The naming was never wrong.** `MethodSymbol.Name` returns `base.Name + "()"`
(`MethodSymbol.cs:43-47`), `RawName` the bare form, and `BaseScope.Define` keys on `Name` verbatim
(`BaseScope.cs:63`) — consistent throughout, and 2b's suspicion of it was misplaced.

**(2) The mangling character was illegal in the assembler.** The emitted instruction is literally
`syscall <symbol name>` (`ByteCodeEmitter.cs:174`), and the assembly lexer split on `$`:
*"no viable alternative at input 'syscallosTeleportAgent3'"*. The separator is now one named
constant, **`Defaults.OverloadSeparator = "__"`**, used by `SymbolNameFor`,
`CandidateSymbolNames`, both passes' lookups and the literal table's own keys. No LSL or OSSL name
contains a double underscore, so it cannot collide.

### The rule, and where it lives

One source of truth: **`Defaults.SymbolNameFor(sig)`**. The first signature declared for a name
keeps the bare name — so every script that compiled before resolves to the same symbol and the same
`TableIndex` — and later overloads become `name__<arity>`. `SymbolTable` defines from it,
`BytecodeGenerator` keys its assembler map from it, and both compiler passes resolve against it,
so the type checker, the emitter and the assembler cannot disagree about which shim a call reaches.
Argument types remain the tree's own implicit-conversion rule: `promoteFromTo` plus `CanAssignTo`
(`TypesVisitor.cs:623-624`), LSL's integer→float widening and its interchangeable key and string.

### Verified

- **`InWorldz.Phlox.Tests`: 38 passed, 0 skipped.** Solution 0 errors.
- **`DispatchIndexGuardTests` green throughout** — no existing built-in moved its index, none
  vanished, indices stay unique and contiguous, every one has a non-null shim.
- **Every script live on this grid compiles.** All 17 distinct script assets in prims across every
  region are committed under `Tests/InWorldz.Phlox.Tests/LiveScripts` and compiled by
  `LiveScriptCompileTests` — content, not fixtures, because they are what actually runs. The only
  failure is the airship, which must fail.
- **The 20-plus built-ins those scripts actually call each keep their original dispatch index**,
  checked from the direction that matters rather than only across the table.

### Standing

- **The manhole (`9898c41e-…`, asset `01d4448d-…`) compiles — fixed, pending deploy.** It has
  failed at every region start since it was rezzed; it will start on the next one that carries this.
- **The airship (`3eb0c62b-…`) still fails, by ruling**, on user-function overloading, with a
  message about the duplicate symbol. It is content to fix, not a compiler defect.
- **`botRemoveBot`'s key** is fixed and pinned (see the PHLOX-2c key commit).
- Nothing is deployed.

---

## PHLOX-2d — freshly started scripts never ran, and it was never a 2b/2c regression

**Logged:** 2026-09-07, against live 1.1.275. **Fixed; not deployed.**

### The defect

`PhloxExecutionScheduler.FinishedLoading` set a fresh script to
**`RunState = Running`** and then posted its `state_entry`
(`PhloxExecutionScheduler.cs:189-195`). But `ProcessEventQueue` only **starts** an event when the
script is **`Waiting`** (`:681`); anything else is **queued** (`:687`). The queue is drained by
`TransitionToWait` (`:558-565`), which runs only for a script already on the run queue — and
`StartEvent` (`:887-894`) is the only thing that puts one there. So a freshly started script sat in
`Running` with its `state_entry` in a queue nothing would ever drain: no execution, no error, no log
line. It is now set to `Waiting`, exactly as the restored-state branch always did (`:200`).

### Why every piece of the evidence fits

| Evidence | Explanation |
|---|---|
| Manhole compiled at 11:12:51 then nothing, touch text never set | fresh start → `Running` → `state_entry` queued for ever |
| New script "Starting from disk cache", then silent | same; the disk-cache path calls the same `BeginScriptRun` |
| Script 96c2d98a executing all day | **restored from saved state** → `Waiting` (`:200`) → events start normally |
| Zero ERROR/EXCEPTION lines | nothing threw; the event was simply never started |

**It is not a PHLOX-2/2b/2c regression.** `git blame` puts both `:189` and `:681` at `02cf1370df`,
the original Phlox import. What 2c changed is that the manhole *compiles* now, so it reached the
fresh-start path for the first time; and every other script on the region had been restored from
state, which is the branch that works. The defect was there all along with nothing to reveal it.

### What the dispatch guard did NOT pin, and what now does

`DispatchIndexGuardTests` pins that no built-in's `TableIndex` **moved**. It says nothing about
whether a compiled call **reaches** the right shim, or whether a script executes at all — the whole
suite compiled scripts and never ran one. `ScriptExecutionTests` now drives a compiled
`state_entry` through the real `Interpreter` against a recording `ISystemAPI` (generated with
`DispatchProxy`, the interface having hundreds of members) and asserts `llSay` arrives. **It passes
on the unmodified tree**, which is what proved dispatch sound and sent this to the start path —
the `__` separator and the `Shim_botRemoveBot` key fix altered no existing built-in's runtime map.

`FreshStartRunStateTests` pins the fix at source level, both halves: the fresh-start branch leaves
the script `Waiting`, and `ProcessEventQueue` starts an event only for a `Waiting` script. The
scheduler needs a `Scene` and a prim to construct, so this is a source assertion of the invariant
rather than a live one — a real gap, and the reason the defect survived.

### Evidence 3 — the edit-and-save producing no compile — is NOT this defect

The save path is the `UpdateScriptTask` cap (`BunchOfCaps.cs:256-258`) →
`UpdateItemAsset.UpdateScriptTaskInventory` (`:146`) → `Scene.CapsUpdateTaskInventoryScriptAsset`
(`Scene.Inventory.cs:427`), which calls **`part.Inventory.CreateScriptInstanceEr(...)`** directly
and then `EventManager.TriggerUpdateScript` and `ResumeScripts()`. It does **not** raise
`OnRezScript`, so the absence of an `OnRezScript` line after a save is expected rather than a fault;
the absence of a `Compiled` line is consistent with the asset already being in the disk cache.

**A19's `IAgentAssetTransactions` void→bool does not sit on this path and is not in this deploy.**
`git log bb4bcd03dc..c94c561cf1` over `OpenSim.Region.Framework/` and
`CoreModules/Agent/AssetTransaction/` is **empty** — A19 shipped at 1.1.262 and the script-save path
was untouched between 1.1.264 and 1.1.275. Whether the saved script then *ran* is the same
fresh-start question this row fixes; whether the save itself stored is unverified. **Not fixed here.**

### Correction to the record

**PHLOX-2c's deploy row claimed the manhole was "fixed, pending deploy" and the deploy row listed
its verification as pending — but the verification that mattered was never possible from what was
checked.** The 1.1.275 deploy verified that the *binaries* landed: hashes, metadata names, the
overload keys. It did not, and could not, show a script running, because nothing in the suite ran
one. The manhole compiled and still did nothing. **A "did it land" check that stops at the artefact
is not a verification of the behaviour**; the behaviour needed a script to execute, and that test
did not exist until now.

---

## PHLOX-2e — the scheduler harness exists, and it says the fault is NOT in the scheduler

**Logged:** 2026-09-07. **PART 1 only. Stopped at the gate, deliberately — no fix was made.**

### The harness

`SchedulerHarness` stands up a whole `PhloxEngine` on a `SceneHelpers` test scene, puts a real
script in a real prim's task inventory (`TaskInventoryHelpers.AddScript`), rezzes it exactly as
`PhloxEngine.OnRezScript` does, and then pumps `PhloxScriptLoader.DoWork` and
`PhloxExecutionScheduler.DoWork` by hand instead of running `PhloxMasterScheduler`'s thread — so a
test is deterministic and cannot hang. `llSay` is observed where it really goes,
`Scene.SimChat` → `EventManager.OnChatFromWorld` (`Scene.PacketHandlers.cs:51-85`), because the
engine builds its own `LSLSystemAPI` inside `FinishedLoading` and offers no seam for a stub.

This is the gap PHLOX-2d named and could not build in the time it had.

### The result, which is not the expected one

| Variant | Result on `9482615186` |
|---|---|
| fresh compile → `state_entry` runs | **PASSES** |
| shared-script start (second instance of a loaded asset) → `state_entry` runs | **PASSES** |
| fresh instance handles a posted `touch_start` | **PASSES** |

**The scheduler runs a fresh instance correctly.** The second variant is the live symptom exactly —
`Starting shared script 2074003b` at 16:55:18 for a new item on a fresh prim — and it works here.
So on the brief's own rule the fault is **outside the scheduler**, and this stops here rather than
producing a fix that passes.

### Three harness faults were found and fixed on the way, and they are worth knowing

Each of them produced a convincing red that meant nothing:

1. **`DispatchProxy` cannot proxy a `sealed` type.** The `IWorldComm` stub threw at construction.
2. **No task-inventory item.** `PhloxScriptLoader.FindAssetId` reads
   `Prim.Inventory.GetInventoryItem` and drops the load when it is absent
   (`PhloxScriptLoader.cs:301-307`). It *does* log an error there — an earlier reading of
   `PerformLoad:237-238` as a silent drop was wrong.
3. **The engine name.** `PhloxEngine.Name` is **`"InWorldz.Phlox"`**, not `"PhloxEngine"`, and
   `OnRezScript` returns immediately when the name does not match (`PhloxEngine.cs:312`). Passing
   the wrong one dropped every rez **silently — no log line at all**, and left every queue empty.
   That is the one to remember: it is indistinguishable, from outside, from the live symptom.

### Where to look next

The scheduler is exonerated for a fresh instance, so the live difference is in what the region does
that the test scene does not. Candidates, none investigated:

- **`TaskInventoryItem.ScriptRunning`** — the region persists a per-item running flag; the harness
  never sets or reads one, and `ProcessEventQueue` skips a disabled script for every event except
  `STATE_ENTRY` (`PhloxExecutionScheduler.cs:655-656`).
- **`OnStartScript` / `ChangeEnabledStatus`** — the region calls these around rez; the harness does
  not.
- **The master scheduler's thread**, which the harness deliberately replaces. If the live failure is
  a lost wake-up, this harness cannot see it by construction — the `m_ActionEvent` comment at
  `PhloxMasterScheduler.cs:70-79` describes exactly that class of bug having been fixed once before.
- **`StateManager.LoadState`** returning something non-null for a brand-new item, which would take
  the restored-state branch and leave the script `Waiting` with no `state_entry` ever posted.

**The last one would fit every observation** and is the first thing to check, but it is a guess and
is recorded as one.

---

## PHLOX-2f — a fresh instance was born disabled, so the region never learned the prim was touchable

**Logged:** 2026-09-07. **Fixed; not deployed.** The timer half was **not reproduced** — see below.

### The defect, and it is one line

`RuntimeState(int numGlobals)`, the constructor used for every fresh interpreter
(`Interpreter.cs:96`), set `LocalDisable` but **never set `GeneralEnable`**, which therefore
defaulted to `false`. Only `Reset()` set it (`RuntimeState.cs:286`).

`PhloxExecutionScheduler.FinishedLoading` computes the script's event mask at **`:184`**, and the
freshStart branch calls `sysApi.OnScriptReset()` at **`:190`** — *after*. And
`LSLSystemAPI.SetScriptEventFlags` gates the entire mask on `GeneralEnable`
(`LSLSystemAPI.cs:96-101`):

```csharp
if (m_thisScript.ScriptState.GeneralEnable && ... )
    foreach (var evt in ...) flags |= MapEventFlag(...);
m_host.SetScriptEvents(m_itemID, flags);      // :102
```

So a fresh instance sent the part a mask of **zero**. `SceneObjectPart.aggregateScriptEvents`
derives `PrimFlags.Touch` from `anytouch` in that mask (`:5254-5256`, into `m_localFlags` at
`:5269`), and the region's own touch dispatch tests the same mask
(`Scene.PacketHandlers.cs:334`). With it empty: **no touch cursor, and `touch_start` can never
fire** — while `state_entry` still ran, because `ProcessEventQueue` lets `STATE_ENTRY` past a
disabled script (`:664`). That is exactly what was seen on 1.1.277 at 15:48-15:53: llSetColor,
llSetText, llSay and llOwnerSay all worked, Running was ticked, and the prim could not be clicked.
It is also why the manhole's `llSetTouchText` ran and the menu still read "Touch".

A restored-state instance takes `savedState.ToRuntimeState()`, which carries a persisted
`GeneralEnable`, which is why restored scripts have always been touchable.

**Fixed at the root**: the fresh constructor sets `GeneralEnable = true`. A brand-new script is
enabled; that is not a property of having been Reset.

### Tests

`EventMaskRegistrationTests` — three, all red before the fix:

- a fresh compile registers `touch_start` on the part, and the aggregate carries `anytouch`;
- a **shared-script start** (second instance of a loaded asset) does too — the live path;
- a touch through the **scene's own route** (`EventManager.TriggerObjectGrab` → the engine's
  `OnObjectGrab`) reaches `touch_start`. Not a directly posted event.

Building the third turned up that `PhloxEngine.BuildTouchDetectParams` dereferences
`remoteClient.AgentId` with no null check (`PhloxEngine.cs:464`). Harmless in world, where there is
always a client; noted, not fixed.

### The timer half was NOT reproduced

`TimerCadenceTests` asserts a `llSetTimerEvent(3.0)` script fires at most twice in 3.5 s. **It
passes on the unmodified tree.** The units are consistent end to end: `SetTimer` stores
`(int)(sec * 1000)` ms (`:421`), `readyOn` adds that to `Util.EnvironmentTickCount()` which is also
ms (`:434`), and `CheckSleepingScripts` compares the two directly (`:601-605`). **No unit or scale
error found, and none invented.**

So the live ~5 ticks/second has another cause, and the most likely place is the one this harness
cannot see by construction — `PhloxMasterScheduler`'s thread and its wake-up arithmetic, which the
harness replaces with a hand-driven pump. `PhloxMasterScheduler.cs:70-79` already documents one
lost-wakeup bug of that family having been fixed. **Next step: measure it in world with the new
`phlox status`, which prints the timer interval the scheduler is actually holding.**

### `phlox status` (PART 3)

Read-only, and it changes nothing:

```
phlox status <script-item-uuid | object-name>
```

Prints, per script: prim and localId, script name, **RunState**, `Enabled` / `GeneralEnable` /
suspended, the item's **Running flag**, **queued events**, LSL state, **timer interval in ms**, and
the **region's event mask** for the prim (`part.ScriptEvents` and `AggregatedScriptEvents`). When
there is no interpreter it says so and reports what the prim still knows about the item.

Every one of those is something the last four sessions had to infer from silence or reach for with
a debugger. The mask line is the one that would have made PHLOX-2f a five-minute diagnosis.

### Amendments to PHLOX-2d and PHLOX-2e

- **PHLOX-2d's `Waiting` fix stands** and was correct — `ProcessEventQueue` does only start an
  event for a `Waiting` script — but it was **not the live symptom**. The live symptom was this:
  the event mask never reaching the region, plus the timer cadence, which is still open.
- **PHLOX-2e's conclusion stands too**: the scheduler does run a fresh instance, and it does. What
  2e could not see is that running is not enough — the region also has to be *told* what the script
  handles, and that is a different call on a different object.

---

## PHLOX-2g — 24 async syscalls never signalled completion; and llSetTouchText never told the viewer

**Logged:** 2026-09-07. **Fixed; not deployed.**

### Two defects, and the brief's framing needs one correction

**`llSetTouchText` is a synchronous shim and it does complete.** `Shim_llSetTouchText`
(`SyscallShim.cs:3075-3081`) pops one operand, calls the API and returns; it never sets
`Status.Syscall`. So it is not what parked the manhole. Two separate things were wrong:

**(1) The menu text never reached the viewer.** `LSLSystemAPI.llSetTouchText` set
`m_host.TouchName = text` and stopped (`:1749-1753`). The context menu comes from the object
update, so without scheduling one the viewer keeps whatever it last received — which is why the
menu still read "Touch" after `state_entry` had run `llSetTouchText("Enter")` perfectly well.
`llSetClickAction` immediately below already marks the group changed for exactly this reason. Now
sets `HasGroupChanged` and calls `ScheduleFullUpdate()`.

**(2) The script was parked in a *different* syscall — and 24 of them could do it.** The contract
for a long-running call is: the shim sets `RunState = Status.Syscall` and hands the body to
`_asyncCallDelegate`, and **the API implementation must call `SysReturn`** to bring the script back
(`PhloxEngine.cs:1100` → `PhloxExecutionScheduler.PostSyscallReturn:461` →
`ProcessSyscallReturns:893-913`). Nothing enforced it. Of the 36 shims that set `Status.Syscall`,
**24 called an implementation that never returns**, so the script stayed in `Syscall` for ever:
no error, no timeout, later events piling up — the manhole's four queued events at 19:21.
`DrainAsyncQueue` even catches a throw from the body and logs it (`:1107-1111`), which strands the
script just as thoroughly.

The manhole's `touch_start` calls `osTeleportAgent`, which is on that list. **Two of the twenty-four
were mine**, added in PHLOX-2b by copying the shape of `Shim_osTeleportAgent` — which was already
broken.

### Fixed at the mechanism, not per function

`SyscallShim.RunAsync` sets the state, runs the body and signals completion **in a `finally`**, so a
throwing body cannot strand a script either. Completion goes through a new
`ISystemAPI.CompleteSyscall()`, implemented as `SysReturn(itemId, null, 0)`. It is safe alongside
implementations that post their own return: `ProcessSyscallReturns:897` ignores a return that
arrives when the script is no longer in `Syscall`, so a duplicate is a no-op.

22 shims converted to `RunAsync`. The **14 that still set `Status.Syscall` by hand are correct as
they are** — their implementations do post a real return, with a value and a delay that the backstop
would have to invent.

### `phlox status` (PART 3)

- **`engine='True'` is fixed.** It printed `item.ScriptRunning` twice; it now prints the engine name.
- **The `RunState` line names the pending syscall**: `RunState : Syscall  (in osTeleportAgent)`.
  `RuntimeState.LastSyscallIndex` records the TableIndex at dispatch (`SyscallShim.Call`), and the
  console maps it back through `Defaults.AllMethods`. Diagnostic only, never persisted.
  **That one word is the difference between a half-hour trace and a five-minute one**, and it is the
  thing this session most wished had existed at the start of the last one.

### Amendment to PHLOX-2c

The manhole's story across four sessions, in order: it **compiled** at 2c (the 3-argument
`osTeleportAgent` overload), it **registered touch** at 2f (`GeneralEnable` on a fresh instance),
and it was **parked in `osTeleportAgent`** until 2g — with its menu text never reaching the viewer
throughout. Each fix was correct and none of them alone made the object work.

### Also

`TimerCadenceTests` was tightened to a loose bound after it proved flaky under full-suite load: it
is wall-clock timing on a shared machine, and it exists to catch the ~17-ticks-in-3.5s signature,
not to measure jitter. **The timer cadence itself is still unreproduced** (PHLOX-2f).

---

## PROPS-1 — the touch label never reached the viewer

**Logged:** 2026-09-08. **Fixed; not deployed.**

### Where it was lost: not set, not copied, not sent, or sent too early?

It was **sent too early and never re-sent**. Each of the other three is ruled out:

- **Set on the part** — yes. `TouchName` writes `osUTF8TouchName` (`SceneObjectPart.cs:1109-1114`),
  and PHLOX-2g's fix made `llSetTouchText` set it; `phlox status` showed the mask and the value.
- **Copied into the reply** — yes. The full ObjectProperties block writes it at
  `LLClientView.cs:6387` (`zc.AddShortLimitedUTF8(sop.osUTF8TouchName)`).
- **Sent on select** — yes. `Scene.PacketHandlers.SelectPrim` calls
  `part.SendPropertiesToClient` for every selected prim (`:223`).
- **Sent on right-click** — **no, and this is the gap.** A right-click sends
  `RequestObjectPropertiesFamily`, which reaches
  `SceneGraph.RequestObjectPropertiesFamily` (`:1588-1593`) →
  `ServiceObjectPropertiesFamilyRequest`. **The Family reply has no touch-name field at all.**

The viewer confirms the same shape from the other side: `LLSelectMgr::processObjectProperties`
fills `LLSelectNode::mTouchName` (`llselectmgr.cpp:6110`) — that is the *full* ObjectProperties
handler — and the context-menu label reads it, falling back to the default label when empty
(`llviewermenu.cpp:3096-3105`). Nothing in the Family path touches it.

**So the label a viewer shows is whatever it was told the last time the object was selected.** A
script that changes it afterwards is invisible until the next select. PHLOX-2g's
`ScheduleFullUpdate` does not help: an ObjectUpdate carries no touch name.

Worth noting the same is true upstream — `LSL_Api.llSetTouchText` (`:8420-8426`) also just assigns
the field (and truncates to 9 characters, which Phlox does not) — so this is a latent OpenSim
behaviour, not a Phlox regression. It only became visible here because a script was setting the
label from `state_entry` on an object nobody had re-selected.

### The fix

`SceneObjectPart.SendPropertiesToAllClients()` pushes the full ObjectProperties to every client in
the region; `llSetTouchText` calls it after setting the value. Pinned by a test that adds a real
client to the scene and asserts the reply was sent for that part — **verified red** by disabling
the push, not just green after it.

**Not done:** `llSetSitText`, `llSetObjectName` and `llSetObjectDesc` have the identical shape and
were left alone. They are the same defect and the same one-line fix, but they were not in evidence
and changing them unasked would be scope I did not measure.

---

## PHLOX-3 framing — Mike Chase's consolidation note, and PHLOX-1 reconciled

Read from `upstream/develop:Docs/PhloxYEngineConsolidation.md` (fetched, **not merged**), captured
2026-09-07, status *discussion / not started*.

- **Merging the two runtimes is not recommended.** The outer contract is already shared
  (`IScriptEngine`/`IScriptModule`, `EventParams`, `DetectParams`, partly `LSL_Types`). The syscall
  layer is the incompatibility: YEngine's emitted IL calls interfaces typed in `LSL_Types` wrapper
  structs, Phlox's VM calls `ISystemAPI` typed in raw CLR primitives — unifying is a rewrite of one
  side, ~30k vs ~13k lines to cross-check. The compilers differ on purpose: Phlox's bytecode VM buys
  **serializable mid-execution state**, YEngine's IL buys speed.
- **Retiring YEngine is directionally right but gated on OSSL parity**, and that gate is wide open.
- **Keep both engines** meanwhile, YEngine config-gated and non-default, as the fallback for
  OSSL-heavy content. Unifying the duplicated `AsyncCommandManager`/Http/Xml/Sensor plugins is the
  one merge rated worthwhile (low–medium); Timer/Dataserver/Listener must stay Phlox-native because
  they are bound up with the serializable-state guarantee.
- **PHLOX-3 is therefore "port the OSSL surface to Phlox"**, with its own conformance suite —
  analogous to `Tests/SluaProofRunner` — *before* any deprecation date is set.

**Reconciled with PHLOX-1's audit, and the numbers agree exactly.** Counted again today from
`Defaults.cs`: **`ll*` 532, `os*` 2, `iw*` 82, `bot*` 58**; `OSSL_Api` exposes **268** `os*`. That
is Mike's table to the digit. PHLOX-1's "271 absent from Phlox" is the same fact from the other
side — 266 missing `os*` plus a handful of newer `ll*`. Core LSL is close to complete; **OSSL is
the hole**, and it is essentially the whole of it.

Two of PHLOX-1's rows are now closed and should not be recounted as gaps: the **2 overload
families** (`osTeleportAgent`, `llLinkPlaySound`) were resolved by PHLOX-2b/2c, which is also why
`os*` reads 2 rather than 1. The **4 arity divergences** and **1 type divergence** (`llMapBeacon`)
remain, and PHLOX-1's standing instruction holds: check those against the **SL wiki**, not against
`OSSL_Api`, because Phlox's table matches Phlox's own implementations.

---

## PHLOX-3 candidates seen on 1.1.287

**Logged:** 2026-09-09 - (i) and (ii) from the MERGE-1 deploy's live verification, (iii) from the startup review of the first two starts on 1.1.287, (iv) from PHLOX-3b. **The timer-floor candidate is resolved - see PHLOX-3b below; (v) was resolved as PHLOX-3a.**

### (i) `phlox status <name>` reports a miss once per scene that does not hold the object

After finding and printing the object in one scene, the command still prints **"No object named ..."** for
every other scene in the process - three regions here, so a successful lookup is followed by two misses.
**Cosmetic**: the answer is correct and complete, it is the per-scene loop reporting rather than the
aggregate. Worth fixing when the command is next touched, not on its own.

### (ii) `3eb0c62b` still fails to compile: "Symbol 'SetVehicleSettings()' already defined"

Unchanged on 1.1.287 - `[PhloxCompile]: "3eb0c62b-f307-41d4-a82c-da59aef5ca05": "line 96:0 Symbol
'SetVehicleSettings()' already defined"` at load. This is the last of PHLOX-1's live compile failures whose
**verdict is still unsettled**, and it stays unsettled for the same reason as before: PHLOX-1's rule is that
if YEngine accepts what Phlox rejects it is a Phlox compiler rule to fix, and if both reject it is the
script's bug and gets recorded rather than fixed. **Nobody has compiled this script under YEngine yet.**
Until that is done, any change to Phlox's duplicate-symbol handling would be a guess.

### (iii) `phlox status` cannot be given the id the warnings actually print

`Slow timeslice for "96c2d98a-..."` prints an **asset id**. `phlox status` accepts an **item id** or a prim
name, so the one identifier an operator has in front of them at the moment they want to diagnose is the one
the command will not take. Tracing 96c2d98a to "Timer test (2)" on 2026-09-09 took a manual search that the
command should have done. **Make `phlox status` accept an asset id** and report every instance running it -
which is also the right answer when one asset is shared across several prims.

---

### (iv) The harness-based tests fail intermittently under xUnit's collection parallelism

**Measured, not assumed, on 2026-09-09:** **2 failures in 8 full runs** of `InWorldz.Phlox.Tests`, on
**two different** tests - `TimerFloorTests.AValueAboveTheFloorIsUntouched` once and
`FreshInstanceExecutionTests.AFreshCompileRunsItsStateEntry` once. **Each passes in isolation.** With
collection parallelism off (`-- xUnit.ParallelizeTestCollections=false`) the suite was green **3/3**, at
four times the wall clock.

**This is called out rather than written off**, because the last time this suite showed an intermittent
failure the answer was a real race - PHLOX-2f's `TimerCadenceTests`, which turned out to be the
unguarded `AsyncCommandManager.StartThread` (PROPS-1). It was twice dismissed as flakiness and a test
bound was widened to hide it. That must not happen again.

**The shape suggests, but does not establish, shared static state.** Every harness test constructs a
whole `PhloxEngine` on its own `TestScene`, xUnit runs collections in parallel, and `PhloxEngine` reaches
static state (`AsyncCommandManager`'s static thread, the SLua-proof console registration guard). PHLOX-3b
added six more harness tests, which raises exposure - it did not introduce the fault, since one of the two
observed failures is in a test that predates it.

**Do not raise a bound, and do not turn parallelism off in the csproj to make it green.** Either would
hide it exactly as before. The first step is to reproduce it deliberately - construct several engines
concurrently in one test and see what breaks - and only then decide whether the fix is isolation in the
harness or a real lock in the engine.

## PHLOX-3a - RESOLVED: `state default;` crashed the compiler

**Logged:** 2026-09-09 as candidate (v). **Fixed the same day; not deployed.**

### What it looked like

Every region start under 1.1.287 logged, twice, at 04:37:36 and again at 04:56:15:

```
ERROR [PhloxCompile]: 4e51f068-...: Object reference not set to an instance of an object.
ERROR [PhloxLoader]: Compilation failed for 1ee3b9b1-... item 4e51f068-...
```

The script is `lmap4` on Ebony, a four-state lamp belonging to a Legion Grid resident. Nothing in it is
exotic, which was the first useful signal: whatever the compiler tripped over had to be something
ordinary.

### The cause, in one line

**`TypesVisitor.VisitStateChangeStmt` dereferenced a null `ID()` for `state default;`**
(`Source/InWorldz.Phlox/Compiler/TypesVisitor.cs:206`). The grammar matches the default state as a
**keyword**, not an identifier:

```
LSL.g4:87    | stateNode='state' (ID | 'default') SEMI    # stateChangeStmt
```

so `context.ID()` is null for exactly that statement, and `.GetText()` threw out of the whole compile.
The very next line read `stateName == "default"`, so the check was written expecting `default` to
arrive as an ID. It never does.

**`state default;` is valid LSL** - the SL wiki's State page demonstrates it - so the fix is to compile
it, not to reject it with a better message. And **any** script that returns to its default state hits
this, which is most state machines; `lmap4` is simply the one that happened to be rezzed.

### Why the back end was never wrong

`GenVisitor.cs:237` already reads `context.ID()?.GetText()` and `ByteCodeEmitter.StateChange` emits
`statechg @default` for a null or empty id. Code generation has always handled this correctly. Only the
type-check pass forgot, so the fix is one null-conditional plus a fallback token for the error position -
no restructuring.

### The second defect, which is the more important one

`CompilerFrontend.Compile` ends in a blanket `catch (Exception e)` that reported `e.Message` and nothing
else. So the crash reached the resident, through PHLOX-2's owner-visible path, as *"Object reference not
set to an instance of an object."* - **indistinguishable from a message about their own script**. Someone
was being told their script was broken when the compiler had fallen over, and that is part of why this
sat in world unexplained.

New `InWorldz.Phlox.Types.CompilerCrash` marks a crash on its way through `ILSLListener` (which carries
strings, and the front end has neither a logger nor a dialog module), so:

- the region log now gets the **exception type and stack**, through the listener that already logs at ERROR;
- the owner gets **`Script <name>: compiler error (not a script syntax error) - <type>; reported to the
  grid operator`**, and never a stack;
- an ordinary error still reports in the `line N:C <message>` form - pinned by its own test, because
  without that the distinction is only a second wording.

### Commits

| commit | what |
|---|---|
| `3efe425650` | red test + `Fixtures/lmap4.lsl`, the asset byte-for-byte |
| `7ae8910a41` | the fix: null `ID()` handled in `TypesVisitor` |
| `39f1a0c113` | owner alert distinguishes a compiler crash from a script error |

Phlox suite **55 -> 60**, all green; solution 0 errors. **Not deployed** - deploy is a separate session.

### Filed while here, not chased

The SL wiki's State page warns: *"NEVER do a state change from within a touch_start event - that can lead
to the next touch_start on return to this state to be missed."* `lmap4` changes state from `touch_start`
in all four states. That is a **script-quality** matter for its owner, not a compiler defect, and nothing
was changed on the resident's script.

---

## PHLOX-3b - RESOLVED: `llSetTimerEvent` had no floor

**Logged:** 2026-09-09 as candidate (iii). **Fixed the same day; not deployed.**

### What it cost

A resident script on Ebony called `llSetTimerEvent(0.01)`. Phlox honoured it exactly - `phlox status`
read back `timer: 10 ms` - and the region logged `Slow timeslice` warnings of **1-1.8 s** for two days,
across three regions, until the prim was deleted. **One script, one prim, three regions degraded**, and
nothing in the engine said no.

### Where the floor came from, and where it did NOT

**The candidate note was wrong, and the correction matters more than the fix.** It said *"SL and InWorldz
clamp"*. Both halves are false, and both were checked against sources this time rather than memory:

| source | what it actually does |
|---|---|
| **SL wiki**, `llSetTimerEvent` | Documents **no minimum**. Only *"Cause the timer event to be triggered a maximum of once every sec seconds"* and *"Passing in 0.0 stops further timer events"*. The `timer` event page says nothing about a minimum or a frame-rate limit either. |
| **Halcyon / InWorldz** (`D:\halcyon-reference`) | **No clamp anywhere.** `LSLSystemAPI.cs:1116` -> `EngineInterface.cs:831-834` -> `ExecutionScheduler.cs:1846-1853`, `TimerInterval = (int)(sec * 1000)`, tested only `> 0`. |
| **Upstream, in this repo** | **Clamps.** `LSL_Api.cs:4005-4011`, `if (sec != 0.0 && sec < m_MinTimerInterval) sec = m_MinTimerInterval;` - code default 0.5 (`:113`), config key `MinTimerInterval` (`:518`), and `OpenSimDefaults.ini` ships `[YEngine] MinTimerInterval = 0.1`, **which is live on this grid**. |

So until this fix, **the same call behaved differently depending on which engine ran the script** - YEngine
floored at 0.1 s, Phlox at nothing.

### The fix

**0.1 s, config-driven** - `MinTimerInterval` in `[InWorldz.Phlox]`, same key name and same default as the
other engine, so an operator sets one number and both agree; `0` disables the floor; the value is logged
at startup next to `Enabled`. **John chose the config-driven form over a hard constant**, having been
shown that neither named source supported any constant.

One clamp, **at the API entry and not in the scheduler**, so `phlox status` reads back the value actually
applied - a script and the log cannot then disagree about how fast a timer is. **Zero and negative are
untouched**: 0.0 still stops the timer per the wiki, and negative still lands `<= 0` where the scheduler
arms nothing. One **DEBUG** line per script instance when a value is clamped, naming item, requested and
applied - not per tick, since a 10 ms timer would write a hundred lines a second.

**A detail worth keeping:** `(int)(sec * 1000)` truncates, so before the clamp `llSetTimerEvent(0.0001)`
did not mean *very fast*, it meant **timer off**. Raising it to the floor changes what such a script does,
not merely how fast it runs. Rare, but it is a behaviour change and not only a limit.

### Commits

| commit | what |
|---|---|
| `ef064538a0` | red test - 6 cases through the whole engine; 2 red (10 ms, and 0 ms for the sub-millisecond case) |
| `516c6a9be8` | the fix: config-driven floor, clamp at the API entry |

Phlox suite **60 -> 66**, all green; solution 0 errors. **Not deployed.**

**Did-it-land, named for the deploy:** a fresh prim calling `llSetTimerEvent(0.01)` shows **`timer: 100 ms`**
in `phlox status`; **one** DEBUG clamp line appears in the log for it; and **no `Slow timeslice`** is logged
for that script.


---

## PHLOX-4 - restored scripts, and one clock for the engine

**Logged and part-fixed 2026-09-09. Not deployed. Two arms deliberately left open - see below.**

### The four saved states, and what each did

`PhloxExecutionScheduler.FinishedLoading` restored the whole saved `RuntimeState` - RunState, Calls,
TopFrame, RunningEvent, EventQueue and a relative NextWakeup - and then **overwrote RunState with an
unconditional `Waiting`**, re-registering only the timer and the listens. Everything the state carried
about what the script was *doing* was dropped. `StateManager.ScriptUnloaded` saves at shutdown, so a
script asleep or mid-event when the region stopped was saved in exactly the state that never resumes.

| saved as | what happened | now |
|---|---|---|
| **Sleeping** | never `TrackSleep`'d - nothing woke it, and the stale frame stayed on the stack, so the next unrelated event pushed on top of it (`RuntimeState.cs:335-336`) and the interrupted handler resumed **nested inside** the new event | **fixed** - `TrackSleep(NextWakeup)`, pinned by test 1a |
| **Waiting with queued events** | `ProcessEventQueue` reads only `m_PendingEvents`; `ScriptState.EventQueue` is drained by `TransitionToWait`, which runs only for a script already on the run queue. A restored script is on neither, so queued events sat for ever | **fixed** - `DeliverNextQueuedEvent`, pinned by test 1c |
| **Running** | never `AddToRunQueue`'d; same nesting on the next event | **fixed in PHLOX-4b** - `AddToRunQueue`, pinned by test 1b; see the correction below |
| **Syscall** | unhandled | **fixed in PHLOX-4b** - `ResumeFromSyscall` with tag 22, pinned by test 1d |

### Running - CORRECTED in PHLOX-4b: the one-liner was sufficient; the finding was a measurement error

PHLOX-4 recorded here that `AddToRunQueue` "does not work" because a script captured mid-loop was
"restored `Running` and stays `Running` through 3000 pump rounds on a 20,000-iteration loop". **That
conclusion was wrong, and the probes in PHLOX-4b show exactly how.** With `AddToRunQueue` in place the
restored script's IP cycles through **eleven addresses** - the loop body - and its locals climb from
`Int32(4), Int32(6)` at capture to **`Int32(8731), Int32(38110815)`** after 2000 pumps. One `PumpOnce`
advances the loop about **4.4 iterations**, so 3000 rounds reached roughly **13,000 of the 20,000**
asked for. It was resuming the whole time; the test simply did not pump long enough for the loop it
chose, and *didn't finish* was read as *doesn't work* - the same shape of error as PHLOX-2f's
"flakiness", pointing the other way. `SerializedLSLPrimitive` was suspected and is **not** implicated:
the locals round-trip with type and value intact.

The arm is `AddToRunQueue`, the warning is gone, and test 1b runs un-skipped against a 2,000-iteration
loop.
### Syscall - done in PHLOX-4b

`SerializedRuntimeState` gains **`[ProtoMember(22)] LastSyscallIndex`**, initialised to **-1** because 0
is a real table index; `FromRuntimeState` copies it and `ToRuntimeState` restores it. A row written
before tag 22 loads with -1 - proved by deserialising a blob that carries only the three required
members - and falls back to Waiting with a warning. With an index, `ResumeFromSyscall` looks the
function up by `TableIndex` in `Defaults.AllMethods`, pushes its `ReturnType`'s default (Void pushes
nothing), puts the script on the run queue and logs the function name at Information. Test 1d captures
in Syscall (index 23, `llSay`), restores, reaches Waiting and answers a touch.
### One clock - three defects, one root

The engine had **two tick sources on different bases**, and both were broken:

- `Clock.GetLongTickCount` P/Invoked `GetTickCount64` on Windows and returned
  `(UInt64)Environment.TickCount` elsewhere. That counter is **signed 32-bit and goes negative at 24.9
  days**; the cast yields ~**1.8e19**, putting every wake-up eighteen quintillion ms away.
- Both schedulers compared against `(ulong)Util.EnvironmentTickCount()` - uptime **masked to 30 bits**
  (`Util.cs:3618-3623`). At every `0x40000000` boundary, a shade over **12.4 days**, `now` drops to
  nearly zero, below every queued `ReadyOn`, and **every timer and sleep on the region stalls** until it
  climbs back.
- And they **disagreed**: the serializer restored `NextWakeup` on the Clock basis, the scheduler
  compared it on the masked basis. PHLOX-4's Sleeping fix depends on those two agreeing, which is why
  this is one row and not two.

`Clock` is now a single static source over `Environment.TickCount64` with a settable `Func<ulong>` for
tests; the P/Invoke and the `IsWindows` branch are gone. **All nine call sites moved** - scheduler 6,
master 1, `LSLSystemAPI` 1, plus a master-loop comment that asserted the opposite of the truth. `grep`
for `EnvironmentTickCount` under `Source/Phlox.ScriptEngine` and `Source/InWorldz.Phlox` now returns
exactly one line: inside `Clock`'s own doc comment, describing what it used to do.

**Four tests step the clock across `0x3FFFFF00` -> `0x40000C00`** - monotonic across the boundary; a
timer armed below it is due after and not before; the master loop's `waitMs` reads 3000 then
non-positive rather than ~12 days; and the real clock is nowhere near the old cast's 1.8e19. Without a
settable source these would need a machine up for twelve days to fail, **which is why this survived**.

### Commits

| commit | what |
|---|---|
| `e5dabd0049` | parts 1+2 - Sleeping and queued-event arms, three round-trip tests, Running and Syscall logged not resumed |
| `a5b95e8e4b` | part 3 - one clock, nine call sites, four boundary tests |

Phlox suite **66 -> 73**: 72 passed, **1 skipped** (1b), 0 failed. Solution 0 errors.

### Noted while here

The restore tests share **one SQLite file** - `StateManager.DB_FILE` is a fixed relative path - so they
are a single non-parallel xUnit collection. That is more global state reachable from a harness test, and
it belongs with **candidate (iv)**, the intermittent harness failures.


---

## PHLOX-4b / 4c - the Running measurement corrected, Syscall done, and a regression I caused

**2026-09-09. Not deployed.** Two sessions; the first ended uncommitted on purpose.

### What 4b established

Three probes on the restored Running script, run before touching the engine - tick count via IP
movement, an IP trace every 100 pumps, and a frame dump at capture and after restore. Verbatim:

```
(c) AT CAPTURE : IP=17 Calls=1 Operands=0 RunState=Running Locals=[0:Int32(4), 1:Int32(6)]
(b) IP every 100 pumps: 33,49,22,38,54,27,43,55,28,44,17,33,49,22,38,54,27,43,55,28
(c) AFTER 2000 PUMPS: IP=28 Calls=1 Operands=1 RunState=Running Locals=[0:Int32(8731), 1:Int32(38110815)]
```

Executing, advancing, locals intact. See the corrected Running paragraph in the PHLOX-4 row above.

### What 4b broke, and why it stayed uncommitted

The full suite finished with `ASleepingScriptWakesUpAndFinishesItsHandler` failing **deterministically**,
alone or in company - the PHLOX-4 symptom back. It was **not** committed. 4c found the cause by reading
the dispatch: 4b had replaced the Running block by **slicing from `case Running` to `case Syscall`**, and
the Sleeping arm sat between them. **It was deleted.** A sleeping script fell to `default` and was
restored Waiting. Not a serializer fault, not `LastSyscallIndex` - an editing error.

Also found and fixed on the way: PHLOX-4 part 3's settable `Clock` source is **process-global**, and
`ClockBasisTests` pins it at `0x3FFFFF00` while running. Any engine test beside it arms timers against a
frozen clock; `TimerFloorTests` failed exactly that way in the full run and passed alone. Every class
that builds a `SchedulerHarness` is now in the single non-parallel `phlox-state` collection, **with the
reason named** - this is not the blanket parallelism switch candidate (iv) warns against, and the
compiler tests stay parallel.

### 4c

- The Sleeping arm is back. The dispatch is on `RunState` **only**; `LastSyscallIndex` is read inside the
  Syscall arm and nowhere else.
- `LastSyscallIndex` now means **"the syscall I am parked in"**: reset to -1 in `RunAsync`'s `finally`
  beside `CompleteSyscall`, and in `SyscallShim.Call` - the one choke point every shim returns through -
  whenever the shim returns without leaving the script in Syscall. So `llSleep`, which sets Sleeping,
  clears it too; 1a's probe reads `-1` at capture and asserts `-1` after the handler finishes. The
  `phlox status` line that reads it keeps working for parked scripts, which is all it ever meant.

### Commits

| commit | what |
|---|---|
| `9a216e2df6` | every engine test in one non-parallel collection; the state probes on the harness |
| `65736aa9f4` | Running and Syscall arms resume; the Sleeping arm put back; tag 22; index reset; 1b un-skipped, 1d and two serializer tests |

Phlox suite **73 -> 76**, **0 skipped**, green **three runs in a row**; solution 0 errors.

### Standing correction to candidate (iv)

Two of its observed intermittent failures now have named causes: the global clock seam (fixed by the
collection) and the shared `script_state.db` (same). Whether anything remains under (iv) is unknown
until the suite has run parallel-by-default for a while with those two removed; the entry stays open.
