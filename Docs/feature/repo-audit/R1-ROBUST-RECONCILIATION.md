# R1 — Robust reconciliation: what is running, what a merge costs, and the two roads

**Date:** 2026-09-04. Recon, then **executed** — see §5.

> **DONE 2026-09-04 (Road A).** Robust was backed up for the first time, the branches were merged, and Robust was
> republished and redeployed. **The live grid server now reports a single commit** — `1.1.208-alpha+a2c8fb63f3`
> across all 45 assemblies — where it previously reported four. Details in §5.

**Headline: the merge is far cheaper than feared, and the real risk is elsewhere.** `feature/ais-v3` and
`integration/legiongrid-trusted-hg` conflict in **exactly one file**, and that file is a solution manifest with
mechanical, purely additive changes on both sides. No source file conflicts. Trusted-hypergrid touches **none**
of the Robust files this programme needs.

**What is actually dangerous** is that Robust has **never been backed up**, and it holds inventory, assets and
accounts.

---

## 1. What is running

45 OpenSim assemblies in `D:\legiongrid\gridserver\`, from **four different commits**.

| Commit | Version | Count | What |
|---|---|---|---|
| `119fea881e` | 1.1.114-alpha | **38** | the 8/25 base publish |
| `0ac27e3f2d` | 1.1.138-alpha | **5** | hand-copied 8/29 |
| `d8461f96e0` | 1.1.139-alpha | **1** | hand-copied 8/29 |
| `1c35f18db7` | 1.1.140-alpha | **1** | hand-copied 8/29 |

### The strays are seven assemblies, not four

The four named in the brief are right, but **incomplete**. `0ac27e3f2d` covers five assemblies, not two:

| Assembly | Stamp |
|---|---|
| `OpenSim.Server.Handlers.dll` | `0ac27e3f2d` *(known)* |
| `OpenSim.Services.Connectors.dll` | `0ac27e3f2d` *(known)* |
| **`OpenSim.Data.dll`** | `0ac27e3f2d` — **not previously recorded** |
| **`OpenSim.Data.MySQL.dll`** | `0ac27e3f2d` — **not previously recorded** |
| **`OpenSim.Data.SQLite.dll`** | `0ac27e3f2d` — **not previously recorded** |
| `OpenSim.Services.HypergridService.dll` | `d8461f96e0` *(known)* |
| `OpenSim.Framework.dll` | `1c35f18db7` *(known)* |

**`OpenSim.Data.MySQL.dll` matters.** It is the inventory data layer — `MySqlFolderHandler`,
`IncrementFolderVersion`, `MySqlItemHandler.Delete` — the code A7's analysis rests on. The live Robust is running
an 8/29 build of it while every other service assembly is 8/25. Nothing is known to be wrong with it; the point
is that the divergence is wider than the record said, and any future hand-patch must account for seven files.

### Branch containment

| Commit | Branches containing it | ancestor of `feature/ais-v3` | of `integration/legiongrid-trusted-hg` | of `fix/maptile-legacy-renderer` |
|---|---|---|---|---|
| `119fea881e` | ais-v3, ssb-appearance, voice-visibility-matrix, maptile-legacy-renderer, test-fixtures, trusted-hg | **yes** | yes | yes |
| `0ac27e3f2d` | **trusted-hg only** | no | yes | no |
| `d8461f96e0` | **trusted-hg only** | no | yes | no |
| `1c35f18db7` | **trusted-hg only** | no | yes | no |

`1c35f18db7` is the current tip of `integration/legiongrid-trusted-hg`.

### Unreachable commits: none

**Every commit stamped into the live Robust is reachable from at least one branch.** The hazard that bit the
region deploy today — binaries stamped with a commit reachable from no branch — **does not exist here**.

Trusted-hypergrid is live and configured: `TrustedHypergridSecret.ini` (99 bytes, Aug 29 14:44) is in the grid
root.

### Publish shape, confirmed

The grid root is a **RID-less, framework-dependent** publish; the region root is **win-x64**. `runtimes/` is
present in both, so that is not a discriminator — the definitive evidence is `deps.json`:

| Root | `runtimeTarget.name` |
|---|---|
| `gridserver` | `.NETCoreApp,Version=v10.0` — portable |
| `regionserver` | `.NETCoreApp,Version=v10.0/win-x64` — RID-specific |

So Robust must be republished **without** `-r win-x64`, or the layout changes shape.

## 2. What a merge would cost

### `feature/ais-v3` ← `integration/legiongrid-trusted-hg`

| | |
|---|---|
| merge-base | `b7fbc717fa` (2026-08-29 08:08) |
| commits only on ais-v3 | 70 |
| commits only on trusted-hg | 10 |
| files changed, ais-v3 side | 120 |
| files changed, trusted-hg side | 42 |
| **files changed on BOTH sides** | **1** |
| `merge-tree` conflicts | **1 file** |

**The single conflict is `Tranquillity.sln`, and it is mechanical.** Both sides add their own test projects at the
same insertion points — `LindenCaps.Tests` and `LindenCaps.AIS.Tests` on the AIS side,
`TrustedHypergrid.Tests` and `HypergridService.Tests` on the trusted-HG side — plus the matching
`Debug|Release × AnyCPU|x64|x86` configuration blocks. **Both sides are pure additions.** The resolution is "keep
both", and it is verifiable by opening the solution.

**There are no semantic conflicts.** Not one `.cs` file was changed on both sides. This is not the ILogger/xunit
churn scenario — that churn is *upstream's*, and it is a separate question (§3).

### Which branch carries the Robust changes this programme needs

| Change | File | Assembly | Changed by ais-v3 | Changed by trusted-hg |
|---|---|---|---|---|
| A2b `ONLYIFTRASH` | `XInventoryInConnector.cs` | `OpenSim.Server.Handlers` | **yes** | no |
| A2b `ONLYIFTRASH` | `XInventoryServicesConnector.cs` | `OpenSim.Services.Connectors` | **yes** | no |
| A7 `EnsureSystemFolder` | `XInventoryService.cs` | `OpenSim.Services.InventoryService` | **yes** | no |
| A2b interface | `IInventoryService.cs` | `OpenSim.Services.Interfaces` | **yes** | no |

**All of them are on `feature/ais-v3`; trusted-hypergrid touches none of them.**

But note where they land: **two of the four are in assemblies that are currently strays**
(`OpenSim.Server.Handlers`, `OpenSim.Services.Connectors`, both at `0ac27e3f2d`). That is precisely why they
cannot be shipped by hand-copying two more DLLs — the versions that carry `ONLYIFTRASH` must also carry
trusted-hypergrid, and only a merged branch produces that.

## 3. The upstream question — two roads

Upstream `OpenSim-NGC` has tagged **v1.0** (`29f312cb79`, 2026-08-31); `upstream/develop` is `ee71b6951b`, which
includes #198 (the ILogger migration) and #201.

| | |
|---|---|
| our merge-base with `upstream/develop` | `93765a999e` (2026-08-24) |
| commits upstream has that we do not | **22** |
| commits we have that upstream does not | 186 |
| upstream's diff from the base | **65 files, +1,023 −2,286** |

### Road A — reconcile locally, defer upstream

Merge `integration/legiongrid-trusted-hg` into `feature/ais-v3`, republish Robust from the result.

**Conflict surface: 1 file, mechanical.**

- **Cost:** essentially nothing beyond the merge itself and a Robust deploy. Ships A2b's `ONLYIFTRASH` and A7's
  prevention fix, and ends the four-commit split. *(Written before A15: this also said it unblocks step 7. Step 7
  turned out not to be reachable through any viewer — see `../ais-v3/A5-LIVE-CHECKLIST.md` step 7. The deploy's
  real value is the single-commit inventory, not that step.)*
- **Cost deferred:** the gap to upstream keeps growing. Today it is 22 commits; every week of local work makes
  the eventual sync larger, and #198 is exactly the kind of broad mechanical churn that is cheap to take early
  and expensive to take late.
- **Risk:** low and well understood.

### Road B — sync to upstream v1.0 first, then reconcile

Merge `upstream/develop` (or the `v1.0` tag) into the fork, then merge trusted-HG, then republish.

**Measured conflict surface, not estimated:**

| Pair | Files changed on both sides | `merge-tree` conflicts |
|---|---|---|
| `feature/ais-v3` ↔ `upstream/develop` | 5 | **1** — `MapImageModule.cs` |
| `integration/legiongrid-trusted-hg` ↔ `upstream/develop` | — | **0 — clean** |

The five files touched on both sides are `EstateManagementModule.cs`, `MapImageModule.cs`,
`OpenSimDefaults.ini`, `OpenSim.Server.RegionServer.csproj`, and `Tests/OpenSim.Tests.Common/Mock/TestClient.cs`.

**This is much smaller than the #198 churn implies.** The reason is that our fork has already done its own
ILogger migration, so most of upstream's 65 files land on code we changed in the same direction rather than
against it — `merge-tree` resolves them.

- **Cost:** one real conflict, in `MapImageModule.cs` — which is **the file `fix/maptile-legacy-renderer` exists
  to change**. That is a semantic conflict in a renderer, and it needs whoever owns that branch to resolve it,
  not a mechanical "keep both".
- **Benefit:** clears the upstream debt while it is 22 commits, gets the fork onto a tagged release, and makes
  every later sync smaller.
- **Risk:** moderate, concentrated in one file — plus the ordinary risk that 22 upstream commits change
  behaviour in ways no conflict marker shows. It also puts a much larger delta into the same Robust deploy.

### The honest comparison

Road A's conflict surface is one mechanical file. Road B's is one mechanical file **plus** one semantic file that
collides with an in-flight branch. **Both are small.** The question is not really "which is cheaper" — it is
whether to put 22 upstream commits into the same deploy as the Robust reconciliation, when Robust has no backup
and no rollback.

**Not deciding, as instructed.** But the two are separable, and separating them is what the sequence below does.

## 4. Recommended sequence

**Step 0 — back up Robust. Nothing else happens first.**

No `gridserver-*` backup has ever been taken. Robust holds inventory, assets, accounts and grid records; a bad
deploy currently has **no rollback**. Two artefacts are needed, and the region-side procedure already exists:

1. the directory: `robocopy D:\legiongrid\gridserver D:\legiongrid\_backup\gridserver-YYYYMMDD-HHMM /E`
2. a database dump, **by byte-exact shell redirection only** — see `BACKUP-AUDIT-2026-09-04.md`; a PowerShell
   text pipeline produces a corrupt, unrestorable file that still ends with `-- Dump completed`.

The last database dump is `legiongrid-predupe-20260904-1332.sql`. If Robust is deployed on a later day, take a
fresh one.

**Step 1 — merge trusted-HG into `feature/ais-v3`.** Resolve `Tranquillity.sln` by keeping both sides' project
entries and configuration blocks. Build. Run the suites. Nothing else should conflict.

**Step 2 — verify the merged tree carries all seven stray assemblies' worth of change**, not just the four in the
old record: confirm `TrustedHypergridRuntime.cs` and `ExternalIPResolver.cs` are present, and that
`OpenSim.Data.MySQL` builds from the merged tree rather than needing a hand-copy.

**Step 3 — publish Robust RID-less.** `dotnet publish Source/OpenSim.Server.GridServer -c Release
--self-contained false`, **no `-r win-x64`**, or the layout changes shape against what is deployed.

**Step 4 — deploy Robust** by the region procedure: precheck for running processes, back up, copy binaries only,
preserve `config/`, `config-include/`, `TrustedHypergridSecret.ini` and any live-only assemblies, verify the
deployed stamps are a **single** commit, and confirm `TrustedHypergridSecret.ini` survived. **After this deploy
the live Robust should report one commit, not four.**

~~**Step 5 — re-run checklist step 7.**~~ **Superseded by A15.** Step 7 is **not reachable through the viewer**:
the only folder-removal routes are move-to-Trash, purge a single item, and Empty Trash, and none produces
`DELETE /category` on a folder outside Trash. `ONLYIFTRASH` is correct and now live, but no in-world gesture
exercises it. The deploy was verified live instead — see §6.

**Step 6 — upstream sync, as its own change**, on its own day, with `MapImageModule.cs` resolved by whoever owns
`fix/maptile-legacy-renderer`.

Steps 1–5 are Road A. Step 6 is Road B, deferred rather than skipped — which is the point of sequencing them
this way: the Robust reconciliation is urgent and cheap, the upstream sync is neither.

---

## 5. What was actually done (Road A, executed 2026-09-04)

### Step 1 — the first rollback point Robust has ever had

| Artefact | Result |
|---|---|
| `D:\legiongrid\_backup\gridserver-20260904-1853\` | **765 files, 530 MB** — same file count as the source. All **79** top-level binaries MZ-probed: **0 non-PE**. `TrustedHypergridSecret.ini`, `config/Robust.ini`, `config/DirectDeliverySecret.ini` and both hand-made `.example.net10` files all present. |
| `D:\legiongrid\_backup\legiongrid-prerobust-20260904-1853.sql` | **2,688,481,364 bytes**, **no BOM**, terminates `-- Dump completed on 2026-09-04 23:54:11`. Taken by byte-exact shell redirection. |

### Step 2 — the merge

`integration/legiongrid-trusted-hg` merged into `feature/ais-v3` as **`a2c8fb63f3`**.

**Exactly one conflict, as predicted: `Tranquillity.sln`**, three hunks, all pure additions on both sides.
Resolved by keeping both. `dotnet sln list` parses, **93 projects, no duplicates**, and all four test projects
survive.

> **One thing the recon did not predict.** `feature/ais-v3`'s own solution file was already **malformed**: its two
> added test projects shared a single `EndProject`, leaving one `Project(` unclosed. MSBuild tolerated it, so it
> had gone unnoticed. Keeping both sides made a second entry unclosed, so the resolution inserts the two missing
> `EndProject` lines. `Project`/`EndProject` now balance at **98/98**.

**Trusted-hypergrid preserved, proven by diff against its own branch:**

| File | vs `integration/legiongrid-trusted-hg` |
|---|---|
| `Source/OpenSim.Framework/TrustedHypergrid/TrustedHypergridRuntime.cs` | **identical** |
| `Source/OpenSim.Services.HypergridService/ExternalIPResolver.cs` | **identical** |

The only difference anywhere under `OpenSim.Services.HypergridService` is +12 lines, and they are A2b's own
`DeleteFolders(..., onlyIfTrash)` NOGO overrides — the AIS side adding to files trusted-HG also owns, not a
regression of trusted-HG.

**Build:** solution, **0 errors**. **Tests:** AIS **140/140**, appearance flush **6/6**, and the two suites the
merge brought in — `OpenSim.TrustedHypergrid.Tests` **25 passed / 3 skipped**, `OpenSim.Services.HypergridService.Tests`
**25/25**.

### Step 3 — publish and deploy

Published **RID-less** (`-c Release --self-contained false`, no `-r win-x64`); `deps.json` confirms
`.NETCoreApp,Version=v10.0`, matching the live shape.

Compared publish against live **by content before writing**: **596 identical, 90 differ, 0 new**. Every differing
file is a `.dll`/`.pdb`/`.exe` except `deps.json`. **No `.ini` in either set.** The dry run listed 591 files by
timestamp — the publish tree is fresh, so most are byte-identical rewrites; the content comparison is the number
that matters.

**Result — four commits became one:**

| Before | After |
|---|---|
| `119fea881e` ×38, `0ac27e3f2d` ×5, `d8461f96e0` ×1, `1c35f18db7` ×1 | **`1.1.208-alpha+a2c8fb63f3` ×45** |

No assembly carries any of the four old commits. All seven previously-stray assemblies — including the three the
old record missed — now carry the merged commit.

**Probed in the deployed binaries, not the publish:**

| Assembly | Found |
|---|---|
| `OpenSim.Framework.dll` | `TrustedHypergridRuntime` |
| `OpenSim.Services.HypergridService.dll` | `ExternalIPResolver`, `HGSuitcaseInventoryService`, `onlyIfTrash` |
| `OpenSim.Server.Handlers.dll` | `ONLYIFTRASH`, `XInventoryInConnector` |
| `OpenSim.Services.Connectors.dll` | `ONLYIFTRASH` |
| `OpenSim.Services.InventoryService.dll` | `EnsureSystemFolder`, `not creating a second {Type} folder` |

**Preserved:** all seven `.ini`/`.example` files including `Robust.ini.example.net10` and
`Robust.HG.ini.example.net10`; `TrustedHypergridSecret.ini` (99 bytes, still Aug 29 14:44); `config/` (3);
`assets/` (469); `inventory/` (26); `maptiles/` (38); `appsettings.json` (Jun 30); 27 log files. No `/MIR`, no
`/PURGE`. **Total file count unchanged at 765** — nothing added, nothing lost.

`config-include/`, `Library/`, `data/`, `Estates/` and `openmetaverse_data/` hold no files in the grid root — they
are region-side directories and were listed for preservation out of caution rather than because they exist here.

**Servers were not started.**

### Not done, deliberately

The upstream v1.0 sync (Road B) was explicitly out of scope. `MapImageModule.cs` remains the one semantic conflict
awaiting `fix/maptile-legacy-renderer`'s owner.

---

## 6. Live verification (2026-09-04, 19:03:58)

The binaries were verified at deploy time; this is the reconciliation confirmed **running**.

Robust restarted cleanly on **`1.1.208-alpha+a2c8fb63f3`** with **zero load failures**:

| Check | Result |
|---|---|
| Version reported at startup | `1.1.208-alpha+a2c8fb63f3` — the merged commit |
| Trusted-hypergrid grid identity | loaded from `TrustedHypergridSecret.ini`, fingerprint `637ee209…` |
| External IP resolver | resolved `legiongrid.ddns.net` |
| Direct Delivery | enabled |
| Connectors | all loaded |
| Load failures | **none** |

This is the point the whole exercise was for. The two features that had only ever existed in *separate* builds —
trusted-hypergrid (previously three hand-copied assemblies) and the AIS-side Robust changes — are now running
together from one commit, and trusted-hypergrid still finds its secret and still resolves the grid's external
address. **The reconciliation is confirmed live, not merely in the binaries.**

> **What it did not do:** unblock checklist step 7. That step is not reachable through any viewer (A15), so
> `ONLYIFTRASH` shipping changed nothing a resident can see. The deploy's value is that Robust now reports one
> commit instead of four, and that a rollback point exists for the first time.
