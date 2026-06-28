# Tiny Cosmos: Production Security Orchestration Requirements

## Goal

A full production version retains every control-plane invariant proven by the
MVP and orchestrates dedicated enforcement components for deployment-scale
networking, host resource control, artifact maintenance, and stronger
containment around the VMM.

These requirements do not expand Tiny Cosmos into a harness prompt-injection
defense system. The harness remains trusted and responsible for its own model,
credential, memory, MCP, and remote-service policy.

## Responsibility model

Tiny Cosmos is the policy and lifecycle control plane. It is responsible for:

- Selecting approved enforcement implementations and versions.
- Translating product policy into concrete component configuration.
- Supplying only validated configuration and resource identifiers.
- Starting components with the intended ownership and privileges.
- Mediating access to their control surfaces.
- Detecting missing, failed, unsupported, or degraded enforcement.
- Verifying effective behavior with integration and acceptance tests.
- Reporting the active policy and known limitations honestly.

Dedicated external components enforce the policy:

- Firecracker/KVM enforce the VM boundary.
- The Firecracker jailer and host kernel primitives constrain the VMM process.
- Cgroups and storage facilities enforce resource limits.
- A future firewall, packet filter, DNS component, and egress proxy enforce
  network policy.
- SSH and VS Code Remote implement the IDE transport after an explicit trust
  transition.

Tiny Cosmos owns a security defect when it selects the wrong component,
generates unsafe configuration, exposes a bypass, routes access incorrectly,
silently loses enforcement, or misrepresents the active policy. It does not own
the implementation of, or claim immunity from vulnerabilities in, the external
enforcement components.

## Required production orchestration

### 1. Internet egress policy

Production deployment requires Tiny Cosmos to integrate a dedicated egress
enforcement solution rather than treating unqualified guest NAT as a security
feature.

The selected firewall or proxy should provide some combination of:

- Per-group network modes
- Destination allow or deny policy
- A host-side egress proxy
- Connection and byte accounting
- Rate and connection limits
- Offline operation
- A network kill switch

Tiny Cosmos must configure the selected component, bind the correct immutable
group and sandbox identities to it, prevent caller-supplied bypass
configuration, detect when it is unavailable, and state what it does and does
not enforce. Mutable logical group names and host workspace metadata must not
retarget network policy. Guest-to-internet data loss remains possible whenever
unrestricted egress is selected.

### 2. Harden the DNS and network data path

Integrate one production DNS model:

- Direct queries to approved external resolvers
- A minimal, pinned, unprivileged, isolated forwarder
- A DNS path integrated with the policy proxy

The selected implementation is responsible for processing and enforcing DNS
traffic. Tiny Cosmos must launch or address it with the approved privileges,
configuration, connectivity, limits, and update policy, without exposing
manager or broker access.

Configure the selected firewall or packet filter for best-effort blocking of
host, LAN, metadata, VPN, and sibling-sandbox destinations:

- Block common local-use and host destinations before NAT.
- Keep IPv6 disabled until equivalent policy and tests exist.
- Account for current host addresses and common route changes where practical.
- Document VPN, unusual-route, host-public-address, and NAT limitations.

The firewall or packet filter performs enforcement. Tiny Cosmos verifies the
installed policy and reports that network filtering remains best effort rather
than a formally proven isolation boundary.

### 3. Advanced Firecracker and host confinement

Configure production-strength jailer and host-kernel confinement around each
Firecracker process:

- Unique per-VM host UID/GID where operationally practical
- Minimal jail contents and root-owned non-writable inputs
- Restrictive seccomp and capability removal
- `no_new_privs`
- Explicit MMDS policy
- Strong cgroup v2 CPU, memory, PID, I/O, and FD controls
- Host-kernel, KVM, microcode, Firecracker, and jailer support policy
- Cross-VM containment and cleanup auditing

The jailer and host kernel enforce these controls. Tiny Cosmos selects the
profile, generates and applies its configuration, mediates access, and verifies
the resulting process state. These controls reduce blast radius after a VMM
escape; Tiny Cosmos does not claim to eliminate inherited host-kernel, KVM,
Firecracker, or jailer vulnerabilities.

### 4. Aggregate resource and abuse protection

Configure dedicated kernel, storage, and network controls beyond MVP VM
profiles:

- Per-user and system-wide concurrent VM limits
- Running and retained logical/allocated disk quotas
- Host memory and disk reserve thresholds
- PID and FD limits
- IOPS and bandwidth controls
- Network connection and rate limits
- Log, serial-output, temporary-transfer, and expanded-export limits
- Concurrent operation limits
- Garbage collection and cleanup pressure handling

Tiny Cosmos owns quota accounting, profile selection, and lifecycle decisions.
The kernel, storage, and network components enforce the configured limits. The
manager must refuse or stop work before consuming reserves needed to keep the
host and cleanup path operational. Production cleanup policy may become more
automated, but it must retain explicit ownership, auditability, and protection
for running or leased sandboxes.

### 5. Continuous hostile-input testing

Retain the MVP control-plane and handoff invariants and continuously test both
configuration and effective enforcement:

- Fuzz guest protocol framing, state transitions, cancellation, and streams.
- Fuzz archive, path, manifest, and Git handoff inputs.
- Test terminal and structured-output sanitization.
- Test immutable group and sandbox routing, logical-name mutation, concurrent
  lazy provisioning, startup coalescing, and operation authorization during
  crashes and retries.
- Test against every supported harness version.
- Compare generated component configuration with approved policy profiles.
- Detect component absence, startup failure, policy load failure, and silent
  downgrade.

Unsupported selected-harness versions may still warn and continue, but
production telemetry and diagnostics must identify degraded or unverified
sessions. Failure of an intended Tiny Cosmos replacement tool or interactive
shell path disables the integration. Unknown future harness-native tools remain
under the trusted harness's policy rather than a Tiny Cosmos interception claim.

### 6. Artifact maintenance

Sign and authenticate all boot, VMM, jailer, firewall, proxy, and related
enforcement artifacts; preserve exact provenance; and support secure package
updates.

The current product decision is to start retained sandboxes with their pinned
versions, even if those versions later become undesirable. Production does not
enforce revocation or migration unless this policy is explicitly changed.
Document the inherited risk and ensure package rollback cannot silently alter
the recorded sandbox provenance.

## Retained trust transitions

Production does not turn the following into containment guarantees:

- Exported code is untrusted after the user materializes it on the host.
- VS Code Remote and extension behavior is outside Tiny Cosmos containment after
  the user opens the guest.
- Values explicitly disclosed by the trusted harness may be used by the guest.
- Harness-global model, memory, MCP, credential, and remote-service behavior
  remains the harness's responsibility.

Production must preserve destination-confined export, scoped VS Code connection
metadata, no ambient host-state sharing, and honest user-visible boundary
status.

## Production acceptance

A production release must:

1. Pass every MVP adversarial proof against packaged artifacts.
2. Demonstrate that the selected egress, firewall, and DNS components receive
   the intended policy and enforce it under malformed and excessive traffic.
3. Exercise host/LAN filtering with VPNs, route changes, multiple interfaces,
   and disabled/enabled IPv6 configurations.
4. Verify the generated jail ownership, privileges, seccomp, cgroups, MMDS
   policy, and cross-VM cleanup configuration for concurrent VMs.
5. Exhaust CPU, RAM, PIDs, FDs, I/O, network, logs, transfers, and retained
   storage without consuming protected host reserves.
6. Recover manager, broker, Firecracker, DNS/proxy, and host restarts without
   losing immutable group or sandbox ownership, rebinding mutable names or
   metadata to different resources, or applying operations to the wrong
   sandbox.
7. Report unsupported harness compatibility and reduced network isolation
   without presenting either as fully protected.
8. Stop, fail, or visibly degrade according to policy when an enforcement
   component is missing, rejects configuration, crashes, or cannot be verified.
