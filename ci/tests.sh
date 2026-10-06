#!/usr/bin/env bash
# Run the target tree's own tests named by a filter against this job's server: ci/tests.sh
# Env: TESTS_PROJECT (path in the target tree), TESTS (dotnet test --filter), KIND (mysql|pgsql|sqlite),
# ADMIN (server connection string without a database). The tests read the server from OPENSIM_TEST_PGSQL or
# OPENSIM_TEST_MYSQL and create and drop their own databases on it.
set -uo pipefail
ws=$GITHUB_WORKSPACE
if [ "$KIND" = pgsql ]; then export OPENSIM_TEST_PGSQL="$ADMIN"; fi
if [ "$KIND" = mysql ]; then export OPENSIM_TEST_MYSQL="$ADMIN"; fi
echo "target tree: $(git -C "$ws/target" rev-parse HEAD)"
# Shallow checkout: Nerdbank.GitVersioning cannot walk history, so it is told not to read git.
NBGV_GitEngine=Disabled dotnet test "$ws/target/$TESTS_PROJECT" -c Release -nodeReuse:false \
  --filter "$TESTS" --logger "console;verbosity=normal"
