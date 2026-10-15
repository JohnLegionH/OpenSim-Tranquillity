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
| 0 | read fully, no creator flag |
| 1 | partial: a corrupt entry, a size limit, or a truncated file; the report says which entry it stopped at |
| 2 | not an archive, or the file could not be opened |
| 3 | read fully and a creator flag fired (see below) |
| 64 | bad command line |

## What it reads

OAR 0.x (single region) and 1.x (multi region), IAR 0.x (creator as `ospa:n=First Last`) and 1.x (creator data), object XML in the xml2 and
original forms, coalesced objects, boxed objects inside object assets (followed recursively with a cycle guard and a depth limit of 8),
parcels, region settings, and the assets (type, size, and whether each is referenced).
The bodies it parses are the control file, object files, inventory item files, parcel and region settings files, and assets of type
object, script, notecard, gesture, wearable, material and settings. Every other body (textures, sounds, meshes, animations) is streamed past without being held.

Binary payloads from other forks (binary object serializations, script state of another engine) are detected and counted by kind; they are never decoded.

## The creator analysis

Records (parts and items, boxed contents included) are grouped by creator id and, where creator data or an OSPA name gives one, by creator name.
Library content and blank creators are counted separately and left out of the shares.

- `SINGLE_CREATOR`: one creator is credited on every counted record, and there are at least 20.
- `DOMINANT_CREATOR`: one creator has 90% or more of the counted records, at least 20 records.

When a flag fires the report also says which of these the numbers match, each with its figures:

- `MATCH_LOADER_REWRITE`: creator, owner and last owner are the same account on 95% or more of the records and no creator data names a different creator. This is what a loader leaves when it replaces creators it cannot resolve.
- `MATCH_BULK_DATES`: 80% or more of the CreationDates fall inside one 24-hour window. Neither loader changes CreationDate, so this points to bulk creation (an import tool or mass re-creation).
- `MATCH_ONE_BUILDER`: creator data names one home grid and one name on 95% or more of the records, the dates are spread out and the owners vary.

The thresholds are `AnalysisSettings` in the library. The analysis explains patterns; it does not judge, and it does not prove anything:
archives can be edited, so the report is a record of what the file says. An archive that has been loaded on another grid and saved again can credit
one person with everything even when that person did not build it; check with the person bringing it.

For an IAR written without `--home` (version 0.x) the creator is a name only, so creator and owner cannot be compared and only the name grouping can fire.

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
| `creatorAnalysis` | `totalRecords`, `libraryRecords`, `blankRecords`, `countedRecords`, `fired`, `flags[]` (`code`, `grouping`, `creator`, `creatorId`, `count`, `total`, `share`), `matches[]` (`id`, `statement`, `numbers`), `advisory` |
| `findings[]` | everything the registered checks reported: `code`, `severity`, `title`, `detail`, `numbers` |
| `loaderWarnings[]`, `editNote` | the loader behaviour and the edit-ability statement |
| `problems[]`, `problemCount` | entries that could not be read, notes |
| `retention` | which lists were cut at the retention limit |
| `stats` | `wallSeconds`, `bytesRead`, `peakWorkingSetBytes` |

## Library

`OpenSim.Archive.Inspection` holds the reader. `ArchiveInspector.Inspect(path or stream)` returns an `ArchiveReport`.
Further checks implement `IArchiveCheck` and are added to `InspectorOptions.Checks`; each receives every parsed record once, in file order, and the body of every text-type asset within the size cap.
