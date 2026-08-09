# Tranquillity Experience Port — Pre-Port Audit v2

**Date:** 2026-07-21 · **Read-only. No code / build / DB / migration.**
**Supersedes** `experience-port-audit-v1.md` (2026-07-18), which predated both Legion's completion and the "Tranquillity is not a stub" finding.
**Legion source of truth:** `/d/legion-grid-source` @ tag `port-source-2026-07-21` (=`1e92caf85f`) — Experience COMPLETE/SL-compliant (39 VERIFIED / 0 OPEN / 11 documented deferrals; ledger `experience-parity-ledger.md`).
**Tranquillity target:** `/d/tranquillity-develop` @ `develop`.

---

## GATE 1 — DB isolation: ✅ **SAFE** (port may proceed)

Active runtime config traced from `Source/OpenSim.Server.RegionServer/bin/Release/net8.0/`:
- `OpenSim.ini:1294` → `Include-Architecture = config-include/Standalone.ini` (**Standalone**, not Grid — no Robust/GridCommon MySQL).
- `StandaloneCommon.ini` `[DatabaseService]:9` → `Include-Storage = config-include/storage/SQLiteStandalone.ini` **(active, uncommented)**.
- `SQLiteStandalone.ini:4-5` → `StorageProvider = OpenSim.Data.SQLite.dll`, `ConnectionString = URI=file:OpenSim.db` (+ Asset.db, inventory.db, auth.db, …) — **local SQLite files inside the tree**.
- Real `.db` files present in the runtime bin (OpenSim.db, Asset.db, auth.db, avatars.db, friends.db, griduser.db, inventory.db, userprofiles.db; dated Jul 3–4).
- The MySQL template (`StandaloneCommon.ini:18-19`) is **still commented** and points at `localhost` (**not** Docker's `opensim_mysql:3306`). Region storage = `OpenSim.Data.Null.dll:NullRegionData` (active). **Zero uncommented MySQL / 3306 / opensim_mysql / Docker references** anywhere in the active config-include.

**Verdict: SAFE** — the standalone is isolated on local SQLite; no path touches Docker MySQL or the Legion `opensim`/`opensim_web` schemas.

## GATE 2 — Legion source is the complete version: ✅ confirmed
Tag `port-source-2026-07-21` → `1e92caf85f`. Spot-checks pass: **14 caps** registered in `ExperienceModule.cs` (AgentExperiences, ExperiencePreferences, ExperienceQuery, FindExperienceByName, GetAdminExperiences, GetCreatorExperiences, GetExperienceInfo, GetExperiences, GetMetadata, GroupExperiences, IsExperienceAdmin, IsExperienceContributor, RegionExperiences, UpdateExperience); **8 bootstrap tables**; the D1 consent flow (`SendScriptQuestion(…experienceId)` + `RegisterPendingExperiencePerm`/`ResolveExperiencePerm`) present.

---

## PART A — What Tranquillity's Experience actually IS (deep inventory)

**Not a stub.** A complete, production-grade **NGC (InWorldz-lineage) Experience stack** with real storage and a Robust grid-service wire protocol. Provenance: scaffolded by **StolenRuby, 2024-08-20** (`26d3971448`, 62 files/6543 lines); last functional core change **2024-09-23** (`fea07bd0fd`); then **21 months of purely mechanical churn** by Mike Dickson (DI, NuGet, EF-model, restructures — zero Experience feature work); revived by **John (`JohnLegionH`) 2026-06-24** with `PhloxExperienceAdapter` (`7b7ff5a71f`) + Phlox.ScriptEngine (`557b526166`). So: **dormant-but-working storage/wire stack + John's fresh Phlox integration seam.**

| Component | What it is |
|---|---|
| **Interface** `IExperienceService.cs:92-110` | **10 coarse/batch methods**: `FetchExperiencePermissions(agent)→Dict<UUID,bool>`, `UpdateExperiencePermissions(agent,exp,ExperiencePermission)`, `GetExperienceInfos(UUID[])`, `GetAgentExperiences(agent)→UUID[]`, `UpdateExperienceInfo(info)`, `FindExperiencesByName(str)`, `GetGroupExperiences(group)`, `GetExperiencesForGroups(UUID[])`, + 6 KV (`GetKeyValue`/`CreateKeyValue`/`UpdateKeyValue`/`DeleteKey`/`GetKeyCount`/`GetKeys`). Real SQL-backed. |
| **Service** `ExperienceService.cs` + `ExperienceServiceBase.cs` | Delegates all to an `IExperienceData` plugin loaded from config. No stubs. |
| **Data / schema** `MySQLExperienceData.cs` (raw ADO.NET, **not EF for data access**) + `Resources/Experience.migrations` | **3 tables**: `experiences` (public_id, owner_id, name, description, group_id, logo, marketplace, slurl, **maturity {13/21/42}**, properties bitmask) · `experience_permissions` (experience, avatar, **`allow BIT(1)`** — true=allowed / false=blocked, ONE table for grant+block) · `experience_kv` (experience, `key VARCHAR(1011)`, `value VARCHAR(4095)`). Model POCOs in `OpenSim.Data.Model/Core/*.cs`; `EstateAllowedExperience`/`EstateKeyExperience` live in estate settings. |
| **Caps module** `LindenCaps/ExperienceModule.cs:120-140` | **13 of 14 SL caps** (all EXCEPT ExperienceQuery). Wire shapes SL-shaped: `experience_keys` (GetExperienceInfo/Find/Update — incl. maturity 13/21/42, expiration, extended_metadata, group_id, properties, agent_id), `experience_ids` (Agent/Admin/Creator/Group), `status` (IsAdmin/IsContributor), `{allowed,blocked,default,disabled,trusted}` (RegionExperiences), `{experiences,blocked}` (GetExperiences/ExperiencePreferences GET/PUT/DELETE). |
| **Wire protocol** Local + Remote connectors + `ExperienceServerPostHandler.cs` (`POST /experience`, dispatched by `METHOD`: getpermissions/updatepermission/getexperienceinfos/getagentexperiences/updateexperienceinfo/findexperiences/getgroupexperiences/getexperiencesforgroups/accesskvdatabase) | **Robust grid-service** — a real distributed-grid design. **Legion has NO equivalent** (Legion is per-region direct-MySQL, no Robust connector — Legion ledger OBS-1). |
| **KV store** | Synchronous service; per-experience quota **16 MiB** (`MAX_QUOTA = 1024*1024*16`), checked on create/update via `SUM(LENGTH(key)+LENGTH(value))`. |
| **Consent** `Phlox.ScriptEngine/LSLSystemAPI.cs` llRequestExperiencePermissions | **AUTO-GRANT — no dialog.** After block/admission/presence checks it calls `GrantPermission` directly and fires `experience_permissions`. (Same as Legion's *pre-D1* state.) |
| **PhloxExperienceAdapter** `Phlox.ScriptEngine/PhloxExperienceAdapter.cs` (John, 2026-06) | The reconcile seam. Principle (in-file): **"NGC is AUTHORITATIVE for storage and is never modified. All translation lives here."** 13 methods mapping Phlox/Legion-shaped script API (ReadKeyValue/CreateKeyValue/…/IsAgentGranted/GrantPermission/GetExperience/GetScriptExperience) → NGC `IExperienceService`/`IExperienceModule`; script→experience via **`TaskInventoryItem.ExperienceID`** (persisted inventory field, no in-memory cache), `InvalidatePermission` = no-op (NGC reads live). |

---

## PART B — Component comparison (Tranquillity vs Legion-complete)

| Area | Tranquillity | Legion (complete) | Classification |
|---|---|---|---|
| **Caps served** | 13/14 (no ExperienceQuery) | 14/14 (ExperienceQuery = no-op) | **≈ EQUIVALENT** — the one gap is Legion's *no-op* cap; trivially added |
| **Cap wire shapes** | SL-shaped (experience_keys/ids/status/…); GetExperienceInfo already emits 13/21/42 + expiration + extended_metadata | Same keys (Legion fixed these in Slice 0) | **EQUIVALENT** |
| **Consent** | **Auto-grant, no dialog** | **Real SL consent** (ScriptQuestion + await, Yes/No/Block, 300s timeout, code 18) | **LEGION-AHEAD** (the biggest gap) |
| **Interface signature** | 10 coarse/batch methods (NGC shape) | ~33 granular methods | **DIVERGENT** (incompatible; a full swap = connector rewrite) |
| **Schema** | 3 tables (single `allow BIT` permissions table; trusted in estate settings; kv 1011/4095) | 8 tables (separate granted/blocked/**trusted**/**agent_blocked**/allowed/keyvalue/**script_experiences**/experiences) | **DIVERGENT** (both cover the concepts; Tranq more compact, Legion more granular) |
| **Agent block** | Modeled in the single permissions table (`allow=false`) | Dedicated `experience_agent_blocked` table | **EQUIVALENT** (Tranq arguably cleaner) |
| **Trusted list** | In estate settings (`EstateKeyExperience`), surfaced in RegionExperiences `trusted` | `experience_trusted` table + enforcement | **DIVERGENT** — list EQUIVALENT; **enforcement** (bypass consent) LEGION-AHEAD |
| **Script→experience assoc** | `TaskInventoryItem.ExperienceID` (persisted inventory field) | `script_experiences` table + in-memory cache | **TRANQ-AHEAD** (simpler, survives restart natively) |
| **KV quota** | **16 MiB** (non-SL) | **128 MiB** + code 11 enforcement | **LEGION-AHEAD** (Tranq's limit is wrong) |
| **Script surface conformance** (error table 0-18, llGetExperienceDetails layout, key caps, land code 17) | StolenRuby-era Phlox (pre-Legion-fixes) | SS-1..9 conformance fixes applied | **LEGION-AHEAD** |
| **Wire/grid architecture** | **Robust grid-service** (Local/Remote connectors + server handler) | Per-region direct-MySQL, **no** Robust connector | **TRANQ-AHEAD** (real distributed-grid capability Legion lacks) |
| **KV empty-read / CAS-vs-empty** | NGC string-status returns | Legion's Slice-7 conservative choices | **DIVERGENT** (both defensible; unverifiable vs SL) |

**Net:** Legion is AHEAD on **conformance & consent** (consent, quota, script-surface fixes, trusted enforcement); Tranquillity is AHEAD on **architecture** (Robust wire protocol, inventory-based script assoc). The interface and schema are DIVERGENT. Crucially, **John already built the bridge** (`PhloxExperienceAdapter`) with the stated principle that NGC storage stays authoritative.

---

## PART C — Port strategy recommendation → **RECONCILE** (flag for John's decision)

**Recommend RECONCILE, not FULL REPLACE.** Extend John's existing `PhloxExperienceAdapter` seam: **keep NGC's storage + Robust wire protocol; port Legion's conformance LOGIC + consent + the missing behaviors into the Phlox/adapter/caps layer.**

**Why RECONCILE:**
1. **John already chose it.** `PhloxExperienceAdapter` exists with the explicit rule "NGC is authoritative for storage and is never modified; all translation lives here." The port is finishing what that seam started.
2. **Tranquillity's wire protocol is architecturally superior for a grid** — a full Robust service (Local/Remote/server-handler) that Legion *lacks*. FULL REPLACE would **delete a real capability** and regress NGC to Legion's per-region direct-MySQL (OBS-1).
3. **Divergent interfaces make FULL REPLACE a rewrite**, not a swap — Legion's ~33-method `IExperienceService` vs NGC's 10 coarse methods means replacing the connectors, server handler, and data layer wholesale.
4. **The real gaps are LOGIC, not storage.** The #1 gap (consent/D1) lives in the *script* layer (llRequestExperiencePermissions) and packet layer — portable without touching NGC storage. Quota is a *constant*. ExperienceQuery is a *no-op cap*. Script-surface conformance is *Phlox code*. Very little needs new schema.
5. **Schema-migration story favors RECONCILE decisively.** FULL REPLACE → migrate every Tranq deployment's 3-table data into Legion's 8-table schema (real data migration, risk on John's SQLite standalone). RECONCILE → **near-zero schema change**: trusted list already exists (estate settings), agent-block already exists (`allow=false`), script-assoc already exists (inventory). The only possible additive need is *trusted enforcement state* if not derivable — TBD in the plan; likely none.

**FULL REPLACE would only make sense if** John wanted to abandon NGC's grid architecture and standardize on Legion's simpler per-region model — which contradicts "port properly to match Legion's *conformance*" (conformance ≠ architecture) and throws away working distributed-grid code.

**Decision needed from John:** confirm **RECONCILE**. (If he instead wants FULL REPLACE, the plan changes to a schema-migration-first, connector-rewrite campaign — larger, riskier, and loses the Robust wire protocol.)

---

## PART D — Contingent slice plan (assuming RECONCILE)

Ordered, individually code-path-verifiable slices (Option-C discipline: **MySQL-only, verified by code-path analysis against the complete Legion source, NEVER executed on John's SQLite tree**). Each cites the Legion reference and the Tranquillity target.

**GATE-PORT (do first, like Legion's D1 gate): consent packet capability.** Verify Tranquillity's LibOMV (the NuGet LibOMV from `#172`) exposes `ScriptQuestionPacket.Experience` (ExperienceBlock/ExperienceID) + `ScriptAnswerYesPacket` — the D1 consent flow's hard dependency. Reflection-inspect the Tranquillity OpenMetaverse assembly. If absent → consent slice is blocked on a LibOMV fix (report to John before any consent code). *(This mirrors the Legion D1 gate that passed.)*

**Slice T1 — script-surface conformance (Phlox).** Port Legion's SS-1..9 fixes into Tranquillity's Phlox `LSLSystemAPI`: `llGetExperienceDetails` SL layout, KV key-cap 1011, `llDeleteKeyValue` code 14, `llAgentInExperience` root-presence+HasExperiencePermission, error table 0-18, land code 17. *Ref:* Legion `LSLSystemAPI.cs` (SS rows). *Target:* Tranq `Phlox.ScriptEngine/LSLSystemAPI.cs`. *Verify:* code-path vs Legion. *Deploy:* Phlox only. *Schema:* none.

**Slice T2 — KV quota 16→128 MiB + code 11 (adapter/service).** Change NGC's `MAX_QUOTA` to 128 MiB and confirm enforcement + `DataSizeKeyValue` returns `used,total`; port Legion's Slice-5 pre-write check emitting code 11. *Ref:* Legion Slice 5. *Target:* NGC `ExperienceService.cs:18` (constant) + Phlox KV functions. *Schema:* none.

**Slice T3 — consent (D1), the big one.** Replace auto-grant with the ScriptQuestion+await flow through the adapter: `SendScriptQuestion(…experienceID)` overload, pending-map + 300s timeout (code 18), `ScriptAnswerYes` correlation → Grant/Deny, trusted-bypass. Route grant/block persistence through `PhloxExperienceAdapter`→NGC `UpdateExperiencePermissions`. *Ref:* Legion D1 (`9315d95324`) + Slice 5 timeout. *Target:* Tranq Phlox client stack + adapter. *Gate:* GATE-PORT. *Schema:* none (NGC permissions table already holds allow/block). *Verify:* code-path.

**Slice T4 — ExperiencePreferences ↔ consent Block loop.** Confirm the Block button PUT persists via NGC `UpdateExperiencePermissions(Block)` and the early-denial reads it (adapter `IsAgentBlocked`). Tranquillity's single `allow BIT` table already models this — likely just wiring + ordering (block-before-grant). *Ref:* Legion Slice 2 CAP3. *Schema:* none.

**Slice T5 — trusted enforcement.** Wire trusted (estate `KeyExperiences`) into admission + consent-bypass in the adapter/Phlox layer. *Ref:* Legion CAP-RE-TRUST-ENF. *Schema:* trusted LIST already exists in estate settings; **flag** whether any per-experience trusted-enforcement state is needed (expected: none — derive from estate KeyExperiences).

**Slice T6 — ExperienceQuery no-op cap + IsExperienceContributor parity.** Add the 14th cap as Legion's documented no-op (Tranquillity's per-agent EEP injection status TBD — same D-EEP question; likely no-op). Confirm IsExperienceContributor cap (Tranq already serves it). *Ref:* Legion Slice 6. *Schema:* none.

**Slice T7 — acquire policy (DEC-3).** Port the grid-config acquire (`AgentExperiences` POST → create via NGC `UpdateExperienceInfo`, gated by estate-managers+region-owners). *Ref:* Legion Slice 6. *Schema:* none.

**Slice T8 — SL-UNVERIFIED tail parity.** Align Tranquillity's KV empty-read / CAS-vs-empty / Keys clamps / ladder tie-breaks / root-presence to Legion's Slice-7 documented choices (or document Tranq's NGC-string-status equivalents). *Ref:* Legion Slice 7.

**Schema-migration slice: NOT REQUIRED under RECONCILE** (this is the key advantage). If a later decision needs it, it would be *implemented-to-match + inspection-verified, NEVER run on John's tree* — but the reconcile plan is expected to need **zero** schema migration (trusted, agent-block, script-assoc all already exist in NGC's model). Any additive column that surfaces gets its own flagged slice for John's approval before writing a migration.

**Verification for every slice (Option C):** code-path analysis against the complete Legion source at `port-source-2026-07-21`; no execution against SQLite or MySQL on John's tree; matched-set/DLL implications noted per slice (most are Phlox-only or Phlox+adapter; none needs the NGC connector/server rewrite that FULL REPLACE would).

---

## Open questions for John

1. **Strategy confirmation:** RECONCILE (recommended) vs FULL REPLACE? *(Recommendation: RECONCILE — preserves NGC's Robust wire protocol + your adapter seam; near-zero schema migration.)*
2. **GATE-PORT / LibOMV:** OK to reflection-inspect Tranquillity's NuGet LibOMV for the ScriptQuestion Experience block before scheduling the consent slice? (If it lacks the block, consent is blocked on a LibOMV fix.)
3. **Quota value:** confirm 128 MiB (SL-correct) over NGC's current 16 MiB — any deployments relying on 16?
4. **Consent model:** confirm Tranquillity should adopt real SL consent (matching Legion/D1) vs keeping NGC auto-grant. *(Real consent is the single biggest conformance gap; matches "port properly to match Legion.")*
5. **Trusted enforcement source:** confirm trusted derives from estate `KeyExperiences` (no new table) — or does John want a dedicated table mirroring Legion's `experience_trusted`?
6. **D-EEP for Tranquillity:** same as Legion — is per-agent EEP environment injection real on Tranq, or is ExperienceQuery a no-op here too? *(Expected: no-op.)*
