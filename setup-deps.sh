#!/usr/bin/bash

set -xeuo pipefail

rm -rf deps/installs
mkdir -p deps/installs

for f in deps/*.ckan; do
    base="$(basename -- "$f")"
    name="${base%.*}"
    installdir="./deps/installs/$name"

    ckan instance fake "HRK-$name" "$installdir" 1.12.5 \
        --game KSP --MakingHistory 1.9.1 --BreakingGround 1.7.1
    ckan instance forget "HRK-$name"

    ckan update  --gamedir "$installdir"
    ckan install --gamedir "$installdir" --headless --no-recommends -c "$f"
done
