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
# Run the checks for one target on one server. Env: KIND (mysql|pgsql|sqlite), ADMIN (server connection
# string without a database), CHECKS, EXPECT (space separated Store:table.column), LABEL.
set -uo pipefail
ws=$GITHUB_WORKSPACE
args=(--db "$KIND" --checks "$CHECKS" --label "$LABEL" --known "$ws/ci/targets.json")
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
