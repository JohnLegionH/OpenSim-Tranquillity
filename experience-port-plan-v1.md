# Experience Port Plan v1 (Option C: faithful Legion MySQL port)

Date: 2026-07-19 · Read-only planning output; no code changed.
Legion source: tag `port-source-2026-07-18` @ `slua-tier2-tables`.
Target: `tranquillity-develop` @ `develop` (`f17a4e0f10`).
Decided: port Legion's MySQL Experience service faithfully, MySQL-only, code-path-verified against live Legion, never executed on this tree. No SQLite variant, no DB execution in any slice.

## Headline finding that reshapes the plan

**Tranquillity's Experience stack is NOT a stub.** The earlier audit's "minimal stub" assessment was wrong. It is a complete StolenRuby/NGC implementation: full `IExperienceData`→`MySQLExperienceData` data layer (v3/v4 migrations), a working `ExperienceService`, an `ISharedRegionModule` caps module with **13 viewer caps** and a permission cache, a **real HTTP wire protocol** (client connector + server POST handler, 9 METHOD verbs), and `PhloxExperienceAdapter` already wiring Phlox's KV functions (sync) to it. Option C therefore **replaces working functionality**, and some NGC capabilities have no Legion equivalent (see Risks / Open Questions). This plan executes Option C as decided but flags every regression explicitly.

## 1. Legion source inventory (what gets ported)

| File | Role | Key surface |
|---|---|---|
| `OpenSim/Services/Interfaces/IExperienceService.cs` | Interface | 29 methods: CRUD (7), permissions (6), KV (7), region allow/block (6), script-association persistence (3) |
| `OpenSim/Services/Interfaces/ExperienceInfo.cs` | DTO + constants | PascalCase fields; `PROP_*`; `XP_ERROR_0..18`; KV limits (key 1011, value 4095, quota 128 MiB) |
| `OpenSim/Services/ExperienceService/ExperienceService.cs` | MySQL service | `ExperienceService(string connectionString)`; idempotent 6-table bootstrap (`experiences`, `experience_permissions`, `experience_keyvalue`, `experience_allowed`, `experience_blocked`, `script_experiences`); **no data-layer abstraction — SQL inline** |
| `OpenSim/Region/CoreModules/Experience/ExperienceModule.cs` | Region module | `INonSharedRegionModule`; **directly constructs** the service (`new ExperienceService(connStr)` at AddRegion:103) and `RegisterModuleInterface<IExperienceService>`; caps `RegionExperiences`, `GetExperienceInfo`, `FindExperienceByName`; `OnEstateExperienceDelta`; public: `CheckPermission`, `InvalidatePermission`, `GetScriptExperience`, `SetScriptExperience` |
| Phlox `LSLSystemAPI.cs` 11352–11583 | KV 610–617 | **Async dataserver pattern**: return request-UUID immediately, `Task.Run` → sync service call → `PostScriptEvent("dataserver", "1,<result>"/"0,<XP_ERROR>")`; incl. Legion-only `llClearKeyValue` (617) |
| Phlox `LSLSystemAPI.cs` 12507–12742 | 659–661 + helpers | `llRequestExperiencePermissions`, `llAgentInExperience`, `llGetExperienceDetails`; `HasExperiencePermission`, region/parcel admission helpers (block-wins > region/grid/parcel-allow) |
| `bin/OpenSim.ini.example` `[Experience]` | Config | `Enabled`, `ConnectionString` (region-local direct MySQL) |

**Critical architecture fact: Legion has NO connectors and NO Robust-side handlers.** The service is region-local, in-process, direct-to-MySQL. There is no remote/grid wire for Experience in Legion at all.

Related but separately-tracked Legion work this plan does NOT cover (needs its own recon before its slice): the parcel experience access-entry storage + block-wins land enforcement (`LAND/ESTATE AUDIT #18`, `EXP-PRECEDENCE-1`, `EXP-REGIONBLOCK-1`) which lives in LandManagementModule/LandObject/land data, not in the files above.

## 2. Tranquillity target inventory — fate per file under Option C

| File | Currently | Fate |
|---|---|---|
| `Services.Interfaces/IExperienceService.cs` | 15 sync methods, snake_case `ExperienceInfo`, `ExperiencePermission`/`ExperienceFlags` enums | **REPLACED** (atomic — same type names, same namespace) |
| `Services.ExperienceService/ExperienceService.cs` + `ExperienceServiceBase.cs` | Working impl + plugin loader + 2 console cmds | **REPLACED** / **DELETED** (Legion has no Base; console cmds lost — flag) |
| `Data/IExperienceData.cs` | Data abstraction (16 methods) | **DELETED** (Legion embeds SQL in the service) |
| `Data.MySQL/MySQLExperienceData.cs` + `Resources/Experience.migrations` | Working MySQL layer, v3/v4 (3 tables) | **DELETED** (bootstrap replaces migrations; schema mapping doc required — see Risks) |
| `Data.Model/Core/{Experience,ExperienceKVP,ExperiencePermission,EstateAllowedExperience,EstateKeyExperience}.cs` | EF entities, **no consumers found** | **DELETE (recommended)** or leave orphaned — John decides |
| `Region.ClientStack.LindenCaps/ExperienceModule.cs` | `ISharedRegionModule`, 13 caps, perm cache, implements `IExperienceModule` | **DELETED**, superseded by Legion's module in `Region.CoreModules/Experience/` — **regression: 13 caps → 3** (flag) |
| `Region.CoreModules/ServiceConnectorsOut/Experience/{Local,Remote}...Connector.cs` | Working local/remote connectors | **DELETED** (no Legion equivalent) |
| `Services.Connectors/Experience/ExperienceServicesConnector.cs` | HTTP client, 9 wire verbs | **DELETED** — **grid-mode remote Experience ceases to exist** (flag) |
| `Server.Handlers/Experience/{ExperienceServerConnector,ExperienceServerPostHandler}.cs` | HTTP server side | **DELETED** (same) |
| `Region.CoreModules/PluginRegistration.cs:110-111` | registers both connectors | **MODIFIED**: remove 2 lines, add Legion `ExperienceModule` registration (NGC uses explicit registration; Legion's `[Extension]` attribute alone may not load here — adaptation point) |
| `Region.Framework/Interfaces/IExperienceModule.cs` | 25+ method region interface | **DELETED** (Legion exposes the concrete module + `IExperienceService`); consumers: Scene property + caps module + adapter — all touched anyway |
| `Region.Framework/Scenes/Scene.cs:303,627-644` | lazy `ExperienceModule` property | **MODIFIED** (property removed or retargeted; verify no other consumers at slice time) |
| `Phlox.ScriptEngine/PhloxExperienceAdapter.cs` | John's seam: Legion-LSL-shape ↔ NGC service/module | **DELETED under faithful port** (Legion's LSL code talks to `IExperienceService`/module directly) — or retained as a thin pass-through; John decides (default: delete) |
| `Phlox.ScriptEngine/LSLSystemAPI.cs` 11331–11480, 12450–12500 | KV 610–616 **sync int-returning**, no 617 wired to async, perm helpers via adapter | **REPLACED** with Legion's async dataserver bodies + helpers |
| `InWorldz.Phlox/Types/Defaults.cs` (+ISystemAPI/SyscallShim) | KV FunctionSigs with **Integer** returns | **MODIFIED**: return types → String for 610–613, 617 etc. — **script-ABI change**, see Risks |
| `AppData/OpenSim.ini.example` | no `[Experience]` | **MODIFIED**: add section — `Enabled = false` and an obviously-fake ConnectionString placeholder (Phase-0 rule: never the localhost/opensim template) |

## 3. Per-component delta

- **Interface**: 15 sync methods → Legion's 29; naming (`GetKeyValue`→`ReadKeyValue`, `DeleteKey`→`DeleteKeyValue`, string-status returns → bool), new domains (region allow/block, script persistence, `GetExperienceByName`, `DeleteExperience`); NGC-only members with NO Legion equivalent: group experiences (`GetGroupExperiences`, `GetExperiencesForGroups`), `FetchExperiencePermissions` bulk map.
- **DTO**: snake_case `public_id/owner_id/...` + `ToDictionary()` (consumed by the 13 caps) → PascalCase `ExperienceId/OwnerId/...` + `XP_ERROR_*`.
- **Schema**: v3/v4 3 tables → 6-table bootstrap. Column mapping for anyone with populated stub tables: `experiences.public_id→experience_id` (+ new `created/updated`), `experience_permissions(experience,avatar,allow BIT)→(experience_id,agent_id,granted)`, `experience_kv(experience,key,value)→experience_keyvalue(experience_id,kv_key,kv_value)`; `experience_allowed/blocked/script_experiences` are new. Delivered as a **documented one-time SQL mapping script — written, never executed by us**.
- **KV LSL surface**: sync `int llCreateKeyValue(...)` → async `string` returning request-UUID + dataserver CSV payloads with XP_ERROR codes; adds `llClearKeyValue` (617). Requires Phlox `FunctionSig` return-type changes (ABI).
- **Module lifetime**: `ISharedRegionModule` (one instance, all scenes) → `INonSharedRegionModule` (per region, per-region service instance and DB connection).
- **Wire contract**: NGC's 9-verb POST protocol → **none**. Grid deployments would run region-local DB connections instead (every simulator needs Experience DB credentials — Legion's model).

## 4. Ordered sub-slices

The naive Option C is one enormous atomic commit (same type names ⇒ old and new interfaces cannot coexist). Recommended decomposition keeps every slice buildable by **renaming the legacy types first**:

| Slice | Content | Depends | Builds alone? | Verification (no execution) |
|---|---|---|---|---|
| **E0** | `[Experience]` ini section (disabled, fake placeholder) + this plan's schema-mapping doc | — | yes (config only) | ini diff review vs Legion's section + Phase-0 rules |
| **E1a** | Mechanical rename of NGC types: `IExperienceService→ILegacyExperienceService`, `ExperienceInfo→LegacyExperienceInfo` (+usages across the 10 consumer files) | E0 | yes — pure rename, zero behavior | build green; `git grep` count parity old vs new names |
| **E1b** | Add Legion's `IExperienceService`, `ExperienceInfo`, `ExperienceService` as new files (no consumers yet) | E1a | yes — dead code until wired | side-by-side diff vs Legion tag files (must be byte-faithful modulo namespace layout) |
| **E1c** ("the swap", still the biggest) | Add Legion `ExperienceModule` (CoreModules/Experience, `INonSharedRegionModule`, PluginRegistration entry); **delete** legacy caps module, both connectors, HTTP client, server handlers, `IExperienceData`/`MySQLExperienceData`/migrations, `ExperienceServiceBase`, `IExperienceModule`, Scene property, PluginRegistration connector lines; retarget or delete `PhloxExperienceAdapter` | E1b | yes IF complete — this is the **atomic slice**: any survivor referencing legacy types breaks the build, which is itself the completeness check | blast-radius grep: zero refs to `ILegacyExperienceService`/`LegacyExperienceInfo` remain ⇒ delete the renamed files in the same commit; caps list diff vs Legion module |
| **E2** | Phlox LSL surface: KV 610–617 async bodies + XP_ERROR payloads + `llClearKeyValue`, 659–661 + admission helpers; `Defaults.cs`/`ISystemAPI`/`SyscallShim` signature updates | E1c | yes | per-function side-by-side vs Legion tag (incl. dataserver post lines); FunctionSig table diff; bytecode-cache flag check |
| **E3** | Estate delta + caps parity check (`estateexperiencedelta` handler lands with E1c's module; this slice verifies the wiring + `OnNewClient`) | E1c | yes | event-wiring trace vs Legion module lines 125–190 |
| **E4** | Parcel/region enforcement (land-side): needs its OWN recon first (#18 storage, PRECEDENCE-1, REGIONBLOCK-1 touch LandManagementModule/LandObject/land data) | E1c, E2 | yes (expected) | recon then per-hunk diff vs Legion tag |
| **E5** | EXP-PERSIST-1 verification sweep + Legion's experience console/test commands | E1c, E2 | yes | trace: script load-on-miss → `GetScriptExperiencePersisted` → cache, vs Legion |

Every slice: build 0 errors, committed unpushed, code-path-verified against the tag per the table. No slice runs anything, no slice writes a DB.

## 5. Risk flags

1. **Script-visible ABI change (E2), highest risk.** KV function return types change Integer→String in `Defaults.cs` FunctionSigs. Phlox caches compiled bytecode; scripts compiled against the old sigs may mis-stack after the swap. Verification: how Legion handled it (Legion never shipped the sync versions to users — Tranquillity DID, on John's standalone). Mitigation to investigate in E2: bytecode-cache invalidation/version bump. **Genuine unknown — code-path analysis alone may not settle it; flag for extra scrutiny.**
2. **Functionality regressions (E1c).** Viewer caps 13→3 (lost: UpdateExperience, ExperiencePreferences, admin/contributor queries, GetMetadata, agent/creator lists, GroupExperiences); group experiences gone; the entire remote/grid wire gone; NGC console cmds gone. Code-path verification is sufficient to prove the *port* faithful; it cannot make the losses smaller. **John must sign off knowingly (Open Questions).**
3. **Wire-protocol deletion is one-way for grid users.** Anyone running NGC grid-mode Experience (Robust handler) loses it. Standalone (John) unaffected.
4. **Schema migration for populated stub tables.** John's standalone: Phase 0 showed SQLite only, MySQL never configured ⇒ stub MySQL tables almost certainly never created for him ⇒ mapping script is documentation for upstream users, not a John-blocker. Verification: sufficient (it's SQL text we never run).
5. **NGC plugin registration.** Legion's `[Extension]` attribute vs NGC's `PluginRegistration.RegisterByName` — the module must be added to NGC's registry or it silently never loads. Code-path verifiable (compare how Phlox/other CoreModules register). Not an unknown, but an easy silent-failure.
6. **EF model orphans.** The 5 entities are consumed by nothing today; deleting is clean but touches NGC's EF surface (Mike's #132/#133 work) — relevant if John ever merges back upstream.
7. **Two `ExperienceModule` classes** (Legion's CoreModules one vs the deleted LindenCaps one) — same class name, different namespaces; fine after deletion, but any stale config referencing the old module id (`ExperienceModule` in ini [Modules]?) needs the E1c sweep.

## 6. Open questions for John

1. **Accept the E1c regressions?** Specifically: 13→3 viewer caps (the SL viewer's full Experience profile/preferences UX stops working), loss of group experiences, loss of grid-mode remote Experience. Alternative (NOT planned, per Option C): a later hybrid slice re-adding NGC's extra caps on top of Legion's service. Decide before E1c, not after.
2. **`PhloxExperienceAdapter`: delete (faithful) or keep as a thin seam?** Faithful deletes it; keeping it would localize any future backend change to one file. Default in this plan: delete.
3. **EF entity models: delete or leave orphaned?** Default: delete in E1c.
4. **Legacy console commands** (`create experience`, `suspend experience`) die with the NGC service; Legion's equivalents arrive in E5. Gap acceptable in between?
5. **Bytecode-cache handling for the E2 ABI change** — accept a one-time cache flush on John's standalone (scripts recompile), or investigate a versioned cache first?
6. The schema-mapping SQL doc: write it in E0 (as planned) even though John's own tree has no populated stub tables?
