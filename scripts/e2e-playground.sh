#!/usr/bin/env bash
set -euo pipefail

host="${PLAYGROUND_HOST:-ubuntu@192.168.10.215}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
remote_dir="${REMOTE_DIR:-/tmp/tiny-cosmos-e2e}"
install_deb="${PLAYGROUND_INSTALL_DEB:-1}"
known_hosts="$(mktemp)"
trap 'rm -f "$known_hosts"' EXIT

ssh_opts=(-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$known_hosts")

"$repo_root/scripts/package-deb.sh"
ssh "${ssh_opts[@]}" "$host" "rm -rf '$remote_dir' && mkdir -p '$remote_dir'"
rsync -az --delete \
  -e "ssh -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=$known_hosts" \
  --exclude .git \
  --exclude bin \
  --exclude obj \
  "$repo_root/" "$host:$remote_dir/"

remote_script=$(cat <<'REMOTE'
set -euo pipefail
cd "$REMOTE_DIR"
if [[ "$INSTALL_DEB" == "1" ]]; then
  sudo apt-get update
  sudo apt-get install -y git openssh-client iproute2 nftables e2fsprogs curl tar systemd dnsmasq-base util-linux
  sudo dpkg -i "$REMOTE_DIR/artifacts/deb/tiny-cosmos_0.1.0_amd64.deb"
  sudo tinycosmos setup
  cleanup() {
    sudo tinycosmos cleanup --purge >/dev/null 2>&1 || true
    sudo dpkg -P tiny-cosmos >/dev/null 2>&1 || true
  }
  trap cleanup EXIT
  TINYCOSMOS_CLI=/usr/bin/tinycosmos \
  TINYCOSMOS_MANAGER=/usr/lib/tiny-cosmos/bin/tinycosmos-manager \
  TINYCOSMOS_GUEST=/usr/lib/tiny-cosmos/bin/tinycosmos-guest \
  TINYCOSMOS_KEEP_E2E_ARTIFACTS="${TINYCOSMOS_KEEP_E2E_ARTIFACTS:-0}" \
    ./scripts/e2e-local.sh
else
  TINYCOSMOS_BIN_DIR="$REMOTE_DIR/artifacts/publish/linux-x64" ./scripts/e2e-local.sh
fi
REMOTE
)

ssh "${ssh_opts[@]}" "$host" "REMOTE_DIR='$remote_dir' INSTALL_DEB='$install_deb' bash -s" <<<"$remote_script"
