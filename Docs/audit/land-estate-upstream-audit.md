**Point-in-time audit, 2026-08-23, against upstream `cbdfba2811`.**
Findings here are as-of that commit and some have since been resolved — see
`Docs/KnownDefects.md` and the git history for what became of them. Do not treat an open
item here as current without checking.

**Since this audit:**

- **The non-atomic parcel access-list write is filed** in `Docs/KnownDefects.md` as "Parcel
  access-list persistence is non-atomic - delete-then-reinsert under a process lock" (added
  by `8e48166373`). The audit's other land/estate items were not revisited and their status
  here is unchanged rather than confirmed.
- This file's own header cites branch tip `5a25c65583`, a pre-rebase SHA that is no longer
  reachable from HEAD. The in-history equivalent is `6743ac4e7c`.

---

# Land / estate — upstream-span audit

**Repo:** `D:\tranquillity-develop`
**Branch:** `feature/voice-visibility-matrix` @ `5a25c65583`
**Span audited:** `81e5c2449d` (old merge base) → `cbdfba2811` (upstream/develop, 11 commits)
**Branch side compared:** `81e5c2449d` → `de94534257` (pre-rebase tip = branch intent)
**Status:** read-only audit. Nothing changed, fixed or committed. Uncommitted working-tree output.

## Method and its limits

`git diff` between revisions; per-file classification of changed lines into ILogger-migration
noise versus semantic (blank lines excluded); reading the surviving semantic lines and their
enclosing methods at both revisions; SHA-256 of extracted method bodies to prove
identity/non-identity; and reading the resolved data-access code.

**No region was booted, no database was touched, and no save was performed.** Section 6 exists
because the provider questions are runtime ones. Anything not established is marked
**NOT ESTABLISHED**.

Branch commits in scope, confirmed by `git log` on each file:

- `b5e3472247` — Estate CAP nullable fix (`EstateChangeInfo.cs`, `EstateManagementModule.cs`)
- `b4f4947286` — parcel persist fixes (`LandManagementModule.cs`, `LandObject.cs`)
- `eb74343dc5` — ban-add instrumentation (`LandManagementModule.cs`)

---

## 1. Direct overlap

**Verified by:** intersecting both sides' changed-file lists; classifying the upstream side of
each; then proving the merge is exactly the union by diffing HEAD against *upstream* and checking
the residue equals the branch's own semantic line count.

Four land/estate files were modified by both sides.

| file | branch semantic lines | upstream semantic lines | HEAD vs upstream | conflicted? |
|---|---|---|---|---|
| `World/Land/LandManagementModule.cs` | 34 | **9** (PR #189) | **34** | no |
| `World/Land/LandObject.cs` | 37 | **0** | **37** | no |
| `World/Estate/EstateManagementModule.cs` | 100 | **0** | **100** | no |
| `LindenCaps/EstateChangeInfo.cs` | 35 | **0** | **35** | no |

The right-hand columns match the branch column exactly in all four rows. That is the proof the
merge is a clean union: everything upstream contributed is present (HEAD is diffed *against*
upstream, so upstream's content is the baseline), and the only residue is precisely the branch's
own semantic changes, with nothing lost and nothing rewritten.

### Resolutions taken at conflicted hunks

**There were none.** None of these four files raised a conflict during the rebase — the conflicts
in that rebase were confined to `Tranquillity.sln`, `LLLoginService.cs` and six voice files
(audited separately in `webrtc-upstream-audit.md`). All four land/estate files auto-merged.

For three of them that is unsurprising: **upstream's contribution was log lines only** — zero
semantic changes in `LandObject.cs`, `EstateManagementModule.cs` and `EstateChangeInfo.cs`. There
was no second intent to reconcile.

`LandManagementModule.cs` is the one file where both sides changed behaviour, and it auto-merged
because the two changes are in **different methods roughly 700 lines apart**:

- **Upstream (#189)** — inside `SendParcelOverlay(IClientAPI remote_client)` (method begins line
  1422; changed condition at line 1474). It replaces `currentParcelLandData.SalePrice > 0` with
  `(currentParcelLandData.Flags & (uint)ParcelFlags.ForSale) != 0` when selecting
  `LandChannel.LAND_TYPE_IS_FOR_SALE` for the parcel-overlay byte, plus a seven-line explanatory
  comment.
- **Branch (`b4f4947286`, `eb74343dc5`)** — inside
  `ClientOnParcelAccessListUpdateRequest(...)` (method begins line 683). Five `DebugFormat`
  diagnostics covering every early-return outcome (flags neither access nor ban; estate TaxFree;
  flags map to no manage power; applied; denied), two `SendAgentAlertMessage` calls, and the
  load-bearing line at 746: `UpdateLandObject(land.LandData.LocalID, land.LandData);` — the
  missing store after `UpdateAccessList`.

Both intents are verifiably present at HEAD. The only branch-side transformation is that the five
`DebugFormat` calls became `LogDebug` with named placeholders during the post-rebase ILogger
conversion (audited in `webrtc-upstream-audit.md`); message text and arguments are unchanged.

---

## 2. The estate CAP work against #188

**Verified by:** reading #188's diff and locating its enclosing methods; reading
`PackEstateFlags` and `ApplyEstateChangeInfo` at HEAD; grepping the whole LindenCaps tree for the
Deny flag names; reading `EstateSettings` field declarations.

### 2.1 What #188 actually changed

`git show --stat 1a742c8c01` confirms **one file: `LLClientView.cs`**, 22 insertions / 4 deletions.
Two changes, both in the region-handshake path:

1. In `LLClientView.GetRegionFlags()` (private, line 798), `RegionFlags.DenyIdentified` and
   `RegionFlags.DenyTransacted` are now folded in from `EstateSettings`. They were previously
   commented out as "unused", so `GetRegionFlags()` could never set them regardless of estate
   configuration.
2. In `SendRegionHandshake()` (line ~869), the capability packet's `RegionDenyIdentified` /
   `RegionDenyTransacted` elements now read those bits (`(regionFlags & …) != 0`) instead of being
   hardcoded `false`. `regionFlags` there comes from `GetRegionFlags()` at line 895.

### 2.2 Does #188 change what the flags mean, when they are evaluated, or what happens downstream?

- **Meaning: unchanged.** They remain the same two `EstateSettings` booleans, with the same
  storage and the same semantics.
- **When evaluated: yes, changed — a new evaluation site.** They are now read during region
  handshake, where previously they were never read at all on that path.
- **Downstream: yes, changed — this is the point of the fix.** The viewer now receives the true
  values and will enforce the corresponding client-side access restrictions, which it previously
  never saw. That is the "dead enforcement chain" being revived.

### 2.3 Does it collide with `PackEstateFlags`?

**No — they are two independent packers serving different consumers, and #188 makes them agree.**

- `EstateManagementModule.PackEstateFlags(EstateSettings)` (line 2344, extracted by the branch
  from the old inline `GetEstateFlags()` body) already set `RegionFlags.DenyIdentified` and
  `DenyTransacted` — lines 2371–2374, each annotated `// unused?`. Its consumer is
  `GetEstateFlags()` (line 2335), used at line 505 in the estate-detail reply.
- `LLClientView.GetRegionFlags()` is the handshake packer, and is where #188 landed.

So the branch's extraction did not move, alter, or duplicate #188's work — `PackEstateFlags`
already reported these bits, and #188 fixed the *other* packer that did not. The net effect is
that the estate tab and the region handshake now report the same thing. The branch's own comment
on `PackEstateFlags` states it is "behaviour-identical to the previous inline `GetEstateFlags`
body", and the diff supports that.

### 2.4 Are DenyIdentified/DenyTransacted now reachable by a write path they were not before?

**No.** This was checked directly and the answer is unambiguous.

- `ApplyEstateChangeInfo` (line 2260) takes exactly **seven** nullable booleans:
  `externallyVisible`, `allowDirectTeleport`, `denyAnonymous`, `denyAgeUnverified`,
  `alloVoiceChat`, `overridePublicAccess`, `allowEnvironmentOverride`. Neither Deny flag is among
  them. Each is applied only under `if (x.HasValue)`.
- A grep of the **entire** `Source/OpenSim.Region.ClientStack.LindenCaps/` tree for
  `DenyIdentified`, `DenyTransacted`, `deny_identified` and `deny_transacted` returns **nothing**.
  The CAP does not parse, carry, or write these flags.
- Their only write path remains `EstateManagementModule.HandleEstateChangeInfo(...)` (method at
  line 2162), which decodes `parms1` bits `0x01000000` / `0x02000000` at lines 2193–2202. That is
  the legacy UDP estate dialog, it predates both the branch and #188, and **neither side changed
  it** (`EstateManagementModule.cs` has zero upstream semantic lines, and the branch's 100 lines
  are the extraction plus nullable plumbing).

So the nullable fix neither widens nor narrows the reachable write surface for these two flags.
There is no interaction.

### 2.5 The one real interaction, and it is a deployment consideration

The nullable fix and #188 do not interact in code, but they compose at runtime in a way worth
naming: **#188 activates whatever is already stored.** If any estate in the live database has
`DenyIdentified` or `DenyTransacted` set to 1 — from a historical estate-dialog save, when those
bits were inert at handshake — that restriction becomes live on first boot after this upgrade,
and viewers will begin enforcing it.

The branch's CAP fix does not cause this and does not prevent it (those flags never passed
through the CAP). But it is the same upgrade, and the symptom would be "users suddenly refused
entry to an estate", which is easy to misattribute.

**NOT ESTABLISHED:** what those columns currently hold in the live grid database. That is a
one-line query, listed in §6.

### 2.6 Nullability is a wire-layer concept only

Worth recording for §4: `EstateSettings.DenyIdentified`, `DenyTransacted`, `DenyAnonymous`,
`DenyMinors`, `AllowVoice`, `TaxFree`, `PublicAccess`, `AllowDirectTeleport` and
`AllowEnvironmentOverride` are all declared as plain **non-nullable `bool`**
(`EstateSettings.cs` lines 142–243). The `bool?` introduced by the CAP fix lives entirely in
`EstateChangeInfo.cs` request parsing and is resolved by `ApplyEstateChangeInfo` before anything
is stored. **No nullable ever reaches the database.**

---

## 3. The parcel persist fixes against #189

**Verified by:** locating #189's enclosing method; extracting `UpdateAccessList` and
`UpdateLandProperties` bodies at both revisions and hashing them; grepping the upstream
`UpdateAccessList` body for store calls.

### 3.1 Does #189 touch the same persist paths?

**No. #189 persists nothing at all.** It sits inside `SendParcelOverlay`, in the loop that fills
the per-`LandUnit` overlay byte array sent to the viewer for the minimap. It is a pure read-and-
classify: it mutates no `LandData`, calls no store, and does not touch `UpdateAccessList`,
`UpdateLandProperties`, `UpdateLandObject` or `StoreLandObject`.

The branch's two fixes are in entirely different places:

- **The UpdateAccessList store fix** — `LandManagementModule.ClientOnParcelAccessListUpdateRequest`
  (line 683), adding `UpdateLandObject(...)` at line 746, which fires
  `TriggerLandObjectUpdated → TriggerLandObjectAdded → StoreLandObject`.
- **The UseBanList clobber fix** — `LandObject.UpdateLandProperties` (line 532), where the inline
  flag computation was replaced by `ComputeSavedFlags(...)` at line 674, with the new pure helpers
  `ComputeSavedFlags` (line 699) and `HasBanEntry` sitting below it.

### 3.2 Are both fixes still correct and still necessary at `cbdfba2811`?

**Yes, both — and upstream did not independently change either target.** Proven by hashing the
extracted method bodies at both revisions:

| method | body SHA-256 (first 16) at `81e5c2449d` | at `cbdfba2811` | verdict |
|---|---|---|---|
| `LandObject.UpdateAccessList` | `1519b24d75b5779c` | `1519b24d75b5779c` | **identical** |
| `LandObject.UpdateLandProperties` | `7e3bdf1c1e8ecf15` | `7e3bdf1c1e8ecf15` | **identical** |

Additionally, grepping upstream's `UpdateAccessList` body at `cbdfba2811` for `StoreLandObject`
or `UpdateLandObject` returns **0 matches** — upstream still does not persist there. The gap the
branch's fix closes is still open in upstream code.

`LandObject.cs` has zero upstream semantic lines across the whole span, so `UpdateLandProperties`
still trusts the client flag word for `UseBanList`, exactly as before. The clobber the branch
fixes is still reachable.

**Conclusion: neither fix has been superseded, neither has become redundant, and neither conflicts
with #189.** They are orthogonal: #189 corrects a display classification; the branch fixes correct
persistence.

---

## 4. Persistence layer

**Verified by:** classifying every land/estate file under `Source/OpenSim.Data*` across the span;
reading the provider `using` directives and csproj package references at both revisions; listing
the actual file sets of the two migration commits; reading the boolean and flag conversion code.

### 4.1 The framing needs correcting first

Two premises in the brief do not hold for land and estate, and this materially reduces the risk.

**(a) There is no SQLite provider swap for land or estate.** `OpenSim.Data.SQLite` was **already**
on `System.Data.SQLite` at `81e5c2449d`. Verified from the source at both revisions:
`SQLiteSimulationData.cs` and `SQLiteEstateData.cs` both carry `using System.Data.SQLite;` at
`81e5c2449d` *and* at `cbdfba2811`. The csproj went from
`System.Data.SQLite 2.0.3` to `System.Data.SQLite 2.0.4` **plus an added `SQLite 3.53.4`**.

`git show --stat 27f222b84f` confirms the "normalize SQLite" commit touched 11 files, and the
only `Microsoft.Data.Sqlite` consumers it migrated were **`Phlox.ScriptEngine/StateManager.cs`**
and **`OptionalModules/World/NPC/BotPersistenceManager.cs`**. Cross-checked by
`git grep -l "Microsoft.Data.Sqlite" 81e5c2449d`, which lists only those two components plus
their csprojs. `OpenSim.Data.SQLite` is not among them; it received a one-line csproj addition.

So for land/estate SQLite the change is **a patch bump plus an added native package**, not a
provider change. (The genuine provider swap, in Phlox's state store, is analysed in
`phlox-upstream-audit.md`.)

**(b) Pomelo → Microting does not touch land or estate.** `git show --stat 9baaf80c9b` lists six
files, all under `Source/OpenSim.Data.Model/` — four EF context factories, the csproj and a
readme. Land and estate do **not** use EF: `MySQLEstateData.cs` and `MySQLSimulationData.cs` both
use raw `MySqlConnector` with hand-built `MySqlCommand` objects. The EF provider swap is invisible
to them.

**The real MySQL-side change for land/estate is the driver bump: `MySqlConnector` 2.5.0 → 2.6.1**
(plus `System.Configuration.ConfigurationManager` 10.0.8 → 10.0.10 and the net10 TFM). That is
where any type-mapping or null-handling change would come from — not from Pomelo.

### 4.2 The DAL code itself is untouched

Every land/estate data file classified across the span:

| file | upstream semantic lines |
|---|---|
| `MySQLEstateData.cs` | **0** |
| `MySQLSimulationData.cs` (land + landaccesslist) | **0** |
| `SQLiteEstateData.cs` | **0** |
| `SQLiteSimulationData.cs` | **0** |
| `PGSQLEstateData.cs` | **0** |
| `NullEstateData.cs` | **0** |
| `MySQLFramework.cs`, `MySQLGenericTableHandler.cs`, `SQLiteGenericTableHandler.cs` | **0** |
| `OpenSim.Data.MySQL.csproj` | 5 (TFM, log4net pin removed, MySqlConnector + ConfigurationManager bumps) |
| `OpenSim.Data.SQLite.csproj` | 6 (TFM, pins, added SQLite native) |

Also verified: **upstream changed no migration resources at all**
(`git diff --name-only 81e5c2449d..cbdfba2811 -- '*/Resources/*'` is empty). The `land`,
`landaccesslist`, `regionsettings` and `estate_*` schemas are byte-identical.

### 4.3 Transaction semantics, type mapping, null handling

**Established statically:**

- **Booleans round-trip as integers, via explicit `Convert`.** Both DALs reflect over
  `EstateSettings` fields; on read, `if (m_FieldMap[name].FieldType == typeof(bool))` →
  `Convert.ToInt32(r[name]) != 0` (`MySQLEstateData.cs:159–161`; `SQLiteEstateData.cs:126–128`);
  on write, the bool branch emits `"1"`/`"0"` (lines 215/217, 263/265 and 207/209, 241/243
  respectively). `Convert.ToInt32` is deliberately provider-tolerant: it accepts `long`, `int`,
  `byte`, `sbyte`, `bool` or a numeric string. A provider changing its CLR type for a
  `TINYINT`/`INTEGER` column therefore cannot break this path. The one input it cannot absorb is
  `DBNull`, which throws — but these columns are `NOT NULL` in the unchanged schema.
- **The estate CAP's absent-vs-false distinction cannot be affected by any provider change**,
  because it never reaches a provider. `EstateSettings` fields are non-nullable `bool` (§2.6);
  the `bool?` exists only between the CAP request parse and `ApplyEstateChangeInfo`. There is no
  nullable bool round-trip through MySQL or SQLite to perturb.
- **Land flag columns:** `newData.Flags = Convert.ToUInt32(row["LandFlags"])` on read
  (`MySQLSimulationData.cs:1350`), `cmd.Parameters.AddWithValue("LandFlags", land.Flags)` on write
  (line 1778) — a `uint`. Same `Convert` tolerance.
- **`landaccesslist`:** `entry.Flags = (AccessList)Convert.ToInt32(row["Flags"])` on read
  (line 1443), `AddWithValue("Flags", entry.Flags)` on write (line 1831). Persistence is
  **delete-all-then-reinsert** per parcel: `delete from landaccesslist where LandUUID = ?UUID`
  (line 741) followed by a loop of inserts (line 746 ff.), inside `StoreLandObject`.
- **Transactions: there are none, on either backend.** MySQL's `StoreLandObject` is guarded by
  `lock (m_dbLock)` — a *process-level* lock, not a database transaction — and the land upsert,
  the `landaccesslist` delete and the re-inserts execute as separate autocommit statements.
  SQLite uses `SQLiteDataAdapter` (DataSet-based) with no explicit transaction either. **This is
  pre-existing and unchanged by upstream**, but it is worth naming here because the branch's
  UpdateAccessList store fix causes this non-atomic delete+reinsert to run *far more often* than
  before. A crash between the delete and the inserts loses that parcel's access/ban list. The fix
  is still right — losing the list on crash is strictly better than never persisting it — but the
  window is now exercised on every access-list edit.

**NOT ESTABLISHED — needs a runtime check:**

1. Whether `MySqlConnector` 2.6.1 returns the same CLR types for `TINYINT`/`INT`/`INT UNSIGNED`
   columns as 2.5.0 did. The `Convert.To*` calls make this very likely to be a non-issue, but it
   is unverified.
2. Whether `System.Data.SQLite` 2.0.4 behaves identically to 2.0.3 for these tables.
3. What the newly added `SQLite 3.53.4` package does to the loaded native engine — whether it
   changes which SQLite build is used at runtime, and with it any pragma or type-affinity
   behaviour. This is the least-understood item in this section.
4. Whether `estate_settings` boolean columns and the `land.LandFlags` / `landaccesslist.Flags`
   columns round-trip identically end-to-end after the upgrade.

---

## 5. Voice moderation state

**Verified by:** reading `VoiceModerationStore.cs`; checking upstream's changes to
`landaccesslist` persistence and to all migration resources.

**Slice 1 is purely in-memory.** `VoiceModerationStore` holds
`private readonly Dictionary<UUID, ParcelModeration> m_byParcel` — no database involvement at all,
so nothing upstream landed can affect it.

**Slice 2's design is unaffected.** The `landaccesslist` model it is intended to mirror is
untouched across the span:

- `MySQLSimulationData.cs` and `SQLiteSimulationData.cs` — **zero** upstream semantic lines.
- Upstream changed **no migration resources**, so the `landaccesslist` schema
  (`RegionStore.migrations:209`) is byte-identical.
- The persistence pattern slice 2 would copy — per-parcel delete-all-then-reinsert inside
  `StoreLandObject`, keyed on `LandUUID`, flags stored as an integer read back via
  `Convert.ToInt32` — is unchanged.

Two things worth carrying into the slice 2 design, both pre-existing rather than newly introduced:

1. **Inherit the non-atomicity knowingly.** If `landvoicemoderation` follows the same
   delete-then-reinsert shape inside the same `StoreLandObject` call, it inherits the same
   crash window described in §4.3, and it will be written on the same trigger path the branch's
   persist fix now exercises.
2. **`StoreLandObject` is the only write trigger.** Slice 2 will need the same lesson the
   UpdateAccessList fix encodes: mutating in-memory parcel state does not persist it; something
   must reach `UpdateLandObject` / `StoreLandObject`.

**NOT ESTABLISHED:** whether a new table added by slice 2 would need a migration-ordering
decision relative to upstream's own future migrations — that is a forward-looking question this
span cannot answer.

---

## 6. Runtime verification plan

Land/estate DAL code and schema are unchanged (§4.2), so these checks are about the **providers**
underneath and about #188's newly-live flags — not about the branch's code, which is verified
statically above.

### 6.0 Back these tables up first

The checks below write. Back up, at minimum:

| database | tables |
|---|---|
| region store | `land`, `landaccesslist`, `regionsettings` |
| estate store | `estate_settings`, `estate_map`, `estate_managers`, `estate_users`, `estate_groups`, `estateban` |

(`estate_allowed_experiences`, `estate_blocked_experiences` and `estate_key_experiences` also live
in the estate store; nothing here touches them, but a whole-schema dump is simplest.)

### 6.1 Pre-flight query — what #188 is about to activate

Before booting, and read-only:

```sql
SELECT EstateID, EstateName, DenyIdentified, DenyTransacted, DenyAnonymous, DenyMinors
FROM   estate_settings
WHERE  DenyIdentified <> 0 OR DenyTransacted <> 0;
```

- **Empty result:** #188 changes nothing observable on this grid. Good.
- **Any rows:** those estates will begin enforcing that restriction client-side on first boot
  after the upgrade (§2.5). Decide deliberately whether that is wanted before users hit it.

### 6.2 Estate CAP absent-vs-false — re-run the pre-rebase verification

This is the check that was performed across a full restart before the rebase; re-run it verbatim
so the result is comparable.

1. Boot the region. In the estate tools, **disable** a setting the CAP owns and that the viewer
   does *not* send on every save — `allow_environment_override` or `override_public_access`
   (which stores as `TaxFree`'s negation) are the ones that carried the original defect.
2. Perform an **unrelated** estate save (change something else entirely — e.g. toggle
   `allow_direct_teleport`, then save).
3. **Observe immediately:** the setting from step 1 is still disabled in the estate dialog.
4. **Restart the region server fully** (not a region restart — the original verification was
   across a full OpenSim restart).
5. **Observe after restart:** the setting is *still* disabled, and
   `SELECT * FROM estate_settings WHERE EstateID = <id>` shows the expected 0.

- **Failure:** the setting reverts to enabled at step 3, step 5, or reads 1 in the database. That
  means an omitted key is again being read as a value — i.e. the nullable parse regressed, or
  `ApplyEstateChangeInfo` is applying a `HasValue == false` field.
- **Also check the negation specifically:** `override_public_access` maps to `TaxFree` inverted.
  Set public access off, save something unrelated, restart, confirm `TaxFree` did not flip.

### 6.3 Parcel access/ban persist — the UpdateAccessList fix

1. On a test parcel, **add a ban entry** for a throwaway avatar.
2. **Observe in the log:** `ParcelAccessListUpdate from <agent> applied to local land <id>
   ("<name>"): flags 0x…, N entries` (the branch's instrumentation, now `LogDebug`, which does
   reach the Serilog file sink).
3. **Query immediately, without restarting:**
   `SELECT * FROM landaccesslist WHERE LandUUID = '<parcel uuid>';` — the row must already be
   there. Before the fix it would not be until some unrelated parcel write flushed it.
4. **Restart the region server.** Confirm the ban entry is still present and still enforcing.

- **Failure:** empty `landaccesslist` at step 3, or the entry gone after restart.
- **Also exercise the negative paths** and confirm the corresponding diagnostics appear and the
  alert reaches the viewer: an update with flags that are neither access nor ban; an update on a
  TaxFree estate; an update from an avatar lacking the manage power (expect
  `You do not have permission to change the access or ban list for this parcel.`).

### 6.4 UseBanList clobber — the `ComputeSavedFlags` fix

This is the loop the fix exists to break, so drive it end to end:

1. With the ban entry from §6.3 in place, confirm `UseBanList` is set:
   `SELECT LandFlags FROM land WHERE UUID = '<parcel uuid>';` — bit `0x40000000`
   (`ParcelFlags.UseBanList`) must be set.
2. Open the parcel's **About Land → Options** (or any tab other than Access) and **save** without
   changing anything. This is the path that retransmits the whole cached flag word.
3. **Re-query `LandFlags`.** `UseBanList` must **still** be set.
4. **Restart the region server**, then repeat steps 2–3 once more — the original defect was
   self-sustaining specifically after a restart with the flag already zero.

- **Failure:** `UseBanList` clears at step 3 or after the restart, while the ban row remains in
  `landaccesslist`. That is exactly the "persisting a ban that no longer enforces" state.
- Note the fix is deliberately scoped to `UseBanList` only: `UseAccessList` remains
  viewer-authoritative by design, so do **not** treat `UseAccessList` clearing as a failure.

### 6.5 Provider sanity — MySQL and SQLite

Covers the §4.3 unknowns. Run whichever backend the grid uses; run both if both are configured.

1. After §6.2–§6.4, confirm no `MySqlConnector` / `System.Data.SQLite` exceptions appear in
   `OpenSim.Server.RegionServer.log` — particularly `InvalidCastException` from a `Convert.To*`
   on an unexpected CLR type, which is the specific shape a driver-bump type-mapping change would
   take.
2. Confirm boolean round-trip explicitly: read `estate_settings` for a region, compare every
   boolean column against what the estate dialog displays. Any column reading `1` while the dialog
   shows disabled (or vice versa) is a mapping failure.
3. Confirm `land.LandFlags` is a plausible flag word (not truncated, not sign-extended) — it is
   read as `uint` via `Convert.ToUInt32`, so a signed/unsigned regression would show as a wildly
   large or negative value.
4. **For SQLite specifically**, since the added `SQLite 3.53.4` package is the least-understood
   item (§4.3 item 3): confirm the region's SQLite file opens, that `land` and `landaccesslist`
   read back after a restart, and note the engine version actually loaded if it can be observed.

### 6.6 Parcel overlay — #189

Cheap and worth doing since it is the only upstream behaviour change in this area:

- Set a parcel **for sale at price 0** with the `ForSale` flag on. On the minimap it must now
  render as for-sale (yellow), where previously it would not.
- Confirm a parcel that is **not** for sale but has a non-zero `SalePrice` left over does **not**
  render as for-sale.
- **Failure:** either case rendering the old way means #189 did not take effect.

### What this plan does not cover

- Transactional atomicity of `StoreLandObject` (§4.3) is a pre-existing design property; proving
  the crash window requires deliberately killing the process mid-write, which is out of scope for
  a verification pass.
- PGSQL is untested here; `PGSQLEstateData.cs` is logger-only across the span but the grid's
  backend is assumed to be MySQL and/or SQLite.
- Slice 2's `landvoicemoderation` table does not exist yet and is not exercised.

---

## Summary of items needing attention

| # | Item | Severity | Action |
|---|---|---|---|
| 1 | #188 activates any `DenyIdentified`/`DenyTransacted` already stored, changing who may enter an estate (§2.5) | **Check before boot** | Run the §6.1 query; decide deliberately |
| 2 | `MySqlConnector` 2.5.0→2.6.1 is the real MySQL-side change (not Pomelo→Microting, which does not touch land/estate) (§4.1) | **Needs runtime check** | §6.5 |
| 3 | Added `SQLite 3.53.4` native package alongside the `System.Data.SQLite` 2.0.3→2.0.4 bump; effect on the loaded engine unknown (§4.3 item 3) | **Needs runtime check** | §6.5 item 4 |
| 4 | `StoreLandObject` is non-atomic (process lock, no DB transaction); the branch's persist fix makes that window fire on every access-list edit (§4.3) | Pre-existing, now more exposed | Note for slice 2; consider a transaction later |
| 5 | Estate CAP absent-vs-false verification should be re-run across a full restart post-rebase (§6.2) | **Do before deploy** | §6.2 |

**Both branch persist fixes remain correct and necessary** — `UpdateAccessList` and
`UpdateLandProperties` are byte-identical at both revisions, and upstream still performs no store
in `UpdateAccessList`. **The estate CAP work does not interact with #188** — the CAP never reads
or writes those two flags, and the nullable never reaches the database. **#189 shares no persist
path with either fix.** All four doubly-modified files auto-merged with no conflict, and the merge
is a verified clean union. No fixes were made.
