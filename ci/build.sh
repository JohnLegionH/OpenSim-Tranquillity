#!/usr/bin/env bash
# Build the store-check harness against one checked-out tree: ci/build.sh <target|baseline>
set -euo pipefail
role=$1
root="$GITHUB_WORKSPACE/$role"
work="$GITHUB_WORKSPACE/work/$role"
mkdir -p "$work"
cp -r "$GITHUB_WORKSPACE/ci/harness/StoreCheck" "$work/src"
echo "$role tree: $(git -C "$root" rev-parse HEAD) $(git -C "$root" log -1 --format=%s)"
# Shallow checkout: Nerdbank.GitVersioning cannot walk history, so it is told not to read git.
NBGV_GitEngine=Disabled dotnet build "$work/src/StoreCheck.csproj" -c Release -nodeReuse:false \
  -p:RepoRoot="$root" -o "$work/bin" -clp:ErrorsOnly
