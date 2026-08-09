## Summary

BinaryFormatter resolves .NET types from the byte stream it deserializes, which makes every place it reads persisted or foreign data a remote-code-execution vector — and it is removed outright in .NET 9, so any remaining use blocks a future .NET 9 build. This series replaces every live BinaryFormatter path in the tree with an explicit, fixed-type serialization format, then drops the `EnableUnsafeBinaryFormatterSerialization` opt-in. Ported from Legion Grid (tag `port-source-2026-07-18`), where these changes run in production. Four commits in dependency order:

1. **FlotsamAssetCache** — on-disk asset cache moves from BinaryFormatter to a fixed-type `XmlSerializer(typeof(AssetBase))` for both read and write; no type is ever resolved from the stream. The cache directory gains a `format2` version segment so legacy BinaryFormatter cache files are never fed to the new reader — the cache simply regenerates.
2. **YEngine script-state migration** — the Ser.\*-tagged migration stream replaces the BinaryFormatter opcodes: `THROWNEX` becomes `THROWNEX2` (encodes only the separately-sent thrown LSL value), the catch-all `SYSERIAL` writer becomes a `SYSUNSUP` refusal marker, and on read all legacy/unsupported opcodes abort the restore (caught by `LoadScriptState` → script restarts clean at `state_entry`). `String2SysType` no longer falls back to `Type.GetType(str, true)`, and the XEngine-format XML state import is gated by an `IsAllowedXStateType` allow-list. `migrationVersion` is intentionally unchanged, so ordinary script state round-trips as before.
3. **KeyframeMotion** — explicit `KFM1` binary format (magic + version + hand-written fields via BinaryWriter) with bounds validation on blob size and frame counts before any allocation; legacy blobs and malformed input are refused (callers already treat null as "no keyframe motion"). The format is byte-compatible with Legion's KFM1, so OARs / prim state moved between the grids interoperate.
4. **Flag removal** — with zero live BinaryFormatter paths left (grep-confirmed: no `new BinaryFormatter`, no `Formatters.Binary` using anywhere), `EnableUnsafeBinaryFormatterSerialization` is removed from the three sites present on this base: `Directory.Build.props` and the GridServer/RegionServer `runtimeconfig.template.json` entries. Safe only after 1–3.

## Security note

This closes the type-resolution RCE surface in script-state restore and keyframe restore — both of which can process attacker-influenced bytes (foreign objects, archives, region crossings) — and in the asset cache. The new readers also add allocation bounds (KFM1 size/count limits, YEngine type allow-list), removing the ability of a hostile blob to force huge allocations or instantiate arbitrary named types.

## One-time deployment costs

None of these are data loss:

- **Asset cache**: regenerates from scratch in `assetcache/format2/` — a cold-cache warmup period, not loss. Old files remain in the parent directory and can be deleted manually to reclaim disk.
- **YEngine**: ordinary script state is unaffected. Only the rare state carrying an in-flight serialized exception or a catch-all system object aborts restore, and that script restarts clean at `state_entry`.
- **KeyframeMotion**: in-progress keyframe animations reset once on first load after upgrade — the object rests at its persisted position and a script can restart the motion. Newly saved state is KFM1 and round-trips normally.

## Testing

- `Tranquillity.sln` builds with 0 errors on top of current `develop` (`cd3b07b1f1`); the `SYSLIB0011` obsolescence warnings are eliminated by the conversions.
- Both modified `runtimeconfig.template.json` files re-validated as JSON.
- The YEngine and KeyframeMotion changes (and the asset-cache change) are running on Legion Grid's live deployment.
- Operators should expect the one-time costs above on first start after upgrading; no migration steps are required.
