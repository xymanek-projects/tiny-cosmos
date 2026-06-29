#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
configuration="${CONFIGURATION:-Release}"
out="$repo_root/artifacts/publish/$rid"

cd "$repo_root"
rm -rf "$out"
mkdir -p "$out"

for project in TinyCosmos.Cli TinyCosmos.Manager TinyCosmos.Broker TinyCosmos.Guest; do
  dotnet publish "src/$project/$project.csproj" \
    -c "$configuration" \
    -r "$rid" \
    -o "$out/${project#TinyCosmos.}" \
    /warnaserror
done

find "$out" -maxdepth 2 -type f -perm -111 -print | sort
