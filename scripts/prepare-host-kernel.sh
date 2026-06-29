#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/prepare-host-kernel.sh [output-dir]

Prepare the current Ubuntu host kernel for a Tiny Cosmos image build. The
output directory receives:

  vmlinux   Uncompressed kernel image for Firecracker.
  initrd    Matching initrd, when present on the host.

Set KERNEL_RELEASE to prepare a release other than `uname -r`.
USAGE
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi

output_dir="${1:-artifacts/host-kernel}"
release="${KERNEL_RELEASE:-$(uname -r)}"
mkdir -p "$output_dir"

find_first_file() {
  local candidate
  for candidate in "$@"; do
    if [[ -f "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

find_extractor() {
  local candidate
  for candidate in \
    "/usr/src/linux-headers-$release/scripts/extract-vmlinux" \
    /usr/src/linux-headers*/scripts/extract-vmlinux; do
    if [[ -f "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  if command -v extract-vmlinux >/dev/null 2>&1; then
    command -v extract-vmlinux
    return 0
  fi

  return 1
}

kernel_out="$output_dir/vmlinux"
if kernel_path="$(find_first_file \
  "/boot/vmlinux-$release" \
  "/boot/vmlinux" \
  "/usr/lib/modules/$release/vmlinux")"; then
  install -m 0644 "$kernel_path" "$kernel_out"
else
  compressed_kernel="$(find_first_file "/boot/vmlinuz-$release" "/boot/vmlinuz")" || {
    echo "could not find /boot/vmlinux or /boot/vmlinuz for $release" >&2
    exit 2
  }
  extractor="$(find_extractor)" || {
    echo "could not find extract-vmlinux for compressed kernel $compressed_kernel" >&2
    exit 2
  }
  kernel_tmp="$kernel_out.tmp"
  rm -f "$kernel_tmp"
  if [[ "$extractor" == */extract-vmlinux ]]; then
    bash "$extractor" "$compressed_kernel" >"$kernel_tmp"
  else
    "$extractor" "$compressed_kernel" >"$kernel_tmp"
  fi
  install -m 0644 "$kernel_tmp" "$kernel_out"
  rm -f "$kernel_tmp"
fi

if [[ ! -s "$kernel_out" ]]; then
  echo "prepared kernel is empty: $kernel_out" >&2
  exit 2
fi

if initrd_path="$(find_first_file "/boot/initrd.img-$release" "/boot/initrd.img")"; then
  install -m 0644 "$initrd_path" "$output_dir/initrd"
fi

printf 'kernel=%s\n' "$kernel_out"
if [[ -f "$output_dir/initrd" ]]; then
  printf 'initrd=%s\n' "$output_dir/initrd"
else
  printf 'initrd=<none>\n'
fi
if [[ -d "/lib/modules/$release" ]]; then
  printf 'modules=%s\n' "/lib/modules/$release"
else
  printf 'modules=<none>\n'
fi
