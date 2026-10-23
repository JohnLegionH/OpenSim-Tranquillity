# OpenSim.ArchiveInspector

Reads an OAR or IAR and reports whose work is in it, before anything is loaded. It extracts nothing, rewrites nothing and
writes only to standard output and to the path given to `--json`.

```
OpenSim.ArchiveInspector <file.oar|file.iar> [--json <path>] [--all] [--stats]
```

| Option | Effect |
|---|---|
| `--json <path>` | also write the full report as JSON to `<path>` (the archive itself is refused as a target) |
| `--all` | list everything; without it each table shows the top 50 and the JSON keeps the first 100,000 records of each kind (counts and totals always cover everything) |
| `--stats` | print wall time, bytes read from the file and peak working set |

The kind (OAR or IAR) comes from the control file `archive.xml`, and from the paths in the archive when the control file does not say, never from the file extension.
Gzip and plain tar files are both accepted.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | read fully, no Warning (Notices and Info findings do not change the code) |
| 1 | partial: a corrupt entry, a size limit, or a truncated file; the report says which entry it stopped at |
| 2 | not an archive, or the file could not be opened |
| 3 | read fully with at least one Warning finding; the creator flags and the checks marked Warning below are Warnings |
| 64 | bad command line |

## What it reads

OAR 0.x (single region) and 1.x (multi region), IAR 0.x (creator as `ospa:n=First Last`) and 1.x (creator data), object XML in the xml2 and
original forms, coalesced objects, boxed objects inside object assets (followed recursively with a cycle guard and a depth limit of 8),
parcels, region settings, and the assets (type, size, and whether each is referenced). Each part's shape PCode is read so built-in trees and grass can be counted apart.
The bodies it parses are the control file, object files, inventory item files, parcel and region settings files, and assets of type
object, script, notecard, gesture, wearable, material and settings. Every other body (textures, sounds, meshes, animations) is streamed past without being held.

Binary payloads from other forks (binary object serializations, script state of another engine) are detected and counted by kind; they are never decoded.

## The creator analysis

Records (parts and items, boxed contents included) are grouped by creator id and, where creator data or an OSPA name gives one, by creator name.
Library content, blank creators and built-in trees and grass (parts whose shape PCode is Tree, NewTree or Grass, which carry whoever placed them as creator) are counted separately,
listed in the report with their counts, and left out of the shares.

Grouping by creator name only flags when the records that carry a name are at least 50% of the counted records (`AnalysisSettings.NameCoverageShare`).
Below that, an Info note (`NAME_COVERAGE_LOW`) says what the name grouping would have shown instead of flagging it. Every flag and note states its coverage, for example
"3,153 of the 3,205 records that carry a name; 3,205 of 20,969 counted records carry one".

- `SINGLE_CREATOR`: one creator is credited on every counted record, and there are at least 20.
- `DOMINANT_CREATOR`: one creator has 90% or more of the counted records, at least 20 records.

When a flag fires the report also says which of these the numbers match, each with its figures:

- `MATCH_SAME_ACCOUNT`: creator, owner and last owner are the same account on 95% or more of the records and no creator data names a different creator. Two readings fit: it is what a loader leaves when it replaces creators it cannot resolve, and it is also what one person's own work looks like when it was never sold or given away. The file cannot tell them apart; only a check against a grid (content dated before the credited account existed) can.
- `MATCH_BULK_DATES`: 80% or more of the CreationDates fall inside one 24-hour window. Neither loader changes CreationDate, so this points to bulk creation: an import tool, mass re-creation, or a rezzing or planting script run by the owner.
- `MATCH_ONE_BUILDER`: creator data names one home grid and one name on 95% or more of the records, the dates are spread out and the owners vary.

The thresholds are `AnalysisSettings` in the library. The analysis explains patterns; it does not judge, and it does not prove anything:
archives can be edited, so the report is a record of what the file says. An archive that has been loaded on another grid and saved again can credit
one person with everything even when that person did not build it; check with the person bringing it.

For an IAR written without `--home` (version 0.x) the creator is a name only, so creator and owner cannot be compared and only the name grouping can fire.

## Checks

After the creator analysis the report has a Checks section, grouped by severity with Warnings first. Each finding has a code, a severity, a title, a plain statement,
the numbers behind it, and the records it is about: the text report prints up to 20 (name, id, folder path or linkset) and the JSON carries all of them
(`InspectorOptions.MaxFindingExamples` bounds the list; `examplesTruncated` says when it was cut). A finding says what the file holds; it is never a verdict.
The checks read the file only. They need no grid, user or asset service.

| Code | Severity | What the file holds |
|---|---|---|
| `IAR_ORDER` | Warning | the control file is missing or not first, or an inventory entry comes after the first asset entry (the order the IAR loader needs) |
| `IAR_UNPARSED` | Warning | an item entry that is not valid XML, or a folder name with no folder id (the loader skips such path segments) |
| `IAR_TYPE_DISAGREE` | Warning | an item whose asset type and inventory type are not a pair in the grid's type table |
| `IAR_ASSET_TYPE_MISMATCH` | Warning | an item whose asset type differs from the type its asset file's extension gives |
| `IAR_LINK_DANGLING` | Warning | a link item or folder link that points at an item or folder not in the archive |
| `IAR_BOXED_UNPARSED` | Warning | an object asset held by an item that does not parse |
| `IAR_SYSTEM_FOLDER` | Notice | a folder named like a single-instance system folder (My Inventory, Current Outfit, Trash, Lost And Found, My Suitcase, Favorites, Photo Album, My Outfits); a name match, because the archive records no folder type |
| `IAR_LIMITS` | Notice | folder depth, items in one folder or total items over the bounds in `StructureSettings` (settings, not loader rules) |
| `PERM_WIDER_THAN_BASE` | Notice | an owner, group, everyone or next-owner mask that grants copy, modify, transfer or export that the base mask does not (move is not compared) |
| `PERM_ALL_OPEN` | Info | records whose base, owner, group, everyone and next-owner masks all have copy, modify and transfer (a count) |
| `PERM_NO_EXPORT_HELD` | Warning | records held by someone other than their creator whose creator did not allow export, counted per creator: the base mask carries the export right and neither the owner nor the everyone mask carries the bit |
| `PERM_NO_EXPORT_INFO` | Info | held records whose base mask has no export bit; they are not counted as refused (an archive saved before the export bit existed has none) |
| `REF_UNKNOWN_EXTENSION` | Notice | asset files whose extension is not in the archive extension table |
| `REF_TYPE_MISMATCH` | Warning | a part, prim item or setting that names an asset type different from the asset file's type (texture encodings, sound encodings and script text and bytecode are one family each; a sculpt field may name a texture or a mesh) |
| `REF_DUPLICATE_ASSET` | Notice | an asset id that appears in more than one entry |
| `REF_OBJECT_UNPARSED` | Warning | an object asset that does not parse |
| `REF_MISSING` | Notice | an asset id the content names with no asset file (only when the archive says assets are included) |
| `REF_NO_ASSETS` | Info | the archive was saved without assets, so references cannot be checked |
| `REF_UNREFERENCED` | Info | asset files nothing in the archive names |
| `AUTHOR_LINES` | Info | lines in scripts and notecards that name an author or rights holder (Copyright, (c), (C) sign, Created by, Made by, Script by, Author, All rights reserved, Do not resell, Licensed to and the like), each distinct name with how many assets carry it and the credited creator of the items that hold them. A list for a person to compare; nothing is matched |
| `CREATOR_ID_MANY_NAMES` | Notice | one creator id with different names or homes |
| `CREATOR_NAME_MANY_IDS` | Notice | one name and home with different creator ids |
| `CREATOR_DATA_UNPARSED` | Notice | creator data that holds no name or a home that is not a URL, or a creator field that is neither a UUID nor an OSPA name |
| `ARCH_UNKNOWN_PATH` | Notice | entries under no path the loaders read, with sizes (the loaders ignore them) |
| `ARCH_DUPLICATE_ENTRY` | Notice | an entry name that repeats an earlier one |
| `ARCH_DIRECTORY_DATA` | Notice | a directory entry that holds data |
| `ARCH_LEGACY_PAYLOAD` | Info | binary payloads from other forks and script state that were found, counted and not decoded |

The export rule: the grid's Hypergrid transfer reads the owner mask, a transfer clears export from the receiver's owner mask, and the creator's choice
survives in the everyone mask, so a held record is counted as refused only when the base mask has the right and neither the owner nor the everyone mask has the bit.

## What the loaders do with creators they cannot resolve

The OAR loader replaces a creator, owner or last owner that has no account on the destination with the estate owner (or `--default-user`), unless `--no-defaultuser`
is given or `--lookup-aliases` finds an alias; a part or item that carries creator data keeps its creator id.
The IAR loader makes the person loading the archive the creator of every item whose creator it cannot resolve, and the owner of every item and boxed object.
The report prints these as warnings next to what it found.

## Limits

Defaults in `InspectorOptions`: 64 MiB for an object body, 4 MiB for a text asset body, 16 MiB for any other parsed file, 64 GiB total uncompressed,
a compression ratio of 200:1 (judged once 64 MiB have been produced), 5,000,000 entries. Reaching a limit stops or skips with a message naming the entry, and the result is partial.
XML is read with DTD processing prohibited and no resolver. Control characters are removed from every name and text before it is printed or written.

## JSON report

`schemaVersion` is `"1"`. Names are camelCase; ids are strings; enums are strings; dates are Unix seconds.

| Field | Content |
|---|---|
| `status` | `Complete`, `Partial` or `NotAnArchive` |
| `stoppedAt`, `stopReason` | the entry and the reason when reading stopped early |
| `archive` | `kind`, `majorVersion`, `minorVersion`, `version`, `creationTime`, `assetsIncluded`, `multiRegion`, `regions[]` (`dir`, `id`, `sizeX`, `sizeY`), `controlFileFound`, `controlFileFirst`, `compressed`, `compressedBytes`, `uncompressedBytes`, `fileBytes`, `entryCount` |
| `linksetCount`, `partCount`, `primItemCount` | found directly in the archive |
| `boxedLinksetCount`, `boxedPartCount`, `boxedItemCount`, `boxedObjectCount` | found inside boxed objects |
| `iarItemCount`, `folderCount`, `parcelCount` | |
| `creators[]`, `owners[]` | `id`, `name`, `home`, `parts`, `linksets`, `items`, `boxedObjects`, `records`, `withCreatorData`, `isLibrary`, `earliestDate`, `latestDate` |
| `blankCreatorRecords` | records with no creator at all |
| `linksets[]` | `entry`, `regionDir`, `regionId`, `linksetId`, `name`, `x`, `y`, `z`, `primCount` |
| `parts[]` | `entry`, `context` (`Archive` or `Boxed`), `boxedAssetId`, `regionDir`, `regionId`, `linksetId`, `linksetName`, `partId`, `name`, `isRoot`, `linkNum`, `creator` (`id`, `data`, `home`, `name`, `raw`), `ownerId`, `lastOwnerId`, `groupId`, `rezzerId`, `masks` (`base`, `owner`, `group`, `everyone`, `nextOwner`), `creationDate`, `x`, `y`, `z` |
| `items[]` | prim inventory and IAR items: `source`, `itemId`, `name`, `assetId`, `assetType`, `invType`, `creator`, `ownerId`, `lastOwnerId`, `hasLastOwner`, `groupId`, `groupOwned`, `masks`, `creationDate`, `folderId`, `folderPath`, `partId`, `partName`, `linksetId`, `context`, `boxedAssetId` |
| `folders[]` | `path`, `name`, `folderId`, `itemCount` |
| `boxedObjects[]` | `assetId`, `format`, `linksetCount`, `partCount`, `itemCount`, `rootCreator`, `depth`, `holderCount` |
| `assetsByType[]`, `assets[]` | counts, bytes, and per asset `assetId`, `assetType`, `typeName`, `size`, `recognised`, `bodyRead` |
| `missingAssets[]`, `unreferencedAssets[]` | with `missingAssetCount`, `unreferencedAssetCount` and `referencedAssetCount` |
| `assetsWithoutCreator[]` | assets with no creator of their own (textures, sounds, meshes, animations, materials, ids found in text): `assetId`, `assetType`, `present`, `uncertain`, `uses`, `kinds[]`, `users[]` (`creatorId`, `creatorName`, `uses`) |
| `parcels[]`, `regionSettings[]` | parcel owner and group, terrain texture references |
| `legacyPayloads[]` | `kind`, `count`, `bytes` |
| `creatorAnalysis` | `totalRecords`, `libraryRecords`, `blankRecords`, `plantRecords`, `countedRecords`, `fired`, `flags[]` (`code`, `grouping`, `creator`, `creatorId`, `count`, `total`, `share`, `coverage`), `matches[]` (`id`, `statement`, `numbers`), `notes[]`, `advisory` |
| `findings[]` | everything the checks and the creator analysis reported: `source` (the check), `code`, `severity` (`Info`, `Notice`, `Warning`), `title`, `detail`, `count`, `numbers`, `examples[]` (`name`, `id`, `where`, `note`) and `examplesTruncated` |
| `loaderWarnings[]`, `editNote` | the loader behaviour and the edit-ability statement |
| `problems[]`, `problemCount` | entries that could not be read, notes |
| `retention` | which lists were cut at the retention limit |
| `stats` | `wallSeconds`, `bytesRead`, `peakWorkingSetBytes` |

## Library

`OpenSim.Archive.Inspection` holds the reader. `ArchiveInspector.Inspect(path or stream)` returns an `ArchiveReport`.
Further checks implement `IArchiveCheck` and are added to `InspectorOptions.Checks`; each receives every parsed record once, in file order, and the body of every text-type asset within the size cap.


## oar-export: build geometry as glTF

```
OpenSim.ArchiveInspector oar-export <archive.oar> --out <folder> [--object <uuid|name>] [--limit N]
```

Writes each build (linkset) of a region archive as a glTF 2.0 binary for Blender and other tools, keeping every part's name, link number and description, because scripts address child prims by name and by link number. The archive is only read. Exit codes: 0 done, 1 the archive was only partly readable (what was read is written), 2 not a region archive or unreadable, 64 bad command line.

Output, under `<folder>`:

- `<name>_<id>/<name>_<id>.glb` and `<name>_<id>/manifest.json` for each build (`<id>` is the first 12 hex digits of the root part's id). A build whose parts are all skipped has a manifest and no `.glb`.
- `export-summary.json`: the archive's file name, how it was read, per-build counts, and the number of parts exported, placeheld and skipped with the reasons.

`--object` keeps one build, matched by the root part's id or the object's name (case-insensitive). `--limit` stops after that many builds.

### What is in the glTF file

- Node 0 is the build, named `<object name> [build]` (the suffix keeps it apart from the root part, which has the object's name). Its extras hold `kind` (`object`), the root part's id, the part count, the archive entry, and the root part's region position and rotation. The node itself has no transform, so a build opens at the origin.
- Its children are one node per part, named exactly as the part, in link-number order (the order in the archive file is not the link order). A single-part object keeps link number 0.
- Part transforms are relative to the root part: the root part has none, the others use the stored offset position and rotation. Part size is applied to the vertices, so node scale is 1.
- Axes: the viewer is Z up, glTF is Y up. Node transforms and vertices use (x, y, z) to (x, z, -y), which Blender's importer undoes. Values in extras use the viewer's axes.
- Each part node's extras: `kind` (`prim`, `sculpt`, `mesh`, `tree`, `grass`, `other`), `linkNumber`, `partUuid`, `partName` (the exact name; Blender renames repeated names), `description`, `isRoot`, `creatorId`, `creatorName`, `ownerId`, `permissions` (`base`, `owner`, `group`, `everyone`, `nextOwner`), `size`, `contentsCount`, `pcode`, `geometry` (`exported` or `pending`) and, for a part with geometry, `faces` and `triangles`.
- A prim is meshed in the prim mesher's viewer mode with one glTF primitive per prim face. Each primitive's extras carry `primFace` (the face number a texture entry uses), the mesh's extras list them in `primFaces`, and every vertex has the application attribute `_PRIM_FACE`. The attribute is what survives a tool that merges primitives (Blender does when faces share a material). Texture coordinates are the viewer's, with V turned for glTF; the bottom end cap's V is mirrored, as the browser viewer draws it.
- Materials are placeholders named by the face's texture id (`extras.textureId`), with the face colour and alpha, and the repeat, offset, rotation, glow and fullbright values in extras. Textures are not exported yet.
- Sculpted parts, mesh parts and the built-in trees and grass are named empty nodes in the right place, with `geometry: pending` and `geometryPending` saying why (sculpt and mesh parts also carry `sculptType` and `sculptTexture`, trees and grass their `species`). They are not skipped: link order and names stay complete.

### manifest.json

One per build: the archive's file name (no folder), the archive entry, the object name, the root part's id, the file name, counts, and a list of every part in link-number order with its link number, name, description, id, creator (id, name, home), owner id, permission masks, `contents` (item names with asset type and inventory type), `kind`, `status` (`exported`, `placeheld` or `skipped`), the `reason` for the last two, and `faces` and `triangles` for an exported prim.

### Export permission

A part is skipped, and listed in the manifest with the reason, when the creator refused export: the part is held by someone other than its creator, its base mask carries the export right, and neither its owner nor its everyone mask grants it. Parts held by their creator, library content, parts with no known creator or owner, and parts whose base mask has no export bit (archives saved before the bit existed) are exported. This is the rule the inspector's permission check uses. There is no option to widen it. A skipped part has no node in the file; its manifest entry has its link number, name, creator and masks, and no contents.

### Through Blender

Checked with Blender's glTF importer and exporter (extras on both ways): node names, `extras` on every part node, the mesh-level `primFaces` and the `_PRIM_FACE` vertex attribute survive. Blender keeps object names unique, so a repeated part name comes back as `Name.001`, `Name.002` (the exact name stays in `extras.partName`); Blender writes a node's children sorted by name, so the node order after a round trip is the link order only when the names sort in link order (use `extras.linkNumber` to restore it); primitive-level extras are dropped and primitives that share a material are merged.
