#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: images/linux/download-firecracker.sh [--manifest path] [--output-dir path]

Download the pinned Firecracker release archive from the Tiny Cosmos image
manifest, verify its sha256, and extract the firecracker and jailer binaries.
USAGE
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
manifest="$repo_root/images/linux/manifest.json"
output_dir="$repo_root/artifacts/firecracker"

while (($#)); do
  case "$1" in
    --manifest)
      manifest="$2"
      shift 2
      ;;
    --output-dir)
      output_dir="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'unknown option: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

extract_json_string() {
  local key="$1"
  sed -n "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" "$manifest" | head -1
}

version="$(extract_json_string version)"
asset_name="$(extract_json_string assetName)"
url="$(extract_json_string url)"
sha256="$(extract_json_string sha256)"

if [[ -z "$version" || -z "$asset_name" || -z "$url" || -z "$sha256" ]]; then
  echo "manifest is missing Firecracker version, assetName, url, or sha256" >&2
  exit 2
fi

case "$url" in
  https://github.com/firecracker-microvm/firecracker/releases/download/*) ;;
  *)
    echo "refusing to download Firecracker from unapproved URL: $url" >&2
    exit 2
    ;;
esac

mkdir -p "$output_dir"
archive="$output_dir/$asset_name"
if [[ ! -f "$archive" ]]; then
  curl -fsSL -o "$archive" "$url"
fi

actual="$(sha256sum "$archive" | awk '{ print $1 }')"
if [[ "$actual" != "$sha256" ]]; then
  echo "Firecracker archive digest mismatch: expected $sha256, got $actual" >&2
  rm -f "$archive"
  exit 2
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
tar -xzf "$archive" -C "$work_dir"

release_dir="$work_dir/release-${version}-x86_64"
firecracker="$release_dir/firecracker-${version}-x86_64"
jailer="$release_dir/jailer-${version}-x86_64"
if [[ ! -x "$firecracker" || ! -x "$jailer" ]]; then
  echo "Firecracker archive did not contain expected executable firecracker/jailer binaries" >&2
  exit 2
fi

install -m 0755 "$firecracker" "$output_dir/firecracker"
install -m 0755 "$jailer" "$output_dir/jailer"
printf 'firecracker=%s\n' "$output_dir/firecracker"
printf 'jailer=%s\n' "$output_dir/jailer"
