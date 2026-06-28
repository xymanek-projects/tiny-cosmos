# Firecracker-style ephemeral VMs on a Windows host (via HCS)

Design notes and findings for running low-overhead, headless, disposable VMs —
both **Windows (Server Core)** and **Linux** guests — driven programmatically
through the **Host Compute Service (HCS)**.

---

## 1. Desired features

- **Run real full VMs** (not containers), with the least overhead practical.
- **Headless / CLI-only** — no GUI, no VMConnect.
- **Both Windows and Linux guests.**
- **Programmatic lifecycle** — create / start / stop from code, no Hyper-V Manager.
- **Ephemeral / disposable** — fast spin-up, small footprint, throwaway instances.
- **Shared network** between VMs (bridge on one host; overlay across hosts).
- **Host ↔ guest communication** — run commands, move files, expose ports.
- **Snapshot-like control** — pause/resume and save/restore (skip cold boot).
- **Repeatable image preparation** for both guest types.

The mental model throughout is **Firecracker** (a minimal userspace VMM on KVM,
booting a kernel directly, API-driven, one disposable microVM per workload).
This document maps that model onto what Windows actually provides.

---

## 2. Which layer to build on

Once Hyper-V is enabled, the host OS is itself the root partition; WSL2,
containers, and everything else ride the same hypervisor. "Least overhead" is
about minimizing device-model and management cost on that one hypervisor, not
finding a different substrate.

| Layer | What it is | Fit for this goal |
|---|---|---|
| **WHP** (Windows Hypervisor Platform) | Low-level API to build your own VMM (partitions, GPA mapping, vCPU run loop, exit handling). The Windows analog of "userspace VMM on KVM" — i.e. the literal Firecracker layer. | True architectural analog, but you own the entire device model. Per-exit cost is routed to user mode (higher than KVM). All-DIY. |
| **HCS** (Host Compute Service) + **HCN** (networking) | Declarative JSON "compute system" documents; platform builds/lifecycles a VM or container. | **Chosen.** Real VMs with synthetic (fast) devices, direct kernel boot, programmatic lifecycle + callbacks, far less work than WHP, lighter than full VMMS/WMI. |
| **Hyper-V WMI / PowerShell** | Fully managed VM stack (VMMS, worker per VM, checkpoints). | Heaviest; slowest cold start; richest management. Overkill for disposable microVMs. |

**Off-the-shelf alternative:** QEMU `-machine microvm -accel whpx` is the closest
ready-made Firecracker clone (the `microvm` machine type is explicitly modeled on
Firecracker), but WHPX has higher exit overhead and rough edges. We chose HCS for
the native, programmatic VM path.

**Key fact:** HCS can create a **standalone VirtualMachine**, not only
"container-in-a-VM." A compute system is either a `Container` or a
`VirtualMachine` (mutually exclusive). WSL2's Linux VM is itself an HCS
VirtualMachine. The "utility VM" framing is convention, not an API limit.

---

## 3. HCS API essentials

- Native C API in **`computecore.dll`** (`computecore.h` / `computecore.lib`).
  The **JSON document is the contract**; every binding just hands it to these
  functions.
- **hcsshim (Go)** is only a wrapper — and its `hcs` / `schema2` / `uvm` packages
  live under `internal/`, so you **vendor/fork it or call the native API directly**.
  The public `hcn` package (networking) *is* importable.
- Other-language bindings prove the native path: Rust `hcs-rs`, C#
  `dotnet-computevirtualization` both P/Invoke / FFI straight into `Hcs*`.
- **Async model** (the main thing the Go wrapper hides):
  `HcsCreateOperation` → `HcsCreateComputeSystem(id, json, op, NULL, &sys)` →
  `HcsWaitForOperationResult(op, ...)` → `HcsStartComputeSystem`.
  `securityDescriptor` is reserved (must be `NULL`). Minimum: Win10 1809 / Server 2019.

```c
HCS_OPERATION op = HcsCreateOperation(NULL, NULL);
HCS_SYSTEM sys = NULL;
HcsCreateComputeSystem(L"lin1", linuxVmJson, op, NULL, &sys);
HcsWaitForOperationResult(op, INFINITE, &resultDoc);
HcsStartComputeSystem(sys, op, NULL);
HcsWaitForOperationResult(op, INFINITE, &resultDoc);
```

---

## 4. VM configuration (the schema)

Both guests use synthetic VMBus devices (SCSI, NIC) — never legacy/emulated
hardware — for fewest emulation exits and fastest boot. Static memory (no dynamic
balloon) trades RAM for less jitter/overhead. No video adapter; console via a COM
port mapped to a named pipe.

### Windows guest
- **Generation-2 UEFI** boot from a **VHDX** on the synthetic SCSI controller.
- Recent builds need a **`GuestState` (.vmgs)** file.
- `NetworkAdapter` references an HCN endpoint by `EndpointId`.

```json
"VirtualMachine": {
  "Chipset": { "Uefi": { "BootThis": { "DeviceType": "ScsiDrive", "DiskNumber": 0 } } },
  "ComputeTopology": { "Memory": { "SizeInMB": 4096 }, "Processor": { "Count": 2 } },
  "GuestState": { "GuestStateFilePath": "C:\\vms\\win\\os.vmgs" },
  "Devices": {
    "Scsi": { "primary": { "Attachments": { "0": { "Type": "VirtualDisk", "Path": "C:\\vms\\win\\os.vhdx" } } } },
    "NetworkAdapters": { "eth0": { "EndpointId": "<guid>", "MacAddress": "00-15-5D-00-11-01" } }
  }
}
```

### Linux guest
- **Direct kernel boot** (`LinuxKernelDirect`): kernel + initrd + cmdline, no firmware.
- `root=/dev/sda` matches a SCSI-attached rootfs VHDX; `console=ttyS0` → named pipe (headless).
- The direct-boot loader accepts **either `bzImage` or uncompressed `vmlinux`** on x86_64.

```json
"VirtualMachine": {
  "Chipset": { "LinuxKernelDirect": {
    "KernelFilePath": "C:\\vms\\lin\\vmlinux",
    "InitRdPath":     "C:\\vms\\lin\\initrd.img",
    "KernelCmdLine":  "console=ttyS0 root=/dev/sda ro"
  } },
  "ComputeTopology": { "Memory": { "SizeInMB": 1024 }, "Processor": { "Count": 2 } },
  "Devices": {
    "Scsi": { "primary": { "Attachments": { "0": { "Type": "VirtualDisk", "Path": "C:\\vms\\lin\\root.vhdx" } } } },
    "NetworkAdapters": { "eth0": { "EndpointId": "<guid>", "MacAddress": "00-15-5D-00-11-02" } },
    "ComPorts": { "0": { "NamedPipe": "\\\\.\\pipe\\lin1-com1" } }
  }
}
```

---

## 5. Networking (HCN)

- Create one `HostComputeNetwork` (a Hyper-V switch); give each VM an endpoint on
  it; the VM NIC references the endpoint via `EndpointId`. Both VMs on one network
  = shared L2 segment, and the host is on it too.
- **`L2Bridge`** = single-host shared L2 (what we want). **`Overlay`** = multi-host
  (VXLAN; needs a VSID subnet policy and normally a control plane such as Kubernetes).
- Networking uses the **public `hcn` package**, so this part needs no forked hcsshim.

**Quirk:** wiring an HCN endpoint to a *VM* NIC is far less documented than
container networking. The `EndpointId` field is the mechanism, but expect to tune
IPAM/policy per Windows build and verify the guest actually gets an address.

---

## 6. Guest OS choices & slimming

### Windows — use **Server Core**, not Nano
- **Nano Server cannot boot as a VM.** Since Server 1709 (2018) it is a *container
  base image only*; standalone host support ended Oct 2018. You can't UEFI-boot a
  Nano VHDX.
- **Server Core** is the realistic floor for a bootable, GUI-less, CLI/PowerShell
  Windows VM. Slim it with `DISM /Cleanup-Image /StartComponentCleanup /ResetBase`,
  remove unused features/FoD. It still lands in the multi-GB range — nowhere near Nano.
- **Why the container UVM seems to contradict this:** a Hyper-V-isolated container's
  utility VM boots a *separate, Microsoft-supplied bootable Windows* shipped inside
  the base image as a `UtilityVM\Files\Windows\...` tree (carrying its own kernel
  clone + the GCS), while your container's rootfs is mounted *inside* it. That
  UtilityVM image is welded to the container-host role (runs the GCS, expects the
  hosting protocol) and isn't exposed as a general-purpose bootable VHDX. So a tiny
  bootable Windows exists — you just can't repurpose it as a normal VM.

### Linux — build minimal
- A small distro (debootstrap Debian/Ubuntu, or Alpine) is straightforward to make
  Firecracker-small. Drivers are built into modern/Azure-tuned kernels.

---

## 7. Image preparation

Treat each prepared image as a **read-only golden base**, then give every launched
VM a **differencing (child) VHDX** so writes go to a thin overlay discarded on
teardown. This is the Firecracker "base rootfs + per-VM overlay" pattern.

### Windows (PowerShell on the host)
1. From the Server ISO's `install.wim`, pick a **Server Core** edition index.
2. Lay it onto a UEFI/GPT VHDX:
   - Fast: `Convert-WindowsImage -VHDFormat VHDX -DiskLayout UEFI -Edition ServerStandardCore`.
   - Manual: create+partition VHDX (ESP + MSR + Windows) → `DISM /Apply-Image` → `bcdboot`.
3. **Offline customize** (mount the VHDX):
   - `Add-WindowsCapability -Path W:\ -Name OpenSSH.Server~~~~0.0.1.0`
   - `DISM /Image:W:\ /Cleanup-Image /StartComponentCleanup /ResetBase`
   - drop `unattend.xml` (Panther) / `SetupComplete.cmd`; write `administrators_authorized_keys`
   - enable SAC: `bcdedit /store W:\EFI\Microsoft\Boot\BCD /ems {default} on`
4. `sysprep /generalize /oobe /shutdown` → golden VHDX.
5. Per launch: `New-VHD -Differencing -ParentPath golden.vhdx`.

**Quirk:** a generalized Windows base still runs specialize/OOBE on first boot
(new SID, sshd start). Either let a baked `unattend.xml` do it quickly, or maintain
a pre-specialized base + per-VM identity injection. No fully-free instant clone.

### Linux (build in WSL2 or a Linux box — not on the Windows host)
- **Kernel** — reuse a Hyper-V-tuned distro kernel or build minimal. Built-in (`=y`):
  `CONFIG_HYPERV`, `CONFIG_HYPERV_STORAGE` (→ `/dev/sda`), `CONFIG_HYPERV_NET`,
  `CONFIG_SERIAL_8250(_CONSOLE)` (→ `ttyS0`), `CONFIG_EXT4_FS`, and
  `CONFIG_HYPERV_VSOCKETS` (for hvsocket, see §9). Output `bzImage` works.
- **Rootfs** — debootstrap/Alpine into a dir; enable serial getty on `ttyS0`,
  sshd + key, DHCP/static IP, fstab. Then:
  ```bash
  mkfs.ext4 root.img && sudo mount -o loop root.img /mnt && sudo cp -a rootfs/* /mnt && sudo umount /mnt
  qemu-img convert -f raw root.img -O vhdx -o subformat=dynamic root.vhdx
  ```
- **initrd** is optional when the storage/serial drivers are built in (kernel mounts
  root directly); otherwise a tiny busybox initramfs that `switch_root`s.
- Per launch: differencing VHDX off the rootfs base — clean and instant (no
  specialize step like Windows).

---

## 8. Interacting with a headless guest

A Server Core / generic Linux VM launched via HCS is a **normal VM**, not a UVM —
so HCS in-guest exec (`HcsCreateProcess`) does **not** apply (that needs the GCS
agent). Manage over the network or serial, and bake first-boot config so the VM is
reachable the instant it starts.

| Channel | Windows | Linux | Notes |
|---|---|---|---|
| **SSH** (preferred, ephemeral) | Server 2025: preinstalled, just enable sshd; 2019/2022: `Add-WindowsCapability OpenSSH.Server`. Set PowerShell as default shell; key in `administrators_authorized_keys`. | Standard sshd + key. | Lightweight, scriptable, cross-platform. |
| **WinRM / PSRemoting** | `Enable-PSRemoting`; workgroup needs `TrustedHosts`. | n/a | Native object pipeline; heavier auth/trust. |
| **Serial console** | **SAC/EMS** over the COM-pipe (`bcdedit /ems on`). Not a login shell — `SAC>` prompt; spawn a `cmd` channel. | `console=ttyS0` getty = full login. | Out-of-band bootstrap / break-glass before networking works. |
| **RDP** | Console session (cmd/PS, no desktop). | n/a | Interactive only. |

**First-boot automation** is the glue for disposability: `unattend.xml` /
`SetupComplete.cmd` (Windows) or cloud-init/baked config (Linux) to set
credentials, enable sshd, drop keys, bring up the NIC — or inject offline by
mounting the VHDX before launch.

---

## 9. Lifecycle, pause/resume, snapshots

State changes are first-class HCS calls; **"snapshots" as a managed feature are not.**

| Operation | API | Behaviour |
|---|---|---|
| Graceful stop | `HcsShutDownComputeSystem` | Asks guest OS to shut down. |
| Force stop | `HcsTerminateComputeSystem` | Kills it. (`HcsCrashComputeSystem` forces a dump.) |
| Pause / resume | `HcsPauseComputeSystem` / `HcsResumeComputeSystem` | In-memory suspend (`{"SuspensionLevel":"Suspend"}`); resume takes null options. Not all systems support resume. |
| Save / restore | `HcsSaveComputeSystem` | `{"SaveType":"ToFile","SaveStateFilePath":"...\\save.vmrs"}` → state to disk, survives host reboot. Restore by recreating the system pointed at the saved state. **This is the "snapshot to skip cold boot" primitive.** |

**No `HcsCreateSnapshot`/checkpoint.** The managed checkpoint tree
(revert/apply, differencing chains) lives in the **Hyper-V VMMS/WMI** layer
(`Checkpoint-VM`), a different API. From HCS you compose a "snapshot" yourself =
**saved `.vmrs` + VHDX differencing disk**.

**Fan-out** (restore many copies from one snapshot with copy-on-write memory —
Firecracker's density trick) ≈ hcsshim **template-and-clone**, but that is
exercised for container UVMs. For arbitrary VMs, build it from one saved-state base
+ per-VM differencing disks and verify memory sharing actually engages.

---

## 10. Host ↔ guest communication

Two transports to a plain VM, plus offline injection. GCS-based conveniences
(`HcsCreateProcess` exec, `VSMB` / `Plan9` / `VirtualPMem` shares) need a guest
agent and don't apply to plain VMs.

### Transport A — network over the HCN NIC
Just TCP/IP on the shared segment. SSH/WinRM for commands; `scp`/`sftp`/SMB for files.

### Transport B — Hyper-V sockets (hvsocket) — the Firecracker-vsock analog
Byte-stream over VMBus, **independent of guest networking**. Works for any guest
with the driver (not just UVMs). Run a small agent in the guest; the host connects.

- **Addressing:** host `AF_HYPERV` uses `<VM_ID GUID, Service_ID GUID>`; Linux guest
  `AF_VSOCK` uses `<context_id, port>`.
- **Mapping:** Linux hv_sock maps a vsock port to a service GUID of the form
  `<port>-FACB-11E6-BD58-64006A7986D3`.
- **Setup:** to *listen on the host*, register the service ID under the
  `GuestCommunicationServices` registry key; for HCS VMs, enable the `HvSocket`
  device in the schema (service table + bind/connect security descriptors). Get a
  VM's ID via `hcsdiag.exe list`. Linux guest needs `CONFIG_HYPERV_VSOCKETS`.

### By need
- **Commands:** SSH/WinRM, or an hvsocket agent that execs.
- **Files:** `scp`/`sftp`/SMB; **offline VHDX injection** for seeding inputs into an
  ephemeral VM (mount the differencing child before boot); a secondary data VHDX for
  one-way hand-off (never mount one VHDX rw from both sides); or stream over hvsocket.
- **Ports:**
  - Same segment → connect to guest `IP:port` directly.
  - Expose beyond host → HCN NAT port-mapping policy, or host `netsh interface
    portproxy` / `Add-NetNatStaticMapping`.
  - No-network → relay over hvsocket (host TCP listener proxies to a guest hvsocket
    service — how WSL2 does localhost forwarding).

**Recommended control plane for disposable VMs:** hvsocket + a tiny guest agent
(commands, file streaming, port relays over one VMBus channel, no network
dependency) + offline VHDX injection for seeding + SSH/SMB over the HCN IP as the
pragmatic fallback.

---

## 11. Consolidated quirks & gotchas

- HCS `hcs`/`schema2`/`uvm` are **internal** to hcsshim → vendor/fork or call
  `computecore.dll` directly. `hcn` is public.
- `HcsCreateComputeSystem` is **async** (operation handle + wait), unlike the Go wrapper.
- **Nano Server can't boot as a VM** — Server Core is the floor; it won't be small.
- The container UVM's bootable Windows comes from the **base image's `UtilityVM`
  tree**, not Nano, and isn't repurposable as a general VM.
- Linux direct-boot needs **Hyper-V drivers built into the kernel**; `bzImage` is fine.
- **VM-NIC ↔ HCN-endpoint** binding is under-documented vs containers.
- Windows differencing clones still run **specialize/OOBE**; Linux clones are instant.
- **No managed snapshot/checkpoint in HCS** — build from save-state + differencing disks.
- `HcsCreateProcess` and VSMB/Plan9 shares are **GCS/UVM-only** — not for plain VMs.
- hvsocket requires guest support (`CONFIG_HYPERV_VSOCKETS` on Linux) and, for
  host-listening, **service-ID registration**.

---

## 12. Process & privilege split on Windows (broker vs per-user agent)

The Linux MVP splits responsibility across two long-lived processes in two
privilege domains: a **root broker** (system service) that owns only the
privileged VM primitives, and a **per-user manager** (systemd *user* unit +
socket activation) that owns host/Git/SSH/state work as the interactive user.
Windows keeps the same two-domain split but changes how each tier is hosted,
because Windows service semantics differ.

| Concern | Linux MVP | Windows |
|---|---|---|
| Privileged tier (broker) | systemd **system** service, root + jailer | Windows **service** as `LocalSystem` or virtual account `NT SERVICE\TinyCosmosBroker` — HCS/HCN need admin/SYSTEM |
| VM primitives | Firecracker + jailer via API socket | `computecore.dll` (`HcsCreateComputeSystem`…) + `hcn`; broker is a thin validated layer over HCS |
| Per-user tier (manager) | systemd **user** unit + socket activation | **Per-user logon agent** — *not* a service (see below) |
| Client↔manager / manager↔broker IPC | Unix domain socket, mode `0600` | **Named pipe** with a security descriptor scoped to the owner SID |
| Peer authentication | `SO_PEERCRED` → UID | `GetNamedPipeClientProcessId` (+ open process token) → **SID** |
| Identity namespace | owner UID | owner **SID** |
| Guest out-of-band channel | virtio-vsock | **hvsocket** (host `AF_HYPERV` ↔ guest `AF_VSOCK`), see §10 |
| Guest data plane | OpenSSH | OpenSSH (identical) |
| Per-user state path | `$XDG_DATA_HOME/tiny-cosmos` | `%LOCALAPPDATA%\TinyCosmos` |

### The manager cannot be a Windows service

Windows has no clean per-user daemon equivalent of a systemd user unit:

- **Session 0 isolation** — a Windows service runs in the isolated services
  session, never the interactive user's session. A service statically bound to
  one user account is not the same as "the currently logged-in interactive
  user," which is what we need.
- **No socket activation** for named pipes.
- Windows "per-user services" (the template mechanism behind built-ins like the
  clipboard `*_LUID` services) technically exist but are awkward for a
  third-party app.

So on Windows the manager becomes a **per-user logon agent**: a normal process
launched in the user's session at logon (Task Scheduler at-logon trigger, or a
Run/Startup launcher), running with the user's token, resident while a sandbox
is live and cheap when idle. Functionally it is the same daemon — long-lived
per-user owner of the SSH connection pool, leases, idle timers, and SQLite — but
it is **born as the user** rather than registered as a service. That is arguably
a cleaner fit than the Linux user unit, since the natural way to "be the user"
on Windows is to launch in their session, not drop into it.

### Why this hardens the "don't act-as-user in the privileged tier" rule

Windows has no `fork`+`setuid`. Acting as another user requires token work —
`LogonUser` / `ImpersonateNamedPipeClient` (impersonation) or
`CreateProcessAsUser` (spawn-as-user, needing `SeAssignPrimaryTokenPrivilege` +
`SeIncreaseQuotaPrivilege`). A single privileged service that impersonated per
call would carry the same fail-open hazard as Linux drop-to-UID, plus Windows'
own sharp edges (impersonation-vs-primary token confusion — the token-EoP CVE
family is exactly "kept running as SYSTEM after a failed/forgotten revert").

Because the per-user agent already holds the user's token natively, it gets
correct host-file semantics (NTFS ACLs, the user profile, network drives via the
user's credentials) for free, with no impersonation to get right. So the rule is
unchanged and reinforced: the **broker takes opaque identifiers only**,
authenticates the caller's SID over the named pipe for ownership checks, and
never touches host files or untrusted guest archives. The agent does that work
as the user from the start. Broker-side impersonation is the thing to avoid,
just as drop-to-UID is on Linux.

### Watch items

- Named-pipe peer auth is slightly weaker than `SO_PEERCRED`: prefer
  `GetNamedPipeClientProcessId` then open the process token for the SID. If you
  use `ImpersonateNamedPipeClient`, impersonate only long enough to read the
  SID, then revert — never run logic while impersonating.
- The broker must grant the per-user agent access to its VM's hvsocket service:
  service-ID registration under `GuestCommunicationServices` plus the `HvSocket`
  device's bind/connect security descriptors (§10) — the analog of the Linux
  broker handing the manager connect-only access to the vsock UDS.

## 13. What this becomes

A minimal launcher (Rust/C++/C# over `computecore.dll`, or vendored hcsshim) that:
1. Ensures one HCN `L2Bridge` network + per-VM endpoints.
2. Creates a per-VM **differencing VHDX** off a golden base (Server Core or Linux rootfs).
3. Builds the VirtualMachine JSON (UEFI for Windows, `LinuxKernelDirect` for Linux)
   with the endpoint + a COM pipe + `HvSocket` service.
4. `HcsCreateComputeSystem` → `HcsStartComputeSystem`.
5. Talks to the guest over hvsocket (agent) or SSH; seeds inputs by VHDX injection.
6. On teardown: `HcsTerminateComputeSystem`, delete endpoint + differencing disk.
   Optionally `HcsSaveComputeSystem` a warm base to skip cold boot next time.
