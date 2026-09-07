#!/usr/bin/env bash
# Populate scripts/ (gitignored) with every script asset held in a prim on this grid.
#
# Usage:  ./fetch-live-scripts.sh
#
# manifest.json (committed) lists each asset id with the sha256 of its body. The bodies are NOT
# committed: they are residents' content. This pulls them from the live grid database, the same
# source LiveScriptCompileTests documents, and verifies each against the manifest.
#
# Nothing is fabricated: an asset that cannot be fetched, or whose body does not match its recorded
# hash, stops the script (exit 1) rather than leaving the suite testing something else.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
container="${LEGIONGRID_MYSQL_CONTAINER:-legiongrid_mysql}"
db="${LEGIONGRID_DB:-legiongrid}"
user="${LEGIONGRID_DB_USER:-legiongrid_app}"

if [ -z "${LEGIONGRID_DB_PW:-}" ]; then
    echo "Set LEGIONGRID_DB_PW to the ${user} password (it is in config-include/GridCommon.ini)." >&2
    exit 1
fi

mkdir -p "$here/scripts"
ids=$(python -c "import json,sys; print('\n'.join(s['assetId'] for s in json.load(open(r'$here/manifest.json'))['scripts']))")

fetched=0
for id in $ids; do
    docker exec "$container" mysql -u"$user" -p"$LEGIONGRID_DB_PW" "$db" -N -B --raw \
        -e "SELECT CONVERT(data USING utf8mb4) FROM assets WHERE id='$id';" 2>/dev/null > "$here/scripts/$id.lsl"
    if [ ! -s "$here/scripts/$id.lsl" ]; then
        echo "FAIL: asset $id returned nothing" >&2
        rm -f "$here/scripts/$id.lsl"
        exit 1
    fi
    want=$(python -c "import json; print([s['sha256'] for s in json.load(open(r'$here/manifest.json'))['scripts'] if s['assetId']=='$id'][0])")
    got=$(sha256sum "$here/scripts/$id.lsl" | cut -d' ' -f1)
    if [ "$want" != "$got" ]; then
        echo "FAIL: $id body does not match the manifest (want $want, got $got)." >&2
        echo "      The script changed in world. Re-record the manifest deliberately, do not edit it to pass." >&2
        exit 1
    fi
    fetched=$((fetched + 1))
done

echo "fetched and hash-verified $fetched scripts into $here/scripts/"
