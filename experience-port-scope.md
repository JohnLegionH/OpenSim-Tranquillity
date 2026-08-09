# Tranquillity Experience Port — Scoping Pass

**Date:** 2026-07-21 · **Read-only. No code.** · Companion to `experience-port-audit-v2.md` (RECONCILE strategy + preserve-list).
**Legion reference:** tag `port-source-2026-07-21` (complete). **Tranquillity:** `/d/tranquillity-develop` @ `develop`.

**Headline:** Tranquillity is **further along than the v2 audit implied** — its caps are ~80% SL-conformant already and its admin/contributor group-power logic is *already present* (Legion only added that in Slices 3–4). The real gap is **consent** (auto-grant → real dialog), plus the **quota value** and a handful of minor cap fixes. Almost all of it is **standalone-verifiable in-process** — with one important storage caveat.

---

## Q1 — How SL-conformant is Tranquillity's existing Experience already?

**10 of 13 caps ALREADY-SL-CONFORMANT; 3 have minor/moderate defects.** Notably, Tranquillity's caps module was built to SL's model from the StolenRuby scaffold, so it **already does owner∪group-power** — the exact thing Legion took Slices 3–4 to add.

| Cap | Status | Notes (Tranq ExperienceModule.cs) |
|---|---|---|
| RegionExperiences | ✅ conformant | allowed/blocked/default/disabled/**trusted** (from estate KeyExperiences), GET-only (:980-1035) |
| GetExperiences | ✅ conformant | `{blocked, experiences}` (:1238-1300) |
| AgentExperiences | ✅ conformant (GET) | `experience_ids` (:1191-1236) — **no POST/acquire** (that's T7) |
| GetAdminExperiences | ✅ conformant | **owner ∪ GP_EXPERIENCE_ADMIN** already (IExperienceModule:418-444) |
| GetCreatorExperiences | ✅ conformant | **owner ∪ GP_EXPERIENCE_CREATOR** already (:446-472) |
| GroupExperiences | ✅ conformant | `experience_ids` by group (:692-731) |
| GetMetadata | ✅ conformant | `{experience:uuid}` from `TaskInventoryItem.ExperienceID` (:733-802) |
| IsExperienceAdmin | ✅ conformant | **owner ∪ GP_EXPERIENCE_ADMIN** (:474-494) |
| IsExperienceContributor | ✅ conformant | **owner ∪ GP_EXPERIENCE_CREATOR** (:496-516) |
| ExperiencePreferences | ✅ conformant | PUT/GET/DELETE → `{blocked, experiences}` (:143-245) |
| **GetExperienceInfo** | ⚠️ **HAS-DEFECTS** | emits `extended_metadata`+`expiration=600`, but: **marketplace hardcoded empty** in the metadata (`<string />`, :1073) while Find includes it; **quota hardcoded 128** (:1079) vs Find's `info.quota`; maturity passes through raw `info.maturity` (:1081) — benign *iff* stored 13/21/42 (see below) |
| **FindExperienceByName** | ⚠️ **HAS-DEFECTS** | **pagination unimplemented** — `// todo: handle pages` (:661); page/page_size accepted but ignored; no `next_page_url`/`previous_page_url` |
| **UpdateExperience** | ⚠️ **HAS-DEFECTS** | admin-gated (:852-854) but **no error on non-admin** (silently returns unchanged); **group_id NOT owner-only** (:858, any admin can change — Legion restricts to owner); maturity **IS** normalized to 13/21/42 on write (:840-841) ✅ |

**Maturity nuance:** Tranquillity stores maturity **natively as 13/21/42** (schema comment + UpdateExperience normalizes on write), so GetExperienceInfo's raw passthrough is *already correct* for data written through the cap — unlike Legion which stored internal 0/1/2 and needed `MaturityToSimAccess`. It's only a latent robustness gap if a non-cap writer inserts an off-enum value. **Effectively conformant.**

**So the Slice-0 conformance work Tranquillity still needs is small:** FindExperienceByName pagination, GetExperienceInfo quota/marketplace consistency, UpdateExperience owner-only-group + error response. Everything else Legion's Slice 0 fixed, Tranquillity already had.

---

## Q2 — Per-slice effort + the real gap

| Slice | What's genuinely missing in Tranq | Effort | Notes |
|---|---|---|---|
| **T1 script-surface + cap fixes** | Cap: Find pagination, GetInfo quota/marketplace, UpdateExp owner-only-group + 403. Phlox: Legion's SS-1..9 (error table 0-18, `llGetExperienceDetails` layout, KV key-cap, land code 17) *if* Tranq's Phlox snapshot lacks them | **M** | ~caps: small edits in LindenCaps handlers; Phlox: verify which SS fixes John's Phlox already has |
| **T2 quota 16→128 + code 11** | `MAX_QUOTA = 16 MiB` → 128; reconcile the display (GetInfo says 128, enforcement is 16 — genuinely wrong today); ensure `DataSizeKeyValue` returns `used,total` | **S** | one constant + display reconcile; enforcement path already exists |
| **T3 CONSENT (D1)** | **The real gap.** Tranq **auto-grants** (no dialog). Port the ScriptQuestion+await: experience-aware `SendScriptQuestion`, pending-map + 300s timeout (code 18), `ScriptAnswerYes` correlation → Grant/Deny, trusted-bypass — routed through `PhloxExperienceAdapter`→NGC `UpdateExperiencePermissions` | **L** | the one substantial slice; gated on LibOMV (see below) |
| **T4 Block loop** | Wire the early-denial (block-before-grant) using NGC's `allow=false`; ExperiencePreferences PUT Block already persists | **S–M** | mostly ordering + adapter `IsAgentBlocked` |
| **T5 trusted enforcement** | Trusted **list** already exists (estate KeyExperiences, surfaced in RegionExperiences); add **enforcement** (admit + consent-bypass) in adapter/Phlox | **S–M** | lands with T3 |
| **T6 ExperienceQuery + ICO** | ExperienceQuery is the **only unserved cap** → add Legion's no-op. IsExperienceContributor cap already served ✅ | **S** | one small no-op handler |
| **T7 acquire (DEC-3)** | AgentExperiences is **GET-only** — add POST/acquire + grid-config policy (create via NGC `UpdateExperienceInfo`) | **M** | new POST branch + config |
| **T8 SL-unverified tail** | KV empty-read / CAS-vs-empty / Keys clamps parity with Legion's Slice-7 choices | **S–M** | small, defensive |

**Realistic total:** **1 Large (consent) + ~2 Medium (T1, T7) + ~4 Small (T2, T4, T5, T6, T8).** This is a **modest port**, not a rebuild — Tranquillity already has the 13 caps, the storage, the wire protocol, and the group-power logic. Genuinely-missing (not "slightly different"): **real consent**, the **quota value**, **Find pagination**, **acquire POST**, and **ExperienceQuery**. Everything else is present or a trivial delta.

---

## Q3 — Verifiability per slice (the decisive table)

**Key architectural fact:** Tranquillity **standalone uses the LOCAL connector** (`Standalone.ini:26` `ExperienceService = LocalExperienceServicesConnector`; `:123-124` `LocalServiceModule = ExperienceService.dll:ExperienceService`) — the service runs **in-process, no Robust**. So the caps → local connector → service → data path executes in standalone. The **Robust wire protocol (Remote connector + server post handler) is only used in Grid.ini/GridHypergrid** and **the RECONCILE port never modifies it** → no slice is Robust-only.

**⚠️ The one real caveat — MySQL-only storage:** the sole `IExperienceData` impl is **`MySQLExperienceData`** (no `SQLiteExperienceData` exists on Tranquillity *or* Legion), and `[Experience] Enabled=false` by default. So in a **pure-SQLite standalone**, Experience storage doesn't work — to *observe* it, John must enable Experience and point its data layer at an **isolated MySQL DB** (not Docker's `opensim`). The code path is in-process; only the persistence needs MySQL.

| Slice | Verifiability | Basis |
|---|---|---|
| T1 caps + script-surface | **STANDALONE-VERIFIABLE** | caps run in-process via local connector; viewer hits region caps directly — no Robust. (Persistence needs the MySQL Exp DB.) |
| T2 quota | **STANDALONE-VERIFIABLE** | KV enforcement in the in-process service |
| **T3 consent** | **STANDALONE-VERIFIABLE** (the headline) | the dialog fires from Phlox in-process; John **can actually see Yes/No/Block** in standalone. (Grant-persist needs the Exp DB.) |
| T4 Block loop | **STANDALONE-VERIFIABLE** | in-process denial path |
| T5 trusted | **STANDALONE-VERIFIABLE** | estate + in-process admission |
| T6 ExperienceQuery no-op | **STANDALONE-VERIFIABLE** (but it's a no-op) | region cap |
| T7 acquire | **STANDALONE-VERIFIABLE** | AgentExperiences POST via local connector |
| T8 tail | **STANDALONE-VERIFIABLE** | in-process KV |
| *(the Robust wire protocol itself)* | **ROBUST-ONLY (BLIND)** | not exercised in standalone — **but untouched by the port**, so never a verification blocker |

**Verdict: essentially the ENTIRE port is standalone-verifiable in-process, including the consent dialog** — because standalone uses the local connector and the port touches logic, not the Robust wire. **Nothing John needs to ship is Robust-only-blind.** The single caveat is storage: since neither tree has a SQLite Experience provider, John needs an **isolated MySQL Experience DB** to see persistence (grants, KV, experiences). *Optional bonus not in the plan:* writing `SQLiteExperienceData` would make Experience fully testable in pure-SQLite standalone with zero MySQL — a self-contained slice if John wants true SQLite isolation.

---

## Q4 — SL-architecture comparison

**Tranquillity's Robust-grid-service shape is *more* SL-faithful than Legion's.** SL's actual model: a **central Experience service** (grid-wide), **region simulators serve the viewer caps**, and the **simulator drives consent** (sends ScriptQuestion, viewer shows the dialog, sim persists the grant).

- **Tranquillity matches this:** central `ExperienceService` (Robust) + per-region caps (`LindenCaps/ExperienceModule`) + Local/Remote connectors. Once consent is ported, the sim drives the dialog. This is SL's three-part process (central service / region caps / sim consent) faithfully.
- **Legion diverges:** per-region `new ExperienceService(connString)` directly to a shared MySQL — **no central service**, relies on every region sharing a connection string (Legion ledger OBS-1). Functional for a small grid, but not SL's central-service model.

**Implication:** the port is **"make Tranquillity's *logic* match Legion's SL-conformance,"** *not* "make Tranquillity match Legion's architecture." Tranquillity's architecture is already closer to SL — which is exactly why RECONCILE (preserve the SL-faithful Robust shape, port the conformance logic) is right, and FULL REPLACE (impose Legion's per-region model) would move Tranquillity *away* from SL.

---

## Bottom line

- **How far is Tranquillity?** ~80% there. **13/14 caps, group-power admin/contributor, storage, and a more-SL-faithful architecture already in place.** The genuine gaps are: **consent (auto-grant → real dialog — 1 Large slice), quota value (16→128), Find pagination, acquire POST, and the ExperienceQuery no-op** — plus a few minor cap fixes. Total: **1 L + ~2 M + ~4 S** — a modest, well-bounded port.
- **How much can John verify?** **Nearly all of it, in standalone, in-process — including the consent dialog** (the headline feature), because standalone uses the local connector and the port doesn't touch the Robust wire. The only verification caveat is that **Experience storage is MySQL-only on both trees**, so John needs an **isolated MySQL Experience DB** to observe persistence (or, optionally, a new `SQLiteExperienceData` provider to make it pure-SQLite testable).
- **Nothing is ship-blind** that the port introduces; the Robust-only path is the pre-existing wire protocol, which RECONCILE preserves unchanged.

### Flags for John's go/no-go
1. **Confirm the storage plan for verification:** isolated MySQL Experience DB (fastest) vs. writing `SQLiteExperienceData` (fully SQLite-standalone, more work). Without one of these, Experience can't persist in his standalone regardless of the port.
2. **Consent is the one Large slice and it's standalone-testable** — highest value, highest visibility. Gated on the LibOMV check (does Tranq's NuGet LibOMV carry the ScriptQuestion Experience block?).
3. The caps are in better shape than expected — **T1 may be mostly the Phlox SS-fixes**, since the cap defects are small.
