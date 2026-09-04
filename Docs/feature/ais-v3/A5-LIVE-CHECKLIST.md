# A5 — the live checklist

**What this is.** The ordered in-world run for the first region with `AIS_Enabled = true`. Every step names what
to do, what the viewer does when it works, and the symptom when it does not. **Nothing in the AIS implementation
has ever met a real viewer**; 114 unit and HTTP tests pass against fakes, which proves the shapes, not the system.

**Why the order matters.** Risk A-R1: once `InventoryAPIv3` is in the seed cap, the LL viewer routes deletes,
purges, slams and creates through it with **no fallback** (spec §1g). A failure is not a degraded experience, it is
an operation that silently does not happen. So the read-only steps come first: if step 1 is wrong, stop and turn
the flag off before touching anything mutating.

---

## Before you start

| # | Do | Confirms |
|---|---|---|
| 0a | Set `AIS_Enabled = true` in the **region's own section** only — `[Ebony]`, not `[AIS]`. Restart the region. | The per-region flag, not a grid-wide flip |
| 0b | Console: `grep "\[AIS\]" OpenSim.Server.RegionServer<date>.log` | One line per enabled region: `region <name> advertises InventoryAPIv3, LibraryAPIv3`. **A second region in that output means the flag leaked — stop.** |
| 0c | On another region, confirm **no** `[AIS]` advertise line | The other regions are untouched |
| 0d | Take a full inventory backup for the test avatar (console `save iar` or a DB dump of `inventoryitems` / `inventoryfolders` for that PrincipalID) | Every mutating step below is destructive; this is the undo |

**Test avatars:** Truly Bazar and Aleric Fenwood, as in the SSB work. Never Legion.

---

## Phase 1 — read only (steps 1–3)

Nothing here writes. If any step fails, turn the flag off; do not continue.

### 1. Full inventory load after a cache clear

**Do:** log out; delete the viewer's inventory cache (`<viewer settings>/<user>/*.inv.llsd.gz`); log in to the test
region; open Inventory and let it settle.

**Works:** the inventory tree fills in, folder by folder, and the item count stops growing. Every folder you open
already has its contents.

**Fails:** folders stay empty or show "Fetching…" forever; the item count climbs and never settles; the same folder
is requested over and over. That last one is the specific signature of a folder we returned without all three
`_embedded` collections — the viewer never gets a descendent count, never accepts the version, and re-fetches
forever (risk A-R3).

**Server side:** `grep -c "InventoryAPIv3" <log>` to see the cap being hit; the request path in the HTTP log shows
`/category/<id>/children?depth=…`. Expect `depth=50` for the recursive sweep and `depth=0` for single folders, and
nothing else — those are the only two the viewer sends (spec §1c-bis).

### 2. Open a deep folder

**Do:** open a folder at least three levels down that was **not** expanded during step 1.

**Works:** contents appear immediately or after one brief fetch.

**Fails:** it stays empty, or reopening it re-fetches every time.

### 3. Current outfit reads back

**Do:** open Appearance → Wearing.

**Works:** every worn item is listed, with its real name rather than "(loading)".

**Fails:** blanks or missing entries — the COF links resolved but their targets did not
(`GET /category/current/links` must carry the link *targets* in `_embedded.items`).

---

## Phase 2 — single-object mutations (steps 4–8)

### 4. Rename an item

**Do:** rename any item.

**Works:** the new name sticks, survives closing and reopening the folder, and survives a relog.

**Fails:** the name reverts after a moment (the viewer applied it optimistically and our response did not confirm
it), or it reverts on relog (we did not write it). The likely cause of the first is a missing
`_updated_category_versions` entry for the parent — without it the viewer discards the update entirely
(spec §1d-bis).

**Server side:** `grep "PATCH /item" <log>`.

### 5. Rename a folder

**Do:** rename a folder you created yourself (not a system folder).

**Works:** as above.

**Fails:** as above. A category PATCH must list **both** the folder and its parent in
`_updated_category_versions`; only one is a bug.

### 6. Delete an item

**Do:** right-click an item → Delete.

**Works:** it moves to Trash and stays there through a relog.

**Fails:** it reappears after a moment or after relog.

### 7. Delete a folder **outside** Trash

**Do:** create a folder, put nothing in it, delete it from where it sits.

**Works:** it is gone.

**Fails:** nothing happens at all. **This is the step most likely to fail**, and if it does the cause is almost
certainly the Robust side: A2b added an `ONLYIFTRASH` field to the inventory wire, and a Robust that predates it
ignores the field and keeps the old trash-only behaviour. See "The Robust question" below.

**Server side:** `grep "DELETE /category" <log>`. A 500 naming the folder means the service declined the delete.

### 8. Empty Trash

**Do:** right-click Trash → Empty Trash. Have at least one folder **and** one loose item in there first.

**Works:** Trash empties completely, including the contents of the subfolder, and stays empty through a relog.

**Fails:** Trash still shows its contents afterwards — the purge response must **enumerate** the direct children
(spec §1d-bis); unlike a folder delete, nothing on the viewer side sweeps them for us. Partially emptied means the
service refused part of it; the response names the survivors.

---

## Phase 3 — outfits, the destructive ones (steps 9–10)

**These are the steps that can strip an avatar.** Do them on Truly first, not on an avatar whose outfit matters.

### 9. Wear an outfit (slam)

**Do:** Appearance → Outfits → wear a saved outfit.

**Works:** the avatar changes to that outfit; Wearing lists exactly the new items and none of the old.

**Fails, in order of seriousness:**
- **The avatar ends up wearing nothing / partially dressed.** This is the failure the slam ordering is built to
  make impossible (links are created before the old ones are removed), so if you see it, stop and report it — the
  ordering is wrong, not just a transient.
- **Duplicated attachments or doubled clothing layers.** The new links were created and the old ones were not
  removed. Recoverable by wearing the outfit again. This is the known window (Ledger A-Q10): there is no
  transaction under a slam.
- The outfit does not change at all: the slam was refused; check the log.

**Server side:** `grep "PUT /category" <log>`.

### 10. Take off a garment (slam)

**Do:** right-click a worn garment → Take Off.

**Works:** it comes off, the rest of the outfit is untouched, and it stays off through a relog.

**Fails:** the garment comes back; or **other** garments come off with it — the slam replaced the folder's links
with the wrong set.

---

## Phase 4 — create and copy (steps 11–12)

### 11. Create a folder

**Do:** Inventory → + → New Folder, then rename it.

**Works:** the folder appears, keeps its name, and survives a relog.

**Fails:** the folder does not appear at all, or appears and vanishes on relog.

**Server side:** `grep "POST /category" <log>`.

### 12. Copy a library outfit

**Do:** open Library → an outfit folder → right-click → Copy to Inventory (or drag it into your inventory).

**Works:** the folder and its contents appear in your inventory, nested as they were in the library, and the items
are usable — wear one to confirm the permissions came across.

**Fails:** nothing arrives; or the folder arrives empty; or the items arrive but cannot be worn (permissions were
degraded — the library copy must carry the source's own masks, not `NextPermissions`).

**Server side:** `grep "COPY /category" <log>`. The destination folder id travels in the `Destination` header.

---

## Phase 5 — the things that are known not to work (steps 13–15)

Confirm these behave as documented rather than in some worse way.

### 13. Creating an inventory **item** — RESOLVED, it just works

**Do:** Inventory → + → New Notecard (or New Script, New Clothing).

**Expected:** the notecard **is** created, normally, and AIS is never involved. Settled in A11 from the source:
the AIS arm of `create_inventory_item` is inside `#ifdef USE_AIS_FOR_NC`
(`llviewerinventory.cpp:1120`-`:1166`), the macro is not defined, so control falls unconditionally to the legacy
`CreateInventoryItem` UDP send at `:1169`. Confirmed in the 2026-09-04 run: the item was created over UDP and the
only AIS request was a `FetchItem` syncing the result.

**This step was once "the single biggest argument against flipping this flag more widely". It is not any more** —
the code is LL's, so stock viewers behave identically, and our 501 route is simply never reached for item
creation. Watch only that the item appears and survives a relog.

### 14. Hypergrid folder deletion — expected refusal

**Do:** only if this region serves hypergrid visitors. As an HG visitor, try to delete a folder.

**Expected:** refused. `HGInventoryService` and `HGSuitcaseInventoryService` answer NOGO for folder deletion
whatever the flag says, so the verification step turns that into a 500.

### 15. Folder thumbnail and favourite — silently dropped

**Do:** set a folder thumbnail, or mark a folder as a favourite.

**Expected:** the operation appears to succeed and the setting does not persist across a relog. This tree's
`InventoryFolderBase` has no column for either, so both are accepted and dropped.

---

## The Robust question — step 7 will fail until Robust is redeployed

A2b added an optional `ONLYIFTRASH` field to the inventory wire so that AIS could ask for a folder delete that is
not restricted to Trash. It was made backward-compatible in both directions on purpose: the simulator sends the
field only when it is `false`, and the Robust handler defaults it to `true` when it is absent
(`XInventoryServicesConnector.DeleteFolders`, `XInventoryInConnector.HandleDeleteFolders`).

Legion Grid resolves inventory **remotely**: `config-include/Grid.ini:12` sets
`InventoryServices = "RemoteXInventoryServicesConnector"`, and `GridCommon.ini:39` points it at
`http://127.0.0.1:8003`. Every folder delete therefore crosses to Robust.

**So until the grid server is redeployed with the A2b change, step 7 (delete a folder outside Trash) will fail.**
The old Robust ignores the new field, keeps its trash-only behaviour, deletes nothing and still reports success;
the AIS route catches that by re-reading the folder and answers 500. Nothing is corrupted — the operation simply
does not happen, and the checklist records it.

Emptying Trash (step 8) and deleting a folder **inside** Trash are unaffected: those satisfy the old gate.

A5 deployed the region side only. Redeploying Robust is a separate decision and a separate outage, and it is
John's to make.

## Firestorm is the only client available here

**Superseded by Ledger P-3 (A8, 2026-09-04).** This section was written expecting the LL viewer to be the
primary run and Firestorm a second pass. That is not possible on this grid: the stock LL viewer will not start
against it, because its Vivox voice component refuses to initialise outside SL. **Every run is a Firestorm run,
and there is no control.**

Firestorm remains a test client and never an authority (Ledger P-1). What changes is the reading of a green
result: it means Firestorm is satisfied, not that the protocol is right. Anything observed only in Firestorm
must be checked against the LL viewer source before it is relied on — step 13's legacy fallback is the live
example (`A5-RUN-2026-09-04.md`). These are the steps where Firestorm's own machinery differs most, so they
carry the least transferable evidence:

- **1** (full load) — Firestorm's fetch pacing differs;
- **9 and 10** (wear / take off) — Firestorm has its own outfit machinery and may still send
  `AvatarNowWearing` over UDP alongside the slam (open question A-Q6 in the SSB ledger);
- **8** (Empty Trash);
- **12** (library copy).

If Firestorm and the LL viewer **source** disagree on any of these, the source is right and the difference is
recorded, not fixed against Firestorm. The disagreement has to be found by reading the source, because the
viewer itself cannot be run here.

---

## Stopping

Turn `AIS_Enabled` back to `false` in the region section and restart the region. The caps disappear from the seed
response and every viewer returns to the legacy paths on its next login. Nothing in inventory needs undoing for the
flag itself — but anything steps 4–12 changed is real, which is what step 0d's backup is for.

**Report:** for each step, pass / fail / not-run, and for any failure the log lines around it. That report is what
decides whether the flag goes anywhere near a second region.
