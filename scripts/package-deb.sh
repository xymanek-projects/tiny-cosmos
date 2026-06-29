#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${VERSION:-0.1.0}"
arch="${DEB_ARCH:-amd64}"
rid="${RID:-linux-x64}"
deb_compression="${DEB_COMPRESSION:-gzip}"
deb_compression_level="${DEB_COMPRESSION_LEVEL:-1}"
pkg_root="$repo_root/artifacts/deb/tiny-cosmos_${version}_${arch}"
publish_root="$repo_root/artifacts/publish/$rid"

cd "$repo_root"
if [[ "${SKIP_PUBLISH:-0}" != "1" ]]; then
  "$repo_root/scripts/publish-native.sh" "$rid"
fi
firecracker_root="$repo_root/artifacts/firecracker"
"$repo_root/images/linux/download-firecracker.sh" --manifest "$repo_root/images/linux/manifest.json" --output-dir "$firecracker_root" >/dev/null
image_build_root="${IMAGE_BUILD_ROOT:-$repo_root/artifacts/images/ubuntu-24.04-dev}"
image_manifest="$repo_root/images/linux/manifest.json"
if [[ -f "$image_build_root/manifest.json" ]]; then
  image_manifest="$image_build_root/manifest.json"
fi

rm -rf "$pkg_root"
mkdir -p \
  "$pkg_root/DEBIAN" \
  "$pkg_root/usr/bin" \
  "$pkg_root/usr/lib/tiny-cosmos/bin" \
  "$pkg_root/usr/lib/tiny-cosmos/images" \
  "$pkg_root/lib/systemd/system" \
  "$pkg_root/usr/lib/systemd/user"
chmod 0755 "$pkg_root/DEBIAN"

install -m 0755 "$publish_root/Cli/TinyCosmos.Cli" "$pkg_root/usr/bin/tinycosmos"
install -m 0755 "$publish_root/Manager/TinyCosmos.Manager" "$pkg_root/usr/lib/tiny-cosmos/bin/tinycosmos-manager"
install -m 0755 "$publish_root/Broker/TinyCosmos.Broker" "$pkg_root/usr/lib/tiny-cosmos/bin/tinycosmos-broker"
install -m 0755 "$publish_root/Guest/TinyCosmos.Guest" "$pkg_root/usr/lib/tiny-cosmos/bin/tinycosmos-guest"
install -m 0755 "$publish_root/Manager/libe_sqlite3.so" "$pkg_root/usr/lib/tiny-cosmos/bin/libe_sqlite3.so"
install -m 0755 "$firecracker_root/firecracker" "$pkg_root/usr/lib/tiny-cosmos/bin/firecracker"
install -m 0755 "$firecracker_root/jailer" "$pkg_root/usr/lib/tiny-cosmos/bin/jailer"
if [[ "$image_manifest" == "$image_build_root/manifest.json" ]]; then
  install -m 0644 "$image_build_root/kernel" "$pkg_root/usr/lib/tiny-cosmos/images/kernel"
  if [[ -f "$image_build_root/initrd" ]]; then
    install -m 0644 "$image_build_root/initrd" "$pkg_root/usr/lib/tiny-cosmos/images/initrd"
  fi
  install -m 0644 "$image_build_root/rootfs.ext4" "$pkg_root/usr/lib/tiny-cosmos/images/rootfs.ext4"
fi
install -m 0644 "$image_manifest" "$pkg_root/usr/lib/tiny-cosmos/images/manifest.json"
"$publish_root/Broker/TinyCosmos.Broker" verify-image-manifest "$pkg_root/usr/lib/tiny-cosmos/images/manifest.json" "$pkg_root" --allow-placeholders >/dev/null
install -m 0644 "$repo_root/packaging/systemd/tinycosmos-broker.service" "$pkg_root/lib/systemd/system/tinycosmos-broker.service"
install -m 0644 "$repo_root/packaging/systemd/tinycosmos-broker.socket" "$pkg_root/lib/systemd/system/tinycosmos-broker.socket"
install -m 0644 "$repo_root/packaging/systemd/tinycosmos-manager.service" "$pkg_root/usr/lib/systemd/user/tinycosmos-manager.service"
find "$pkg_root" -type d -exec chmod 0755 {} +

cat > "$pkg_root/DEBIAN/control" <<CONTROL
Package: tiny-cosmos
Version: $version
Section: devel
Priority: optional
Architecture: $arch
Maintainer: Tiny Cosmos <noreply@example.invalid>
Depends: systemd, git, openssh-client, iproute2, nftables, e2fsprogs, curl, tar, dnsmasq-base, util-linux
Description: Local VM sandbox manager for coding agents
 Tiny Cosmos provides the shared MVP manager, broker, CLI, and guest supervisor
 stack for Firecracker-backed coding-agent sandboxes.
CONTROL

cat > "$pkg_root/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
systemctl daemon-reload >/dev/null 2>&1 || true
POSTINST
chmod 0755 "$pkg_root/DEBIAN/postinst"

cat > "$pkg_root/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "deconfigure" ]; then
  systemctl disable --now tinycosmos-broker.socket >/dev/null 2>&1 || true
  systemctl stop tinycosmos-broker.service >/dev/null 2>&1 || true
fi
PRERM
chmod 0755 "$pkg_root/DEBIAN/prerm"

cat > "$pkg_root/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
systemctl daemon-reload >/dev/null 2>&1 || true
if [ "$1" = "purge" ]; then
  rm -rf /run/tiny-cosmos
  rm -rf /var/lib/tiny-cosmos
fi
POSTRM
chmod 0755 "$pkg_root/DEBIAN/postrm"

deb_path="$repo_root/artifacts/deb/tiny-cosmos_${version}_${arch}.deb"
dpkg-deb --root-owner-group -Z"$deb_compression" -z"$deb_compression_level" --build "$pkg_root" "$deb_path"

control_check="$(mktemp -d)"
trap 'rm -rf "$control_check"' EXIT
dpkg-deb --control "$deb_path" "$control_check"
test -x "$control_check/postinst"
test -x "$control_check/prerm"
test -x "$control_check/postrm"
contents_check="$control_check/contents.txt"
dpkg-deb --contents "$deb_path" >"$contents_check"
grep -F "usr/lib/tiny-cosmos/bin/libe_sqlite3.so" "$contents_check" >/dev/null
grep -F "usr/lib/tiny-cosmos/bin/firecracker" "$contents_check" >/dev/null
grep -F "usr/lib/tiny-cosmos/bin/jailer" "$contents_check" >/dev/null
if [[ "$image_manifest" == "$image_build_root/manifest.json" ]]; then
  grep -F "usr/lib/tiny-cosmos/images/kernel" "$contents_check" >/dev/null
  if [[ -f "$image_build_root/initrd" ]]; then
    grep -F "usr/lib/tiny-cosmos/images/initrd" "$contents_check" >/dev/null
  fi
  grep -F "usr/lib/tiny-cosmos/images/rootfs.ext4" "$contents_check" >/dev/null
fi
