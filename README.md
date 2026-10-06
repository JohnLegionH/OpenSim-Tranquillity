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

## Targets

Each push to `ci/store-checks` (or a manual run) checks every target in `targets.json` that is not marked
`"skip": true`, on all seven servers. Fields of a target:

- `repo`, `ref`: the tree checked; pin `ref` to a commit when a result has to name the code it ran on.
- `baseline_repo`, `baseline_ref`: when set, the upgrade check also runs from that tree.
- `checks`: the harness round trips (`general`, `sit`); `expect`: columns that must exist (`Store:table.column`).
- `restore_repo`, `restore_ref`, `restore_paths`: files (space separated) copied from that tree over the target
  before the build, e.g. develop's version of a store file, to show a test red against the unfixed code.
- `overlay_paths`: files copied from this branch's `overlay/<path>` over the target before the build.
- `tests_project`, `tests`: a test project in the target and a `dotnet test --filter`, run on each server by
  `ci/tests.sh`; the server is passed in `OPENSIM_TEST_PGSQL` / `OPENSIM_TEST_MYSQL`.
- `skip`: `true` leaves the target out of the run (the plan job filters it out).

### Turning a target back on

Delete its `"skip": true` line in `targets.json` (or set it to `false`), commit, and push `ci/store-checks`;
the next run includes it. Check its `ref` first: `develop` is pinned to 86b1824cc5, which is not develop's
head any more, and `known_develop_faults` were recorded on that commit. For a new store fix, add a new target
rather than editing a finished one, and set `skip` on targets that need not run again, so a run stays at seven
jobs per target.
