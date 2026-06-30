#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: images/linux/build-image.sh [options]

Build the Tiny Cosmos Ubuntu 24.04 Firecracker guest image.

Options:
  --output-dir PATH       Directory for rootfs.ext4, kernel, and manifest.json.
  --guest-binary PATH     Native AOT tinycosmos-guest binary to install.
  --kernel PATH           Firecracker-compatible kernel image to copy.
  --initrd PATH           Optional initrd image to copy and use at boot.
  --modules-dir PATH      Optional /lib/modules/<version> tree to copy into rootfs.
  --rootfs-size SIZE      Root filesystem size passed to truncate. Default: 3G.
  --suite NAME            Ubuntu suite. Default: noble.
  --mirror URL            Ubuntu mirror. Default: http://archive.ubuntu.com/ubuntu.
  --components LIST       Ubuntu archive components. Default: main,universe.
  --dry-run               Print the plan and validate local inputs without root.
  -h, --help              Show this help.

The real build path requires root because it runs debootstrap, mounts an ext4
image, and configures the guest rootfs. Run it on the playground VM or another
Ubuntu host with sudo/root.
USAGE
}

repo_root() {
  local dir
  dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
  printf '%s\n' "$dir"
}

copy_rootfs_overlay() {
  local root="$1"
  local overlay
  overlay="$(repo_root)/images/linux/rootfs"
  if [[ -d "$overlay" ]]; then
    rsync -a "$overlay"/ "$root"/
  fi
}

require_tool() {
  local tool="$1"
  if ! command -v "$tool" >/dev/null 2>&1; then
    printf 'missing required tool: %s\n' "$tool" >&2
    exit 2
  fi
}

sha256_file() {
  sha256sum "$1" | awk '{ print $1 }'
}

json_array_packages() {
  printf '    "openssh-server",\n'
  printf '    "git",\n'
  printf '    "ripgrep",\n'
  printf '    "tar",\n'
  printf '    "ca-certificates",\n'
  printf '    "sudo",\n'
  printf '    "kmod",\n'
  printf '    "docker.io"\n'
}

write_manifest() {
  local manifest="$1"
  local kernel_name="$2"
  local kernel_sha="$3"
  local rootfs_name="$4"
  local rootfs_sha="$5"
  local guest_sha="$6"

  cat >"$manifest" <<EOF
{
  "schemaVersion": 1,
  "imageId": "ubuntu-24.04-dev",
  "guestOs": "ubuntu-24.04",
  "architecture": "x86_64",
  "firecracker": {
    "version": "v1.16.0",
    "assetName": "firecracker-v1.16.0-x86_64.tgz",
    "url": "https://github.com/firecracker-microvm/firecracker/releases/download/v1.16.0/firecracker-v1.16.0-x86_64.tgz",
    "sha256": "bd04e26952d4e158085778c6230a0b383d2619c319182e27eaa9d61a212e92d6"
  },
  "artifacts": {
    "kernel": {
      "path": "$kernel_name",
      "sha256": "$kernel_sha"
    },
    "rootfs": {
      "path": "$rootfs_name",
      "sha256": "$rootfs_sha"
    },
    "guestSupervisor": {
      "path": "/usr/lib/tiny-cosmos/bin/tinycosmos-guest",
      "sha256": "$guest_sha"
    }
  },
  "packages": [
$(json_array_packages)
  ]
}
EOF
}

cleanup_mounts() {
  local root="$1"
  for mount_point in "$root/proc" "$root/sys" "$root/dev/pts" "$root/dev"; do
    if mountpoint -q "$mount_point"; then
      umount "$mount_point"
    fi
  done
}

run_chroot() {
  local root="$1"
  shift
  chroot "$root" /usr/bin/env DEBIAN_FRONTEND=noninteractive "$@"
}

configure_rootfs() {
  local root="$1"
  local guest_binary="$2"
  local modules_dir="$3"

  mkdir -p "$root/usr/lib/tiny-cosmos/bin" "$root/home/agent/.ssh" "$root/etc/systemd/system/ssh.service.d" "$root/workspace/project" "$root/lib/modules"
  install -m 0755 "$guest_binary" "$root/usr/lib/tiny-cosmos/bin/tinycosmos-guest"
  if [[ -n "$modules_dir" ]]; then
    rsync -a "$modules_dir" "$root/lib/modules/"
  fi
  copy_rootfs_overlay "$root"

  cat >"$root/etc/hostname" <<'EOF'
tiny-cosmos
EOF
  cat >"$root/etc/hosts" <<'EOF'
127.0.0.1 localhost
127.0.1.1 tiny-cosmos
::1 localhost ip6-localhost ip6-loopback
EOF
  cat >"$root/etc/sudoers.d/90-tiny-cosmos-agent" <<'EOF'
agent ALL=(ALL) NOPASSWD:ALL
EOF
  chmod 0440 "$root/etc/sudoers.d/90-tiny-cosmos-agent"

  run_chroot "$root" useradd --create-home --shell /bin/bash --groups sudo,docker agent
  chown -R 1000:1000 "$root/home/agent"
  chown -R 1000:1000 "$root/workspace"
  chmod 0700 "$root/home/agent/.ssh"

  run_chroot "$root" ssh-keygen -A
  run_chroot "$root" systemctl enable workspace-project.mount
  run_chroot "$root" systemctl enable tinycosmos-guest.service
  run_chroot "$root" systemctl enable ssh.service
  run_chroot "$root" systemctl enable docker.service || true
  run_chroot "$root" apt-get clean
  rm -rf "$root/var/lib/apt/lists/"*
}

build_rootfs_tree() {
  local root="$1"
  local suite="$2"
  local mirror="$3"
  local components="$4"

  local attempt
  for attempt in 1 2 3; do
    if debootstrap \
      --variant=minbase \
      --components="$components" \
      --include=systemd-sysv,openssh-server,git,ripgrep,tar,ca-certificates,sudo,kmod,docker.io,iproute2,iptables,curl \
      "$suite" \
      "$root" \
      "$mirror"; then
      return 0
    fi

    printf 'debootstrap attempt %s failed; retrying with a clean rootfs tree\n' "$attempt" >&2
    rm -rf "$root"
    mkdir -p "$root"
    sleep 5
  done

  return 1
}

build_ext4_image() {
  local root="$1"
  local image="$2"
  local size="$3"

  truncate -s "$size" "$image"
  mkfs.ext4 -F -L tiny-cosmos-root "$image"
  local mount_dir
  mount_dir="$(mktemp -d)"
  mount -o loop "$image" "$mount_dir"
  rsync -aHAX --numeric-ids "$root"/ "$mount_dir"/
  sync
  umount "$mount_dir"
  rmdir "$mount_dir"
  e2fsck -fy "$image" >/dev/null
}

output_dir="$(repo_root)/artifacts/images/ubuntu-24.04-dev"
guest_binary="$(repo_root)/artifacts/publish/linux-x64/Guest/TinyCosmos.Guest"
kernel_path=""
initrd_path=""
modules_dir=""
rootfs_size="3G"
suite="noble"
mirror="http://archive.ubuntu.com/ubuntu"
components="main,universe"
dry_run=0

while (($#)); do
  case "$1" in
    --output-dir)
      output_dir="$2"
      shift 2
      ;;
    --guest-binary)
      guest_binary="$2"
      shift 2
      ;;
    --kernel)
      kernel_path="$2"
      shift 2
      ;;
    --initrd)
      initrd_path="$2"
      shift 2
      ;;
    --modules-dir)
      modules_dir="$2"
      shift 2
      ;;
    --rootfs-size)
      rootfs_size="$2"
      shift 2
      ;;
    --suite)
      suite="$2"
      shift 2
      ;;
    --mirror)
      mirror="$2"
      shift 2
      ;;
    --components)
      components="$2"
      shift 2
      ;;
    --dry-run)
      dry_run=1
      shift
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

if [[ -z "$kernel_path" ]]; then
  if [[ -e /boot/vmlinux ]]; then
    kernel_path="/boot/vmlinux"
  else
    kernel_path="$(find /boot -maxdepth 1 -type f -name 'vmlinux-*' 2>/dev/null | sort -V | tail -1 || true)"
  fi
fi

printf 'output_dir=%s\n' "$output_dir"
printf 'guest_binary=%s\n' "$guest_binary"
printf 'kernel=%s\n' "${kernel_path:-<missing>}"
printf 'initrd=%s\n' "${initrd_path:-<none>}"
printf 'modules_dir=%s\n' "${modules_dir:-<none>}"
printf 'rootfs_size=%s\n' "$rootfs_size"
printf 'suite=%s\n' "$suite"
printf 'mirror=%s\n' "$mirror"
printf 'components=%s\n' "$components"

if [[ ! -x "$guest_binary" ]]; then
  printf 'guest binary is missing or not executable: %s\n' "$guest_binary" >&2
  exit 2
fi

if [[ -z "$kernel_path" || ! -f "$kernel_path" ]]; then
  printf 'kernel image is missing; pass --kernel PATH to a Firecracker-compatible kernel\n' >&2
  exit 2
fi
if [[ -n "$initrd_path" && ! -f "$initrd_path" ]]; then
  printf 'initrd image is missing: %s\n' "$initrd_path" >&2
  exit 2
fi
if [[ -n "$modules_dir" && ! -d "$modules_dir" ]]; then
  printf 'modules directory is missing: %s\n' "$modules_dir" >&2
  exit 2
fi

if [[ "$dry_run" == 1 ]]; then
  printf 'dry-run: inputs look usable; privileged image build was not run\n'
  exit 0
fi

if [[ "$(id -u)" != 0 ]]; then
  printf 'image build requires root; rerun with sudo on the playground VM or another build host\n' >&2
  exit 2
fi

for tool in debootstrap chroot mount umount rsync mkfs.ext4 e2fsck truncate sha256sum awk; do
  require_tool "$tool"
done

work_dir="$(mktemp -d)"
root_tree="$work_dir/rootfs"
trap 'cleanup_mounts "$root_tree"; rm -rf "$work_dir"' EXIT

mkdir -p "$root_tree" "$output_dir"
build_rootfs_tree "$root_tree" "$suite" "$mirror" "$components"
mount --bind /dev "$root_tree/dev"
mount --bind /dev/pts "$root_tree/dev/pts"
mount -t proc proc "$root_tree/proc"
mount -t sysfs sys "$root_tree/sys"
configure_rootfs "$root_tree" "$guest_binary" "$modules_dir"
cleanup_mounts "$root_tree"

kernel_out="$output_dir/kernel"
initrd_out="$output_dir/initrd"
rootfs_out="$output_dir/rootfs.ext4"
manifest_out="$output_dir/manifest.json"
guest_out="$output_dir/usr/lib/tiny-cosmos/bin/tinycosmos-guest"
installed_kernel_out="$output_dir/usr/lib/tiny-cosmos/images/kernel"
installed_initrd_out="$output_dir/usr/lib/tiny-cosmos/images/initrd"
installed_rootfs_out="$output_dir/usr/lib/tiny-cosmos/images/rootfs.ext4"
if [[ "$(realpath "$kernel_path")" != "$(realpath -m "$kernel_out")" ]]; then
  install -m 0644 "$kernel_path" "$kernel_out"
fi
if [[ -n "$initrd_path" ]]; then
  if [[ "$(realpath "$initrd_path")" != "$(realpath -m "$initrd_out")" ]]; then
    install -m 0644 "$initrd_path" "$initrd_out"
  fi
fi
build_ext4_image "$root_tree" "$rootfs_out" "$rootfs_size"
install -D -m 0755 "$guest_binary" "$guest_out"
install -D -m 0644 "$kernel_out" "$installed_kernel_out"
if [[ -f "$initrd_out" ]]; then
  install -D -m 0644 "$initrd_out" "$installed_initrd_out"
fi
mkdir -p "$(dirname "$installed_rootfs_out")"
ln -f "$rootfs_out" "$installed_rootfs_out"

write_manifest \
  "$manifest_out" \
  "/usr/lib/tiny-cosmos/images/kernel" \
  "$(sha256_file "$kernel_out")" \
  "/usr/lib/tiny-cosmos/images/rootfs.ext4" \
  "$(sha256_file "$rootfs_out")" \
  "$(sha256_file "$guest_binary")"

printf 'built image manifest: %s\n' "$manifest_out"
