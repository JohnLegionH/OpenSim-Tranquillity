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

## 3. Risks

| ID | Risk | Mitigation |
|---|---|---|
| A-R1 | **Partial AIS is worse than none.** Once `InventoryAPIv3` is in the seed cap, the LL viewer routes delete, purge, slam and create through it with no fallback (spec §1g); a 501 on any of them breaks that operation for every LL-viewer user on the region | `[AIS] Enabled` defaults to `false` (A-D4); A0 registers nothing when disabled; the flag is flipped only on a test region once every operation is implemented and the harness is green |
| A-R2 | **SlamFolder atomicity.** PUT `/category/{id}/links` replaces the folder's links as one operation and the viewer expects one `_updated_category_versions` entry for it; OpenSim has no transactional multi-item write (`DeleteItems` then `AddItem` per link, each bumping the folder version) — a crash or a concurrent UDP `LinkInventoryItem` between them leaves a half-slammed COF | Implement slam as delete-all-links-then-add in one handler call under a per-folder lock, read the version once at the end; document that it is not transactional across the service boundary; consider a service-side `SlamLinks` call in Phase 2 |
| A-R3 | Descendent-count / version drift: a folder returned without all three `_embedded` collections never gets a version on the viewer and is re-fetched forever (spec §1c) | Every category the handler emits carries `categories`, `items` and `links`, even when empty |
| A-R4 | Links emitted as items (`AssetType.Link` inside `items`) would be parsed as items, not links, and the outfit machinery breaks | The backend splits links out of `InventoryCollection.Items` into `_embedded.links` (spec §1c, T5); golden fixtures pin the shape |
| A-R5 | Error bodies that carry `item_id`/`category_id` + `parent_id` are parsed as updates (spec §1f) | The 501 body is a flat map with `error_code`, `error_description`, `message` only; fixture `error.xml` pins it |

## 4. Session log

| Date | Session | Commit | Result | Decisions/questions raised |
|---|---|---|---|---|
| 2026-09-03 | A0 | see DONE in the session report (spec commit, skeleton commit) | `AIS-V3-SPEC.md`: 16 operations, 7 meta keys + 5 `_embedded` collections extracted with viewer file:line; tree state T1–T6; `AISv3Module` skeleton (disabled by default, 501 on every route), `IAisInventoryBackend`, `AisRouter` with one NUnit test per URL shape, golden envelope fixtures, HTTP-level 501 test (27 tests, new project `Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests`; the existing caps test project is not in the solution and does not compile at HEAD); `Source/OpenSim.Services.AISv3` (webapi template, never in the solution) deleted | A-D1, A-D2 ruled; A-D3, A-D4 proposed; A-Q1..A-Q7; A-R1..A-R5 |

## 5. Cross-references

- `Docs/feature/ssb-appearance/S0a-VERIFICATION.md` V6 (folder-version increment sites) and V9 (Robust handler pattern for Phase 2) — reused, not re-derived.
- `Docs/feature/ssb-appearance/ADR-SET-ssb-appearance.md` ADR-006 (SSB reads COF `Version` from the inventory service; no AIS dependency).
