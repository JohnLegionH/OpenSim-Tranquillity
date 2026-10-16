#!/usr/bin/env bash
# Copyright (c) 2026 Legion Builds
# All rights reserved.
#
# Redistribution and use in source and binary forms, with or without
# modification, are permitted provided that the following conditions are met:
#     * Redistributions of source code must retain the above copyright
#       notice, this list of conditions and the following disclaimer.
#     * Redistributions in binary form must reproduce the above copyright
#       notice, this list of conditions and the following disclaimer in the
#       documentation and/or other materials provided with the distribution.
#     * Neither the name of the OpenSimulator Project nor the
#       names of its contributors may be used to endorse or promote products
#       derived from this software without specific prior written permission.
#
# THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
# EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
# WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
# DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
# DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
# (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
# LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
# ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
# (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
# SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
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
# A malformed line in the step's GITHUB_ENV file once failed this step after its tests had passed (likely the
# build's version tasks writing to it from parallel project builds). The build gets scratch files instead.
GITHUB_ENV="$RUNNER_TEMP/tests-env" GITHUB_OUTPUT="$RUNNER_TEMP/tests-output" \
NBGV_GitEngine=Disabled dotnet test "$ws/target/$TESTS_PROJECT" -c Release -nodeReuse:false \
  --filter "$TESTS" --logger "console;verbosity=normal"
