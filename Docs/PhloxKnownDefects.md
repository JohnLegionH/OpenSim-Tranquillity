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

