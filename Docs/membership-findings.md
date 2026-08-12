# Membership-tiers feasibility audit — findings

**Anchor commit:** `git rev-parse HEAD` = `3fc320d17e3a6497d2da7239fe9cfb7afba65070`
**Branch:** `feature/membership-tiers` (cut off `tranq-baseline`)
**Date:** 2026-08-11
**Scope:** READ-ONLY. Every `file:line` below is from **this tree** (`D:\tranquillity-develop`,
Tranquillity @ `81e5c244` + 13 local commits). Nothing was read from or cited to `/d/legion-grid-source`
or NGC master — those were derived fresh. Claims marked ★ differ from a naive expectation or refine an
initial reading; the audit prefers surfacing those now.

---

## 1. Group-join cap — **REFUTED** (no server-side cap exists)

There is **no** server-side enforcement of a maximum group count per agent.

- `MaxAgentGroups = 60` is defined at `Source/OpenSim.Framework/Constants.cs:35` — but it is used **only for
  advertising**, never for enforcement:
  - login response (see §4), and
  - `Source/OpenSim.Region.ClientStack.LindenCaps/SimulatorFeaturesModule.cs:142-144`
    (`MaxAgentGroups` / `…Basic` / `…Premium`).
- The canonical join/add-membership entry point does **no** count or reject:
  `Addons/OpenSim.Addons.Groups/Service/GroupsService.cs:423` `AddAgentToGroup(...)` simply calls
  `_AddAgentToGroup(...)` and `return true;` — no membership count, no cap check (verified: lines 423-430).
  The protected `_AddAgentToGroup` (~`:813`) only checks *already-in-this-group*, not the agent's total.
- Region-side `JoinGroupRequest` (`Addons/OpenSim.Addons.Groups/GroupsModule.cs:~1120`) checks group
  existence + membership fee, then calls `AddAgentToGroup` — no total-count gate.

**Where a cap would have to go:** server-side, in `GroupsService.AddAgentToGroup`
(`…/Service/GroupsService.cs:423`) — count the agent's memberships (`m_Database.RetrieveMemberships(AgentID)`)
and reject before `_AddAgentToGroup`. The invite-accept path (`AddAgentToGroupInvite`, same file) needs the
same gate. **★ Note:** there is a **second** groups stack — `Source/OpenSim.Region.OptionalModules/Avatar/
XmlRpcGroups/` — so if that one is ever enabled instead, a cap must be duplicated there. The active stack is
`Addons/OpenSim.Addons.Groups`.

---

## 2. Attachment cap — **CONFIRMED**, and per-agent is feasible with no signature change

- Constant: `Source/OpenSim.Framework/Constants.cs:33` → `public const int MaxAgentAttachments = 38;`
- Enforced in `Source/OpenSim.Region.CoreModules/Avatar/Attachments/AttachmentsModule.cs:690-694`:
  `if (attachments.Count - toRemove.Count >= Constants.MaxAgentAttachments) { …Warn…; return false; }`
- Method: `AttachObjectInternal(IScenePresence sp, SceneObjectGroup group, uint attachmentPt, bool silent,
  bool addToInventory, bool resumeScripts, bool append, UUID experience)` (`:600`).
- **★ Agent identity IS in scope at the check** — `sp` (the `IScenePresence`) is the first parameter, so
  `sp.UUID` is available at line 690. A per-agent cap (dictionary / per-account lookup keyed on `sp.UUID`)
  needs **no** signature change. **CONFIRMED.**

---

## 3. SimulatorFeatures — **CONFIRMED** (per-request copy + agent-scoped delegate; no result cache)

`Source/OpenSim.Region.ClientStack.LindenCaps/SimulatorFeaturesModule.cs`:
- `m_features` is a shared template `OSDMap` (`:66`), populated once under `lock (m_features)` (`:128`+).
- The request handler builds a **per-request deep copy** (`OSDMap copy = DeepCopy();` ~`:282`; `DeepCopy()`
  serializes+deserializes `m_features` ~`:262-270`) — it does **not** hand back the shared map.
- It invokes the event **with the requesting agent's UUID**:
  delegate `SimulatorFeaturesRequestDelegate(UUID agentID, ref OSDMap features)`
  (`Source/OpenSim.Region.Framework/Interfaces/ISimulatorFeaturesModule.cs:33`); event fired
  `sd?.Invoke(caps.AgentID, ref copy)` (~`:299-307`), then serialized straight to the response (~`:310`).
- **Nothing caches the built result per-region** — each request gets a fresh copy; per-agent variation is
  not defeated. **CONFIRMED.**
- **★ Caveat for the feature:** `MaxAgentGroups` is baked into the template at build time from the grid-wide
  constant (`:142-144`), so per-agent group limits require a subscriber to `OnSimulatorFeaturesRequest` that
  mutates `copy["MaxAgentGroups"]` (and the Basic/Premium variants) using `agentID`. That delegate is the
  clean injection seam.

---

## 4. LLLoginService max-agent-groups — **CONFIRMED grid-wide**; clean per-account seam exists

`Source/OpenSim.Services.LLLoginService/`:
- Sourced grid-wide: `LLLoginService.cs` defaults `m_MaxAgentGroups = Constants.MaxAgentGroups;` (`~:189`)
  with a `[Groups] MaxAgentGroups` config override (`~:190-192`). Passed to the response via the
  **constructor** (`~:630`).
- Written into the response: `LLLoginResponse.cs` `responseData["max-agent-groups"] = MaxAgentGroups;`
  (`~:530`, Hashtable) and `map["max-agent-groups"] = OSD.FromInteger(MaxAgentGroups);` (`~:662`, OSD).
- **Cleanest per-account injection (no constructor change):** `LLLoginResponse` exposes a **settable public
  property** `public int MaxAgentGroups { get; set; }` at `LLLoginResponse.cs:1096-1099`. In
  `LLLoginService.Login()` the `UserAccount account` is in scope, so after the response is constructed
  (`~:630`) set `response.MaxAgentGroups = <per-account value>;`. **CONFIRMED.**
- **★ Two advertising paths, one constant.** The login response (this section) **and** SimulatorFeatures
  (§3) both advertise the group cap, both from `Constants.MaxAgentGroups` (60). A per-account tier must
  inject in **both** places (the login property here + the SimFeatures delegate in §3) or the two will
  disagree and the viewer may use either.

---

## 5. IMoneyModule blast radius — **CONFIRMED: three implementers**

- Interface: **`Source/OpenSim.Framework/IMoneyModule.cs`** (★ note: `OpenSim.Framework`, **not**
  `Region.Framework/Interfaces` as one might assume). Charge members: `int UploadCharge { get; }` (`:46`),
  `int GroupCreationCharge { get; }` (`:47`).
- **Implementers (3 — verified via `class … : IMoneyModule`):**
  | Class | File | In `Tranquillity.sln`? |
  |---|---|---|
  | `SampleMoneyModule` | `Source/OpenSim.Region.OptionalModules/World/MoneyModule/SampleMoneyModule.cs:55` | Yes (OptionalModules) — `UploadCharge`/`GroupCreationCharge` return 0 |
  | `DTLNSLMoneyModule` | `Source/OpenSim.Region.OptionalModules/World/Currency/DTLNSLMoneyModule.cs:177` | Yes (OptionalModules) — return `PriceUpload` / `PriceGroupCreate` |
  | `GloebitMoneyModule` | `Addons/Gloebit.GloebitMoneyModule/GloebitMoneyModule.cs:100` | Yes (Gloebit project) — return `PriceUpload` / `PriceGroupCreate` |
- **Consumers of the charge members (the blast radius):**
  - `UploadCharge`: `Source/OpenSim.Region.ClientStack.LindenCaps/BunchOfCaps/BunchOfCaps.cs:477`
    (`baseCost = mm.UploadCharge`); `Source/OpenSim.Region.CoreModules/Agent/AssetTransaction/
    AssetTransactionModule.cs:263` (`UploadCovered(agentId, mm.UploadCharge)`).
  - `GroupCreationCharge`: **★ two group stacks read it** —
    `Addons/OpenSim.Addons.Groups/GroupsModule.cs:914,926,927` **and**
    `Source/OpenSim.Region.OptionalModules/Avatar/XmlRpcGroups/GroupsModule.cs:1028,1031,1035`
    (both: `AmountCovered(...)` gate + `ApplyCharge(..., GroupCreationCharge, GroupCreate, name)`).
- **Blast-radius takeaway:** a per-account upload/group charge must account for **3 money backends** and
  **2 group modules** — and the charge is a plain interface **getter** (no agent in scope), so per-account
  pricing can't be done by reading `UploadCharge`/`GroupCreationCharge` alone; it needs a change at the
  **consumer** call sites (which do have the agent UUID) or a new per-agent charge member. **CONFIRMED
  (>1 implementer).**

---

## 6. UserTitle / CharterMember — **CONFIRMED flow; PARTIAL on "safe to write"**

**Flow (verified `Source/OpenSim.Region.CoreModules/Avatar/UserProfiles/UserProfileModule.cs:124-185`):**
- `:132-135` — if `acc.UserTitle` is empty → `membershipType[0]` = `(UserFlags & 0x0f00) >> 8` (the SL
  account-type byte); else → `membershipType = Utils.StringToBytes(acc.UserTitle)`.
- `:181` `client.SendAvatarProperties(..., membershipType, ...)` →
  `Source/OpenSim.Region.ClientStack.LindenUDP/LLClientView.cs:3303`
  `avatarReply.PropertiesData.CharterMember = membershipType;` → the viewer's profile "account type" line.
- `UserTitle` is defined on the account: `Source/OpenSim.Data.Model/Core/UserAccount.cs:21` and
  `Source/OpenSim.Services.Interfaces/IUserAccountService.cs:91` (+ ctor parse `:142`); DB column
  `Source/OpenSim.Data.MySQL/Resources/UserAccount.migrations` (`UserTitle varchar(64)`).

**Writers of `.UserTitle` (verified grep):**
| File:line | Kind | Sets to |
|---|---|---|
| `Source/OpenSim.Server.Handlers/UserAccounts/UserAccountServerPostHandler.cs:315` | **persistent — admin/API** | value from the account-store POST |
| `Source/OpenSim.Region.CoreModules/Avatar/UserProfiles/UserProfileModule.cs:1865` | in-memory, HG-visitor path | `"HG Visitor"` |
| `Source/OpenSim.Services.UserAccountService/UserAccountService.cs:220` and `:222` | **DB-load population** (deserialize) | from DB dict, or `string.Empty` |

**★ PARTIAL — writing UserTitle for a tier is workable but has three sharp edges (this refines an initial
"no collision" reading):**
1. **Single contended field.** `UserTitle` is one string already owned by (a) the admin-set-title API
   (`:315`) and (b) the HG-Visitor label (`:1865`). A tier writer is last-writer-wins against those — it
   would overwrite an admin-set title, and would need to not clobber "HG Visitor" for foreign agents.
2. **★ The profile is CACHED.** `UserProfileModule.cs:166` stores `membershipType` in `m_profilesCache`
   with `PROFILECACHEEXPIRE`. A change to `UserTitle` will **not** show until that per-user cache entry
   expires or is invalidated — the feature must force a refresh, not just write the field.
3. **Alternative exists.** The `0x0f00` `UserFlags` path (`:133`) is the SL-native account-type byte; a tier
   could ride those flag bits instead of the free-text `UserTitle`, avoiding the string contention (at the
   cost of only encoding a small enum). Design choice, flagged.

`CharterMember` is the **wire field** (`AvatarPropertiesReplyPacket.PropertiesData.CharterMember`, a
`byte[]`); `UserTitle`/the flag byte is the **source**. The viewer renders whatever bytes arrive.

---

## 7. Experience subsystem — real inventory (this tree; reconciled vs upstream)

| Component | Path | Class / note |
|---|---|---|
| Service iface | `Source/OpenSim.Services.Interfaces/IExperienceService.cs` | `IExperienceService` |
| Service | `Source/OpenSim.Services.ExperienceService/ExperienceService.cs` (+ `ExperienceServiceBase.cs`) | reads `[DatabaseService]`/`[ExperienceService]`, loads `IExperienceData` |
| Robust handler | `Source/OpenSim.Server.Handlers/Experience/ExperienceServerConnector.cs` | ★ **file** `ExperienceServerConnector.cs` but **class `ExperienceServiceConnector : ServiceConnector`** (`:10`) — the `[ServiceList]` name is **`ExperienceServiceConnector`** (the upstream rename from `ExperienceServiceServerConnector` is present here) |
| Robust POST handler | `Source/OpenSim.Server.Handlers/Experience/ExperienceServerPostHandler.cs` | `ExperienceServerPostHandler`, `/experience` verbs |
| Region remote connector | `Source/OpenSim.Services.Connectors/Experience/ExperienceServicesConnector.cs` | `ExperienceServicesConnector` |
| Region local module | `Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Experience/LocalExperienceServiceConnector.cs` | `LocalExperienceServicesConnector` |
| Region remote module | `Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Experience/RemoteExperienceServiceConnector.cs` | `RemoteExperienceServicesConnector` (`Modules` → `ExperienceServices = "RemoteExperienceServicesConnector"`) |
| Data iface | `Source/OpenSim.Data/IExperienceData.cs` | `IExperienceData` |
| Data impl | `Source/OpenSim.Data.MySQL/MySQLExperienceData.cs` | `MySqlExperienceData` (only impl — see §8) |
| Migrations | `Source/OpenSim.Data.MySQL/Resources/Experience.migrations` | v3: `experiences`,`experience_permissions`; v4: `experience_kv` |
| csproj | `OpenSim.Services.ExperienceService.csproj`, `OpenSim.Server.Handlers.csproj`, `OpenSim.Services.Connectors.csproj`, `OpenSim.Data.MySQL.csproj`, `OpenSim.Data.csproj` | — |

**.ini wiring:**
- Grid: `Source/OpenSim.Server.GridServer/AppData/Robust.ini.example` — `[ServiceList]`
  `ExperienceServiceConnector = "${Const|PrivatePort}/OpenSim.Server.Handlers.dll:ExperienceServiceConnector"`
  (`~:122`, commented in the template) + `[ExperienceService]` section (`~:665-669`,
  `LocalServiceModule = "OpenSim.Services.ExperienceService.dll:ExperienceService"`). Same in
  `Robust.HG.ini.example`.
- Region: `Source/OpenSim.Server.RegionServer/AppData/config-include/GridCommon.ini.example` `[ExperienceService]`
  → `LocalServiceModule = "OpenSim.Services.Connectors.dll:RemoteExperienceServicesConnector"`.

This is the architectural template to mirror for a new membership/tier service.

---

## 8. Experience data layer — **CONFIRMED MySQL-only**; grid runs MySQL

- **Only** `Source/OpenSim.Data.MySQL/MySQLExperienceData.cs` implements `IExperienceData`.
  `Source/OpenSim.Data.SQLite` has **no** Experience data class (verified: no `*experience*` file there).
  No PGSQL/MSSQL/Null Experience impl either. So even if `StorageProvider` were pointed at SQLite,
  Experience would have no data layer → **Experience is MySQL-only. CONFIRMED.**
- Which DB the grid runs: the shipped templates list `MySQL` first (others commented), and — decisively for
  the whole migration — this grid's live `legiongrid` DB and every service run against **MySQL**
  (`OpenSim.Data.MySQL.dll`). A new tier service should follow suit (MySQL data layer + migration), matching
  the Experience template above.

---

## Verdict summary
| # | Area | Verdict |
|---|---|---|
| 1 | Group-join cap | **REFUTED** — none exists; would go in `GroupsService.AddAgentToGroup:423` |
| 2 | Attachment cap | **CONFIRMED** — 38 (`Constants.cs:33`), `AttachmentsModule.cs:690`; per-agent feasible, `sp` in scope |
| 3 | SimulatorFeatures | **CONFIRMED** — per-request copy + `agentID` delegate; no result cache |
| 4 | LLLogin max-agent-groups | **CONFIRMED** grid-wide; seam = `LLLoginResponse.MaxAgentGroups` setter (no ctor change); ★ two advertising paths |
| 5 | IMoneyModule | **CONFIRMED** — 3 implementers; iface in `OpenSim.Framework`; charge is a getter (no agent) → change at consumers |
| 6 | UserTitle/CharterMember | **CONFIRMED** flow; **PARTIAL** on safe-to-write (contended field + profile cache + flag-byte alternative) |
| 7 | Experience inventory | **CONFIRMED** layout (handler class `ExperienceServiceConnector`) |
| 8 | Experience data layer | **CONFIRMED** MySQL-only; grid runs MySQL |

---

## Implemented — M2 user-visible changes (2026-08-11)

Both are **inert when membership is unconfigured** (byte-identical to before).

### Part A — profile tier badge (field = **UserTitle**, decided)
- On membership change (`MembershipService.SetMembership` / `RemoveMembership`) the resolved tier's
  `display_title` is written to the account's `UserTitle` via `IUserAccountService.StoreUserAccount`.
  Verified: that is a real `REPLACE INTO` write (not a stub), preserves `DisplayName`, and needs no
  `AllowSetAccount` gate (that gate is only on the admin HTTP endpoint; we call the service in-process).
- Badged **only** for LOCAL accounts (`GetUserAccount` succeeds); HG visitors have no local row so are
  never written — the "HG Visitor" transient stand-in (`UserProfileModule.cs:1865`) is untouched.
- An empty `display_title` (Basic / no tier) writes `UserTitle=""`; `StoreUserAccount` omits it and the
  REPLACE resets the column to `''`, so the account falls back to the `UserFlags & 0x0f00` byte path.
- **★ 5-minute staleness, by design.** The region-side profile cache (`PROFILECACHEEXPIRE = 300s`,
  `UserProfileModule.cs`) has no per-user eviction and lives in a different process, so a title change is
  visible after **≤5 min** (next profile fetch after TTL) or a relog. We deliberately do **not** build
  cross-process cache invalidation — it self-heals on TTL. Operator repair for an admin-clobbered title:
  console `membership resync <first> <last>`.

### Part B — per-account group limit at login
- `LLLoginService` optionally loads `IMembershipService` (`[LoginService] MembershipService`, commented by
  default) and, after building the response, sets `response.MaxAgentGroups = GetMembership(id).max_groups`.
  Absent service → the grid-wide value stands (unchanged). Empty tiers table → Basic fallback == grid-wide
  → still unchanged.
- **★ Wire semantics:** Firestorm treats login `max-agent-groups = 0` as **UNLIMITED**; a tier's
  `max_groups` of 0 is passed through as 0 unchanged (so an "unlimited" tier = `max_groups 0`).
- **★ Interim inconsistency:** this is the LOGIN path only. **SimulatorFeatures still advertises the
  grid-wide constant until M3**, so the two advertising paths disagree for non-default tiers meanwhile.

## Known issues

Observed on the live grid after the M1+M2+M3 deploy (2026-08-12). Not yet fixed.

1. **Stale tier cache.** After seeding a tier row directly in SQL, `membership show` kept resolving to
   Basic even though the row existed; it only picked up the new tier after a `membership set`. Something
   caches the tier list without invalidation, so an operator who adds tiers via SQL won't see them take
   effect. Candidate fix: a `membership reload` console command, or a short TTL on the tier cache.
2. **Console commands echo twice.** Every `membership` command prints its output twice on the GridServer
   console. Cosmetic, but suggests a doubled command registration. Before assuming it's ours, check
   whether other console commands in this tree also double-print (rule out a shared console/registration
   quirk vs. a duplicate `AddCommand` in the membership service).
