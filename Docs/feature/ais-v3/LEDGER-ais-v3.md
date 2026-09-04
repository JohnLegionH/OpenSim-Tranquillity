# Ledger — AIS v3 (Inventory API v3)

Living document. Update at every session close. **Date opened:** 2026-09-03. Branch `feature/ais-v3` on the
deployed tree `db7c746248`.

## 0. Principles

| ID | Principle |
|---|---|
| P-1 | The authority for the AIS v3 protocol is the LL viewer source (`indra/newview/llaisapi.h`, `llaisapi.cpp` and the AIS call sites in `llinventorymodel.cpp` / `llviewerinventory.cpp`), captured in `AIS-V3-SPEC.md` with file:line. Firestorm is a test client only; no code, config, threshold or test may be keyed on Firestorm. Later sessions implement the spec document and do not open the viewer tree. |
| P-2 | Nothing in the AIS handler may take a `Scene` or `ScenePresence`: only an agent id, an `IAisInventoryBackend` and the request. Phase 2 hosts the same handler on Robust. |

## 1. Decisions

| ID | Decision | Status | Ruled | Notes |
|---|---|---|---|---|
| A-D1 | Phase 1 is region-side: `AISv3Module` (`ISharedRegionModule`, `[AIS] Enabled = false`) registers `InventoryAPIv3` per agent from `OnRegisterCaps`; all inventory work goes through `IAisInventoryBackend`, so the handler is host-agnostic | **Ruled** | 2026-09-03 | Spec §Tree state T1/T6: registration + the viewer's own request of the cap name are the two conditions; module home `Source/OpenSim.Region.ClientStack.LindenCaps/AIS/` |
| A-D2 | Merge order: AIS first, before SSB's server-initiated bake work lands on `master` | **Ruled** | 2026-09-03 | SSB (`feature/ssb-appearance`) reads COF `Version` from the inventory service and does not depend on AIS (ADR-006 there); AIS changes the viewer's inventory traffic wholesale (§1g) and must be proven alone |
| A-D3 | `LibraryAPIv3` is not advertised until the library backend exists; A0 registers only `InventoryAPIv3` | Proposed | — | The viewer asks for both (`llaisapi.cpp:72-76`); `FetchItem`/`FetchCategory*` with `type == LIBRARY` and `CopyLibraryCategory` go to the library cap and fail closed without it |
| A-D4 | The `[AIS]` switch stays `false` in every shipped ini until every §1a operation returns something other than 501 | Proposed | — | Risk A-R1 |

## 2. Open questions

| ID | Question | Owner | Blocks | Status |
|---|---|---|---|---|
| A-Q1 | Item / link / category field set as `unpackMessage(const LLSD&)` reads it (`indra/llinventory/llinventory.cpp`, `llviewerinventory.cpp`) — not in A0's permitted files | A1 (needs permission to read those two files) | A1 | open |
| A-Q2 | Wire encoding the viewer sends and accepts for AIS bodies (LLSD XML vs binary) and the `Destination` header of COPY — both live in `llcorehttputil.cpp` | A1 | A1 (COPY), A2 | open |
| A-Q3 | Request bodies of `SlamFolder` (built by `LLAppearanceMgr`), `UpdateItem` / `UpdateCategory` (which fields), `CreateInventory` `items` / `links` arrays | A1 | A2 | open |
| A-Q4 | Which viewer code paths call the remaining `Fetch*` operations and `CopyLibraryCategory` (`llinventorymodelbackgroundfetch.cpp`, `llappearancemgr.cpp`, ...) | A1 | A3 | open |
| A-Q5 | The viewer's retry policy on 403 with `depth > 0` (caller re-requests at a lower depth?) and on transport errors | A1 | A3 | open |
| A-Q6 | Does OpenSim need to synthesise `_updated_category_versions` for every folder the data layer bumped (V6 shows the increment happens per store/delete/move) — i.e. the handler must re-read `GetFolder` after each mutation to report the new version | A2 | A2 | open (spec §1e says yes) |
| A-Q7 | Whether the sim should also serve `simulate` (named in the A0 brief; the viewer never sends it) | — | — | open, low |
| A-Q8 | **Depth semantics are our contract, not the viewer's.** A1 implemented `depth=N` as "N counts generations expanded below the requested folder" (`AisInventory.Walk`). Nothing in the permitted viewer files fixes this: the viewer sends a number and consumes whatever arrives, and its only hard rule is the all-three-or-none one of A-R3. If SL's AIS expands a different amount for the same N, our tests pin the wrong shape and a real viewer will over- or under-fetch. | **John** | A5 (advertising the cap) | **Open.** Must be checked against a live SL AIS capture — one `GET /category/{id}/children?depth=1` against an SL region, comparing how many generations come back — before the cap is advertised anywhere. |

## 3. Risks

| ID | Risk | Mitigation |
|---|---|---|
| A-R1 | **Partial AIS is worse than none.** Once `InventoryAPIv3` is in the seed cap, the LL viewer routes delete, purge, slam and create through it with no fallback (spec §1g); a 501 on any of them breaks that operation for every LL-viewer user on the region | `[AIS] Enabled` defaults to `false` (A-D4); A0 registers nothing when disabled; the flag is flipped only on a test region once every operation is implemented and the harness is green |
| A-R2 | **SlamFolder atomicity.** PUT `/category/{id}/links` replaces the folder's links as one operation and the viewer expects one `_updated_category_versions` entry for it; OpenSim has no transactional multi-item write (`DeleteItems` then `AddItem` per link, each bumping the folder version) — a crash or a concurrent UDP `LinkInventoryItem` between them leaves a half-slammed COF | Implement slam as delete-all-links-then-add in one handler call under a per-folder lock, read the version once at the end; document that it is not transactional across the service boundary; consider a service-side `SlamLinks` call in Phase 2 |
| A-R3 | Descendent-count / version drift, **in both directions** (reworded A2 on A1's finding). Too few collections: a folder returned without all three never gets a descendent count, so the viewer never accepts its version and re-fetches it forever. Too many: a **partial** view padded with empty siblings gives the viewer a descendent count that is wrong but plausible, and it then banks a version for content it never received — the worse failure, because it is silent. | A **complete** view (`/children`) carries all three collections at every expanded level, even when empty. A **partial** view (`/categories`, `/links`, a subset) carries only the collection asked for, and no empty siblings. For a Current Outfit or Outfit folder `links` alone is a complete count by the viewer's own rule (`llaisapi.cpp:1466-1482`). |
| A-R4 | Links emitted as items (`AssetType.Link` inside `items`) would be parsed as items, not links, and the outfit machinery breaks | The backend splits links out of `InventoryCollection.Items` into `_embedded.links` (spec §1c, T5); golden fixtures pin the shape |
| A-R5 | Error bodies that carry `item_id`/`category_id` + `parent_id` are parsed as updates (spec §1f) | The 501 body is a flat map with `error_code`, `error_description`, `message` only; fixture `error.xml` pins it |
| A-R6 | **Listing a folder the viewer does not have in `_updated_category_versions` dereferences null in the viewer.** `doUpdate` does `LLViewerInventoryCategory *cat = gInventory.getCategory(id); ... if (cat->getVersion() != version)` with **no null check** (`llaisapi.cpp:1760-1762`). A server that reports a version for a folder the viewer has never fetched crashes it. | Only ever list folders the operation itself touched and the viewer necessarily knows — the parent of the object it just named, or the object itself. Never list a folder speculatively, and never list the whole path to the root. |

## 4. Session log

| Date | Session | Commit | Result | Decisions/questions raised |
|---|---|---|---|---|
| 2026-09-03 | A0 | see DONE in the session report (spec commit, skeleton commit) | `AIS-V3-SPEC.md`: 16 operations, 7 meta keys + 5 `_embedded` collections extracted with viewer file:line; tree state T1–T6; `AISv3Module` skeleton (disabled by default, 501 on every route), `IAisInventoryBackend`, `AisRouter` with one NUnit test per URL shape, golden envelope fixtures, HTTP-level 501 test (27 tests, new project `Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests`; the existing caps test project is not in the solution and does not compile at HEAD); `Source/OpenSim.Services.AISv3` (webapi template, never in the solution) deleted | A-D1, A-D2 ruled; A-D3, A-D4 proposed; A-Q1..A-Q7; A-R1..A-R5 |

## 5. Cross-references

- `Docs/feature/ssb-appearance/S0a-VERIFICATION.md` V6 (folder-version increment sites) and V9 (Robust handler pattern for Phase 2) — reused, not re-derived.
- `Docs/feature/ssb-appearance/ADR-SET-ssb-appearance.md` ADR-006 (SSB reads COF `Version` from the inventory service; no AIS dependency).
