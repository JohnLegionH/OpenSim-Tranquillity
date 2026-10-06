#!/usr/bin/env bash
# Run the checks for one target on one server. Env: KIND (mysql|pgsql|sqlite), ADMIN (server connection
# string without a database), CHECKS, EXPECT (space separated Store:table.column), LABEL.
set -uo pipefail
ws=$GITHUB_WORKSPACE
args=(--db "$KIND" --checks "$CHECKS" --label "$LABEL")
for e in $EXPECT; do args+=(--expect-column "$e"); done
if [ "$KIND" = sqlite ]; then
  fresh=(--sqlite-dir "$ws/work/db/fresh")
  upgrade=(--sqlite-dir "$ws/work/db/upgrade")
else
  fresh=(--admin "$ADMIN" --database fresh)
  upgrade=(--admin "$ADMIN" --database upgrade)
fi
rc=0
echo "::group::fresh"
dotnet "$ws/work/target/bin/StoreCheck.dll" --mode fresh "${args[@]}" "${fresh[@]}" || rc=1
echo "::endgroup::"
if [ -f "$ws/work/baseline/bin/StoreCheck.dll" ]; then
  echo "::group::upgrade: baseline writes"
  dotnet "$ws/work/baseline/bin/StoreCheck.dll" --mode baseline "${args[@]}" "${upgrade[@]}" --snap "$ws/work/snap.json" || rc=1
  echo "::endgroup::"
  if [ -f "$ws/work/snap.json" ]; then
    echo "::group::upgrade: target opens, migrates, reads"
    dotnet "$ws/work/target/bin/StoreCheck.dll" --mode upgrade "${args[@]}" "${upgrade[@]}" --snap "$ws/work/snap.json" || rc=1
    echo "::endgroup::"
  else
    echo "no baseline snapshot: upgrade not run"; rc=1
  fi
fi
exit $rc
