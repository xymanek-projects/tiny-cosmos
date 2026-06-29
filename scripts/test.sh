#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dotnet restore TinyCosmos.slnx
dotnet build TinyCosmos.slnx --no-restore
dotnet test TinyCosmos.slnx --no-build
