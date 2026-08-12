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
