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
# Build the store-check harness against one checked-out tree: ci/build.sh <target|baseline>
set -euo pipefail
role=$1
root="$GITHUB_WORKSPACE/$role"
work="$GITHUB_WORKSPACE/work/$role"
mkdir -p "$work"
cp -r "$GITHUB_WORKSPACE/ci/harness/StoreCheck" "$work/src"
echo "$role tree: $(git -C "$root" rev-parse HEAD) $(git -C "$root" log -1 --format=%s)"
# Shallow checkout: Nerdbank.GitVersioning cannot walk history, so it is told not to read git.
# A malformed line in the step's GITHUB_ENV file once failed this step before the build ran (likely the
# build's version tasks writing to it from parallel project builds). The build gets scratch files instead.
GITHUB_ENV="$RUNNER_TEMP/build-env-$role" GITHUB_OUTPUT="$RUNNER_TEMP/build-output-$role" NBGV_GitEngine=Disabled dotnet build "$work/src/StoreCheck.csproj" -c Release -nodeReuse:false \
  -p:RepoRoot="$root" -o "$work/bin" -clp:ErrorsOnly
