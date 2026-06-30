#!/usr/bin/env bash
set -euo pipefail

host="${PLAYGROUND_HOST:-ubuntu@192.168.10.215}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
remote_dir="${REMOTE_DIR:-/tmp/tiny-cosmos-e2e}"
install_deb="${PLAYGROUND_INSTALL_DEB:-1}"
known_hosts="$(mktemp)"
trap 'rm -f "$known_hosts"' EXIT

ssh_opts=(-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$known_hosts")

"$repo_root/scripts/publish-native.sh" linux-x64
ssh "${ssh_opts[@]}" "$host" sh -s -- "$remote_dir" <<'REMOTE_MKDIR'
set -eu
remote_dir="$1"
if command -v sudo >/dev/null 2>&1; then
  sudo rm -rf -- "$remote_dir"
else
  rm -rf -- "$remote_dir"
fi
mkdir -p -- "$remote_dir"
REMOTE_MKDIR
rsync -az --delete \
  -e "ssh -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=$known_hosts" \
  --exclude .git \
  --exclude bin \
  --exclude obj \
  "$repo_root/" "$host:$remote_dir/"

remote_script=$(cat <<'REMOTE'
set -euo pipefail
REMOTE_DIR="$1"
INSTALL_DEB="$2"
cd "$REMOTE_DIR"
if [[ "$INSTALL_DEB" == "1" ]]; then
  sudo tinycosmos cleanup --purge >/dev/null 2>&1 || true
  sudo dpkg -P tiny-cosmos >/dev/null 2>&1 || true
  sudo apt-get update
  sudo apt-get install -y git openssh-client iproute2 nftables e2fsprogs curl tar systemd dnsmasq-base util-linux clang zlib1g-dev debootstrap zstd lz4 rsync
  sudo apt-get install -y "linux-headers-$(uname -r)" || true
  sudo ./scripts/prepare-host-kernel.sh artifacts/host-kernel
  sudo chown -R "$USER:$USER" artifacts/host-kernel
  image_args=(
    --guest-binary "$REMOTE_DIR/artifacts/publish/linux-x64/Guest/TinyCosmos.Guest"
    --kernel "$REMOTE_DIR/artifacts/host-kernel/vmlinux"
    --modules-dir "/lib/modules/$(uname -r)"
  )
  if [[ -f "$REMOTE_DIR/artifacts/host-kernel/initrd" ]]; then
    image_args+=(--initrd "$REMOTE_DIR/artifacts/host-kernel/initrd")
  fi
  sudo ./images/linux/build-image.sh "${image_args[@]}"
  sudo chown -R "$USER:$USER" artifacts/images
  SKIP_PUBLISH=1 ./scripts/package-deb.sh
  rm -rf artifacts/deb/tiny-cosmos_0.1.0_amd64
  find artifacts/images -type f \( -name rootfs.ext4 -o -name tinycosmos-guest \) -delete
  sudo apt-get install -y "$REMOTE_DIR/artifacts/deb/tiny-cosmos_0.1.0_amd64.deb"
  sudo tinycosmos setup
  cleanup() {
    sudo tinycosmos cleanup --purge >/dev/null 2>&1 || true
    sudo dpkg -P tiny-cosmos >/dev/null 2>&1 || true
  }
  trap cleanup EXIT
  TINYCOSMOS_CLI=/usr/bin/tinycosmos \
  TINYCOSMOS_MANAGER=/usr/lib/tiny-cosmos/bin/tinycosmos-manager \
  TINYCOSMOS_GUEST=/usr/lib/tiny-cosmos/bin/tinycosmos-guest \
  TINYCOSMOS_E2E_ARTIFACT_DIR="$REMOTE_DIR/artifacts/e2e" \
  TINYCOSMOS_KEEP_E2E_ARTIFACTS="${TINYCOSMOS_KEEP_E2E_ARTIFACTS:-0}" \
    ./scripts/e2e-local.sh
else
  TINYCOSMOS_BIN_DIR="$REMOTE_DIR/artifacts/publish/linux-x64" ./scripts/e2e-local.sh
fi
REMOTE
)

ssh "${ssh_opts[@]}" "$host" bash -s -- "$remote_dir" "$install_deb" <<<"$remote_script"
