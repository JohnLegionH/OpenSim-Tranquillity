# A7 — two Current Outfit folders, and which one wins

**Date:** 2026-09-04. **Region:** Ebony, `AIS_Enabled = true`. **Avatar:** Truly Bazar
(`a7d2ff2e-dc32-44d8-aa61-3d22070a4964`). **Checklist step:** 10, take off a garment.

**Symptom:** the skirt came off and was back after a relog. The viewer slammed
`71c3c184-410b-4dae-b20a-855741cf1faf` twice (12:36, 12:37); at login 12:38 it fetched `/category/current/links`
and immediately rebuilt links in `71c3c184…`. The avatar has two type-46 folders:

| folder | name | type | version |
|---|---|---|---|
| `71c3c184-410b-4dae-b20a-855741cf1faf` | Current Outfit | 46 | **457** — the one the viewer uses |
| `52c327c4-cb7d-4365-a7f0-62a6f7545265` | Current Outfit | 46 | 1 — the one we returned |

## The short version

**Our `"current"` alias resolves to an arbitrary one of the agent's type-46 folders, and it picked the wrong
one.** The take-off did happen — it was written to a folder no viewer reads. The relog then read the real COF,
which still had the skirt link, so the skirt came back. Nothing was lost and nothing was corrupted; the write
landed in the wrong place.

## (a) What the resolution actually promises: nothing

`AisInventory.GetCurrentOutfit` (`AisInventory.cs:106-107`) → `IAisInventoryBackend.GetFolderForType`
(`IAisInventoryBackend.cs:20`) → `InventoryServiceBackend` (`AISv3Module.cs:172`) → `IInventoryService`. On Legion
Grid inventory is remote (`RemoteXInventoryServiceConnector.cs:175-178` → `XInventoryServicesConnector.cs:186-195`,
`METHOD=GETFOLDERFORTYPE`) → Robust `XInventoryInConnector.cs:260` → `XInventoryService.GetFolderForType`.

Robust runs the plain service for the region-facing port (`Robust.ini:107-108`,
`LocalServiceModule = "OpenSim.Services.InventoryService.dll:XInventoryService"`), so this is the code that answers:

```csharp
private InventoryFolderBase GetSystemFolderForType(InventoryFolderBase rootFolder, FolderType type)  // XInventoryService.cs:272-294
{
    if (type == FolderType.Root)
        return rootFolder;

    XInventoryFolder[] folders = m_Database.GetFolders(
            ["agentID", "parentFolderID", "type"],
            [rootFolder.Owner.ToString(), rootFolder.ID.ToString(), ((int)type).ToString()]);

    if (folders.Length == 0)
        return null;

    return ConvertToOpenSim(folders[0]);          // <-- first row wins. No ordering, no tie-break, no warning.
}
```

`folders[0]`, and the query behind it has **no `ORDER BY` and no `LIMIT`**:
`MySQLXInventoryData.GetFolders` (`:56-59`) → `MySqlFolderHandler` (no `Get` override, `:250-256`) →
`MySQLGenericTableHandler.Get(string[], string[])` (`:154-157`), which delegates to the three-argument overload
with `options` = `String.Empty` (`:159-185`) and builds `select * from inventoryfolders where ... `.

So the answer is **not deterministic in any sense the code can rely on**. Row order is whatever InnoDB and the
optimiser produce for that index choice; it can change after a page split, an `ANALYZE`, or a different plan, with
no schema or data change at all. The schema offers no help either — `inventoryfolders`
(`InventoryStore.migrations:30-40`) has `PRIMARY KEY (folderID)` and non-unique keys on `agentID` and
`parentFolderID`; there is **no unique constraint on `(agentID, type)`**, so duplicates are legal.

**A consequence worth stating:** this query filters on `parentFolderID = rootFolder.ID`. Two rows can only both
match if **both COFs are direct children of the same root folder**. `52c327c4…` therefore certainly is.

## (b) Every site that creates a type-46 folder

| # | Site | When it fires | Guarded? | Parents to |
|---|---|---|---|---|
| 1 | `XInventoryService.CreateUserInventory` `:132-133` | account creation (`UserAccountService.cs:817`), `RemoteAdminPlugin.cs:2630`, **every Direct Delivery** (`DirectDeliveryPostHandler.cs:134, :217`), any region calling `CreateUserInventory` (`XInventoryInConnector.cs:205`) | yes — `GetSystemFolders` `:194-208` scans **all** root children with `Array.Exists`, so no `folders[0]` non-determinism | **the real root** |
| 2 | `HGSuitcaseInventoryService.CreateSystemFolders` `:185-186` | once per user, from `GetRootFolder` `:136-170` when they have no suitcase yet | yes, same `Array.Exists` shape | **the suitcase** (`:157, :164`) |
| 3 | `HGInventoryService` `:103-115` | HG visitor with no suitcase | — | creates only a suitcase, **no type-46** |
| 4 | IAR load | — | — | creates no type-46 (`InventoryArchiveReadRequest.cs:413` only maps `MyOutfits`→`Outfit`) |

Sites 2, 3 and 4 are **excluded by the parentage consequence in (a)**: site 2 parents its COF to the suitcase, so
that COF is a grandchild of root and `GetSystemFolderForType` can never return it. (Site 2 also does not loop:
`SetAsNormalFolder` is a no-op — its body is commented out at `:534-537` — so the suitcase keeps type 100 and
`GetSuitcaseXFolder` `:508-514` finds it on every later call.)

**That leaves site 1 as the only in-tree path that can put a second type-46 under the root.** Its guard is a
complete scan, but it is a **non-atomic read-then-write**: the folder list is read once at `:114` and the COF is
created at `:133`, after up to a dozen intervening DB round trips, with no transaction and no unique constraint to
catch a loser. Two overlapping calls for the same principal both read "no COF" and both create one.

Repeated invocation is normal, not exceptional: `DirectDeliveryPostHandler` calls `CreateUserInventory(buyer)` on
**every delivery**, commented "idempotent" (`:133-134`, `:216-217`) — idempotent only for as long as that guard
holds. That is a web handler, so concurrency is the default, and it explains a *subset* of accounts rather than
all of them.

**Honest limit on this conclusion.** Site 1 is the only site that *can* produce the observed row, and that part is
settled by the parentage argument. Which *invocation* did it is inference, and one read-only query would settle it
— see "What would confirm it" below. This session was told not to touch the live database, so it is recorded as
the leading explanation, not as fact.

## (c) Does this predate AIS? Yes

`GetSystemFolderForType` and the unordered query are untouched by this branch, and every consumer of
`GetFolderForType` inherits the same coin flip — `ScenePresence.cs:2237`, `AvatarFactoryModule.cs:1097, :1112`,
`UserAccountService.cs:850, :1042`, `InventoryTransferModule.cs:348` (Trash). **An OpenSim grid with no AIS at all
is equally affected.**

What differs is the blast radius, and it is not that AIS made the bug worse in kind:

- The **legacy** path tolerates it. The viewer builds its inventory from the login skeleton
  (`LLLoginService.cs:505` → `GetInventorySkeleton`, `XInventoryService.cs:210-228`), which is queried by
  `agentID` **only** and so returns *both* folders. The viewer then picks the COF itself and keeps using the same
  one; the server's opinion is never consulted for reads.
- **AIS** makes the server's opinion authoritative for writes: `/category/current/links` is resolved server-side
  (`AisHandler.cs:256, :478, :659`), so a slam is applied to whichever folder the server chose. Under AIS the
  disagreement between viewer and server becomes a silent lost write; under the legacy path it stays latent.

So AIS did not create the fault, it made an existing data fault visible — and it is visible as an operation that
silently does not happen, which is exactly the class of failure risk A-R1 was opened for.

## The rule chosen, and why

**Among all the agent's folders of the wanted type, take the highest `Version`; break ties on the lowest folder id
(ordinal).** Ground truth is the folder the viewer uses, and here that is `71c3c184…` at version 457.

Justification, from this tree rather than from preference:

- **Version is a direct proxy for "the viewer has been writing here."** A folder's version is bumped on every
  child add/remove — `MySqlFolderHandler.Store` `:283-291` and `Delete`/`MoveFolder` `:258-281` call
  `IncrementFolderVersion`, which is literally `update inventoryfolders set version=version+1 where folderID = ?`
  (`MySQLXInventoryData.cs:303-317`). The COF the viewer uses is by construction the one it writes links into, so
  it is by construction the one whose version climbs. 457 versus 1 is that history.
- **It only ever goes up.** The increment is `version+1`, and `XInventoryService.UpdateFolder` refuses to lower a
  system folder's version (`:423-427`, `return false`) and clamps a normal folder's up (`:439-440`). So the rule
  cannot be flipped by an ordinary update.
- **"Earliest created" is not implementable.** `XInventoryFolder` has exactly six fields — `folderName`, `type`,
  `version`, `folderID`, `agentID`, `parentFolderID` (`IXInventoryData.cs:32-45`) — and the table has no created
  or modified column (`InventoryStore.migrations:30-40`). There is no creation order to consult. Ruled out on
  evidence, not taste.
- **"Most descendants" is worse here, and costs more.** It needs a contents fetch per candidate, and it reads zero
  for a legitimately empty COF — which is precisely the state step 10 produces when it strips the last garment.
  Version survives an emptied folder; a descendant count does not.
- **Ties are broken, not left open.** Two folders both at version 1 (a fresh account with a fresh duplicate) have
  no usage history to separate them, so the tie-break only has to be *stable*: same answer on every call, on every
  region, forever. Lowest id ordinal is that.

**Resolution is done from the skeleton, not from `GetFolderForType`.** `GetInventorySkeleton`
(`XInventoryService.cs:210-228`) queries by `agentID` alone, so it sees every candidate the viewer sees, including
one that is not a direct child of root — which `GetSystemFolderForType` structurally cannot return. This also
means the fix does **not** depend on the unverified claim that `71c3c184…` is parented to root: it wins on version
wherever it sits. When the skeleton is unavailable or holds no folder of that type, the old
`GetFolderForType` call is used unchanged, so no behaviour is lost.

The rule is applied by folder **type**, not special-cased to COF, because every type resolves through the same
`folders[0]`.

## What would confirm the creation site

Read-only, and for John to run when he chooses — **not run in this session**:

```sql
SELECT folderID, folderName, type, version, parentFolderID
FROM   inventoryfolders
WHERE  agentID = 'a7d2ff2e-dc32-44d8-aa61-3d22070a4964'
ORDER  BY type, version DESC;
```

- If **both** type-46 rows share the root's `parentFolderID`, site 1 is confirmed as the source.
- If other system types (5 Clothing, 8 Objects, 10 Textures …) are **also** duplicated in pairs under root, it was
  one duplicated `CreateUserInventory` sweep — a race between two overlapping calls.
- If **only** type 46 is duplicated, the account was missing just its COF when two calls overlapped, which is what
  an older account that predates COF in the skeleton looks like.
- If the second row's parent is the suitcase, the parentage argument in (a) is wrong and the diagnosis must be
  reopened.

## What the dedupe should do — proposed, not built

Deliberately not written this session. For each affected agent:

1. **Back up first.** `inventoryfolders` and `inventoryitems` for the affected principals, and take the grid down
   or at least keep the accounts logged out — a COF being rewritten mid-merge is the one way to make this worse.
2. **Pick the keeper by the same rule the code now uses** — highest version, lowest id on a tie — so the dedupe and
   the running server can never disagree about which folder is real.
3. **Re-parent, do not delete.** Move any items and sub-folders from the losers into the keeper before removing
   the loser rows. The losers are expected to be empty (version 1 means nothing was ever added), so this should be
   a no-op — but a dedupe that assumes empty and is wrong destroys worn links.
4. **Delete the loser folder rows only after they are empty**, and bump the keeper's version so viewers refetch.
5. **Verify**: every agent has exactly one folder per system type, and `GetFolderForType` returns the keeper.
6. **Then** consider the unique index below, which is what stops it coming back.

## Ledger items this opens

- **A-R8** — no unique constraint on `(agentID, type)` for system folders. The in-code narrowing shrinks the race
  window but cannot close it; only a unique index can, and that is a migration plus a dedupe of existing data, in
  that order. Migration deliberately not written here.
- **A-Q13** — `InventoryFolderBase.Version` is `ushort` (`InventoryFolderBase.cs:67`) while the column is
  `int(11)`. A COF past 65535 wraps on the way through, which would make a version comparison — ours or anyone's —
  pick wrongly. Not reachable at version 457; worth fixing before it is.
