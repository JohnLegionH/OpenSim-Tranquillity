# Backup integrity audit — `D:\legiongrid\_backup\`, 2026-09-04

**Why this was run.** During A9 a `mysqldump` was piped through PowerShell's `Set-Content -Encoding utf8`. The
result was a 4.08 GB file with a UTF-8 BOM — and it was **corrupt**: PowerShell decodes and re-encodes the byte
stream, replacing every byte that is not valid text with U+FFFD, which mangles binary column data. The 1.4 GB of
inflation over the correct 2.50 GB *was* the damage. That file was deleted and the dump re-taken with byte-exact
shell redirection. This audit answers the question that left open: **is anything else in `_backup\` corrupt?**

**Nothing was deleted. Nothing was modified.** This was a read-only audit.

---

## Verdict

**No corrupt backup remains. Every file examined is intact.**

**The gap that matters: there is no database backup of any kind before 2026-09-04.** Both dumps are from that
day. Any need to restore the grid to a state before 2026-09-04 cannot be met from this folder.

---

## SQL dumps

| File | Size (bytes) | UTF-8 BOM | Terminating line | Verdict |
|---|---|---|---|---|
| `legiongrid-full-20260904.sql` | 2,672,741,306 | **none** | `-- Dump completed on 2026-09-04 14:16:27` | **good** |
| `legiongrid-predupe-20260904-1332.sql` | 2,685,971,589 | **none** | `-- Dump completed on 2026-09-04 18:34:21` | **good** |

No `.sql.gz` files are present.

Both are the expected ~2.5 GB for this database and carry no BOM, which is the corruption signature. The two are
close in size and 5 hours apart, which is consistent — the second was taken immediately before the (abandoned)
dedupe.

> **Timestamps.** The `Dump completed` line is UTC; the file mtimes are local, five hours behind. `14:16:27` UTC
> is the 09:16 local file, and `18:34:21` UTC is the 13:34 local one. Not a discrepancy.

## Directory backups

Seven region-server backups. **No `gridserver-*` backup exists at all** — see Decisions.

| Backup | Files | Probe DLL size | PE header | Probe DLL SHA256 (12) | Cross-check |
|---|---|---|---|---|---|
| `regionserver-20260903-1918` | 4,333 | 167,936 | `MZ` | `01a710d25419` | — |
| `regionserver-20260903-2135` | 4,566 | 167,936 | `MZ` | `298f89554c50` | — |
| `regionserver-20260904-0848` | 4,610 | 167,936 | `MZ` | `d287c2d22f08` | — |
| `regionserver-20260904-1139` | 4,640 | 216,576 | `MZ` | `0772b61f53ea` | **matches** the hash recorded at deploy time |
| `regionserver-20260904-1308` | 4,835 | 217,088 | `MZ` | `628c1b27fc77` | **matches** |
| `regionserver-20260904-1401` | 4,909 | 218,624 | `MZ` | `2046617ae749` | **matches** |
| `regionserver-20260904-1703` | 4,942 | 220,160 | `MZ` | `5e28d135835b` | **matches** |

Probe DLL is `OpenSim.Region.ClientStack.LindenCaps.dll` — chosen because it changed on every AIS deploy, so a
mangled copy would be obvious.

### How the verdict was determined

Three checks, in increasing strength:

1. **PE header sweep.** Every top-level `.dll` and `.exe` in every backup — **1,070 binaries** — was checked for
   the `MZ` signature. **Zero** failed. A file that had been through a PowerShell text pipeline would have lost
   its header, because `0x4D 0x5A` survives but the first invalid UTF-8 sequence a few bytes later does not.
2. **Size progression.** The probe DLL grows 167,936 → 216,576 → 217,088 → 218,624 → 220,160 across the deploys,
   tracking the known build sequence. Corruption inflates files substantially — the A9 dump grew 63% — so a flat,
   plausible progression is itself evidence against it.
3. **Hash cross-check, the decisive one.** For the four backups taken during deploys I had already recorded the
   publish-tree SHA256 of that DLL *independently*, at deploy time, before the backup existed. **All four match
   exactly.** That proves those copies are byte-exact, not merely structurally valid.

The three oldest backups (`20260903-1918`, `20260903-2135`, `20260904-0848`) predate this work, so there is no
recorded hash to compare them against. They pass checks 1 and 2, and their probe DLLs are all 167,936 bytes —
mutually consistent, and the size of the pre-AIS build. **Treat them as good, with the caveat that the evidence
for them is structural rather than a byte-for-byte match.**

**Why they were never at risk:** all seven were produced by `robocopy`, which copies bytes and has no text mode.
The corruption is specific to routing a *byte stream* through a PowerShell *text* pipeline, which only ever
happened to the one `mysqldump`.

---

## The rule for future dumps

**Byte-exact shell redirection only. Never `Set-Content`. Never a PowerShell pipe.**

```bash
# correct — bash redirection passes bytes straight to the file
docker exec -e MYSQL_PWD="$PW" legiongrid_mysql \
    mysqldump -uroot --single-transaction --routines --triggers --events legiongrid > backup.sql
```

```powershell
# WRONG — decodes and re-encodes; corrupts binary column data, adds a BOM, inflates the file
docker exec ... mysqldump ... | Set-Content -Path backup.sql -Encoding utf8
```

`Out-File`, `>` and `>>` in PowerShell, and `Tee-Object` are all the same hazard: they are text writers. If a
dump must be driven from PowerShell, redirect inside `cmd /c`, or use `mysqldump --result-file=` inside the
container followed by `docker cp`.

**Verify every dump before trusting it**, which takes seconds:

```bash
head -c 3 backup.sql | od -An -tx1     # must NOT be  ef bb bf
tail -1 backup.sql                     # must be  -- Dump completed on ...
stat -c %s backup.sql                  # must be in the expected range, not inflated
```

A BOM and an inflated size together are conclusive. Note that a corrupt dump **still ends with
`Dump completed`** — that check alone is not sufficient, which is exactly why the A9 file passed a casual look.

## Coverage

| Range | Database backup | Region-server backup |
|---|---|---|
| before 2026-09-03 19:18 | **none** | **none** |
| 2026-09-03 → 2026-09-04 | none | 3 backups |
| 2026-09-04 | 2 good dumps | 4 backups, all hash-verified |

**The only real gap is the database before 2026-09-04, and it is total.**
