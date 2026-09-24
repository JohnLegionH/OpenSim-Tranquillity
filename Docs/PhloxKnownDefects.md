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


---

## PHLOX-5 - SL names and arities for six built-ins

**2026-09-09. Landed; not deployed.** SL is the authority for names and signatures; every older Phlox
spelling and arity stays as an alias or overload, so current Legion content compiles unchanged.

### The six, each against its wiki page

| Phlox had | SL says | kind | index | body |
|---|---|---|---|---|
| `llSRGB2Linear(vector)` 570 | `vector llsRGB2Linear(vector srgb)` - lowercase s | rename, old kept | 677 | same conversion; the wiki itself notes the name is a misnomer (LSL colour is Rec.709) |
| `llSortListStrided(list,int,int,int)` 564 | `list llListSortStrided(list src, integer stride, integer stride_index, integer ascending)` | rename, old kept | 678 | same body; wiki bounds rule (`[-stride, stride)` or empty list) already held |
| `llSHA256String(string, integer)` 567 | `string llSHA256String(string src)` | overload | 679 | **SHA-256 of the UTF-8 bytes, 64 lowercase hex, nothing appended** - not a forward to the nonce form, which hashes `src + ":" + nonce` |
| `llTargetedEmail(int, string, string, string)` 653 | `llTargetedEmail(integer target, string subject, string message)` | overload | 680 | address derived from the target, routing per upstream `LSL_Api.cs:4362-4375`: OBJECT_OWNER mails the owner's account (skipped if group-owned); ROOT_CREATOR only when this item's creator is the root creator; 4096-char cap; 20 s sleep |
| `llUpdateKeyValue(string, string, string)` 612 -> int | `key llUpdateKeyValue(string k, string v, integer checked, string original_value)` | overload | 681 | **asynchronous**: returns a request key and answers on **dataserver** with `1,value` or `0,<XP_ERROR_*>`; checked -> compare-and-set through the Experience KV adapter, `XP_ERROR_RETRY_UPDATE` on mismatch; unchecked -> unconditional write; quota -> `XP_ERROR_QUOTA_EXCEEDED` |
| `llDerezObject(key)` 544 -> void | `integer llDerezObject(key id, integer flag)` | overload | 682 | both wiki rules enforced (rezzer must be this object, owner must match); `DEREZ_DIE` deletes, `DEREZ_MAKE_TEMP` sets `TemporaryOnRez`; **`DEREZ_TO_INVENTORY` returns 0 and logs** - `Scene.DeRezObjects` needs an `IClientAPI` and there is no viewer session to receive the item |

**Constants added** (`DefaultConstants.cs`): `DEREZ_DIE = 0`, `DEREZ_MAKE_TEMP = 1`, `DEREZ_TO_INVENTORY = 2`
from the wiki; `TARGETED_EMAIL_ROOT_CREATOR = 1`, `TARGETED_EMAIL_OBJECT_OWNER = 2` from upstream
`ScriptBase/LSL_Constants.cs:1033-1034`. **Plumbing added:** `PhloxEngine.PostScriptEvent`, the one public
door from the API to the scheduler's event queue - no API function had ever posted a `dataserver`
event before, which is why the SL `llUpdateKeyValue` contract needed it.

### What pins it

- **Six dispatch tests**, one per SL form, that compile the SL spelling, RUN it against the recording
  `ISystemAPI`, and assert that every call of that name reaching the API has the **SL arity** - so
  `llSHA256String("abc")` lands on the one-argument body and not the nonce one, and
  `llTargetedEmail(TARGETED_EMAIL_OBJECT_OWNER, ...)` arrives as `(2, s, m)`, the constant resolved.
- **Six alias compiles**, one per older spelling and arity, so none of them can quietly vanish.
- **The dispatch baseline was regenerated, not hand-edited**: `DispatchIndexGuardTests.RegenerateBaseline`
  rewrites `dispatch-baseline.txt` from the live table when `PHLOX_REGEN_BASELINE=1` is set and is a
  no-op otherwise. The diff is exactly the two new names (`llsRGB2Linear 677`, `llListSortStrided 678`);
  the four overloads share existing names and add no entries. Expected count 674 -> 676. No existing
  index moved.

### Verified

Phlox suite **76 -> 89**, 0 skipped, green; solution 0 errors. The 17-script live set compiles unchanged
(`LiveScriptCompileTests` in the same run), and **none of the six older spellings appears in any of the
17** - `grep` over `LiveScripts/scripts/*.lsl` for all six names returns nothing - so no live script's
resolution changes.

### Found in the harness on the way, fixed

The recording `ISystemAPI` returned `null` for `string` and `list` results. A null pushed onto the
operand stack throws in `SafeOperandsPush`, the aborted syscall is re-dispatched, and every non-void
call was recorded twice - which is why `llGetKey()` had never been usable in a recorded script. It now
returns an empty string / empty list. Separately, driving the interpreter by hand without the scheduler
replays `state_entry` once more after it finishes, so the dispatch tests assert the SL arity on **every**
recorded call of the name rather than on a count; "exactly one" would have been a claim about the
harness, not about dispatch.

### Decisions

- Renames get a **new index and shim** rather than sharing the old one: the guard forbids two
  signatures on one index, and the raw-key rule allows only `name` or `name__N` as a key.
- `DEREZ_TO_INVENTORY` is refused honestly (0, logged) rather than faked; doing it needs a client-less
  derez path, which is its own change.
- The four-argument `llUpdateKeyValue` returns a fresh request key and posts the reply immediately
  rather than deferring it; the contract a script sees is the SL one.


---

## PHLOX-6 - the five SL events Phlox did not recognise

**2026-09-09. Landed; not deployed.** A script declaring any of these handlers failed to compile - the
compiler rejected the handler name outright. Signatures are the SL wiki's, checked page by page.

| event | wiki signature | compiles | mask bit | delivers | trigger |
|---|---|---|---|---|---|
| `path_update` | `(integer type, list reserved)` | yes | `scriptEvents.path_update` (1<<40, already existed upstream) | **yes** | `BotManager.FirePathEvent` (`BotManager.cs:298-322`) now posts it beside `bot_update` through `IScriptModule.PostScriptEvent`; `BOT_MOVE_COMPLETE` (1) -> `PU_GOAL_REACHED` (1), `BOT_MOVE_FAILED` (3, a navigation timeout) -> `PU_FAILURE_UNREACHABLE` (4) |
| `on_damage` | `(integer num_detected)` | yes | new `1<<44` | **compile-only** | none exists - damage is applied inline with no event before or after |
| `final_damage` | `(integer num_detected)` | yes | new `1<<45` | **compile-only** | same |
| `on_death` | `()` | yes | new `1<<46` | **yes** | `EventManager.OnAvatarKilled` -> `PhloxEngine.OnAvatarKilled` posts it to every script on every attachment of the dead avatar, which is exactly the wiki's scope ("all attachments worn by an avatar when that avatar's health reaches 0") |
| `game_control` | `(key id, integer button_levels, list axes)` - **three** parameters | yes | new `1<<47` | **compile-only, permanently** | "triggered when compatible viewer sends fresh `GameControlInput` message" - a viewer->sim message Tranquillity does not carry. Nothing fakes a trigger |

**One disagreement with the brief that scoped this:** it gave `game_control` four parameters
(`integer button_edges` between `button_levels` and `axes`). The wiki page has three. The wiki wins,
and the table comment says so.

### The damage trio - evidence for compile-only

Phlox's `llAdjustDamage` and `llSetHealth` (`LSLSystemAPI.cs:11185-11225`) subtract from
`ScenePresence.Health` via `setHealthWithUpdate` and, at zero, call `TriggerAvatarKill`. Its `llDamage`
(`:12687`) only sends the client a health figure. The region's own damage path is
`ScenePresence.PhysicsCollisionUpdate` (`ScenePresence.cs:5245`), which decrements `Health` inline for
velocity and `ParentGroup.Damage` collisions and calls `TriggerAvatarKill` at `:5344`. **There is no
event raised when damage is applied - only when it kills.** So `on_death` has a real hook and the
other two do not. **The future hook for `on_damage` / `final_damage` lives in
`ScenePresence.PhysicsCollisionUpdate`**, batched per frame: post `on_damage(count)` before the
subtraction, `final_damage(count)` after, to the presence's attachments - and the same two lines in
`llAdjustDamage`. Not done here; that is a combat-semantics change, not an event-recognition one.

### path_update - a decision

`BotData` carries no record of whether a bot was created by `llCreateCharacter` or by the InWorldz bot
API, so `path_update` is posted **beside** `bot_update` for every bot path outcome rather than only
for pathfinding characters. A script declares one handler or the other, and an engine drops an event
the script has no handler for, so nothing sees both. Thirteen `PU_*` constants added from the wiki
(`PU_FAILURE_OTHER` = 0xF4240 = 1000000); neither Phlox nor upstream had any.

### What pins it

Ten tests in `SlEventRecognitionTests`: the five handlers compile together and each alone; a prim
holding `on_damage` / `final_damage` / `on_death` shows all three bits in `ScriptEvents` (what `phlox
status` prints); `on_death` reaches a script on an attachment when the wearer is killed through the
real `TriggerAvatarKill`; and `path_update` reaches its handler with the PU code through the same
`IScriptModule.PostScriptEvent` door `BotManager` uses, for both mapped outcomes.

**Red first, honestly stated:** the test project would not even build against the unchanged engine -
`'scriptEvents' does not contain a definition for 'on_damage' / 'final_damage' / 'on_death'` - which
proves the mask bits did not exist; the handler-name rejection was the premise and was not separately
executed, since the same file carries both halves.

Phlox suite **89 -> 99**, 0 skipped, green; solution 0 errors. Four new `scriptEvents` bits (44-47) in
`OpenSim.Region.Framework` - unused by YEngine, harmless to it.

**Did-it-land for the deploy:** a prim with all five handlers compiles with no `[PhloxCompile]` error,
and `phlox status` lists them in the mask.


---

## PHLOX-7a - four stubs that upstream LSL_Api already implements

**2026-09-09. Landed; not deployed.** Each was a no-op or a default return in `LSLSystemAPI.cs`; each is
ported from the upstream body, the SL wiki page checked first, Phlox's own conventions kept.

| function | was | upstream ported from | wiki agrees | now |
|---|---|---|---|---|
| `llCollisionFilter(name, id, accept)` | empty (`/* NotImplemented in Halcyon */`) | `LSL_Api.cs:4036-4043` | yes - accept TRUE keeps only matches, FALSE excludes; blank name / null id match everything | stored via `SceneObjectPart.SetCollisionFilter`, **and** `PhloxEngine`'s three collision handlers consult `CollisionFilteredOut` through a new `FilteredColliders` helper, so the count a script sees is the count it was allowed to see |
| `llStartObjectAnimation` / `llStopObjectAnimation` | no-ops with a *"not supported in OpenSim"* comment that was wrong | `:4533-4546` | yes - resolved by inventory name, then the default animation names, never by UUID | `SceneObjectPart.AddAnimation` / `RemoveAnimation`, which the Framework already had |
| `llGetObjectAnimationNames()` | `[]` | `:4548-4556` | yes | reads `SceneObjectPart.AnimationsNames` |
| `llGetStartString()` | `""` | `:4589-4593` | yes - *"a string that was passed to the object's root prim on rez with llRezObjectWithParams"* | reads `ParentGroup.RezStringParameter`, which the Framework had (`SceneObjectGroup.cs:488`) and Phlox never set |
| `llXorBase64Strings(s1, s2)` | `""` (*"deprecated"*) | `:14509-14580`, as-is | yes - deprecated for `llXorBase64`, 0.3 s sleep, and *"incorrectly performs an exclusive or"* - the padding quirk **is** the documented behaviour | the upstream body, quirk and comment included |

### The start-string path, traced - and what it turned up

Upstream parses `REZ_PARAM_STRING` in `llRezObjectWithParams` (`LSL_Api.cs:3796-3803`), stores it on the
rezzed group (`:3894`, `sog.RezStringParameter`) and reads it back in `llGetStartString` (`:4589`).
**Phlox never stored it - it never parsed it.** Its `llRezObjectWithParams` handled `REZ_PARAM` (the
integer) and nothing else string-shaped, and delegated to `llRezObject` / `llRezAtRoot`, which drop the
rezzed groups on the floor. Now: `RezObjectInternal` takes a `startString` and sets `RezStringParameter`
on every group `World.RezObject` returns (`LSLSystemAPI.cs:3441-3450`), and `llRezObjectWithParams`
parses rule 13 and threads it through. Pinned by a test that rezzes a real inventory object in the
test scene and reads the string off the rezzed group.

**Two things found on the way, both fixed and both bigger than the stub:**

- **No `REZ_*` constant existed in Phlox at all.** A script could only call `llRezObjectWithParams`
  with bare numbers. All 21 (`REZ_PARAM` .. `REZ_PARAM_STRING`, and the eight `REZ_FLAG_*`) are now in
  `DefaultConstants`, values from `LSL_Constants.cs:1131-1154`.
- **Phlox's private rule numbering matched neither SL nor upstream.** The method's own constants were
  `REZ_POS=1, REZ_ROT=2, REZ_VEL=3, REZ_DAMAGE=4, REZ_PARAM=7, REZ_FLAGS=8`; SL's are
  `REZ_PARAM=0, REZ_FLAGS=1, REZ_POS=2, REZ_ROT=3, REZ_VEL=4, REZ_DAMAGE=8`. A script written to the SL
  constants had every rule misread. Aligned to upstream. **This is a behaviour change** for any script
  that used Phlox's private numbers as literals; none of the 17 live scripts calls
  `llRezObjectWithParams` at all (grep), so nothing on the grid moves.

### The collision filter - why the engine consults it too

The Framework already applies `CollisionFilteredOut` before raising the event on the physics path
(`SceneObjectPart.cs:2812-2820`, `ScenePresence.cs:6462-6470`), so the store alone *does* filter physics
collisions. It does nothing for a collision that arrives by any other door - a direct
`TriggerScriptCollidingStart`, which is exactly what the harness test uses. `PhloxEngine` now filters in
its own handlers as well, so the guarantee does not depend on the entry point.

### What pins it

Six tests in `UpstreamPortedStubTests`, **all six red on the unchanged engine** (run before the port,
6/6 failed) and green after: reject-by-name delivers 0 events for the rejected name and 1 for another;
accept-by-name delivers exactly 1 of 2; object animations are listed by name after start and gone after
stop; the start string reads back from the group; `llRezObjectWithParams` stores it on the rezzed
object; and `llXorBase64Strings` returns the upstream algorithm's identities (`""` for empty s1, s1 for
empty s2, `AAAA` for equal inputs) rather than `""`.

A test-shape note: `DetectParams.Populate` cannot name a collider that is not in the scene, so
`llDetectedName(0)` is blank in these tests and the assertions are on the **count** of events - which
is the stronger claim anyway: a filtered collision must produce no event, not an event with no name.

Phlox suite **99 -> 105**, 0 skipped, green; solution 0 errors. `grep` for a `Stub(` tag or a
*not supported* / *NotImplemented* comment on any of the four: **0 hits**.


---

## PHLOX-7b - the remaining stubs

**2026-09-09. Landed; not deployed.** Same method as 7a: wiki page first, upstream body where one exists,
Phlox conventions kept, every test red on the unchanged engine (5/5 failed) and green after.

| function | was | wiki | upstream | now |
|---|---|---|---|---|
| `llSetLinkSitFlags` / `llGetLinkSitFlags` | no-op / `0` | five `SIT_FLAG_*` bits, values = `LSL_Constants.cs:1156-1160` | `llGet` (`:21155-21166`) hard-codes `ALLOW_UNSIT \| NO_COLLIDE \| NO_DAMAGE` as "forced"; `llSet` (`:21168`) is a no-op | **real state.** `SIT_TARGET` read-only from `IsSitTargetSet`; **`ALLOW_UNSIT` and `SCRIPTED_ONLY` honoured today** - they land on `AllowUnsit` / `ScriptedSitOnly`, which `ScenePresence` already checks (`:2656`, `:3399`, `:3411`); **`NO_COLLIDE` and `NO_DAMAGE` stored and read back only** (new `SceneObjectPart.SitFlagsStored`) - the presence has no seated collision-volume toggle and no damage distribution to seated avatars |
| `llMinEventDelay` | no-op, comment claiming the scheduler made it unnecessary | "the minimum time between events being handled"; events inside the window are **queued** | forwards to the engine (`:4433-4444`); YEngine's implementation **drops** some event types inside the window (`XMRInstRun.cs:86-105`) | a per-script floor between handler **starts**, in the execution scheduler on the `Clock` basis, events held on the script's own queue and released by a `WakeEvent.MinDelay` sleep-heap entry. **The wiki is the parity rule where YEngine drops** |
| `llScriptProfiler` / `llGetSPMaxMemory` | no-op / constant `16384` | `PROFILE_SCRIPT_MEMORY` starts recording; after `PROFILE_NONE`, "the most memory used at any one time" | both effectively no-ops (`:17570`, `:17558` returns 65536) | a `PeakMemoryUsed` high-water mark over the `MemInfo.MemoryUsed` the VM already maintains, sampled at **event boundaries, both profiler calls and each read** - coarser than SL's continuous tracking, and stated as such |
| `llRequestSimulatorData` | local region only; remote returned `NULL_KEY` and raised nothing; rating was the raw maturity number | `DATA_SIM_POS` global position vector, `_STATUS` "up"/..., `_RATING` "PG"/"MATURE"/"ADULT"/"UNKNOWN", 1.0 s sleep; unknown region "unknown region" / "rating or region unknown" | `:13389-13485`: local from `RegionInfo`, remote via `GridService.GetRegionByName` with the hypergrid `RegionSecret` handling for POS | ported, remote via `World.GridService`, reply through the dataserver door PHLOX-5 opened |

**Two departures from upstream, both towards the wiki**, in `llRequestSimulatorData`: POS is in **metres**
(`WorldLocX`) - upstream returns `RegionLocX`, which in this tree is region units against the wiki's
"global position"; and an unknown region answers with the wiki's two texts rather than upstream's
single "unknown".

**State:** `RuntimeState` gains `MinEventDelayMs` (tag 23), `ProfilingMemory` (24), `PeakMemoryUsed` (25);
old rows load as 0 / false / 0. `NextEventAllowedOn` is deliberately **not** persisted - it is relative to
the old process's clock, and a restore starts allowed. Constants added: the five `SIT_FLAG_*`, `PROFILE_NONE`,
`PROFILE_SCRIPT_MEMORY` (`DATA_SIM_*` already existed).

### What pins it

Five tests in `RemainingStubTests`: sit flags read back `34` then `35` once a sit target exists and
`AllowUnsit` is true on the part; two touches posted 10 ms apart under `llMinEventDelay(1.0)` are handled
**1.0006 s apart and both are handled** (`3.018`, `4.018`) - queued, not dropped; the profiler reports
`before=0`, then a 400-string list inside the window lifts the peak from **402 to 27,626** bytes; the
local region answers `up` / `PG` / `<256000, 256000, 0>`; an unknown region raises the dataserver event
with both wiki texts and returns a real key.

One fix inside the session: the first profiler body cleared the flag before sampling, so a
`PROFILE_NONE` lost everything allocated since the last event boundary (`peak=402`, `grew=0`). Sample
first, then change the flag.

Phlox suite **105 -> 110**, 0 skipped, green; solution 0 errors. `grep` for a `Stub(` tag or a no-op /
not-supported comment on any of the five: **0 hits**.

### No longer a stub

`llDetectedDamage` was left a stub here until the damage hook existed; **PHLOX-10** built the hook and the
function with it (a list, per the wiki).

## PHLOX-8 - constants audit: Phlox vs SL, by name and by value

**2026-09-09. Landed; not deployed.** `Docs/audit/phlox-constants-audit.py` parses both tables
(`InWorldz.Phlox/Compiler/DefaultConstants.cs`, 832 names before / 1140 after; upstream
`ScriptBase/LSL_Constants.cs`, 964 names, every expression resolved), normalises each side to what a
**script** sees - upstream's `A | B`, `1 << n`, `unchecked((int)0x...)` and `ZERO_VECTOR` aliases evaluated in
declaration order; Phlox's STRING/KEY text put through the same unescape the assembler applies
(`BytecodeGenerator.UnescapeStringChars`) - and classifies. It re-runs to **A 0 / B 0**.

| class | before | after | what |
|---|---|---|---|
| **A** upstream has it, Phlox did not | **308** | **0** | added in one batch at the end of the table, upstream's values in upstream's order: 296 integers, 12 strings (`NAK`, the eleven `IMG_USE_BAKED_*`). Families: `PRIM_PROJECTOR`, `PRIM_REFLECTION_PROBE`, `PRIM_GLTF_*`, `PRIM_RENDER_MATERIAL`, `PRIM_PHYSICS_MATERIAL`, `PRIM_SIT_FLAGS`, `PRIM_DAMAGE`/`PRIM_HEALTH`, `OBJECT_*` 29-54, `PARCEL_DETAILS_*`, `CHANGED_RENDER_MATERIAL`, `INVENTORY_SETTING`/`_MATERIAL`, `HTTP_USER_AGENT`/`_ACCEPT`/`_EXTENDED_ERROR`, `VEHICLE_FLAG_NO_X/Y/Z`, `STATUS_DIE_AT_NO_ENTRY`, `AGENT_BY_USERNAME`, `CLICK_ACTION_DISABLED`/`_IGNORE`, `LSL_STATUS_*`, `STATS_*`, `RC_REJECT_HOST*`, and **26 `OS_*`/`OSTPOBJ_*` names** (OSSL's - not SL's; added because the brief said all of A, and they are harmless: a constant with no consumer) |
| **B** same name, different value | **4** | **0** | the table below; **one real defect**, three that only looked like one |
| **C** Phlox-only | 176 | 176 | **135 heritage** (`IW_`, `BOT_`, `PHLOX_`, `IWTIMER`, `TRAVELMODE`, `IWERR_` by prefix, plus 10 names the Halcyon reference `LSL_Constants.cs` declares: `DATA_ACCOUNT_TYPE`, `ESTATE_ACCESS_QUERY_*`, `VEHICLE_TYPE_SAILBOAT`/`_MOTORCYCLE`, `WIND_SPEED_*`); **27 that SL has and upstream lacks** (`PU_*` 13, `STATUS_*` 8, `OBJECT_RETURN_*` 3, `DEREZ_*` 3 - each family checked on its wiki page); **14 flagged**: `VEHICLE_MOUSELOOK_AZIMUTH`/`_ALTITUDE`, `VEHICLE_BANKING_AZIMUTH`, `VEHICLE_DISABLE_MOTORS_HEIGHT`/`_DELAY`, `VEHICLE_INVERTED_BANKING_MODIFIER`, `VEHICLE_LINEAR`/`ANGULAR_WIND_EFFICIENCY`, `VEHICLE_FLAG_REACT_TO_CURRENTS`/`_WIND`, `VEHICLE_FLAG_LIMIT_MOTOR_DOWN`, `VEHICLE_FLAG_MOUSEPOINT_STEER`/`_BANK`, `OBJECT_TOTAL_UPDATES` - in neither SL, upstream nor the Halcyon reference, used nowhere in this tree outside the table, all from the Phlox import (`02cf1370df`). Left in: an SL script cannot name them, so they cost nothing; an SL script that does would not compile in SL either |

### The four B hits, each against its wiki page

| name | Phlox had | upstream | SL wiki | verdict | now |
|---|---|---|---|---|---|
| `TOUCH_INVALID_FACE` | `0x7FFFFFFF` (2147483647) | `-1` (`:808`) | [TOUCH_INVALID_FACE](https://wiki.secondlife.com/wiki/TOUCH_INVALID_FACE): `0xFFFFFFFF`, which is **-1** as a 32-bit integer; Halcyon `:653` also `-1` | **Phlox wrong.** `if (llDetectedTouchFace(0) == -1)` - the idiom the wiki documents - never matched here | `-1` |
| `EOF` | table text backslash-n x3 | three 0x0a (`:624`) | [EOF](https://wiki.secondlife.com/wiki/EOF): "three newline characters (0x0a)" | **Phlox right at runtime** - the audit's first pass compared table text; the STRING ConstValue is an `sconst` operand and the assembler unescapes it. Audit corrected; the value test proves three real newlines reach `llSay` | unchanged |
| `TOUCH_INVALID_VECTOR` | `<0,0,0>` | `= ZERO_VECTOR` (`:810`) | [TOUCH_INVALID_VECTOR](https://wiki.secondlife.com/wiki/TOUCH_INVALID_VECTOR): `<0.0, 0.0, 0.0>` | **both right** - the audit's vector regex missed the alias. Audit corrected | unchanged |
| `JSON_APPEND` | integer `-1` | **string** `"-1"` (`:945`) | [JSON_APPEND](https://wiki.secondlife.com/wiki/JSON_APPEND): `integer JSON_APPEND = -1`; Halcyon `:736` integer | **Phlox right, upstream's type is its own** (its JSON functions take `list` and compare the string form). Listed in the audit's `SL_SIDES_WITH_PHLOX` with the page, reported outside B | unchanged |

**Live scripts:** `grep` of `Tests/InWorldz.Phlox.Tests/LiveScripts/scripts/*.lsl` for the four B names - **0 hits**;
no live script depended on the old `TOUCH_INVALID_FACE`. It is still a behaviour change for any script
in the grid that compared against `2147483647` literally or against the constant and expected the old
value; the wiki idiom now works.

### What pins it

`ConstantsAuditTests`: **`Fixtures/phlox8-upstream-names.lsl`** references every one of the 308 added
names once and must compile with no errors (red before the batch: 308 unresolved identifiers); five
runtime value probes on the hand-driven interpreter with the recording API - `(string)TOUCH_INVALID_FACE`
is `-1` (red before: `2147483647`), `(string)JSON_APPEND` is `-1`, `TOUCH_INVALID_VECTOR == ZERO_VECTOR`,
`EOF` reaches `llSay` as three 0x0a, `NAK` as `0x0a 0x15 0x0a` (the one added string with a control
character; red before: unresolved). Red 3/6, green 6/6; suite **110 -> 116**, 0 skipped.

**`DispatchIndexGuard` untouched, confirmed:** constants live in `DefaultConstants` and reach bytecode as
`iconst`/`sconst` operands (`GenVisitor.VisitIdExpr` -> `ByteCodeEmitter.SysConstLoad`); `Defaults.cs`,
`SyscallShim.cs` and the guard test reference neither `DefaultConstants` nor `ConstantSymbol` (0 hits), and
`dispatch-baseline.txt` is unchanged. `CACHE_SCHEMA_VERSION` likewise: a cached script that used
`TOUCH_INVALID_FACE` carries the old `iconst` until it is recompiled - only such scripts, and only the
fix, not the additions (a script naming an added constant never compiled, so has no cache entry).

## PHLOX-9 - four small SL-parity leftovers

**2026-09-09. Landed; not deployed.** Each against its wiki page, each red on the unchanged engine first
(`SlParityLeftoverTests`: 7 of 10 red, then 10 of 10 green; suite **116 -> 126**, 0 skipped).

| # | was | SL (wiki) | now |
|---|---|---|---|
| 1 | `LSLSystemAPI.ShoutError` sent `"Script error: ..."` as a **Shout on channel 0** - local chat, every avatar in 100 m read it | [DEBUG_CHANNEL](https://wiki.secondlife.com/wiki/DEBUG_CHANNEL) `0x7FFFFFFF`: "chat channel reserved for script debugging and error messages"; the viewer shows it in the script-error window, and "most viewers filter out messages received on DEBUG_CHANNEL from objects owned by others" | channel **DEBUG_CHANNEL**, text unchanged. `ChatModule.cs:211` turns that channel into `ChatTypeEnum.DebugChannel` before delivery. **Wider than the brief's "run-time errors"**: `ShoutError` is the API's one error door - `TerminateWithError` (`PhloxExecutionScheduler.cs:1286`), the VM's own raise (`Interpreter.cs:122`), `SyscallShim.cs:801`, and ~100 sites in `LSLSystemAPI` ("No permissions to track the camera", "No item named ...", "PERMISSION_DEBIT not granted", ...). All of those are DEBUG_CHANNEL messages in SL as well, so every caller moves together; there is no second door left on channel 0 |
| 2 | `quaternion` was not a type: `quaternion q;` failed with *Unknown type 'quaternion'* | [Quaternion](https://wiki.secondlife.com/wiki/Quaternion): "a keyword supported by the LSL compiler that means the same thing as, and is interchangeable with, rotation" | `'quaternion'` is a `TYPE` token in `LSL.g4`; `SymbolTable.CanonicalTypeName` maps it to `rotation` at the three places TYPE text becomes a type (`DefVisitor.ResolveType`, `TypesVisitor.ResolveType`, `AnalyzeVisitor.VisitFuncDef`), so it resolves to the one `ROTATION` instance the type tables compare by reference. Declarations, parameters, return types and `(quaternion)` casts all work |
| 3 | `<<=` and `>>=` accepted (`assignmentStmt` `:75`, `assignmentExpression` `:131`) | [LSL_Operators](https://wiki.secondlife.com/wiki/LSL_Operators): no shift-assign exists; YEngine's acceptance is its own extension | both removed from both rules; `x <<= 1;` is a syntax error **on its line** (`line 6:`), `x = x << 1;` unchanged. **LiveScripts: 0 uses of either** |
| 4 | `Op_Lneq` (`Interpreter.Actions.cs:2564`) pushed `0`/`1` | [LSL_Operators](https://wiki.secondlife.com/wiki/LSL_Operators): "Equality test on lists does not compare contents, only the length"; `a != b` is `llGetListLength(a) - llGetListLength(b)` | `Op_Lneq` pushes the length difference: `[1,2,3] != [1]` is **2**, `[1] != [1,2,3]` is **-2**, `[1] != [2]` is **0**. `Op_Leq` stays `0`/`1` (`[1,2] == [3,4]` is 1) |

**The other two error paths, checked and left alone:** the PHLOX-2 compile-error surfacing goes
through `PhloxCompileErrorReport.ToOwner` - `SendAlertToUser(part.OwnerID, ...)` (`PhloxScriptLoader.cs:685`), owner-directed, and the scheduler's
`TerminateWithError` has no delivery of its own - it calls `ShoutError`, so it moved with item 1
(the test's line is exactly that path: `Script error: Script <asset> stopped: Attempted to divide by zero.`).

### The grammar was regenerated, not hand-edited

`Source/InWorldz.Phlox/grammar/buildgrammar4.sh` (new) is the command:
```
bash Source/InWorldz.Phlox/grammar/buildgrammar4.sh
```
ANTLR **4.13.1** (the version every generated header carries) via the `antlr4` launcher (antlr4-tools +
JDK 21; the jar was fetched from Maven Central on first use), raw output to `grammar/generated/` (not
compiled), the six `.cs` copied into `Compiler/` with the one hand post-edit the committed copies have
always carried and that nothing documented: the listener interface renamed `ILSLListener ->
ILSLParseTreeListener` (the compiler already has an `ILSLListener`, the status listener), and two
`using`s in `LSLParser.cs`. **Gate before the grammar change:** the script on the *unchanged* `LSL.g4`
reproduced the committed `Compiler/` files byte for byte, bar the `Generated from <path>` header line
(the old one names `D:/legion-grid-source/...`). Token numbering shifted (`T__41`, `T__42` gone; `TYPE`
44 -> 42) - a tree-wide regeneration, 16 generated files, and the reason the diff is large.

### What pins it

`SlParityLeftoverTests`: divide by zero on the scheduler harness yields **one** chat on `2147483647`
containing `Script error` and **none** on channel 0 (red: `0:Script error: ... stopped: Attempted to
divide by zero.`); `quaternion` globals, locals, a `llSetRot` argument, a `rotation r = q` and a
`(quaternion)` cast compile, and `q == ZERO_ROTATION` says `1` on the real path; `x <<= 1` / `x >>= 1`
each fail with an error at `line 6:`; `x = x << 3; x = x >> 1` says `4`; the five list comparisons above.
`SchedulerHarness` now records `(channel, message)` as `SaidOn`; `HandDrivenRun` is the shared bare
interpreter helper (PHLOX-8's copy stays as it was).

**Harness limitation found, not fixed:** a script with a *local variable store* throws
`NullReferenceException` in `Op_Store` on the bare hand-driven interpreter (`new Interpreter(compiled,
shim)` + `DoEvent`), while the same script runs on the scheduler path. `MemInfo.ReplaceStored` over a
never-initialised local is the suspect; not investigated (time). The two runtime probes that need
locals use the scheduler harness instead; the list and constant probes have no locals and stay bare.
A harness candidate, not an engine defect, recorded here.

### PHLOX-9b - DEBUG_CHANNEL object chat reaches the owner only

**2026-09-10. Landed; not deployed.** The did-it-land for 1.1.315 came back **half wrong**: Legion's
divide-by-zero prim raised the script-warning box on a second avatar's viewer standing nearby, with the
Owner field correctly showing Legion. The channel change landed (it was DEBUG_CHANNEL, not local chat);
the *outcome* depended on the viewer. The [DEBUG_CHANNEL](https://wiki.secondlife.com/wiki/DEBUG_CHANNEL)
page says the sim broadcasts and "most viewers filter out messages received on DEBUG_CHANNEL from objects
owned by others" - this viewer did not. The sim filters now.

| | |
|---|---|
| where | `ChatModule.DeliverChatToAvatars` (`ChatModule.cs`), the module that retypes channel 0x7FFFFFFF to `ChatTypeEnum.DebugChannel` at `:211` |
| rule | chat whose type is `DebugChannel` **and** whose source is an object is delivered only to the presence whose UUID equals the sending part's `ParentGroup.OwnerID` (the root part's owner). Everything else - channel 0 from the same part, avatar-typed `/2147483647`, every other channel - is untouched |
| range | **kept as it was, and as it was is "none"**: `TrySendChatMessage` applies a distance only for Whisper/Say/Shout; `DebugChannel` falls to the `default` arm (`maxDistSQ = -1`), so a DEBUG_CHANNEL line already reached every avatar in the region regardless of distance, and the owner now hears it from anywhere in the region. The original `Shout` type is overwritten at `:211` before the distance check, so there is no llSay-range rule to keep. A say/shout range for DebugChannel would be a separate change - flagged, not made |
| scripted listens | **unaffected, checked**: `PhloxEngine` subscribes to `EventManager.OnChatFromWorld` itself (`PhloxEngine.cs:159`, handler `:524` -> `ListenManager.DeliverChat`) as a sibling of `ChatModule` on the same event - the listen manager sees every chat *before and independently of* this module's delivery filter, which only decides which viewers get a `ChatFromSimulator` packet. `llListen(DEBUG_CHANNEL, ...)` in a script keeps working; `WorldCommModule` (upstream engines) hangs off `OnChatFromClient` the same way |
| not touched | `OnChatBroadcast` (`SimChatBroadcast`, region-wide chat) still sends DEBUG_CHANNEL to everyone; nothing in Phlox uses it for errors (`ShoutError` goes through `SimChat`). Recorded, not changed |

### What pins it

`DebugChannelOwnerOnlyTests` (`Tests/OpenSim.Region.CoreModules.Tests/Avatar/Chat/Tests/`), at the ChatModule
level with a `TestScene`, the real `ChatModule`, two presences 2 m apart and a prim owned by one of them:
a DEBUG_CHANNEL `SimChat` from the prim reaches **exactly the owner** (red on the unchanged module: *"the
other avatar heard: 6:Script error: ..."* - type 6 is DebugChannel); channel 0 from the same prim reaches
**both**; DEBUG_CHANNEL typed by the *other* avatar reaches **both**. Red 1/3, green 3/3.

The rest of `OpenSim.Region.CoreModules.Tests` ran alongside: 95 pass, **5 fail** - three
`InventoryArchiveLoadTests` and two `AvatarFactoryModuleTests` - none of which touch chat, and whose
failures are recorded as found, not proven pre-existing (no second checkout in budget): the IAR tests assert creator names `"Lord Lucan"` / `"Mr Tiddles"` and a coalesced-item count (`Assert.Single` found 2 parts); the avatar-factory pair fails `Assert.NotNull` at `AvatarFactoryModuleTests.cs:88`. Neither file references `ChatModule`, `SimChat` or `OnChatFromWorld`; the avatar-factory test last changed 2026-09-05 (`483c2d7a13`). Candidate for a separate look.

## PHLOX-10 - the damage-applied hook: on_damage, final_damage, llDetectedDamage, llDamage

**2026-09-10. Landed in two commits; not deployed.** Wiki pages read first: on_damage, final_damage,
llDetectedDamage, llAdjustDamage, llDamage, llSetDamage (DAMAGE_TYPE_* from the llDamage page - the
constant pages themselves 404).

### PART 1 - one door (`9bef63058a`)

Damage reached `ScenePresence.Health` from two doors with their own arithmetic and their own
`TriggerAvatarKill`: `PhysicsCollisionUpdate` (ground falls, prim collisions, `llSetDamage` prims, inline)
and `LSLSystemAPI.llAdjustDamage` / `llSetHealth` via `setHealthWithUpdate`. Now every door builds a
**`DamageEntry`** (`Scenes/DamageEntry.cs`: source object, owner, local id, `OriginalDamage`, mutable
`Amount`, `DamageType`) and calls **`ScenePresence.ApplyDamage(batch, announce)`**; the physics frame's
collisions arrive as **one batch** (the wiki's `num_detected`). The 0..100 clamp, the client-update rule
(scripted: always; collision: only past 1.0, the anti-spam rule) and the kill live in the door.

`DamageDoorTests` characterise it - **green before and after the refactor**: a `Damage = 10` prim
collision takes 10 and the prim dies; a -10 m/s ground fall takes 1.0 and -4 takes nothing; scripted 30
then a -50 heal gives 70 then 100; a `Damage = 100` prim fires `OnAvatarKilled` exactly once with the
prim's local id. Two normalisations, stated: Health is clamped to **0** at death (the collision path sent
a negative value to the client); `llSetHealth` on an invulnerable presence is a no-op like every other
door (it alone bypassed that check).

### PART 2 - the events, SL order (`this commit`)

| step | where | what |
|---|---|---|
| 1 | `EventManager.OnAvatarDamage(presence, batch)`, raised **synchronously** from `ApplyDamage` before anything is subtracted | `PhloxEngine.OnAvatarDamage` posts **`on_damage(num_detected)`** to every script on every part of every attachment the presence wears, one `DetectParams` per entry: `llDetectedKey` / `llDetectedOwner` name the source, `llDetectedDamage(n)` is **`[damage, damage_type, original_damage]`** (the wiki's order - the brief said `[amount, adjusted, type]`), `llAdjustDamage(n, new_damage)` writes the entry's `Amount` through an `AdjustDamage` callback carried on the detect record. **Then it waits** |
| 2 | `ApplyDamage` | subtracts the adjusted amounts, clamps |
| 3 | `EventManager.OnAvatarDamageApplied` | **`final_damage(num_detected)`** to the same scripts; `llDetectedDamage` inside it is what landed. Not waited on |
| 4 | `TriggerAvatarKill` if Health hit 0 | `on_death`, as PHLOX-6 built it |

**How the wait works - the one place SL is synchronous.** `PostedEvent` gained a `Completed` callback
(not serialized). The scheduler fires it exactly once when the event is *done with*: handler finished
(`TransitionToWait`), script terminated (`TerminateWithError`), or the event dropped - no handler in the
current state, script disabled, script not loaded (deferred), queue full, script suspended. The engine
posts every on_damage with `Completed` wired to a `CountdownEvent` and blocks the calling thread on it,
**bounded by `PhloxEngine.OnDamageWaitMs = 500`**; on timeout it logs and applies the batch as adjusted
so far. The caller is the physics thread (collisions) or an async-syscall thread - **`llDamage` and
`llSetHealth` are now `RunAsync` shims** precisely so a script can never wait on itself; if the caller *is*
the script thread anyway (`PhloxExecutionScheduler.WorkerThreadId`, recorded at each `DoWork`), the
events are posted and not waited on. This is not the PHLOX-5 dataserver shape (post and forget); it is a
bounded region-side wait on script completion, and nothing else in the engine does that.

**Functions.** `llDetectedDamage(integer)` returns a **list** now (was a `float` stub at index 655; same
index, new return type). `llAdjustDamage` is SL's **`(integer number, float new_damage)`** at index 604 -
the OpenSim-form `(key, float)` with the same arity is gone (the resolver keys overloads by arity; 0 live
scripts used it; `llDamage` is the SL way to deal damage). `llDamage(key, float, integer)` goes through
the door with the calling prim as source: avatars only, region damage must be on, **no 10-per-30-s
throttle and no seat redirect** (both wiki rules, not done). Sixteen **`DAMAGE_TYPE_*`** constants added
(IMPACT -1 .. EMOTIONAL 14); collision-door entries carry `DAMAGE_TYPE_IMPACT`, scripted ones what the
caller passed.

### What pins it

`DamageEventsTests`, on the scheduler harness with the region-side call on a second thread and the test
thread pumping (production's shape): an attachment script that halves the damage in `on_damage` sees
`od=1:20/5/20 from <source>` then `fd=1:10/5/20` and Health drops **by the halved amount** (90); a second
attachment with no `on_damage` still gets `final_damage` and sees the halved value; `llDamage` from a prim
reaches a worn attachment's `on_damage` with **`llDetectedKey` == the prim** and `llDetectedOwner` == its
owner; `llAdjustDamage` outside `on_damage` is a DEBUG_CHANNEL error and changes nothing. **Red 4/4 on the
PART 1 engine, green 4/4.** Suite **126 -> 134**, 0 skipped; `DispatchIndexGuard` unchanged (no index
moved); region server builds clean.

### Not done, on record

The wiki's "region must allow damage adjustment" flag (no such setting here - `AllowDamage` gates the
scripted doors as before); `llDamage` on a task target and the seated-avatar redirect; the llDamage
throttle; `on_damage` on the *hit object's* scripts (SL raises it on tasks too - only avatars' attachments
here). The PHLOX-6 note that the hook "would live in `ScenePresence.PhysicsCollisionUpdate`" was half
right: it lives in the door that path now calls.

## PHLOX-11 - script_state.db: no more "database is locked"

**2026-09-10. Landed; not deployed.** Live on 1.1.319, start 05:47:01: `05:47:08 WARN [PhloxState] Failed to
load state for 77147179-... "database is locked"` (348 ms after that script started from disk cache),
then `05:47:09 ERROR [PhloxState] Batch flush failed: "database is locked"`. A failed load was a fresh
start: `LoadState` swallowed the exception and returned null, `FinishedLoading` read null as "no saved
state", posted `state_entry`, and the next flush **saved the fresh state over the row** - the globals
were gone for good, not just for that run. Three regions load in parallel and each engine's
`StateManager.FlushLoop` writes every 2.5 s, all on one SQLite file.

### PART 1 - the reproduction that did not reproduce, and what it proved instead

Three shapes against a real temp DB with the pre-fix connection setup, 10 rounds each, **0 BUSY**:
one manager with three loader threads restoring 50 rows each against a writer feeding the flush loop;
**three managers on one file** (three engines, three flush loops - the live shape); and pure open/close
churn, nine threads x 800 loads with no long-lived connection. Two probes explain why:

- a load against a held `BEGIN EXCLUSIVE` returned its row in **12 ms** (WAL readers do not block on
  the writer), and a write against the same held lock **waited 2098 ms and succeeded** - System.Data.SQLite
  retries a plain `SQLITE_BUSY` inside `Step` until its 30 s command timeout. In-process contention
  therefore never surfaces as an exception, which is why the live failure - **348 ms** after the load
  began, not 30 s - cannot have been a plain busy. What the provider does *not* retry is the busy a
  connection gets while another is rebuilding the WAL index, and the old code opened and closed a
  connection **per call**, so between calls the connection count dropped to zero and the WAL was torn
  down and rebuilt, over and over, across three engines. That is the mechanism this fix removes;
  it could not be provoked on this machine in the budget, and that is recorded as such rather than
  as a red test. **The cited exception is the live one**, not a test's.
- the churn probe ran **2015 ms** on per-call connections and **53 ms** on the persistent ones.

`StateDbContentionTests` (the three-manager shape, 10 rounds) stays as the regression: it must read 0
load failures, 0 flush failures, 0 null rows, and prints the first error text if it ever does not.

### PART 2 - the fix, all in `StateManager`

| | |
|---|---|
| (a) | `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;` **once** per manager under the writer lock (the old code re-issued the journal pragma on **every** open) |
| (b) | `busy_timeout` **5000 ms** on every connection - connection string `BusyTimeout=5000` and the pragma |
| (c) | **one writer**: every write in the process - all three engines' flush loops, `SaveSingle`, `DeleteState` - serialises on a **static** `s_WriterLock` (the old `m_Lock` was per manager, so three engines had three writers). Each manager keeps **one persistent writer and one persistent reader** open for its lifetime, so the WAL is never torn down between calls; loads use the reader under the manager's own read lock |
| held, not fresh | `LoadState`: a database failure is **not** "no state". One retry, then `StateLoadFailedException`; `FinishedLoading` catches it, logs ERROR with the item id, and holds the script **Disabled** with the new `LocalDisableFlag.StateLoadFailed` - loaded, in `phlox status` as `HELD: StateLoadFailed (state load failed - row kept, never run or saved this process; restart to retry)`, no `state_entry`, no run queue. `StateManager.MarkLoadFailed` makes `ScriptChanged` and `ScriptUnloaded` **refuse to save** that id, so the row survives shutdown too |

**The row is not overwritten - proven.** `StateLoadFailedHoldTests`: process 1 runs the script (a
global lands at 41) and saves; process 2 has the database "locked" for that item (the
`FailLoadForTest` seam, throwing as SQLite would) - the load fails twice, the script is held
(`Enabled=False LocalDisable=StateLoadFailed`), never says `up`, ignores a touch, and the process ends
through `ScriptUnloaded`; the row's blob and `saved_at` are **byte-identical** before and after; process 3
loads it - no `up`, and the touch says **`g=42`**. Suite green; region server builds.

### Found on the way, not fixed

`05:58:12 WARN [PhloxState] Failed to save 6b726988-...: "Collection was modified after the enumerator
was instantiated."` - a flush serialising a script's event queue while the script thread changes it.
Different bug (a snapshot under the queue lock in `SerializedRuntimeState.FromRuntimeState`); a
dropped save, not a lost row. Candidate.

**The older entry, closed against this one.** It is not in the tracked docs (`grep` of `Docs/` for the
phrase, `SQLITE_BUSY`, `busy_timeout`: 0 hits); it is the perf note in the 09-08 ops handoff, §3.4:
"`[PhloxState] database is locked` 09-06 14:19" - the same line, four days earlier, filed under physics
perf notes because nothing was known about it. Marked closed by PHLOX-11 in the current handoff. The
nearest tracked item is candidate (iv)'s note that the harness tests share one `script_state.db` - which
the persistent connections and the busy timeout now serve as well.

## PHLOX-12 - the OSSL lane opens; three PHLOX-11 leftovers ride along

**2026-09-10. Landed in two commits; not deployed.**

### PART 0 - the leftovers

**0a - the verify scripts counted lines from earlier starts.** Every count *was* scoped to
`/tmp/thisstart.log`, but that file was built with `awk '$0 >= "<timestamp>"'` - a string compare - and an
exception's continuation lines carry no timestamp: `database is locked"` alone on a line sorts after any
`2026-...` string and came through from the 05:47 start on every run. The 15:30 start showed the count as
2 with 0 `[PhloxState]` errors. All three scripts (`verify-phlox8-9`, `9b-10`, `11`) now scope by the
**line number** of the last `[STARTUP]`. Re-run against the 15:30 start of 1.1.321: `database is locked`
**0**, `[PhloxState]` WARN/ERROR **0**, `Restored state for` **17** - the row that failed on 1.1.319 loads.
That is the first of PHLOX-11's two required clean starts.

**0b - the flush race (`92552f74d1`).** `SerializedRuntimeState.FromRuntimeState` already snapshotted
`Calls`, `EventQueue` (under `EventQueueLock`), `ActiveListens`, `MiscAttributes` and `Globals` - and
then handed the **live operand stack** to `FromPrimitiveStack`, which enumerated it in place while the
script thread pushed and popped. That is the 05:58:12 line, verbatim: `FlushRaceTests` hammers a
`RuntimeState` from one thread (events queued and removed, a reset every 25 rounds, 20 pushes and pops)
while another serialises it 100 times, protobuf included - **red: 17 of 100 threw
`InvalidOperationException: Collection was modified after the enumerator was instantiated.`**; green:
**0 of 100**. Two `EventQueue.Clear()`s (`Reset`, `StateChangePrep`) and the timer `Remove` also ran
without the lock the saver snapshots under; they take it now.

**0c - WAL growth, measured, no fix.** Read-only against the live DB during the 15:30 run: **396 rows**,
blob average **624 B** (max 901), `page_size` 4096. `FlushAllDirty` saves **dirty scripts only** - rows
saved in the last 10/30/60/300 s were **2/2/2/7**, not 396 - so it is not "saves everything"; a script is
dirty after every timeslice it ran (`ScriptChanged` at `PhloxExecutionScheduler.cs:773`), so a timer
script re-saves every 2.5 s whether or not its state changed, at ~12 KB of WAL per commit (leaf page +
index page + commit frame for a 70-byte row). WAL: 2.6 MB at +17 s, **3.41 MB at 15:31:27, 4.10 MB at
15:32:40** (+111 KB in 10 s), **4.12 MB at 15:50 and flat**; the main DB's mtime moved from 06:10:12 to
**15:50:47** - `wal_autocheckpoint` (1000 pages = 4,096,000 B) fired as soon as the WAL crossed it, so
the WAL plateaus at ~4 MB and stays. The startup burst is 17 restored scripts' first saves plus the
loaders' traffic. A "did the state actually change" check before marking dirty would cut the steady
write rate; it is a one-line-ish change but not the fix the brief conditioned on, so not made.

### PART 1 - the OSSL map

`Docs/audit/phlox-ossl-surface-audit.py` **undercounts**: it reported 57 absent `os*` names, but the
upstream `OSSL_Api.cs` declares **268** and Phlox's table carries **2** (`osGetAvatarList`,
`osTeleportAgent`) - **266 absent**. The audit's regex finds all 268 on the upstream side; the shortfall is
in how it prints (60 rows) - not fixed this session, the map below was built directly from the two files.

| family | absent | names |
|---|---|---|
| osNpc* | 23 | osNpcCreate osNpcGetOwner osNpcGetPos osNpcGetRot osNpcLoadAppearance osNpcLookAt osNpcMoveTo osNpcMoveToTarget osNpcPlayAnimation osNpcRemove osNpcSaveAppearance osNpcSay osNpcSayTo osNpcSetProfileAbout osNpcSetProfileImage osNpcSetRot osNpcShout osNpcSit osNpcStand osNpcStopAnimation osNpcStopMoveToTarget osNpcTouch osNpcWhisper |
| osGetGrid*/osGetRegion*/osGetSim*/osGetAvatar*/osGetAgent* (read-only information) | 65 → **48** after PART 2 | osGetAgentCountry osGetAgentIP osGetApparentRegionTime(+String) osGetApparentTime(+String) osGetAvatarHomeURI osGetCurrentSunHour osGetGender osGetHealRate osGetInventory{Desc,ItemKey,ItemKeys,LastOwner,Name,Names} osGetLastChangedEventKey osGetLinkColor osGetLinkInventory{Desc,ItemKey,ItemKeys,Key,Keys,Name,Names} osGetLinkNumber osGetLinkPrimitiveParams osGetLinkSitActiveRange osGetLinkStandTarget osGetNotecard osGetNotecardLine osGetNumberOfAttachments osGetNumberOfNotecardLines osGetPSTWallclock osGetParcel{Details,Dwell,ID,IDs} osGetPrimCount osGetPrimitiveParams osGetRegionMapTexture osGetRezzingObject osGetSitActiveRange osGetSittingAvatarsCount osGetStandTarget osGetSunParam osGetTerrainHeight osGetWindParam |
| osDraw*/osSetDynamicTexture*/osMovePen/osSetPen/osSetFont | 28 | osDraw{Ellipse,FilledEllipse,FilledPolygon,FilledRectangle,Image,Line,Polygon,Rectangle,ResetTransform,RotationTransform,ScaleTransform,Text,TranslationTransform} osGetDrawStringSize osMovePen osSetDynamicTexture{Data,DataBlend,DataBlendFace,DataFace,URL,URLBlend,URLBlendFace} osSetFontName osSetFontSize osSetPenCap osSetPenColor osSetPenColour osSetPenSize |
| osParcel*/osEstate*/osSetParcel*/osSetEstate*/terrain/wind/sun | 25 | osParcelJoin osParcelSetDetails osParcelSubdivide osRegionNotice osRegionRestart osReplaceParcelEnvironment osSetEstateSunSettings osSetParcelDetails osSetParcelMediaURL osSetParcelMusicURL osSetParcelSIPAddress osSetRegionSunSettings osSetRegionWaterHeight osSetSunParam osSetTerrainHeight osSetTerrainTexture osSetTerrainTextureHeight osSetTerrainTextures osSetWindParam osSunGetParam osSunSetParam osTerrainFlush osTerrainGetHeight osTerrainSetHeight osWindActiveModelPluginName |
| osAgent*/osAvatar*/osForce*/osKick*/osCause*/osTeleport* | 30 | osAgentSaveAppearance osAvatarName2Key osAvatarPlayAnimation osAvatarStopAnimation osAvatarType osCauseDamage osCauseHealing osDie osDropAttachment osDropAttachmentAt osEjectFromGroup osForceAttachToAvatar osForceAttachToAvatarFromInventory osForceAttachToOtherAvatarFromInventory osForceBreakAllLinks osForceBreakLink osForceCreateLink osForceDetachFromAvatar osForceDropAttachment osForceDropAttachmentAt osForceOtherSit osInviteToGroup osKickAvatar osOwnerSaveAppearance osSetHealRate osSetHealth osSetOwnerSpeed osSetSpeed osTeleportObject osTeleportOwner |
| osSet* prim/object/sound/misc side-effects | 39 | osAdjustSoundVolume osClearInertia osClearObjectAnimations osCollisionSound osConsoleCommand osLocalTeleportAgent osLoopSound osLoopSoundMaster osLoopSoundSlave osMakeNotecard osMessageAttachments osMessageObject osPlaySound osPlaySoundSlave osReplaceAgentEnvironment osReplaceRegionEnvironment osReplaceString osRequestSecureURL osRequestURL osResetAllScripts osSetContentType osSetInertia osSetInertiaAsBox osSetInertiaAsCylinder osSetInertiaAsSphere osSetLinkSitActiveRange osSetLinkStandTarget osSetPrimFloatOnWater osSetPrimitiveParams osSetProjectionParams osSetRot osSetSitActiveRange osSetSoundRadius osSetStandTarget osStopSound osTriggerSound osTriggerSoundAtPos osTriggerSoundLimited osVolumeDetect |
| osString*/osList*/osFormat*/osRegex*/osUnix*/crypto/pure helpers | 39 | osAESDecrypt osAESDecryptFrom osAESEncrypt osAESEncryptTo osAngleBetween osApproxEquals osCheckODE osDetectedCountry osFormatString osIsNotValidNumber osIsNpc osIsUUID osKey2Name osListAsFloat osListAsInteger osListAsRotation osListAsString osListAsVector osListFindListNext osListSortInPlace osListSortInPlaceStrided osListenRegex osMatchString osMax osMin osRegexIsMatch osRound osSHA256 osSlerp osStringEndsWith osStringIndexOf osStringLastIndexOf osStringRemove osStringReplace osStringStartsWith osStringSubString osUnixTimeToTimestamp osVecDistSquare osVecMagSquare |
| misc | 17 | osGetInertiaData osGetNPCList osGetSitTargetPos osGetSitTargetRot osGiveLinkInventory osGiveLinkInventoryList osLinkParticleSystem osLoadedCreationDate osLoadedCreationID osLoadedCreationTime osOldList2ListStrided osParticleSystem osPerlinNoise2D osPreloadSound osRemoveLinkInventory osResetEnvironment osTemperature2sRGB |

### PART 2 - the first family: seventeen read-only information functions (`this commit`)

**The gate first.** `OsslGate` (`Source/Phlox.ScriptEngine/OsslGate.cs`) is `OSSL_Api.CheckThreatLevel`
/ `CheckThreatLevelTest` ported (`OSSL_Api.cs:180-215, 301-530`), reading the **same `[OSSL]` keys**
(falling back to the engine's own section when `[OSSL]` is absent, as upstream does):
`AllowOSFunctions` (default true), `OSFunctionThreatLevel` (default VeryLow), `PermissionErrorToOwner`,
`Allow_<function>` (true / false / a comma list of owner UUIDs and `PARCEL_OWNER`, `PARCEL_GROUP_MEMBER`,
`ESTATE_MANAGER`, `ESTATE_OWNER`, `ACTIVE_GOD`, `GOD`, `GRID_GOD`) and `Creators_<function>`. The
`ThreatLevel` enum is upstream's own (`IOSSL_Api.cs`). A denied call throws `VMException` with upstream's
text verbatim; the script stops and the owner reads it on DEBUG_CHANNEL, as on YEngine.

| function | upstream | gate | index |
|---|---|---|---|
| `osGetGridName` / `osGetGridNick` | `:2580` / `:2575` | none (upstream has none) | 683-684 |
| `osGetGridHomeURI` / `osGetGridLoginURI` / `osGetGridGatekeeperURI` / `osGetGridCustom(key)` | `:2601` / `:2585` / `:2608` / `:2615` | Moderate | 685-688 |
| `osGetRegionSize` | `:3640` | master switch only | 689 |
| `osGetRegionStats` | `:3626` | Moderate | 690 |
| `osGetSimulatorVersion` | `:2078` | High | 691 |
| `osGetAgents` | `:1145` | None | 692 |
| `osGetMapTexture` | `:3582` | master switch only | 693 |
| `osGetPhysicsEngineType` | `:2040` | High, **non-throwing** (empty string when denied, as upstream) | 694 |
| `osGetPhysicsEngineName` | `:2064` | master switch only | 695 |
| `osGetSimulatorMemory` / `osGetSimulatorMemoryKB` | `:3651` / `:3665` | Moderate | 696-697 |
| `osGetHealth(agent)` | `:3749` | None | 698 |
| `osGetScriptEngineName` | `:1997` | High ("InWorldz.Phlox" -> `Phlox`, upstream's strip-to-the-dot) | 699 |

Two departures, stated: `osGetGridLoginURI` and `osGetGridCustom` read `[GridInfoService]` only - upstream
also asks the login server's info page when the key is absent; not done. Left for the next pass:
`osGetCurrentSunHour` (environment module), `osGetRegionMapTexture` (grid lookup + sleep),
`osGetNumberOfAttachments`, `osGetAvatarHomeURI`.

### What pins it

`OsslInfoFunctionsTests`, through the harness: under the default config a script dispatches the ungated
and None/master-switch functions and says `size=256x256`, `agents=1`, `map=<TerrainImageID>`,
`health=100.000000`, `phys=basicphysics 1.0|ptype=` (High, empty, no error); `osGetGridHomeURI` under the
default VeryLow stops the script with **`OSSL Permission Error: osGetGridHomeURI permission denied.
Allowed threat level is VeryLow but function threat level is Moderate`**; `OSFunctionThreatLevel =
Moderate` allows it; `Allow_osGetGridHomeURI = true` allows it, a UUID list without the prim's owner denies
with `permission denied`, the same list with the owner allows, `false` denies with `disabled in region
configuration`; `AllowOSFunctions = false` with `PermissionErrorToOwner = true` stops even the master-switch
function with `(OWNER)OSSL Permission Error: All unsafe OSSL funtions disabled`. Dispatch baseline
**regenerated** (676 -> 693 names, all appended). Suite **138 -> 144**; region server builds.

### Found on the way, not fixed

A script killed by a syscall exception is **run again**: every denial test logs three stops for the one
script - the permission error, then `Unable to cast object of type 'System.Int32' to type
'System.String'`, then `Stack empty` - the interpreter re-entered its half-finished handler with a torn
operand stack. YEngine kills and stays killed. Candidate: `TerminateWithError` sets `Killed` but something
still schedules the script (the queued-event drain, or the harness's pump). Not investigated.

## PHLOX-13 - a thrown syscall stays dead; OSSL pure helpers

**2026-09-10. Landed in two commits; not deployed.**

### PART 0 - the re-dispatch (`f6f1b0911c`)

PHLOX-12 logged **three** stops per OSSL denial: the permission error, then `Unable to cast object of type
'System.Int32' to type 'System.String'`, then `Stack empty`. The PHLOX-5 harness note pointed at
`SafeOperandsPush`; the cause was one level up. `PhloxExecutionScheduler.DoTimeslices` catches the
exception from `Tick()`, calls `TerminateWithError` (which sets `RunState = Killed`) and breaks out of the
timeslice with `terminated = true` - **without** going through `CheckRunstateChange`, whose `Killed` arm
is the only place a dead script leaves the run queue. The node stayed on the queue; the next pass ticked
the dead script again, from the instruction after the syscall, on an operand stack the aborted shim had
half-popped: the cast error, then one more pass, the empty stack. Three terminations, three shouts, three
`[PhloxExe] terminated` lines.

Now: the catch takes the node off the run queue and the run index; `TerminateWithError` resets
`LastSyscallIndex`; `SyscallShim.Call` wraps the shim so an escaping exception resets the index and
un-parks a `Syscall` state before rethrowing. `ThrownSyscallStaysDeadTests`: an OSSL denial
(`Allow_osGetSimulatorVersion = false`) and a forced `ArgumentException` from a shim (`ThrowForTest`, a
test seam on the shim's one choke point) each give **exactly one** DEBUG_CHANNEL line, `RunState=Killed`,
`LastSyscallIndex=-1`, not on the run queue, and the statement after never runs. **Red, with the two
removal lines disabled: 3 stops with the live texts. Green: 1.**

### PART 1 - the pure helpers (`this commit`)

**32 names, 36 dispatch entries (700-735)** from the 39 in PHLOX-12's map, each ported from `OSSL_Api.cs`
at its line and threat level through `OsslGate`: `osAESEncrypt`/`Decrypt`/`EncryptTo`/`DecryptFrom`
(`:6586-6640`, ungated, `Util.AES*`), `osAngleBetween` (`:5010`), `osApproxEquals` float forms 2 and 3 args
(`:5417`, `:5424`), `osCheckODE` (`:2026`, master switch), `osFormatString` (`:2649`, VeryLow),
`osIsNotValidNumber` (`:5973`), `osIsUUID` (`:4434`), `osListAsFloat`/`Integer`/`String`/`Vector`/`Rotation`
(`:6799-6863`), `osMatchString` (`:2656`, VeryLow), `osMax` (`:4456`, **gated under the key
`osGetRezzingObject` at None** - upstream's copy-paste, honoured as is so the same `Allow_` line governs
it on both engines), `osMin` (`:4445`), `osRegexIsMatch` (`:4600`, Low), `osRound` (`:4990`), `osSHA256`
(`:2557`), `osSlerp` rotation form (`:5919`), `osStringStartsWith`/`EndsWith`/`IndexOf` x2/`LastIndexOf` x2/
`SubString` x2 (`:5252-5360`, master switch), `osStringRemove`/`Replace` (`:5384`, `:5405`),
`osUnixTimeToTimestamp` (`:4012`), `osVecDistSquare`/`osVecMagSquare` (`:5004`, `:4999`).

**Remaining, 7 names:** `osListFindListNext` (needs upstream's `ListFind_areEqual` coercion rules),
`osListSortInPlace` / `osListSortInPlaceStrided` (in-place list mutation - Phlox lists are values),
`osListenRegex` (a listen, not pure), `osDetectedCountry`, `osKey2Name`, `osIsNpc` (user / NPC module
lookups). Also not landed, by the resolver's arity keying: `osApproxEquals`'s vector and rotation forms
and `osSlerp`'s vector form (same arity as the float / rotation forms that did land).

### What pins it

`OsslPureHelpersTests`: one script calls every helper and says a value the test asserts **exactly** -
AES round trips both ways, `angle=1.570796`, `approx=110`, `fmt=a-2`, `las=1.500000|7|s|1|1|0`,
`match=o, 4, o, 7`, `round=3.000000|2.350000`, `sha=ba7816bf...`, `str=10|2|3|3|-1|ho|heLLo|llo|ell`,
`ts=1970-01-01T00:00:00.0000000Z`, `vec=25.000000|25.000000`, and no error chat; `osRegexIsMatch` (Low)
is denied at the default VeryLow with one stop and answers `rx=10` at `OSFunctionThreatLevel = Low`.
Two things the first runs taught about the 5-argument `osStringLastIndexOf`: with `offset 0, count 3`
it throws `ArgumentOutOfRange`, and with `offset 4, count 5` it answers **-1** where a human expects 3 -
**both are upstream's behaviour**, ported faithfully: .NET's `LastIndexOf` searches *backwards* from
`offset`, but upstream clamps `count` to `Length - offset` as if searching forward, so the 5-arg form can
only ever see `Length - offset` characters below `offset`. The test asserts the faithful -1. And the
throw produced exactly one stop - PART 0 at work. Dispatch baseline **regenerated**
(693 -> 725 names: 36 entries, four of them overloads sharing a name). Suite **144 -> 148**; region server builds.

## PHLOX-14 - OSSL osNpc* on top of BotManager

**2026-09-10. Landed; not deployed.** A second door onto the same bots, not a second NPC system.

### PART 0 - what is there

- **Upstream:** `INPCModule` (`OpenSim.Region.Framework/Interfaces/INPCModule.cs:87-303` - CreateNPC x2,
  IsNPC, GetNPC, CheckPermissions, SetNPCAppearance, MoveToTarget, StopMoveToTarget, Say/Shout/Whisper, Sit,
  Stand, Touch, DeleteNPC, GetOwner) and the 23 `osNpc*` in `OSSL_Api.cs:2808-3460` (+ `osNpcLookAt`
  `:6335`). Every one, bar `osIsNpc` (master switch), `osNpcSay(2)` (delegates) and `osNpcGetOwner` (None),
  is **High** or (the two profile setters) **Low**, and every one checks `INPCModule.CheckPermissions(npc,
  m_host.OwnerID)`: an unowned NPC obeys anyone, an owned one only its owner.
- **This tree:** `NPCModule` (OptionalModules, the upstream module as is) and **`BotManager`**
  (`OptionalModules/World/NPC/BotManager.cs`, 1437 lines) - the InWorldz bot subsystem the `bot*` family
  and `llCreateCharacter` sit on. **BotManager is built ON NPCModule**: `CreateBot` calls
  `m_npcModule.CreateNPC`, `RemoveBot` calls `DeleteNPC`, and `GetBotWithPermission` delegates to
  `NPCModule.CheckPermissions`. One `BotData` per NPC, keyed by the NPC's own key.
- **Neither is live.** `NPCModule.Initialise` reads `[NPC]` and is enabled only if the section exists;
  `BotManager.Initialise` reads the same section. The live `config/OpenSim.ini` has **no `[NPC]` section**,
  so `NPCModule.Enabled` is false, `INPCModule` is never registered, and BotManager never registers
  `IBotManager` (the 15:30 log of 1.1.321 has not one `[BotManager]` line). **Every `bot*` call on the
  grid today silently does nothing**, and so will `osNpc*` until `[NPC] Enabled = true` is added - the
  operator's file, the operator's edit. That is a precondition of the did-it-land.

### PART 1 - the family, 25 dispatch entries (736-760), 22 names

**Two additions to BotManager / `IBotManager`**, so osNpc* needs nothing the bot door lacks:
`CreateBot(..., bool owned, bool senseAsAgent, out reason)` - the old signature delegates with `true,
true`; the NPC and its `BotData` are owned by nobody when `owned` is false (OS_NPC_NOT_OWNED), and
`senseAsAgent` is the flag rather than the always-true the bot door passes. `SaveBotOutfit(botID, name,
ownerID, out reason)` - the BOT's current appearance into the caller's outfit store
(`SaveOutfitToDatabase` captures the owner's, which is what `botSetOutfit` wants and not what
`osNpcSaveAppearance` means). And **`GetBotsWithTag("")` now returns every bot** - no bot carries `""` as
a tag, so that query was always empty; it is how `botGetBotsWithTag("")` lists an osNpcCreate'd NPC.

| option flag / concept | mapping |
|---|---|
| `OS_NPC_NOT_OWNED` (0x2) | NPC owner and `BotData.OwnerID` = `UUID.Zero`; `CheckPermissions` then admits anyone - **mapped** |
| `OS_NPC_SENSE_AS_AGENT` (0x4) | `CreateNPC(..., senseAsAgent, ...)` - **mapped**; the bot door still always senses as agent |
| `OS_NPC_OBJECT_GROUP` (0x8) | BotManager's `CreateNPC` overload carries no group; `BotData` has no group field - **accepted, not applied** |
| `OS_NPC_CREATOR_OWNED` (0x1) | the default; same as no flag |
| `notecard` (create / load / save) | **the bot outfit store, by name, scoped to the calling prim's owner**: `""` on create = the owner's current appearance (BotManager's rule); `osNpcLoadAppearance(npc, name)` = `ChangeBotOutfit`; `osNpcSaveAppearance(npc, name)` = `SaveBotOutfit`, returning the outfit key where upstream returns a notecard asset id; `includeHuds` accepted, the store keeps the whole appearance. No notecard is written or read - the bot store IS the appearance store on Legion |
| `osNpcMoveTo` / `MoveToTarget` | one navigation point through `SetBotNavigationPoints`: `OS_NPC_RUNNING` -> `Run`, `OS_NPC_NO_FLY` -> `Walk`, otherwise `Fly` (upstream's `noFly = false`); `OS_NPC_LAND_AT_TARGET` accepted, not applied (BotManager lands on arrival anyway) |
| `osNpcSit(npc, target, options)` | `SitBotOnObject`; `OS_NPC_SIT_NOW` is the only option and the only behaviour |
| `osNpcSay/Shout/Whisper` | `BotChat` with the chat type; upstream's 2 s say-throttle not applied |
| `osNpcTouch(npc, object, link)` | `BotTouchObject` after upstream's link resolution (0/`LINK_ROOT` -> root, `LINK_THIS` -> the part, n -> link n) |
| `osNpcGetOwner` | `GetBotOwner`; an unowned NPC answers **its own key**, as upstream |
| `osIsNpc` | the presence's `IsNPC` - true for any NPC, bot or not |
| not landed | `osNpcSayTo` (targeted delivery has no door in Phlox's listen manager yet), `osNpcLookAt` (BotManager has no look-at) |

**Gate:** every function under its upstream key at its upstream level through `OsslGate`
(`Allow_osNpcCreate`, `Allow_osNpcRemove`, ... - the live `osslDefaultEnable.ini` maps them all to
`${OSSL|osslNPC}` = `ESTATE_MANAGER,ESTATE_OWNER`, so on Legion only estate managers' and the owner's
prims may drive NPCs, the same as YEngine).

### What pins it

`OsslNpcTests`, on a harness scene built the way upstream's `NPCModuleTests` builds one (AvatarFactory,
UserManagement, Attachments, NPCModule, BasicInventoryAccess) plus `BotManager` and `ChatModule` (an NPC is
a client; its chat reaches the scene through the chat module), `[NPC] Enabled`, `OSFunctionThreatLevel =
High`: `osNpcCreate("Test","Npc", llGetPos()+<2,0,0>, "")` yields a key for a presence named **Test Npc**;
`osIsNpc` 1, `botIsBot` 1, `osIsNpc(owner)` 0; `botGetBotsWithTag("")` lists it at index 0;
`osNpcGetOwner` is the prim owner; `osNpcSay(npc, "hello")` arrives as **client chat from the NPC's key**
on channel 0; `osNpcGetPos` reads the position back. An owned NPC survives `osNpcRemove` from another
owner's prim and is removed by its owner's; an `OS_NPC_NOT_OWNED` NPC reports itself as owner, has
`BotData.OwnerID` zero, and is removed by anyone. At the default VeryLow, `osNpcCreate` is denied with
one stop and no bot exists. Dispatch baseline **regenerated** (725 -> 747 names). Suite **148 -> 152**;
region server builds.

## PHLOX-15 - OSSL side-effect functions (osSet*, osForce*, sound, links, misc)

**2026-09-10. Landed; not deployed.** 49 names, 50 dispatch entries (761-810), from the 39 in PHLOX-12's
"osSet* prim/object/sound/misc side-effects" row plus the eleven the brief named from the osForce*/teleport
row (links, attachments, `osTeleportObject`, `osSetSpeed`, `osSetOwnerSpeed`, `osGetLinkPrimitiveParams`).
Same method as PHLOX-13/14: every body ported from `OSSL_Api.cs` with its line range cited in the
`<summary>`, every gated function under its upstream `Allow_` key and threat level through `OsslGate`,
upstream's bare `CheckThreatLevel()` as the master switch, an ungated upstream function left ungated. Where
Phlox already had the door, the OSSL form is that door: `osSetRot` -> `UpdateGroupRotationR` (the llSetRot
group path); `osForceCreateLink` / `osForceBreakLink` -> `CreateLinkCore` / `BreakLinkCore`, which are
`llCreateLink` / `llBreakLink` split after their `PERMISSION_CHANGE_LINKS` check (the split upstream makes
with `m_LSL_Api.CreateLink`); `osForceBreakAllLinks` -> `llBreakAllLinks` (no permission check in Phlox to
skip); the twelve link-addressed sound forms -> `ISoundModule` through upstream's `GetSingleLinkPart` rule;
`osSetPrimitiveParams` / `osGetPrimitiveParams` -> `SetPrimParams` / `GetPrimParams` on a same-owner prim
by key; `osTeleportObject` -> `SceneObjectGroup.TeleportObject` (the group teleport, `OSTPOBJ_*` flags);
`osForceAttachToAvatar` / `osForceDetachFromAvatar` -> the `IAttachmentsModule` calls llAttachToAvatar /
llDetachFromAvatar make, onto the owner, without `PERMISSION_ATTACH`; `osMessageObject` ->
`PostObjectEvent("dataserver", [sender, message])`; `osResetAllScripts` -> `ResetScript` / `ApiResetScript`.

| landed | gate |
|---|---|
| osSetRot | VeryHigh |
| osForceCreateLink, osForceBreakLink, osForceBreakAllLinks, osSetPrimFloatOnWater, osReplaceString | VeryLow |
| osMessageObject | Low |
| osSetSpeed, osSetOwnerSpeed (capped at 4), osRequestURL, osRequestSecureURL (`allowXss` option) | Moderate |
| osGetLinkPrimitiveParams, osForceAttachToAvatar, osForceAttachToAvatarFromInventory, osForceDetachFromAvatar, osForceDropAttachment, osForceDropAttachmentAt | High |
| osForceAttachToOtherAvatarFromInventory | VeryHigh |
| osTeleportObject (another owner's object only where the land-owner rule allows, `checkAllowObjectTPbyLandOwner` ported), osSetContentType (MIME string verbatim), osConsoleCommand (plus `CanRunConsoleCommand`) | Severe |
| osVolumeDetect, osSetPrimitiveParams, osGetPrimitiveParams, osSetInertia, osSetInertiaAsBox, osSetInertiaAsSphere, osSetInertiaAsCylinder, osClearInertia, osCollisionSound | master switch |
| osSetProjectionParams (5- and link-addressed 6-arg), osSetSitActiveRange, osSetLinkSitActiveRange, osSetStandTarget, osSetLinkStandTarget, osAdjustSoundVolume, osSetSoundRadius, osPlaySound, osLoopSound, osLoopSoundMaster, osLoopSoundSlave, osPlaySoundSlave, osTriggerSound, osTriggerSoundLimited, osStopSound, osTriggerSoundAtPos, osResetAllScripts, osClearObjectAnimations, osLocalTeleportAgent (owner / PERMISSION_TELEPORT granter / land-owner rule) | ungated upstream |

**Not landed, with reason:** `osMakeNotecard` x2 (Phlox has no notecard-asset writer door yet;
`SaveNotecard` would be a new asset-creation path, not a port); `osMessageAttachments` (a 60-line point
filter over the target's attachments - its own session); `osReplaceAgentEnvironment` /
`osReplaceRegionEnvironment` (need the environment module door Phlox does not have); the key-addressed
`osSetProjectionParams(key, ...)` (`OSSL_Api.cs:3921`) - it shares arity 6 with the link-addressed form and
Phlox keys overloads by arity. Ported faithfully but worth knowing: `osSetInertia` uses `rot.z` where
upstream's `:4767` has a `rot.y` typo; `osVolumeDetect` does not record the flag in the script's state as
`llVolumeDetect` does (upstream's own note: lost on rez/restart).

**Wiki:** `osForceCreateLink` read ("identical to llCreateLink except that it doesn't require the link
permission", VeryLow, 1 s delay - the delay is llCreateLink's own `ScriptSleep(1000)` in the core);
`osTeleportObject` read (Severe; returns 1 for a local teleport, 0 for a crossing started, negative on
failure); **`osSetRot` has no wiki page** ("There is currently no text in this page").

### What pins it

`OsslSideEffectTests`, harness scene, `OSFunctionThreatLevel = Severe`: `osSetRot(llGetKey(),
<0,0,0.707,0.707>)` leaves `GroupRotation` at z=w=0.7071; `osForceCreateLink(<other prim>, 1)` with no
`PERMISSION_CHANGE_LINKS` ever granted makes `llGetNumberOfPrims` say **2** and `osForceBreakLink(2)` puts it
back to **1**; the prim setters land on the scene part (`SitActiveRange` 12, `StandOffset` <1,2,3>,
`ProjectionEntry` with FOV 1.5, `SoundRadius` 7.5, `CollisionSoundType` -1 for `""`/0, `Name` "renamed"
through `osSetPrimitiveParams` by key and read back through `osGetPrimitiveParams`, `osReplaceString("aaa",
"a","b",2,0)` = `bba`); `osTeleportObject` to <100,100,30> returns 1 and the linkset is there;
`osMessageObject(llGetKey(), "ping")` raises `dataserver` with the sender's key; at VeryLow `osSetRot` is
denied with one stop and the rotation is unchanged. **Attachments are landed but not pinned:** the harness
attempt (`osForceAttachToAvatar(ATTACH_CHEST)` with an owner presence and the NPC-scene modules) set
`IsAttachment` but left `AttachedAvatar` zero and stopped the script with an NRE inside the attach - a
harness-scene gap or a real one, undetermined in the time box, so the attach family stays on the did-it-land
list. Dispatch baseline **regenerated** (747 -> 796 names). Suite **155 -> 161**; region server builds.

**Did it land:** a prim calling `osSetRot(llGetKey(), <0,0,0.707,0.707>)` then `osForceCreateLink` with a
second prim - the prim rotates 90 degrees and `llGetNumberOfPrims` says 2.

## PHLOX-16 - OSSL agent, teleport, kick, animation and group functions

**2026-09-10. Landed; not deployed.** 19 names, 25 dispatch entries (811-835): what was left of PHLOX-12's
"osAgent*/osAvatar*/osForce*/osKick*/osCause*/osTeleport*" row after PHLOX-15 took the link, attachment,
teleport-object and speed functions, plus `osKey2Name` and `osGetAgentIP` from the brief. Same method: every
body ported from `OSSL_Api.cs` with its line range in the `<summary>`, every gated function under its upstream
`Allow_` key and level through `OsslGate`, existing Phlox doors reused - `osTeleportOwner` x3 through
`iwTeleportAgent` and the PHLOX-2b five-argument `osTeleportAgent` (the owner's key as the agent);
`osCauseDamage` and a lowering `osSetHealth` through **PHLOX-10's `ApplyDamage` door** with this prim as the
source, on an async shim like `llDamage` because the door waits on `on_damage`; `osOwnerSaveAppearance` x2
into **BotManager's outfit store** under the owner (`SaveOutfitToDatabase`, the same mapping as PHLOX-14's
`osNpcSaveAppearance`, returning the outfit's key; `includeHuds` accepted, not applied); the animation pair on
`ScenePresenceAnimator` as `llStartAnimation` does; `osForceOtherSit` through `HandleAgentRequestSit`.
**`osTeleportAgent` needed nothing** - its three arities (region name, grid x/y, local) have been in Phlox
since PHLOX-2b (`Defaults.cs:4744-4780`).

| landed | gate |
|---|---|
| osTeleportOwner (3 arities) | None |
| osInviteToGroup, osEjectFromGroup (IGroupsModule; owner needs Invite / Eject power) | VeryLow |
| osAvatarName2Key, osKey2Name, osDie (same owner, rezzed by this linkset, never itself) | Low |
| osDropAttachment, osDropAttachmentAt (PERMISSION_ATTACH = 0x20 or a shout) | Moderate |
| osCauseDamage, osCauseHealing (capped 100), osSetHealth (1..100), osSetHealRate, osOwnerSaveAppearance x2 | High |
| osAvatarPlayAnimation, osAvatarStopAnimation, osForceOtherSit x2 | VeryHigh |
| osKickAvatar x2 (Kick with the alert, then CloseAgent), osGetAgentIP (plus `IsGod`) | Severe |
| osAvatarType x2 (-1 not a key, 0 absent, 1 avatar, 2 NPC) | ungated upstream |

**Not landed, with reason:** `osAgentSaveAppearance` x2 - the outfit store is keyed by the presence being
saved, and neither `SaveOutfitToDatabase` (the owner's own appearance) nor PHLOX-14's `SaveBotOutfit` (bots
only) saves *another* agent's appearance into the *caller's* store; that is a new IBotManager door, not a
port. `osSetSpeed`, `osSetOwnerSpeed`, `osTeleportObject` and the nine `osForce*` in the row were PHLOX-15.
**Deliberate deviations, both toward this tree's rules:** `osCauseDamage` is admitted by the parcel's
`AllowDamage` flag (upstream) **or** the region's `AllowDamage` setting (the rule `llDamage` applies here);
`osAvatarPlayAnimation` also accepts a key, as the stop form and `llStartAnimation` do (upstream's play form
takes an inventory name or a default-animation name only). Death on a lowered health is the door's business
and is not repeated from upstream's body.

**Wiki:** `osCauseDamage` (High, `${OSSL|osslParcelO}ESTATE_MANAGER,ESTATE_OWNER`), `osTeleportOwner`
(None, 5 s delay in the wiki - Phlox's teleport door carries no sleep), `osKickAvatar` (Severe, key form
added 2019), `osForceOtherSit` (VeryHigh, "always disabled by default") read over plain HTTP.

**Verify scripts:** the "expect 1: a0d421cb" note in `verify-phlox12-13`, `-14` and `-15-restart.sh` now
reads **expect 0** - the divide-by-zero test prim is gone.

### What pins it

`OsslAgentTests`, harness scene, `OSFunctionThreatLevel = Severe`: **`osCauseDamage(avatar, 10.0)` from the
harness prim reaches a worn attachment's `on_damage` with the prim as `llDetectedKey(0)` and 10.0 as the
damage, and the presence's Health is 90** (the recipe of PHLOX-10's llDamage test); `osSetHealRate` 2.5 lands
on `HealRate`, `osCauseHealing` 5 on a 50 reads back 55, `osSetHealth` 30 goes through the door to 30;
`osAvatarType` says 1 by key and by name and -1 for a non-key, `osKey2Name` and `osAvatarName2Key` agree
with the presence; `osAvatarPlayAnimation` puts the key on the animator (`HasAnimation`) and the stop form
takes it off; `osForceOtherSit` seats the presence on the prim (`IsSatOnObject`, `ParentID` = the prim);
`osKickAvatar` removes the presence from the scene; `osDie` deletes the object this linkset rezzed and not
the other; at VeryLow `osKickAvatar` is denied with one stop and the presence stays. Teleport is not pinned:
the harness scene has no entity-transfer module, so the did-it-land carries it. Dispatch baseline
**regenerated** (796 -> 815 names). Suite **161 -> 169**; region server builds.

**Did it land:** from a manager's prim, `osTeleportAgent(self, "Transylvania", <128,128,30>, <1,0,0>)` moves
you, and `osCauseDamage(self, 10.0)` shows `on_damage` in a worn attachment (region or parcel damage on).

## PHLOX-17 - OSSL parcel, estate, terrain, wind and sun functions

**2026-09-11. Landed; not deployed.** 29 names, 31 dispatch entries (836-866), from PHLOX-12's
"osParcel*/osEstate*/osSetParcel*/osSetEstate*/terrain/wind/sun" row (25) plus the four the brief named that
the row lacked (`osGetParcelDetails`, the two `osGet/SetTerrainHeight` aliases are in the row; `osSetParcelMediaURL`,
`osSetParcelSIPAddress`, `osTerrainFlush` too). Same method: every body ported from `OSSL_Api.cs` with its line
range in the `<summary>`, every gated function under its upstream `Allow_` key and level through `OsslGate`,
upstream's bare `CheckThreatLevel()` as the master switch. **Doors reused:** the terrain functions write
`Scene.Heightmap` and taint through `ITerrainModule` (the door `llModifyLand` already used); **`iwSetGround` is
real now as a side effect** - the heightmap over the rectangle where the owner may terraform, then a taint - it
was a stub that said `ITerrainModule.SetTerrain` was unavailable, and the channel indexer is the door;
`iwSetWind` stays a no-op (Halcyon's `WindSet(type, pos, speed)` has no counterpart; the wind module's
parameter door is `osSetWindParam`). `llGetParcelDetails` was split over a `LandData` so `osGetParcelDetails`
(by parcel id) shares it. Sun: EEP - `osGetCurrentSunHour` and `day_length` come from `IEnvironmentModule`,
as upstream; `osSetSunParam` / `osSunSetParam` go to `ISunModule`, which no EEP region carries, so they are
the no-op upstream ships (the wiki says the same).

| landed | gate |
|---|---|
| osTerrainFlush (once a minute per script), osSetParcelMusicURL, osSetParcelMediaURL, osSetParcelSIPAddress (land owner, `IVoiceModule`), osSetWindParam, osGetWindParam | VeryLow |
| osSunGetParam, osSetSunParam, osSunSetParam, osWindActiveModelPluginName | None |
| osSetTerrainHeight, osTerrainSetHeight (0/1, `CanTerraformLand`), osRegionRestart x2 (`CanIssueEstateCommand`; <15 s aborts), osRegionNotice x2, osSetRegionWaterHeight, osSetRegionSunSettings, osSetEstateSunSettings (see below), osParcelJoin, osParcelSubdivide, osSetParcelDetails, osParcelSetDetails, osSetTerrainTexture and osSetTerrainTextures (skipped for a god owner), osSetTerrainTextureHeight (High, and god only reaches the estate module) | High |
| osGetTerrainHeight, osTerrainGetHeight, osGetCurrentSunHour, osGetSunParam | master switch |
| osGetParcelDetails | ungated upstream |

**Not landed, with reason:** `osReplaceParcelEnvironment` (the environment door Phlox does not have - the
same as PHLOX-15's region/agent forms); `osEstateOwnerMessage` **does not exist in `OSSL_Api.cs`** - the
map row carried it, nothing upstream defines it. **Landed as the no-op upstream ships:**
`osSetEstateSunSettings` - its whole body is commented out since EEP (`OSSL_Api.cs:1524-1538`), so here it
is the gate and nothing else, kept so the call compiles. **Ported with one simplification:**
`osSetParcelDetails` takes NAME, DESC (LandOptions), OWNER and CLAIMDATE (estate manager or owner), GROUP
(land owner or manager - upstream's membership check through the groups module is not repeated),
SEE_AVATARS, ANY_AVATAR_SOUNDS, GROUP_SOUNDS; committed through `UpdateLandObject`, the overlay resent when
SEE_AVATARS moved. `osRegionRestart(seconds, msg)`: the message is accepted and dropped, which is what
upstream's own `RegionRestart(seconds, msg)` does (`:654-658`).

**Wiki:** `osSetParcelDetails` (High; OWNER/GROUP/CLAIMDATE "VeryHigh" in the text, one High key in the
code), `osRegionRestart` (High, managers only), `osSetTerrainTexture` (High, "obsolete, use
osSetTerrainTextures"), `osSetSunParam` (None, "does nothing on 0.9.2") read over plain HTTP;
**`osTerrainSetHeight` has no page of its own** ("replaced by osSetTerrainHeight").

### What pins it

`OsslWorldTests`, harness scene plus a real `LandManagementModule` with its default parcel,
`OSFunctionThreatLevel = Severe`: `osSetTerrainHeight(10,10, osGetTerrainHeight(10,10)+1.0)` returns 1 and
`Scene.Heightmap[10,10]` reads one higher, `osTerrainFlush` runs, `iwSetGround(20,20,21,21,15.0)` puts 15 on
both corners; `osSetParcelDetails(llGetPos(), [NAME, DESC])` renames the prim's parcel in the land channel,
`osSetParcelMusicURL` lands on its `MusicURL`, and `osGetParcelDetails(<parcel id>, [NAME])` reads the new name
back; `osRegionRestart(120)` reaches a recording `IRestartModule` as `ScheduleRestart(120)` and
`osRegionRestart(5, msg)` as `AbortRestart`, both returning 1 - nothing restarts; `osGetSunParam("year_length")`
is 365 and `osWindActiveModelPluginName` is empty without a wind module; at VeryLow `osRegionRestart` and
`osSetTerrainHeight` are each denied with one stop, the restart module untouched and the heightmap unchanged.
Terrain textures are not pinned (they need `IEstateModule`, which the harness does not carry) - on the
did-it-land list. Dispatch baseline **regenerated** (815 -> 844 names). Suite **169 -> 173**; region server
builds.

**Did it land (combined deploy):** from a manager's prim, `osTerrainSetHeight(10,10,
osTerrainGetHeight(10,10)+1.0); osTerrainFlush();` then `osTerrainGetHeight(10,10)` reads 1.0 higher and the
ground visibly moves; `osSetParcelDetails` on the prim's parcel changes its name in About Land.

## PHLOX-18 - killed scripts stay killed; OSSL draw and dynamic textures

**2026-09-11. Landed in two commits; not deployed.**

### PART 0 - a killed script stays stopped until reset (`<p0 commit>`)

**What was wrong.** `TerminateWithError` set `RunState = Killed` and nothing else (`PhloxExecutionScheduler.cs:1327-1339`).
The Running flag stayed on, so the killed state was saved at unload and restored as **Waiting** (`FinishedLoading`'s
`default` arm) - a half-dead script that answered touches off a dead frame - and a restart that found no usable
state ran `state_entry` again and re-threw. **The live case:** the osSetRot-denied prim `013b8258` (item
`a37d3c87`) at the 05:36 start on 09-11 - `Discarding stale state ... saved asset 2074003b, current 013b8258`
then the same `OSSL Permission Error: osSetRot` - and the trace shows the older cause underneath: the script had
been **edited** at 21:35 on 09-10 (new asset id), so its state was stale, and a killed script had nothing that
said "stopped" outside its state. SL keeps a crashed script halted until it is reset.

**Now.** The kill turns the Running flag off the way `llSetScriptState(FALSE)` and the viewer's checkbox take it
off - `GeneralEnable` in the persisted state, the item's `ScriptRunning` (through `ForceInventoryPersistence`
so the group saves it), timers and listens unregistered - and keeps the reason: `RuntimeState.TerminatedReason`,
serialized as **tag 26** of `SerializedRuntimeState` (rows written before it load with null). `phlox status`
prints `terminated : <reason> (PHLOX-18: stays stopped; reset it, or tick Running, to start it fresh)` under
the Running flag. A restore of a killed state **holds** it: no `state_entry`, not on the run queue, reason
intact. A reset (`ResetNow`) clears the reason and turns Running back on; **ticking Running on a crashed script
resets it** (`ProcessEnableDisable`) rather than resuming the dead frame. The loader now also honours the
**item's** Running flag at a fresh start - an item rezzed with it off (unticked in the viewer, or crashed before
a restart whose state was lost) loads held, and enabling it later owes it its `state_entry` (`m_HeldFresh`).

**Risk, named:** any script whose item flag is already `false` in the live inventory - unticked by an owner
long ago and still running under Phlox because the flag was ignored - stops at the first restart after this
deploys. That is the checkbox meaning what it says; the did-it-land looks for the `loaded STOPPED` line.

### What pins PART 0

`TerminatedScriptStaysStoppedTests` (collection `phlox-state`, `OSFunctionThreatLevel = VeryLow`, the script
calls `osSetRot` - the live denial): engine 1 says "up" once, is `Killed`, `GetScriptState` false, the item's
`ScriptRunning` false, status carries `terminated=... osSetRot ...`, not on the run queue; saved through
`ScriptUnloaded`. Engine 2 restores the same item and asset: **no "up", no error line, still stopped, reason
intact**; `ResetScript` then says "up" once more and is killed again. `TriggerStartScript` on a crashed script
runs `state_entry` a second time from a fresh state. An item rezzed with `ScriptRunning = false` loads held,
says nothing, and runs when ticked. Suite **173 -> 176**.

### PART 0b - the live restart re-ran a killed script (PHLOX-18b)

**2026-09-11. Landed; not deployed.** Item `9262c036` (divide by zero, asset `b1c1b9d1`) crashed at 15:35 on
1.1.341 and `phlox status` showed Killed / Running False. At the 16:31 start of 1.1.344 it ran `state_entry`
again and crashed again (log line 11842), and the verify script's held count was 0. PART 0's round trip passed
because it saved through `SaveState` = `StateManager.ScriptUnloaded`, **which a region stop never calls.**

**The path the live restart takes.** `PhloxEngine.OnShutdown` calls `StateManager.Stop()` and nothing else
(`PhloxEngine.cs:489-493`); `Stop()` runs `FlushAllDirty()` (`StateManager.cs:114-123`), which writes the
**dirty set only** (`:285-289`). A script becomes dirty through `ScriptChanged`, called once per finished
slice at `PhloxExecutionScheduler.cs:813` - and the crash branch just above it (`:803-808`) returns
**before** that line. A script that died in its first slice was therefore never dirty, and the state
PART 0 set (`RunState = Killed`, `GeneralEnable = false`, `TerminatedReason`) never reached the row. The row
still carried the item's *previous* asset (`2074003b`, the 15:33 compile), so the 16:31 load logged
`Discarding stale state ... (saved asset 2074003b, current b1c1b9d1)` (line 11841) and took the fresh-start
branch. That branch reads the item's `ScriptRunning` flag - and the region DB does not store it:
`TaskInventoryItem.ScriptRunning` (`TaskInventoryItem.cs:146`) has no column in the MySQL prim-item store and
defaults to `true` on every load (`:174`). PART 0's `SetItemRunningFlag(false)` + `ForceInventoryPersistence`
persisted nothing that survives a restart; the only carrier across a restart is the Phlox state row. Of the
brief's three suspects: the shutdown save not carrying the killed state is the cause, the flag being re-read as
`true` is the consequence of the DB not storing it, and nothing forces `GeneralEnable` on a restored instance
(`SerializedRuntimeState.ToRuntimeState` restores it as saved, `:250`; the PROPS-1 code is a name/description
path in `LSLSystemAPI`, not the loader).

**Fix.** `TerminateWithError` marks the script dirty (`StateManager.ScriptChanged`) after turning it off, so
the flush loop and `Stop()` write the killed state; the disable branch of `ProcessEnableDisable` does the same,
because a script the viewer's checkbox (or `llSetScriptState(FALSE)`) stopped never runs again to get itself
saved - the same hole for the checkbox across a restart. And the restored branch of `FinishedLoading`, when the
row says `GeneralEnable = false`, pushes the item's Running flag off (the DB gave it the default) and logs
`{id} restored STOPPED (terminated: ...)` / `(Running flag off)`, the restored twin of PART 0's `loaded STOPPED`,
so the checkbox, `phlox status` and the verify script agree with the state. The PHLOX-4 comment that said
"ScriptUnloaded saves at shutdown" now says what does.

**What pins it:** `Crashed_script_stays_stopped_across_the_live_shutdown_path` in
`TerminatedScriptStaysStoppedTests` - engine 1 crashes the script and then calls **`StateManager.Stop()` only**
(new harness `ShutdownStateManager`, exactly `OnShutdown`); engine 2 rezzes the same item and asset with the
item's flag at its default `true`, as the DB presents it: **no "up", no error line, `GetScriptState` false, the
item's flag false, not on the run queue, reason intact.** Red before the fix with the live symptom verbatim
(`said=[up | Script error: ... osSetRot permission denied ...] RunState=Killed`). Suite **186 -> 187**.

**Did it land:** a crashed prim stays Running=False across a restart, with `terminated:` = 0 at load and one
`restored STOPPED (terminated: ...)` line for it in the start-up log.

### PART 1 - OSSL draw and dynamic textures (`<p1 commit>`)

**The renderer is real here.** `DynamicTextureModule` (`OpenSim.Region.CoreModules/Scripting/DynamicTexture/`,
registers `IDynamicTextureManager` in `AddRegion`, :349-356) and `VectorRenderModule`
(`Scripting/VectorRender/`, SkiaSharp, registers as content type `"vector"`, :98-100 / :233) both exist,
both compile, and the render is **synchronous** (`AsyncConvertData` :134-143, upstream's own "XXX: This isn't
actually being done asynchronously!"). Not stubbed, so this stayed a Phlox session.

**27 names, 29 dispatch entries (867-895)** from PHLOX-12's draw/dynamic-texture row (28), ported from
`OSSL_Api.cs` with the line range in each `<summary>`: the six texture calls (`URL` x3 **VeryHigh**, `Data`,
`DataBlend`, `DataBlendFace` **VeryLow** under the `osSetDynamicTextureData` family keys) hand the draw list to
the manager, `""` extraParams meaning 256 as upstream; the twenty draw-list helpers append the command string
the renderer parses and are upstream's bare `CheckThreatLevel()` - the master switch; `osGetDrawStringSize` asks
the renderer to measure. **Not landed, with reason - both are same-arity overloads and Phlox keys overloads by
arity:** `osSetDynamicTextureDataFace(6)` beside `DataBlend(6)` (the five-argument `Data` is `DataFace` with face
-1, so all faces are covered), and `osSetPenColor(string, vector)` beside `osSetPenColor(string, string)` (the
three-argument vector-and-alpha form is landed). `dynamicID` and `timer` are accepted and unused, as upstream.

**Wiki:** `osSetDynamicTextureData` (VeryLow, `${OSSL|osslParcelOG}ESTATE_MANAGER,ESTATE_OWNER`; "turn on a
cache or you see white" - the same cache rule the test hit), `osDrawText` (no threat check; "the pen position is not updated"), `osSetPenColor` (no
threat check; three forms) read over plain HTTP.

### What pins PART 1

`OsslDrawTests`: every helper in one script builds exactly
`MoveTo 20,20;PenColor Red; PenColor 7FFF0000; FontSize 24; ... ResetTransf;RotTransf 90;ScaleTransf 2,3;TransTransf 4,5;`,
a two-point polygon is `""`, and without a texture manager the string size is zero; **end to end**, with
`DynamicTextureModule` and `VectorRenderModule` in the scene, `osSetDynamicTextureData("", "vector",
"MoveTo 20,20; PenColour RED; FontSize 24; Text Hello;", "", 0)` returns the **new texture's id** (this tree's
`AddDynamicTextureData` returns `updater.newTextureID`, not upstream's updater id), **the prim's default face
texture changes to that id, and the asset is a LOCAL one in the asset cache** - `DataReceived` refuses to work
without an `IAssetCache` ("this are local assets and will not work without cache", `DynamicTextureModule.cs:471-473`),
so the test registers a memory cache and reads the rendered asset back from it (Legion runs `FlotsamAssetCache`, `GridCommon.ini:28`, so the rule is met live); the string size is non-zero;
at VeryLow `osSetDynamicTextureURL` is denied with one stop and the face untouched. Dispatch baseline
**regenerated** (844 -> 871 names). Suite **176 -> 179**; region server builds.

**Did it land:** a prim running `osSetDynamicTextureData("", "vector", "MoveTo 20,20; PenColour RED; FontSize
24; Text Hello;", "", 0)` shows "Hello" in red on its faces; and `phlox status <item>` on a crashed script shows
`Running flag : False` with the `terminated :` line, and a viewer reset starts it fresh.

## DRAW-1 - osSetDynamicTextureData renders a flat grey prim (a renderer defect surfaced by PHLOX-18)

**2026-09-11. Landed; not deployed.** Cross-reference: PHLOX-18 PART 1 landed the `osSetDynamicTexture*` doors
and pinned a new texture id on the face; live on 1.1.341 the brief's draw list turned the prim flat grey.
The defect is in `VectorRenderModule`, not in the Phlox port - a texture the sim considered valid that the
viewer could not decode.

**Diagnosis, in the order the brief asked.** A pixel test (`OsslDrawPixelTests`) renders the live draw list
through the real modules and decodes the bytes the face points at with CoreJ2K, the sim's own decoder.
**Font:** resolved - "Arial" exists on both hosts (`SKTypeface.FromFamilyName`, `VectorRenderModule.cs:551`)
and 220 red pixels were drawn. **Clear colour:** right - the background default is `SKColors.White`
(`:265`), the pixel at (200,200) decodes white. **Alpha:** right - `Rgba8888`/`Premul`, alpha 255, decoded
254 after the lossy 9/7 wavelet; not what a viewer shows as grey. **Container - the defect:** the bytes
began `00 00 00 0C 6A 50 20 20`, the **JP2 signature box**. CoreJ2K's `J2kImage.ToBytes` wraps the codestream
in a JP2 file by default (`jP` at 0, `ftyp` at 12, `jp2h` at 32, `jp2c` at 77 - the codestream itself,
`FF 4F FF 51`, starts at byte 85), and **the live asset pulled from Legion's Flotsam cache
(`DynamicImage9372`, `5b45234d-...`, 3047 bytes, cached 15:31:33) walks exactly so and is byte-for-byte the
harness's render.** The viewer's decoder is created for bare codestreams only:
`opj_create_decompress(OPJ_CODEC_J2K)` (`indra/llimagej2coj/llimagej2coj.cpp:311`, `:385`), so
`opj_read_header` fails on the signature box, `decodeImpl: failed to decode image!` (`:872`, DEBUG only),
and the texture is the viewer's missing-texture grey. Nothing on the sim side logs anything: the asset is
valid, cached, served. The server-side bake codec hit the same thing and already carries the answer -
`WithFileFormat(false)` (`OpenSimNGC.Appearance.Baking/J2kCodec.cs:72`, "raw codestream (no JP2 wrapper)") -
and its bakes render.

**A second, smaller defect found by the same test:** SkiaSharp's `DrawText` takes the **baseline** where
System.Drawing's `DrawString` took the **top-left**, so `MoveTo 20,20; Text Hello` drew its glyphs at y 3-19,
above the pen, and the brief's pixel at (30,30) was white even with a decodable texture. Every script written
against upstream places text by its top-left.

**Fixed:** `BuildEncoderConfig` ends in `.WithFileFormat(false)` (`VectorRenderModule.cs:87-94`), and the
text baseline is offset by the font's ascent so the pen is the top-left again (`:587`). **Not touched, same
defect:** `MapImageModule.BuildEncoderConfig` (`World/LegacyMap/MapImageModule.cs:93-99`) has no
`WithFileFormat(false)` either, so the region's J2K map-tile asset is a JP2 file too - a map session, one line.

### What pins it

`OsslDrawPixelTests`: (1) the renderer alone, `ConvertData(<live draw list>, "256")`, decodes to 256x256 with
a **raw-codestream magic `FF-4F-FF-51`**, white at (200,200), more than 50 red pixels, the red box starting
at or below the pen y, red within 3 px of (30,30); (2) the same through `osSetDynamicTextureData` in a scene
with `DynamicTextureModule`, `VectorRenderModule` and a memory `IAssetCache`, on the bytes the face now points
at. **Red first:** magic `00-00-00-0C-6A-50-20-20` and the red box at y 3-19. Suite **179 -> 181**; region
server builds.

**Did it land:** the same prim, `osSetDynamicTextureData("", "vector", "MoveTo 20,20; PenColour RED;
FontSize 24; Text Hello;", "", 0)`, shows red "Hello" on white - and the word sits below y=20, not above.

## PHLOX-19 - OSSL read-only remainder

**2026-09-11. Landed; not deployed.** 42 names, 44 dispatch entries (896-939): what was left of PHLOX-12's
information row after PHLOX-12 took seventeen and later sessions took `osGetAgentIP`, `osGetCurrentSunHour`,
`osGetLinkPrimitiveParams`, `osGetPrimitiveParams`, `osGetParcelDetails`, `osGetSunParam`, `osGetTerrainHeight`
and `osGetWindParam` - the row's `{...}` groups expanded (six `osGetInventory*`, seven `osGetLinkInventory*`, two
apparent-time pairs, three parcel readers) plus the brief's `osListenRegex` (the PHLOX-13 leftover) and
`osDetectedCountry`. `osIsUUID` was already there (PHLOX-13). Same method: every body ported from
`OSSL_Api.cs` with its line range in the `<summary>`, every gated function under its upstream `Allow_` key and
level through `OsslGate`, upstream's bare `CheckThreatLevel()` as the master switch, an ungated upstream
function left ungated.

**The notecard trio - the most-used OSSL functions in ported content.** `osGetNotecardLine`, `osGetNotecard`,
`osGetNumberOfNotecardLines` (VeryHigh) read the notecard **synchronously** through the same asset-service read
the async `llGetNotecardLine` path uses and the same `StripNotecardHeader`; upstream's `NotecardCache` is a
cache over that read, and this tree's asset cache already sits in front of the service. A missing notecard is
a shout and `"ERROR!"` / `-1`, an out-of-range line is EOF, as upstream. **And the shared stripper was wrong for
every notecard that exists:** the body is followed by `}` + newline (`AssetNotecard.Encode` and the viewer both
write it so), and `StripNotecardHeader` only stripped `newline + }` or a bare `}` - so the last line of every
notecard came back as `text}` with an empty line after it, for `llGetNotecardLine` and
`llGetNumberOfNotecardLines` too, since May. It now takes exactly the `Text length N` the header declares and
falls back to the brace strip only when N does not fit. The trio's test showed it (`n=3`, `l1=second line}`)
before the fix.

**Doors reused:** `llGetColor` became `ColorOf(part, face)` so `osGetLinkColor` shares it; the inventory
family reads the part's `Inventory` (the link forms through upstream's `GetSingleLinkPart` rule); `osListenRegex`
goes to Phlox's own listen manager, which **now matches regexes** - `ListenEntry` carries a compiled `NameRegex`
/ `MsgRegex` when `OS_LISTEN_REGEX_NAME` (1) / `OS_LISTEN_REGEX_MESSAGE` (2) is set, and the plain `Add` is the
new one with bitfield 0; the regexes are validated first, a shout and -1 when invalid, as upstream.

| landed | gate |
|---|---|
| osGetNotecardLine, osGetNotecard, osGetNumberOfNotecardLines | VeryHigh |
| osGetRegionMapTexture (grid service by name or id; upstream's 1 s sleep not applied) | High |
| osGetNumberOfAttachments (strided [point, count]), osDetectedCountry, osGetAgentCountry (a non-god owner only for a present agent) | Moderate |
| osGetAvatarHomeURI, osListenRegex | Low |
| osGetRezzingObject, osGetGender (the shape's "male" param), osGetHealRate | None |
| osGetLinkNumber, osGetApparentTime, osGetApparentTimeString, osGetApparentRegionTime, osGetApparentRegionTimeString | master switch |
| osGetPSTWallclock, osGetLastChangedEventKey, osGetLinkColor, osGetSitActiveRange, osGetLinkSitActiveRange, osGetStandTarget, osGetLinkStandTarget, osGetPrimCount x2, osGetSittingAvatarsCount x2, osGetParcelDwell, osGetParcelID, osGetParcelIDs, osGetInventoryLastOwner, osGetInventoryItemKey, osGetInventoryName, osGetInventoryDesc, osGetInventoryItemKeys, osGetInventoryNames, osGetLinkInventoryName, osGetLinkInventoryDesc, osGetLinkInventoryKey, osGetLinkInventoryKeys, osGetLinkInventoryItemKey, osGetLinkInventoryItemKeys, osGetLinkInventoryNames | ungated upstream |

**Not in `OSSL_Api.cs` at all, so not landed:** `osGetSimulatorHostname` and `osGetGridStats` from the brief -
nothing upstream defines them.

**Wiki:** `osGetNotecardLine` (VeryHigh, "skips the dataserver event"), `osListenRegex` (Low, the two
bitfield constants, "an error will be shouted" for a bad regex), `osGetNumberOfAttachments` (Moderate, the
strided list), `osGetAvatarHomeURI` (Low) read over plain HTTP.

### What pins it

`OsslReadOnlyTests`: a real notecard asset "cfg" with two lines in the harness asset service reads back
**`n=2`, `l0=first line`, `l1=second line`**, EOF for line 9, the whole text from `osGetNotecard`, `-1` and one
shout for a missing notecard; the inventory family answers by name, by id and by permission (a copy-only item's
key is NULL_KEY, the full-permission one's is not; names, keys and the link forms agree with the items); the
prim, parcel and misc readers answer from the scene (prim counts by key, sit range, stand target, link colour
after `llSetColor`, link number 0 for an unlinked prim, the parcel id and count with a real land module, the
rezzer id, the EEP time strings without an environment module, the home URI); on a presence `osGetHealRate`,
`osGetGender` (the default shape is female), a strided attachment count and the country; **`osListenRegex(7,
"", NULL_KEY, "^hel+o$", OS_LISTEN_REGEX_MESSAGE)` hears "hello" from a second prim and not "goodbye"**, and an
invalid name regex is a shout and -1; at VeryLow `osGetNotecardLine` is denied with one stop. Dispatch baseline
**regenerated** (871 -> 913 names). Suite **181 -> 186**; region server builds.

**Did it land (combined deploy with DRAW-1):** a prim holding a notecard "cfg" with two lines says
`osGetNumberOfNotecardLines("cfg") = 2` and `osGetNotecardLine("cfg", 0)` = its first line **with no trailing
brace on the last line**; and the draw prim shows red "Hello" on white.

## PHLOX-20 - the OSSL close-out: overloads by type, the misc row, and what is left

**2026-09-11. Landed; not deployed.** Three parts, three commits.

### PART 0 - overload resolution by type, not arity

PHLOX-2b/2c chose a built-in's overload by the NUMBER of arguments. Two signatures of one name and one arity
mangled to the same symbol (`name__arity`) and the second `Define` would have collided, so five OSSL forms
could not be landed at all - they are the reason for the "not landed: Phlox keys overloads by arity" notes
left in PHLOX-13, PHLOX-15 and PHLOX-18.

The type pass now picks among the signatures of the call's arity by the argument types it has already
computed (`Defaults.SelectOverload`): an exact match scores 2, an LSL implicit widening (integer to float, key
and string either way) scores 1, anything else makes the candidate unviable; the best score wins and two
viable candidates of equal score are a compile error naming both signatures. An argument whose type is unknown
- an error subtree - matches anything and scores nothing, so a broken argument is still exactly one error.
The chosen symbol is annotated on the call context and the gen pass emits **that**, so the two passes cannot
disagree about which shim the `syscall` reaches. `SymbolNameFor` gives a same-arity overload a name of its own
by appending its parameter types (`osApproxEquals__2_vv`); the arity-only name is kept wherever it is still
unique, so every overload landed before today resolves to the symbol it always did.

| landed with it | indices |
|---|---|
| osSetProjectionParams by prim key (OSSL_Api.cs:3921), osSetDynamicTextureDataFace (:790), osSetPenColor by vector (:1400) | 940-942 |
| osApproxEquals for vectors and rotations, with and without a margin (:5432 :5450 :5469 :5491) | 943-946 |
| osSlerp for vectors (:5931 - upstream's `Vector3.Slerp` does NOT normalise its inputs; mirrored) | 947 |

**What pins PART 0:** `OverloadByTypeTests` - `osSetPenColor("", "Red")` and `osSetPenColor("", <1,0,0>)`
reach two different shims (`PenColor Red; ` against `PenColor FFFF0000; `) at two table indices under two
symbol names; the other forms answer from their own shims (the vector slerp returns a three-component value,
the rotation one four); the chooser prefers exact over widened, finds nothing of a wrong arity and invents no
ambiguity from unknown types; and **every signature in the table has a symbol name to itself**. Red first with
the chooser disabled.

### PART 1 - PHLOX-12's misc row and the list family

Seventeen names, nineteen entries (948-966): `osGetSitTargetPos` / `osGetSitTargetRot`, `osLoadedCreationDate`
/ `Time` / `ID` (Low), `osTemperature2sRGB`, `osOldList2ListStrided`, `osListFindListNext`,
`osListSortInPlace` and `osListSortInPlaceStrided`, `osParticleSystem` / `osLinkParticleSystem`,
`osPreloadSound`, `osGetInertiaData`, `osGetNPCList` (None), `osRemoveLinkInventory`, `osPerlinNoise2D` and
`osAgentSaveAppearance` in both forms (VeryHigh).

**The two functions with value semantics.** `osListSortInPlace(list, stride, ascending)` sorts *the caller's
own list* instead of returning a new one, and it can do that here because a list argument reaches a syscall as
the same `LSLList` instance the variable slot holds - `ConvToLSLList` is a cast, not a copy - so writing back
into that instance's `Data` array is exactly what the script sees in its variable afterwards. The ordering
itself is `llListSort`'s (and `llListSortStrided`'s for the strided form), so it cannot drift from LSL's.

Doors reused: `PrimParticleSystem` for both particle forms, `ISoundModule.PreloadSound` per link part,
`TerrainUtil.PerlinNoise2D` (`OpenSim.Region.Framework.Scenes`, which Phlox already references),
`BotManager.SaveOutfitToDatabase` for `osAgentSaveAppearance` - the same store `osOwnerSaveAppearance` uses,
with the agent required to be in this region.

**What pins PART 1:** `OsslMiscRowTests`, 3 - a script sorts its own `list src = [3,1,2]` to `1,2,3` and a
strided pair list to `a,1,b,2` **through the variable**, `osOldList2ListStrided([0..5],0,5,2)` is `0,2,4`, and
`osListFindListNext` answers 0, 2, 2 (last) and -1 for the instances of `[1,2]`; the readers answer from the
prim and the region (sit target after `llSitTarget`, both ends of the temperature fit, a repeatable noise
value, no NPCs, four inertia elements) and the side-effect calls return without a shout; `osAgentSaveAppearance`
on an absent agent is NULL_KEY and one shout.

### PART 2 - the audit, fixed, and the closing state

`Docs/audit/phlox-ossl-surface-audit.py` had two faults from PHLOX-12. It keyed the Phlox side by the raw
table KEY, so every overload past the first was invisible (`osTeleportAgent__5` was not `osTeleportAgent`),
and it printed only the first 60 absent names and the first 40 type mismatches - the list this session was
supposed to close was never shown in full. It now keys by function name with every signature under it,
compares each API signature against all of them, prints both lists whole, and writes `audit.json` beside
itself instead of into one session's scratchpad.

**Absent from Phlox, by name - the OSSL lane's closing state (13 of 756 API names):**

| name | why |
|---|---|
| `osGiveLinkInventory`, `osGiveLinkInventoryList` | `llGiveInventory` is written against `m_host`; the link forms need it split by part first, as `ColorOf` was for PHLOX-19 |
| `osMakeNotecard` (both forms) | the one real new door in the set: create the notecard asset and the prim inventory item. Not attempted here |
| `osMessageAttachments` | needs the attachment-point filter and the three option flags |
| `osNpcSayTo` | needs the listen manager's targeted delivery |
| `osNpcLookAt` | **no door exists**: this tree's `BotManager` has no look-at or head rotation at all (`SetBotRotation` turns the whole body) |
| `osResetEnvironment`, `osReplaceRegionEnvironment`, `osReplaceParcelEnvironment`, `osReplaceAgentEnvironment` | `IEnvironmentModule` **does** have setters (`StoreOnRegion`, `ResetEnvironmentSettings`, `WindlightRefresh`), so these are landable - what is missing is the `ViewerEnvironment` / day-cycle asset plumbing (`CycleFromOSD`, parcel `StoreEnvironment`) and the estate permission checks. Landable in a following session, not in this one's budget |
| `llCastRayV3`, `llRemoteDataSetRegion`, `llRemoteLoadScript` | LSL, not OSSL; outside this lane |

Everything the audit still reports as an overload gap (10 names) or a type mismatch (114) is **Key against
String**: Phlox types a key parameter `VarType.Key` where the API writes `LSL_Key`, which the audit maps to
String. Not a defect - the two are interchangeable by LSL's own rule and by `SelectOverload`'s widening.

**Also deferred:** the notecard trio (`osGetNotecardLine`, `osGetNotecard`, `osGetNumberOfNotecardLines`) is
still **synchronous**, and PHLOX-19's 30 s worst case on a cold asset stands. Wrapping them in `RunAsync` is
not the one-line change it looks like: every `RunAsync` shim in the tree is a void call, and a *returning*
async syscall has to come back through the `LastSyscallIndex` / `ResumeFromSyscall` path that PHLOX-4b built
for restarts. That deserves its own change with a restart test, not a drive-by.

**Suite hygiene, from PHLOX-18b:** `SsbNpcAppearanceTests`, `OsslAgentTests.AvatarTypeNameAndKeyAgreeOnAPresence`
and `RemainingStubTests.TwoTouchesTenMillisecondsApartAreHandledAtLeastOneSecondApart` fail **intermittently in a
full run** and always pass in isolation - the same full suite went 194 of 194 green on the next run - so it is
a run-order or timing coupling between test scenes, not a product defect. A candidate for a session of its own.

**Did it land:** a prim's script calls `osSetPenColor("", <1,0,0>)` and `osSetPenColor("", "Red")` in one
script and gets two different draw-list strings; and a script that sorts its own list with
`osListSortInPlace(src, 1, TRUE)` sees `src` sorted afterwards.


## PHLOX-21 - audit 2026-09-23 fixes

Branch `phlox/audit-fixes-2026-09-23` off `b2/o121-deferral` (B2 is required: parts E5 and F return values
through its sequenced return). One commit per part; F was done before E because E3's test needs a
`llRequestAgentData` that returns at all.

### What changed

- **A - permissions.** `SlConst` (Source/Phlox.ScriptEngine/SlConst.cs) is the implementation's one copy
  of the SL constants, pinned to `DefaultConstants` by `SlConstMatchesTable`. `llAttachToAvatar`,
  `llAttachToAvatarTemp` and `llDetachFromAvatar` test PERMISSION_ATTACH (0x20), not TRIGGER_ANIMATION
  (0x10); `llAttachToAvatar` also needs the granter to be the owner (YEngine). Implicit grants are SL's:
  the wearer gets TAKE_CONTROLS, TRIGGER_ANIMATION, ATTACH, TRACK_CAMERA, CONTROL_CAMERA and
  OVERRIDE_ANIMATIONS; an avatar sitting anywhere on the linkset gets TAKE_CONTROLS, TRIGGER_ANIMATION,
  TRACK_CAMERA and CONTROL_CAMERA (no longer RELEASE_OWNERSHIP). A permission answer counts only for the
  item that asked, is ANDed with the requested mask, and releases controls when TAKE_CONTROLS is not
  granted. `PermissionsTests`.
- **B - content protection.** `llGetInventoryKey` gives the asset key only for a copy+modify+transfer item,
  else NULL_KEY. `llSetContentType` uses SL's numbering (0 text ... 8 rss) and serves text/html only to the
  owner's own viewer in the region (YEngine's rule). `ContentProtectionTests`.
- **C - robustness.** `DepthGuard` (EnsureSufficientExecutionStack) at the LSL parser's rule entry (a
  partial of the generated parser), the Def/Types/Analyze/Gen visitors' dispatch and the SLua parser and
  codegen walkers: deep nesting is the compile error `line L:C expression nested too deeply`, not a stack
  overflow that kills the region. The loader compiles on a dedicated thread with a **16 MB** stack
  (`PhloxScriptLoader.CompileStackSize`). *Corrected by PHLOX-22 A:* the "about 4,000 levels" first given here was a
  cold-process figure - how deep 16 MB reaches moves with JIT warm-up (~3,017 parser levels cold, ~9,739 warm) - and
  the limit is now counted; see PHLOX-22.
  Every script-built regex (osMatchString, osRegexIsMatch, osReplaceString, osListenRegex, iwMatchString)
  has a 250 ms match timeout (`ScriptRegex`); a timeout is no match plus `regex timed out`, and in the
  listen manager it fails only that listener. `RobustnessTests`.
- **D - SL constants.** llCastRay's RC_* fix (2b84e458b3) cherry-picked. LSLSystemAPI.cs keeps no private
  copy of an SL constant: 206 local declarations removed, 88 commented literals replaced by name, all from
  `SlConst`. Corrected families: STATUS_*, AGENT_* (llGetAgentInfo), AGENT_LIST_* scope (llGetAgentList,
  iwGetAgentList - YEngine's reading), PSYS glow/blend and SL's default blend, PRIM_RENDER_MATERIAL and
  PRIM_GLTF_*, WATER_*, LINK_* in llBreakLink (YEngine's handling), DATA_PAYINFO, CHARACTER_DESIRED_SPEED,
  REGION_FLAG_SANDBOX, PERMISSION_SILENT_ESTATE_MANAGEMENT. STATUS_SANDBOX is now set and read. 151 public
  SL constants Phlox lacked were added to `DefaultConstants` from secondlife/lsl-definitions @ 10741b9
  (fixture `Tests/InWorldz.Phlox.Tests/Fixtures/sl-constants-10741b9.txt`); no existing value changed.
  `SlConstantsTests` (SlConstantsMatchLL with its allow-list, LocalConstantsScan, runtime JSON_*/EOF/NAK,
  one behaviour test per family). The runtime test settles the notation question: EOF and NAK are spelt as
  escapes in the table but scripts get SL's characters.
- **E - small correctness.** Index 407 (`llGetMassMKS`) reaches `Shim_llGetMassMKS` (it was wired to
  `Shim_llGetMass`) and returns 100 x llGetMass. `llClearLinkMedia` passes link and face. DATA_ONLINE answers
  from presence. `llSetInventoryPermMask` is a god function ([InWorldz.Phlox] AllowGodFunctions + an
  administrator owner). `llManageEstateAccess` returns TRUE/FALSE. `SmallCorrectnessTests`.
- **F - dataserver query keys (C-5).** `llRequestAgentData`, `llRequestUsername`, `llRequestDisplayName` and
  `iwAvatarName2Key` returned nothing (their async shim returns only what the body hands to `SysReturn`), so
  any script calling them stopped with "Stack empty". Each returns its query key through B2's sequenced
  return, and the dataserver event carries the same key. `DataserverQueryKeyTests`.
- **21b A - every value-returning async syscall returns (symptom: the script "was killed with Stack empty",
  or for iwRezAt "Attempt to push null operand").** The complete list of table entries that return a value
  and whose shim uses `RunAsync` is 13 (`AsyncReturnGuardTests.TheListIsEveryValueReturningAsyncEntryInTheTable`
  derives it from the table and the shims' IL, so a new one cannot land uncovered):

  | Function | Returns | Before 21b | Fix |
  |---|---|---|---|
  | llRequestAgentData | key | nothing if `m_host` null | NULL_KEY on that path |
  | llRequestUsername | key | ok (F) | - |
  | llRequestDisplayName | key | ok (F) | - |
  | iwAvatarName2Key | key | nothing if `m_host` null | NULL_KEY on that path |
  | iwRezObject | key | **never** | root key / NULL_KEY (Halcyon) |
  | iwRezAtRoot | key | **never** | root key / NULL_KEY (Halcyon) |
  | iwRezAt | key | shim pushed null before the body ran | value through the sequenced return |
  | llManageEstateAccess | integer | ok (E5) | - |
  | botCreateBot | key | ok (finally) | - |
  | botGetBotOutfits | list | ok (finally) | - |
  | botSearchBotOutfits | list | ok (finally) | - |
  | iwDeliverInventory | integer | **never** | IW_DELIVER_* code, 100 ms (Halcyon) |
  | iwDeliverInventoryList | integer | **never** | IW_DELIVER_* code, 100 ms (Halcyon) |

  `AsyncReturnGuardTests` runs each as a statement and as an assignment and asserts the script is not
  stopped and its operand stack is empty afterwards.
- **21b B - a timed-out listen regex disables its listener.** An osListenRegex name or message filter that
  times out switches that listener off, as llListenControl(handle, FALSE) would (TRUE turns it back on).
  The owner sees `Script error: osListenRegex: pattern timed out; listener disabled` once on DEBUG_CHANNEL
  and the region logs it once. Before, the listener stayed active and every later line on the channel paid
  the 250 ms timeout again (20 lines: ~5 s; now ~250 ms). `ListenRegexTimeoutTests`.
- **21b C - llGetMass scope.** YEngine's rule (LSL_Api.llGetMass): an attachment reports the wearer's mass,
  anything else `m_host.ParentGroup.GetMass()` - the whole object from the root or any child. Phlox returned
  the script's own prim. llGetMassMKS stays 100 x llGetMass. `MassScopeTests`.

### Deployed

- **2026-09-23 17:24, region root only.** Build `1.1.499-alpha+e751d7c8ba` (branch
  `build/deploy-20260923-phlox21`, PHLOX-21 + 21b merged onto the live `5d02d1b001`). Four files replaced in
  `D:/legiongrid/regionserver`: `Phlox.ScriptEngine.dll` (`B81B6C8C...`, stamped 1.1.499-alpha+e751d7c8ba) and
  `.pdb`, `InWorldz.Phlox.dll` (unstamped 1.0.0.0; SHA-256 `DFC48CA11519F0211DC14F47E8A7FE5E5EB0F8301AB7F69315F96E585004CF34`
  from build HEAD `e751d7c8ba`) and `.pdb`. Everything else in the publish was restamp-only by IL/metadata
  compare; protobuf-net stays at the live 3.4.21. Backup: `D:/legiongrid/_backup/regionserver-phlox21-20260923-1724`.
  Restart 17:26: `verify-phlox21-restart.sh` PASS (0 Stack empty, 0 Phlox NREs, no new compile errors).

### Still open

- S-4: PERMISSION_TELEPORT handling.
- ESTATE_ACCESS_* renumbering to SL's 4..128 (Phlox keeps 0..5) and the bytecode-cache bump it needs.
- Signature and return fixes elsewhere in the table (the void-async returns are closed by 21b A).
- Experience KV async; the fetch trace; the namespace move.
- `ENV_DAY_LENGTH` (200) / `ENV_DAY_OFFSET` (201) in llGetEnvironment are Phlox names, not SL's
  (SL has ENVIRONMENT_DAYINFO = 200).

### What residents will notice

- Pose balls and AO HUDs stop asking: a sitter or wearer is granted animation (and a wearer
  OVERRIDE_ANIMATIONS) silently, as in SL. A sitter is no longer silently given RELEASE_OWNERSHIP.
- Scripts that attached with only an animation grant must ask for PERMISSION_ATTACH.
- `llGetInventoryKey` on a no-modify (or no-copy/no-transfer) item returns NULL_KEY.
- `llSetContentType(..., CONTENT_TYPE_HTML)` serves HTML only to the owner's viewer; everyone else gets
  text/plain. JSON, XML and the other types now match their constants.
- Particle glow and blend follow SL numbering; particle systems get SL's default blend.
- llGetAgentInfo walking/typing/away bits, llGetAgentList scopes, llBreakLink(LINK_THIS), STATUS_SANDBOX and
  the corrected PRIM_/WATER_ constants behave as documented on the SL wiki.
- `llRequestAgentData` / `llRequestUsername` / `llRequestDisplayName` / `iwAvatarName2Key` work again (the
  script no longer stops), and the returned key matches the dataserver event.

**Did it land:** (A) sit on a child prim of a pose ball that calls `llRequestPermissions(sitter,
PERMISSION_TRIGGER_ANIMATION)` - no dialog, the pose plays. (B) `llOwnerSay((string)llGetInventoryKey(<a no-mod
texture>))` says NULL_KEY. (C) a script whose state_entry nests 5,000 parentheses fails to save with
"expression nested too deeply" and the region stays up. (D) `llOwnerSay((string)(llGetAgentInfo(llGetOwner()) &
AGENT_WALKING))` while walking says 128. (E) `llOwnerSay((string)llGetMassMKS())` on a 0.5 m cube says about
100 x `llGetMass()`. (F) `key q = llRequestAgentData(llGetOwner(), DATA_NAME);` then in dataserver
`llOwnerSay((string)(id == q))` says 1.

## PHLOX-22 - compiles never freeze scripts, errors reach the editor, counted nesting limits, gives to absent avatars

Branch `phlox/audit-fixes-2026-09-23`, one commit per part: A `9fd21cf8a8`, B `fc487c0cc7`, C `13c7154edd`,
D `0c6293063b`. E (linear code generation) was not done - see below.

**Deployed 2026-09-23 20:05** (DEPLOY-PHLOX-22): build `build/deploy-20260923-phlox22` `d89a25367e` (this branch at
`c0a4a4dde0`, merged onto the live PHLOX-21 build `e751d7c8ba`), `Phlox.ScriptEngine.dll` 1.1.507-alpha+d89a25367e.
Only InWorldz.Phlox.dll / Phlox.ScriptEngine.dll and their .pdb copied (IL compare: no other assembly changed in
code; protobuf-net stays 3.4.21). Backup `D:/legiongrid/_backup/regionserver-phlox22-20260923-2003`.
`verify-phlox22-restart.sh`: PASS - 18 loaded / 18 restored against 17 / 17 at the previous start, none missing;
the only compile failure is the known `3eb0c62b`; 0 nesting-limit errors; 0 Phlox NREs.

### What changed

- **A - counted nesting limits.** PHLOX-21's guard tripped when the stack ran low, so its limit moved with JIT warm-up
  (on the 16 MB compile thread: ~3,017 parser levels in a fresh process, ~9,739 warm; ~2,410 / ~7,668 in the later
  passes): the deep-nest script compiled on the warm live region and failed in the cold harness. Nesting is now
  COUNTED - at the LSL parser's rule entry, in DefVisitor/TypesVisitor/AnalyzeVisitor/GenVisitor and in the SLua
  parser - and `EnsureSufficientExecutionStack` is only the backstop (`InWorldz.Phlox.Compiler.NestingLimits`):

  | Limit | Value | Message |
  |---|---|---|
  | expression nesting (parentheses, unary operators, casts, call arguments, list/vector/table elements, SLua `..`) | 1,000 | `expression nested too deeply (limit 1000)` |
  | blocks (LSL statement bodies and bare blocks; SLua then/else/loop/function bodies) | 500 | `blocks nested too deeply (limit 500)` |
  | else-if chain branches (its own allowance - chain links are not blocks) | 2,500 | `else-if chain too long (limit 2500)` |
  | chained assignments in one LSL expression (`x = a = b = 0` is 2) | 64 | `assignment chain too long (limit 64)` |

  LSL errors carry `line L:C`; SLua keeps its `SLua: ... (line N)` form (its tokens have no column).
  **Why these values.** Each sits under the lowest cold stack capacity (expressions: ~2,410) and under the TIME a
  script at the limit can cost with a syntax error: such a script is parsed again with full-context (LL) prediction,
  which grows much faster than the nesting - else-if chains 1,000 / 2,500 / 5,000 branches: 0.9 / 3.7 / 19.7 s cold;
  assignment chains 50 / 100: 0.4 / 1.0 s (1,000: over 15 minutes). At the limits, cold: 1,000 nested calls 0.4 s,
  500 blocks 0.7 s, 2,500 branches 1.3 s (3.7 s with a syntax error), 64 assignments 0.5 s with a syntax error.
  **Also found and fixed.** In today's (and the live, 1.1.499-alpha+e751d7c8ba) build, ANTLR's full-context
  prediction for the dangling `else` overflowed the stack at a 10,000-branch else-if chain - in
  `ParserATNSimulator.Closure_`, below every guard, killing the process - and 500 nested ifs took 8.7 s, 2,000 took
  229 s, 1,000 chained assignments over 15 minutes, all ON THE SCHEDULER THREAD until B. The LSL parse is now
  two-stage (SLL; LL only for a script SLL rejects, as ANTLR recommends - an SLL parse that succeeds is the tree LL
  would build), and a linear pre-pass bounds brace depth: `statement : funcBlock | funcBlockContent` is ambiguous for
  every `{`, so prediction read ahead to the matching `}` once per level. `NestingLimitTests` runs every case in a
  child process (`Tests/PhloxCompileProbe`), cold and warm, at N, N+1 and 200,000. `phlox21-deepnest.lsl` is now
  1,001 levels and fails by the counted limit in any state (proved warm in `Phlox21DeployScriptTests`).
- **B - the master scheduler never waits on a compile.** The loader compiled on a 16 MB thread but `Join()`ed it from
  DoWork on the master scheduler thread, so every script on the scheduler stopped for the whole compile.
  **Design:** ONE long-lived `Phlox compile` thread (16 MB stack) consumes a queue; DoWork hands a job over and
  returns; the thread saves the bytecode to the disk cache and posts the finished job back; a later DoWork starts it
  (`FinishedLoading`, state restore, on_rez/state_entry unchanged and still on the master thread).
  **Preserved:** rez order within a prim (a prim's later loads wait behind its outstanding compile); a re-save or
  removal while compiling discards the stale result (per-item generation - only the newest compile starts);
  llSetScriptState / the Running box and resets aimed at an item still compiling are applied when it starts; region
  shutdown drops queued compiles and throws away a running one, never half-started. `CompileOffSchedulerTests`:
  during a 3 s compile another script's 0.1 s timer fired 1 time before, 27 now, and a touch is handled.
- **C - compile errors reach the script editor.** `PhloxEngine.GetScriptErrors` answered an empty list at once, so
  the viewer said "compiled" for any script. It now waits (on the caps thread that answers the Save, never the
  scheduler's) for the compile of the item just saved, as YEngine's `GetScriptErrors` does, bounded by the region's
  own 15 s and its `timedout waiting for errors`, and answers YEngine's `(line,col) Error: message` for LSL and SLua.
  The owner alert stays for compiles no editor waits on (rez, restart, remote load); an editor save is not also
  alerted. `CompileErrorsToEditorTests`.
- **D - gives to an absent avatar; iwGetObjectMassMKS.** llGiveInventory, iwGiveLinkInventory and
  iwDeliverInventory passed a null client into `Scene.MoveTaskInventoryItem(IClientAPI, ...)` for an avatar not in
  the region, which threw at `Scene.Inventory.cs:1479`: nothing was delivered, iwDeliverInventory returned
  IW_DELIVER_PRIM. The list gives used `Scene.MoveTaskInventoryItems`, which gives up without a client. All six now
  deliver like YEngine's llGiveInventory (prim: task-to-task; an avatar here, with an account or online on the grid:
  the no-client overload, a list into a new folder; the notice to the client or through the IM transfer module), with
  Halcyon's codes (OK delivered, USER no such avatar, PERM/ITEM from the move). iwGetObjectMassMKS is
  100 x llGetObjectMass, as llGetMassMKS is 100 x llGetMass. `GiveToAbsentAvatarTests`.
- **D, llGiveInventoryList (follow-up).** llGiveInventoryList is the one give that does NOT reach an absent avatar.
  SL requires the avatar to be in, or able to see into, the region, or to have been there recently (SVC-868), and
  YEngine refuses too (`LSL_Api.cs:8185-8193`: no part and no `TryGetScenePresence` -> "we could check if it is a
  grid user and allow the transfer as in older code / but that increases security risk", then
  `Error("llGiveInventoryList", "Unable to give list, destination not found")`). Phlox mirrors it: a recipient with no
  presence here (child agents count - they see into the region) gets nothing, and the owner's script-error window
  gets `llGiveInventoryList: Unable to give list, destination not found` on DEBUG_CHANNEL. llGiveInventory still
  delivers anywhere (SL allows it for avatars); iwDeliverInventory / iwDeliverInventoryList keep Halcyon's
  deliver-anywhere and IW_DELIVER_* codes; gives to prims are unchanged. iwGiveLinkInventoryList was not in scope and
  still delivers anywhere. `GiveToAbsentAvatarTests` (absent: nothing and the error; present: the folder).
- **C, follow-up (DEPLOY-PHLOX-22 in-world, 2026-09-23 20:10).** On Ebony the editor showed no error for
  `phlox22-syntaxerror.lsl` or `phlox21-deepnest.lsl`, and the deep-nest error came as the owner pop-up
  (`line 8:1021 expression nested too deeply (limit 1000)`, item `793ce35b`: OnRezScript 20:10:48,869, failed
  20:10:48,886 - 17 ms; no `timedout waiting for script errors` in the log). **Cause:** the live region runs YEngine and
  Phlox, and YEngine joins the scene first, so `SceneObjectPartInventory.GetScriptErrors` (`:389-403`) asks YEngine
  first. YEngine's `GetScriptErrors` (`XMREngine.cs:1967`) `Monitor.Wait`ed with no timeout until an entry for the
  item appeared - for a script it declined in OnRezScript (`XMREngine.cs:1316-1324`) none ever does, and nothing
  pulses that lock. So every editor save of a Phlox script blocked its caps thread for good: no answer reached the
  viewer, CreateScriptInstanceEr's 15 s timeout could not fire (the thread was inside it), and Phlox was never asked,
  so no editor claimed the failure and Phlox sent the pop-up. PHLOX-22 C's tests registered Phlox alone.
  **Fix:** YEngine's `GetScriptErrors` answers an empty list at once when it has no entry for the item. It compiles
  synchronously in OnRezScript (`RegisterInstance -> LoadThreadWork`), so its own results are always posted
  first. **Also fixed:** a compile that failed before the editor's GetScriptErrors reached Phlox was published with
  no waiter, so the owner got the pop-up AND the editor got the errors. Now a failure that nothing is waiting for is
  held for `OwnerAlertGrace` (2 s), and an editor that collects it in that time claims it. A rez or restart
  still alerts once. `EditorErrorsWithYEngineTests` (a real YEngine on the harness scene, added first).
  Deploying this also ships `OpenSim.Region.ScriptEngine.YEngine.dll`. The syntax-error save left no trace in the
  region log (no OnRezScript for any item after 20:09:48 except `793ce35b`), so it never reached the region.
- **E - not done.** At the counted limit the quadratic code generation costs little (1,000 nested calls: 0.40 s cold,
  0.36 s warm, off the scheduler), and making GenVisitor emit into one StringBuilder touches ~120 methods under a
  byte-identical requirement. Left open with those timings as its baseline.

### Still open

- E: linear code generation, and TypesVisitor's quadratic step for nested calls (not located).
- iwGiveLinkInventoryList (Halcyon's link-number llGiveInventoryList) still delivers to an absent avatar; whether it
  follows llGiveInventoryList or iwDeliverInventoryList was not decided (DEPLOY-PHLOX-22 PART 0 left it as it was).
- Item `3eb0c62b` (asset b8079466) overloads a user function (`SetVehicleSettings()` and
  `SetVehicleSettings(string)`): YEngine accepts overloads by signature, SL and Phlox do not. Unchanged here.

### What residents will notice

- Compile errors show in the script editor's error pane, with line and column, like any other engine; the viewer no
  longer says a broken script compiled. Scripts rezzed or loaded at restart still tell the owner by alert.
- Saving a large or deeply nested script no longer pauses every other script in the region while it compiles.
- A script nested past a limit gets the same error on every region and after every restart:
  `expression nested too deeply (limit 1000)` and its siblings.
- Gifts to someone who is elsewhere on the grid or offline arrive in their inventory, with the usual notice -
  except llGiveInventoryList, which, as in SL and on YEngine regions, gives a folder only to someone in (or seeing
  into) the region and otherwise says `Unable to give list, destination not found` in the script-error window.
