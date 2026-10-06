# Store checks

Runs the region, estate and inventory stores of the trees named in `targets.json` against MySQL 8.0 and
8.4, MariaDB 10.11 and 11.4, PostgreSQL 15 and 17 (service containers on GitHub's runners) and SQLite.

For each target and server:

- **fresh**: on an empty database every store migrates from its first step. Fails on any failed migration
  command (Migration.Update logs those at Debug and carries on, so the harness captures the stores' own log
  lines), on any error the stores log, if the recorded version is not the newest step, or if a table or
  column the newest step creates is missing. Then the round trips named by `checks`.
- **upgrade** (when the target names a baseline): the baseline tree's stores create the schema and write a
  fixed data set; the target tree's stores open the same database and migrate it. The baseline's rows must be
  unchanged on the baseline's columns and must load exactly as the baseline loaded them; then the round trips
  run on the upgraded database.
- **round trips**: `general` (region objects, task inventory, region settings, terrain, extra; estate
  settings and the manager, access, group and ban lists, including a removal; inventory folders and items,
  including a move, an update and a delete) and `sit` (sit target states and the stored SitTargetActive
  value), each written by one store instance and read back by a second one.

The harness (`harness/StoreCheck`) is built on the runner against each checked-out tree. Nothing connects to
any database outside the runner.
