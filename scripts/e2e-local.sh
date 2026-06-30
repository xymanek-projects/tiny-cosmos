#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime_dir="${XDG_RUNTIME_DIR:-/tmp}/tiny-cosmos-e2e-$$"
if [[ -n "${TINYCOSMOS_E2E_ARTIFACT_DIR:-}" ]]; then
  state_dir="$TINYCOSMOS_E2E_ARTIFACT_DIR"
  rm -rf "$state_dir"
  mkdir -p "$state_dir"
else
  state_dir="$(mktemp -d)"
fi
socket="$runtime_dir/manager.sock"
manager_log="$state_dir/manager.log"

cleanup() {
  if [[ -n "${manager_pid:-}" ]]; then
    kill "$manager_pid" >/dev/null 2>&1 || true
    wait "$manager_pid" >/dev/null 2>&1 || true
  fi
  if [[ "${TINYCOSMOS_KEEP_E2E_ARTIFACTS:-0}" != "1" ]]; then
    rm -rf "$runtime_dir" "$state_dir"
  else
    echo "tiny-cosmos e2e artifacts kept at $state_dir" >&2
  fi
}
trap cleanup EXIT

cleanup_host_leftovers() {
  if command -v sudo >/dev/null 2>&1; then
    sudo systemctl list-units --all 'tinycosmos-sandbox*' --no-legend 2>/dev/null | awk '{print $1}' | while read -r unit; do
      sudo systemctl stop "$unit" >/dev/null 2>&1 || true
      sudo systemctl reset-failed "$unit" >/dev/null 2>&1 || true
    done
    sudo ip netns list 2>/dev/null | awk '/^tc-/ {print $1}' | while read -r ns; do sudo ip netns del "$ns" >/dev/null 2>&1 || true; done
    sudo rm -f /run/netns/tc-* >/dev/null 2>&1 || true
  fi
}

mkdir -p "$runtime_dir"
cd "$repo_root"
cleanup_host_leftovers

if [[ -n "${TINYCOSMOS_CLI:-}" || -n "${TINYCOSMOS_MANAGER:-}" || -n "${TINYCOSMOS_GUEST:-}" ]]; then
  cli_bin="${TINYCOSMOS_CLI:-}"
  manager_bin="${TINYCOSMOS_MANAGER:-}"
  guest_bin="${TINYCOSMOS_GUEST:-}"
  if [[ ! -x "$cli_bin" || ! -x "$manager_bin" || ! -x "$guest_bin" ]]; then
    echo "TINYCOSMOS_CLI, TINYCOSMOS_MANAGER, and TINYCOSMOS_GUEST must be executable paths" >&2
    exit 1
  fi
  cli=("$cli_bin")
  manager=("$manager_bin")
elif [[ -n "${TINYCOSMOS_BIN_DIR:-}" ]]; then
  cli_bin="$TINYCOSMOS_BIN_DIR/Cli/TinyCosmos.Cli"
  manager_bin="$TINYCOSMOS_BIN_DIR/Manager/TinyCosmos.Manager"
  guest_bin="$TINYCOSMOS_BIN_DIR/Guest/TinyCosmos.Guest"
  if [[ ! -x "$cli_bin" || ! -x "$manager_bin" || ! -x "$guest_bin" ]]; then
    echo "TINYCOSMOS_BIN_DIR must point at artifacts/publish/<rid>" >&2
    exit 1
  fi
  cli=("$cli_bin")
  manager=("$manager_bin")
else
  dotnet build TinyCosmos.slnx
  cli=(dotnet run --project src/TinyCosmos.Cli --)
  manager=(dotnet run --project src/TinyCosmos.Manager --)
  guest_bin="$repo_root/src/TinyCosmos.Guest/bin/Debug/net10.0/linux-x64/TinyCosmos.Guest"
fi

bash -n "$repo_root/images/linux/build-image.sh"
"$repo_root/images/linux/build-image.sh" --dry-run --guest-binary "$guest_bin" --kernel /etc/hosts --output-dir "$state_dir/image-dry-run" >/dev/null
"${cli[@]}" setup --dry-run >/dev/null
"${cli[@]}" cleanup --purge --dry-run >/dev/null
"${cli[@]}" vscode open tc-e2e /workspace/project --dry-run >/dev/null

"${manager[@]}" --socket "$socket" --state "$state_dir/state.sqlite3" >"$manager_log" 2>&1 &
manager_pid=$!

for _ in {1..50}; do
  [[ -S "$socket" ]] && break
  if ! kill -0 "$manager_pid" >/dev/null 2>&1; then
    echo "manager exited before creating socket; log follows:" >&2
    cat "$manager_log" >&2 || true
    exit 1
  fi
  sleep 0.1
done

if [[ ! -S "$socket" ]]; then
  echo "manager socket was not created; log follows:" >&2
  cat "$manager_log" >&2 || true
  exit 1
fi

"${cli[@]}" doctor || true
created="$("${cli[@]}" group get-or-create opencode:e2e --workspace "$repo_root" --socket "$socket")"
group_id="$(printf '%s' "$created" | sed -n 's/.*"groupId":{"value":"\([^"]*\)".*/\1/p')"
if [[ -z "$group_id" ]]; then
  echo "could not parse group id from create response: $created" >&2
  exit 1
fi
"${cli[@]}" group list --socket "$socket" >/dev/null
"${cli[@]}" group fork "$group_id" opencode:e2e-fork --socket "$socket" >/dev/null
"${cli[@]}" group get-or-create opencode:e2e-two --workspace "$repo_root" --socket "$socket" >/dev/null

exec_log="$state_dir/exec.log"
if ! "${cli[@]}" exec "$group_id" --socket "$socket" -- bash -lc 'printf tiny-exec && sudo true && docker --version >/dev/null' >"$exec_log" 2>&1; then
  echo "exec command failed:" >&2
  cat "$exec_log" >&2
  exit 1
fi
if ! grep -q '"exitCode":0' "$exec_log" || ! grep -q 'tiny-exec' "$exec_log"; then
  echo "exec returned an unexpected result:" >&2
  cat "$exec_log" >&2
  exit 1
fi

pty_log="$state_dir/pty.log"
if ! "${cli[@]}" pty "$group_id" --socket "$socket" -- bash -lc 'printf tiny-pty' >"$pty_log" 2>&1; then
  echo "pty command failed:" >&2
  cat "$pty_log" >&2
  exit 1
fi
if ! grep -q '"exitCode":0' "$pty_log" || ! grep -q 'tiny-pty' "$pty_log"; then
  echo "pty returned an unexpected result:" >&2
  cat "$pty_log" >&2
  exit 1
fi

"${cli[@]}" file write "$group_id" /workspace/project/e2e.txt --text 'tiny needle' --overwrite --socket "$socket" >/dev/null
read_log="$state_dir/read.log"
"${cli[@]}" file read "$group_id" /workspace/project/e2e.txt --socket "$socket" >"$read_log"
if ! grep -q 'dGlueSBuZWVkbGU=' "$read_log"; then
  echo "file read returned unexpected content:" >&2
  cat "$read_log" >&2
  exit 1
fi
"${cli[@]}" file list "$group_id" /workspace/project --socket "$socket" | grep -q 'e2e.txt'
"${cli[@]}" file search "$group_id" /workspace/project tiny --socket "$socket" | grep -q 'e2e.txt'

"${cli[@]}" group update "$group_id" --name opencode:e2e-renamed --socket "$socket" >/dev/null
"${cli[@]}" group inspect "$group_id" --socket "$socket" | grep -q 'opencode:e2e-renamed'
"${cli[@]}" diagnostics --socket "$socket" >/dev/null

sandbox_id="$(printf '%s' "$created" | sed -n 's/.*"primarySandboxId":{"value":"\([^"]*\)".*/\1/p')"
if [[ -z "$sandbox_id" ]]; then
  echo "could not parse sandbox id from create response: $created" >&2
  exit 1
fi
"${cli[@]}" sandbox stop "$sandbox_id" --socket "$socket" >/dev/null
sleep 8
"${cli[@]}" sandbox start "$sandbox_id" --socket "$socket" >/dev/null
restart_log="$state_dir/restart-exec.log"
"${cli[@]}" exec "$group_id" --socket "$socket" -- bash -lc 'cat /workspace/project/e2e.txt' >"$restart_log"
if ! grep -q '"exitCode":0' "$restart_log" || ! grep -q 'tiny needle' "$restart_log"; then
  echo "restart persistence check failed:" >&2
  cat "$restart_log" >&2
  exit 1
fi

echo "tiny-cosmos local e2e completed"
