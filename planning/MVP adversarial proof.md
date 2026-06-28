# Tiny Cosmos: MVP Adversarial Proof

## Goal

The MVP must prove that Tiny Cosmos correctly establishes and mediates an
execution environment whose isolation is enforced by Firecracker/KVM, the
Firecracker jailer, and host kernel controls.

Tiny Cosmos is not required to implement those security boundaries. It must
prove that a confused, over-eager, or prompt-injected agent can use arbitrary
guest root capabilities without Tiny Cosmos misconfiguration or access
mediation granting ambient authority over host files, host processes, host
credentials, the manager, the broker, or sibling filesystems and processes
through Tiny Cosmos control surfaces.

This is a proof plan, not a comprehensive production security program. Passing
requires observable tests against packaged artifacts on the supported Ubuntu
24.04 and Firecracker configuration.

## Proof obligations

### 1. VM enforcement configuration and broker boundary

Prove that:

- The selected harness, its harness adapter, and the per-user manager run
  without root.
- Firecracker launches through the pinned upstream jailer.
- Tiny Cosmos uses an approved Firecracker, jailer, kernel, and resource profile
  rather than implementing VM isolation itself.
- The broker constructs configuration from approved identifiers and rejects
  caller-selected executables, arguments, kernels, disks, host paths, devices,
  mounts, network primitives, and Firecracker JSON.
- Firecracker's control socket, boot artifacts, writable disks, and broker
  resources are unavailable for guest or harness modification.
- Two sessions receive separate immutable group and sandbox identifiers, VMs,
  disks, namespaces, and workspaces.
- Manager and broker control sockets are not exposed as guest-network services.

Black-box tests must verify the resulting boundary. They do not prove that
Firecracker, KVM, the jailer, or the host kernel is free of vulnerabilities.
Tiny Cosmos passes when it selects, configures, launches, and restricts access
to those components as specified.

The MVP uses bounded CPU, RAM, and disk resource profiles. It does not prove
comprehensive denial-of-service resistance.

### 2. No ambient host state in the guest

Prove that ordinary guest commands cannot observe:

- Host environment variables
- Host home-directory configuration
- SSH agents or host sockets
- Git credential helpers or credential files
- Harness, manager, or broker credentials
- The host project except for explicitly seeded content

Guest commands start from a guest-owned environment baseline. A value
explicitly supplied through a trusted harness operation may be used in the
guest; once disclosed, preventing its use is outside Tiny Cosmos's scope.

### 3. Pessimistic guest protocol

The manager must close the guest channel and propagate an error for:

- Invalid or oversized frame lengths
- Invalid protocol versions or negotiation
- Unknown request identifiers
- Unsolicited responses or events
- Duplicate terminal responses
- Invalid state transitions or sequencing
- Late data after cancellation or completion
- Truncated or malformed binary streams

Failure never activates a permissive parser, guessed state, alternate protocol,
or host-local execution fallback. Repeated retries may repeatedly fail when the
guest supervisor is broken or replaced.

The boot nonce detects accidental stale or wrong-VM connections. It is not a
security token against guest root.

### 4. Destination-confined export

An explicit export may create files only inside its newly created destination.
Prove that the exporter:

- Resolves paths relative to an already-open destination without following
  intermediate symlinks or magic links.
- Allows only directories, regular files, and explicitly validated symlinks.
- Rejects traversal, absolute paths or links, hardlinks, devices, sockets,
  FIFOs, duplicate normalized paths, unsupported names, and guest-provided
  `.git` administrative entries.
- Strips ownership, setuid/setgid, capabilities, ACLs, and xattrs.
- Preserves only file data, directories, validated links, and the ordinary
  executable bit.
- Enforces per-file and total byte, file-count, depth, path-length, and time
  limits.
- Builds in a temporary location and publishes only after validation.

Guest-generated archives and manifests remain untrusted because guest root can
replace the supervisor.

### 5. Git handoff uses a safer trust transition

Prove that:

- Handoff occurs only after an explicit trusted user action.
- `git worktree add --no-checkout` is used, preventing automatic
  `post-checkout` execution during worktree creation.
- Working-tree files are materialized through the destination-confined export
  path.
- Handoff never updates, resets, or merges into an existing worktree.
- Tiny Cosmos does not automatically open, build, or run the result.
- The result is presented as untrusted host-local content.

Tiny Cosmos does not claim that guest Git objects or code the user later chooses
to execute are safe.

### 6. Corruption retires and replaces the sandbox

Prove that an unbootable or invalid sandbox:

- Is marked failed.
- Is not mounted or repaired by a host process.
- Does not cause the broker to run `fsck` against guest-controlled disks.
- Retains inert disks until explicit cleanup.
- Can be replaced by a new primary sandbox identifier inside the same immutable
  group.
- Remains linked to the group as retired history and cannot start.
- Uses the current host workspace metadata as a fresh seed and reports that
  guest-only changes were not recovered.

Recovery of data from a corrupted sandbox is not an MVP guarantee.

### 7. Safe host rendering

Prove that structured logs and non-interactive tool results escape dangerous
control characters, including ANSI and OSC sequences.

Test invalid UTF-8, clipboard attempts, terminal-title changes, bidirectional
controls, newline-bearing filenames, and misleading links. Raw terminal control
behavior is allowed only in an explicitly interactive PTY session.

### 8. Group and sandbox routing never guesses

Prove that:

- Manager-assigned group and sandbox identifiers never change.
- Logical group names use exact case-sensitive lookup within one UID namespace.
- Explicit creation rejects duplicate names instead of aliasing.
- Concurrent first guest operations by logical name create exactly one group
  and current primary sandbox.
- Group-level guest operations resolve exactly the current primary member.
- Direct sandbox-ID operations target exactly that member.
- No operation guesses, promotes, or substitutes a sibling, stale, retired, or
  failed sandbox.
- Host workspace paths and other mutable metadata do not participate in identity
  or routing.

Renaming a logical group or changing host workspace metadata must leave group
and sandbox identifiers, disks, networking, active commands, leases, and IDE
relays unchanged. The old name is released immediately without an alias.

### 9. Authorization is independent of operation IDs

Prove that:

- The caller is authenticated through the defined OS identity mechanism.
- Every operation is authorized against the target resource and owner.
- Operation, session, correlation, and log identifiers grant no authority.
- Idempotency is applied only after authorization and only for retry behavior.
- Reconciliation relies on broker-owned resource records, not guest claims.

### 10. Selected-harness containment status is honest

For supported versions of the selected harness:

- Override every intended guest-local tool and interactive shell path.
- Use the block-first profile for project-provided executable resources unless
  the harness selection gate records a narrower exception.
- Audit host process creation and filesystem writes covered by the integration.
- Do not deliberately fall back to host-local execution when the manager is
  unavailable.

An unsupported harness version may continue only with a clear degraded or
unverified warning. If any intended replacement tool or interactive shell path
cannot function through the manager, the integration disables itself. Unknown
future harness-native tools remain governed by the trusted harness and are not
presented as Tiny Cosmos-intercepted.

### 11. Start and stop behavior is explicit

Prove that:

- Guest-dependent work lazily provisions a missing logical group and starts its
  current primary sandbox.
- Compatible operations coalesce around one provisioning or startup operation.
- Contradictory start and stop requests return `LifecycleConflict`.
- Explicit start of a ready sandbox returns `AlreadyRunning` or `NotModified`.
- Client timeout discards the pending command so it never executes later, but
  does not cancel manager startup.
- Startup emits structured progress and succeeds only after supervisor
  readiness.
- The five-minute default hard startup limit force-terminates the VM, records a
  transient error, and restores `Stopped`.
- Nonrecoverable corruption or ownership failure enters `Failed`; transient
  failures remain retryable.

### 12. VS Code is an explicit transition

Prove that **Open in VS Code**:

- Requires the trusted user/harness path.
- Uses a sandbox-scoped SSH key, host alias, known-host entry, relay, and lease.
- Does not disable host-key checking globally or edit unrelated SSH
  configuration.

After connection, ordinary VS Code Remote, extension, workspace, credential,
and user behavior is outside the containment guarantee.

## Acceptance tests

1. Run two sessions from one project and attempt filesystem, process, manager,
   broker, and disk access across the externally enforced VM boundary;
   verify the generated Firecracker, jailer, cgroup, storage, and access
   configuration matches the approved profile.
2. Place canary secrets in host environment, home configuration, credential
   helpers, agents, and sockets; verify they are absent in ordinary guest
   commands.
3. Replace the supervisor with a protocol fuzzer and verify malformed or
   ambiguous communication closes the channel and propagates an error.
4. Export traversal paths, links, special files, privileged metadata, duplicate
   names, and expansion bombs; verify nothing escapes the destination.
5. Verify `git worktree add --no-checkout` does not execute `post-checkout`.
6. Corrupt a guest disk; verify no host mount or repair occurs, replacement
   creates a new primary sandbox in the same group, and the failed member is
   retired.
7. Emit terminal control sequences and malformed text; verify structured output
   is safe and explicit PTY behavior remains interactive.
8. Rename a live logical group, update host workspace metadata, and verify all
   immutable resources and active operations remain unchanged. Exercise
   duplicate names, concurrent first operations, retired members, and exact
   ID/name routing without guessed targets.
9. Replay operation IDs across users and resources; verify each target is
   independently authorized.
10. Exercise coalesced startup, conflicting stop/start, client timeout, hard
    startup timeout, and transient versus nonrecoverable failure handling.
11. Run an unsupported selected-harness compatibility profile and verify a
    clear degraded warning. Break one intended replacement and verify the
    integration disables itself.

## MVP exclusions

The MVP does not prove:

- Absence of vulnerabilities in Firecracker, KVM, the jailer, the host kernel,
  or other external enforcement components
- Protection from harness-global MCP, memory, secrets, or remote side effects
- Prevention of guest-to-internet exfiltration or abuse
- Comprehensive host denial-of-service resistance
- Any MVP network isolation from reachable host, LAN, VPN, metadata, or sibling
  services
- Safety of exported code the user later builds or runs
- Safety of normal VS Code Remote or extension behavior
- Recovery of data from a corrupted guest filesystem
- Revocation or migration of pinned retained-sandbox artifacts
