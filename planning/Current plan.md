# Tiny Cosmos: Current Plan

## Status

This document summarizes the current product and architecture direction. The
project is still in the ideation phase; implementation details and some platform
choices remain open.

[MVP technical plan.md](MVP%20technical%20plan.md) is authoritative for the
Linux MVP whenever this broader product-direction document leaves a choice open
or describes a later capability.

## Product idea

Tiny Cosmos provides simple, disposable local VM sandboxes for coding agents and
similar automation.

The agent harness runs outside the sandbox. Each thread or session receives an
independent sandbox in which the agent has full control, including root or
administrator access. The user can allow unrestricted local execution with
confidence that the guest has no ambient access to the host workspace or
another thread's filesystem or processes through Tiny Cosmos control surfaces.
MVP networking is not part of that isolation claim.

Tiny Cosmos is a sandbox manager and harness integration layer, not a new agent
harness. The first integration target will be either Pi or OpenCode; the choice
is intentionally deferred. The MVP will implement and ship exactly one of those
integration tracks, not both. Agents are the primary consumers of sandboxes;
Tiny Cosmos is not intended to become a general-purpose user
development-environment manager.

## Product principles

### Experience priority

When product, integration, and implementation concerns compete, use this order:

1. **User experience.** After any mandatory one-time, root-elevated host setup,
   supported projects should work out of the box with minimal friction. Tiny
   Cosmos should select secure defaults, establish and verify the intended
   isolation configuration, fail closed when it cannot do so, and explain any
   material limitation clearly. Ordinary users should not need to inspect
   hypervisor, manager, harness, or tool configuration or run their own
   isolation tests before trusting the supported workflow. This confidence is
   bounded by the stated threat model: Tiny Cosmos is designed for confused,
   over-eager, or prompt-injected agents and ordinary development workloads. It
   does not claim to make running outright malicious executables safe or to
   prevent exploitation of the hypervisor, host kernel, or other enforcement
   components. Exotic or unsupported setups may require explicit intervention,
   but the product must identify that condition rather than silently weakening
   the supported defaults.
2. **Basic agent experience.** Existing shell and file operations should
   transparently target the current sandbox. An agent should be able to request
   an ordinary operation such as "execute this Bash command" without learning
   Tiny Cosmos lifecycle, VM, group, transport, or workspace-transfer concepts.
   The adapter and manager resolve or create the binding, start the sandbox,
   select the guest working directory, stream results, and handle cancellation.
   They must never silently fall back to host-local execution.
3. **Advanced agent experience.** Explicit inspection, sandbox recreation,
   additional VMs, resource changes, and other management workflows are useful
   but secondary. They may be delivered through agent-facing tools and shipped
   skills, but their exact interface is deferred until after the MVP unless a
   capability is required to prove the core architecture.
4. **User-driven debugging.** The CLI, direct inspection, and manual tooling
   configuration are important recovery and diagnostic paths, not substitutes
   for reliable defaults. The normal workflow must not depend on users
   understanding or repairing Tiny Cosmos internals.

Lower-priority surfaces must not add setup, concepts, prompts, or failure modes
to a higher-priority workflow merely to make the lower-priority case more
flexible.

### User simplicity

The default user experience should be opinionated and require little or no
sandbox knowledge. A user should be able to open a project and start or return
to an agent thread without choosing VM images, configuring networks, managing
mounts, or understanding the sandbox lifecycle.

The system should infer or select sensible defaults for the guest OS, image,
workspace location, resources, networking, idle stop, and cleanup. Advanced
controls may exist for unusual projects, but should not become routine setup.

Restrictions should be enforced by architecture rather than repeatedly surfaced
as user-facing friction. Normal agent development operations inside the guest
should work without the user managing a development environment.

### Agent-oriented management

The basic agent-facing surface should preserve the harness's ordinary shell and
file workflow while transparently relocating execution into the guest. Sandbox
management concepts should appear only when the requested operation inherently
needs them.

The agent may later receive a more capable and explicit management surface than
the user. This can include tools for inspecting its environment, recreating a
sandbox, creating child sandboxes, publishing ports, exporting artifacts, or
requesting additional resources. Capable models may use this richer surface
when needed, provided every operation remains narrowly scoped and policy
checked. The MVP must not depend on this advanced surface for ordinary shell,
file, build, or test work.

## Core goals

- Keep the agent harness, its credentials, memory, and harness-global MCP
  integrations outside the sandbox. A future sandbox-local MCP server may run
  inside the guest, but it must not inherit host credentials or authority.
- Give the agent unrestricted local execution inside the guest, including
  `sudo`/administrator access, package installation, builds, debuggers, LSPs,
  and Docker where the platform permits it.
- Use full but lightweight VMs rather than containers as the isolation boundary.
- Create an isolated sandbox per thread or session, with no guest-plane
  cross-session filesystem or process contamination. MVP networking is ordinary
  NAT and is not an isolation boundary. Same-user management clients remain
  mutually trusted.
- Make sandbox creation, selection, starting, stopping, and cleanup automatic
  during ordinary use.
- Run the harness and its adapter as the interactive, non-root host user.
- Provide a first-class action to open the current sandbox workspace using VS
  Code Remote when the user needs to inspect or interject in the agent's work.
  Other IDE integrations may follow.
- For Git repositories, provide a harness command that materializes the current
  sandbox repository state into a new host-local Git worktree for handoff from
  agent-driven to manual development.
- Support Linux and Windows hosts in the first broad product wave. macOS is not
  currently planned.
- Support Linux guests and make a documented best-effort attempt at Windows
  guests for Windows-native software development.
- Post-MVP, allow a sandbox to create a limited group of additional sandboxes
  for uses such as multi-node systems or Kubernetes testing.
- Make offline operation possible after images and other dependencies are
  available locally, where practical. This is not an initial hard requirement.
- Optimize for low overhead rather than GPU, device passthrough, or other exotic
  hardware support.

## Trust boundaries

### Harness control plane

The harness remains a trusted host process. It controls:

- Model access and conversation state
- Harness-global MCP tools and remote service integrations
- Memory systems and cross-thread information
- User permission prompts
- Credentials and other harness-owned capabilities

These capabilities keep their normal harness access controls even when local
execution is in "YOLO" mode.

### Sandbox execution plane

The guest VM is untrusted and disposable. It controls:

- Shells and CLIs
- Project files inside the guest
- Builds, tests, package managers, and language toolchains
- LSPs, debuggers, project-local MCP servers, and local services
- Docker and other guest-local infrastructure

The agent may damage or completely destroy its guest without affecting the
harness or host.

### Sandbox manager

The sandbox manager is a trusted broker between authorized clients and the
hypervisor. It owns lifecycle, images, storage, resource limits, guest
communication, and eventually network policy.

Root inside a guest must not grant authority over the manager, host, harness, or
another sandbox.

Harness adapters are expected to be the primary clients of the management API,
but the API is not a harness API. The `tinycosmos` CLI and other authorized
clients can call the same lifecycle, execution, file, transfer, export, and
intervention operations directly.

The CLI is a general management interface, not a standalone adapter and not an
owner of a separate class of sandboxes. It can list, inspect, debug, execute in,
export, stop, or delete any sandbox group owned by the invoking operating-system
user. Operations may address a group by its immutable Tiny Cosmos group
identifier or its current logical group name, and may address a sandbox member
by its immutable sandbox identifier.

The management API has no concept of a harness session, thread, harness kind, or
harness version. Every sandbox group receives an immutable manager-assigned
group identifier and a client-supplied logical group name. Logical names are
unique within the authenticated operating-system user's manager namespace and
use the form `namespace:opaque`. The encoded name is at most 512 UTF-8 bytes and
is compared using exact case-sensitive equality. The manager validates this
shape and uniqueness but does not interpret the namespace or opaque portion.

Harness adapters conventionally use names such as `pi:<session-id>` and
`opencode:<session-id>`, with the session identifier percent-encoded as needed.
The integration is responsible for choosing collision-free names. Direct
clients follow the same rule. A name is lookup metadata, not a credential or
resource identity.

Logical group names and other group metadata, including the current host
workspace path and an optional harness archive timestamp, may change while the
group is running. A rename atomically replaces the name index, releases the old
name immediately, and creates no alias. It does not change the group identifier,
sandbox identifiers, disks, networking, leases, active commands, or IDE
connections.

Each sandbox member receives its own immutable manager-assigned sandbox
identifier. The group owns its frozen network allocation, and each sandbox
member receives a frozen address within that allocation. The MVP maintains
exactly one current primary sandbox per live group, while retaining separate
group and sandbox identities for later multi-VM groups.

All clients running as the same operating-system user are assumed to cooperate.
They share one management namespace and may inspect, operate, stop, export, or
delete any group or sandbox owned by that user. Tiny Cosmos does not attempt to
protect one same-user harness, adapter, CLI invocation, or direct API client
from another.

The adapter-to-manager flow separates group acquisition from work on the group.
When establishing or recovering a session binding, the adapter calls
get-or-create group with the logical name and any metadata required to create
the group. The manager atomically returns the existing group or provisions the
missing group, frozen network allocation, and primary sandbox, then returns its
immutable group identifier. The adapter retains that identifier for subsequent
operations, so creation metadata does not have to be repeated in every operation
request. Concurrent get-or-create requests for the same name converge on the
same group.

An explicit create-group operation carries the same creation metadata, always
attempts creation, and rejects a duplicate logical name. All lifecycle, guest,
transfer, export, and intervention operations require an existing group or
sandbox target and never create one. Their payloads contain only the target and
operation-specific inputs, not group-creation metadata. They return a structured
not-found error for an unknown target. An operation may still start an existing
stopped sandbox when guest access is required. The manager serializes group
acquisition and startup, coalesces compatible concurrent requests, and returns a
structured conflict for contradictory lifecycle requests.

## Tool classes

Tools should be treated according to the boundary they cross:

| Class | Examples | Enforcement |
|---|---|---|
| Guest-local | Shell, files, Git, builds, LSPs, project-local MCP servers | Unrestricted inside the VM |
| Boundary-crossing | Import files, export changes, publish ports | Manager and invoking-client policy |
| Harness-global | Credentialed MCP, memory, secrets, remote services | Existing harness permissions |
| Infrastructure | Spawn VM, resize, checkpoint, networking | Narrow manager capabilities and quotas |

The selected harness integration should redirect guest-local operations into
the VM without moving the harness process itself into the VM. Memory and
harness-global integrations remain in the harness.

### MVP harness integration choice

The shared manager protocol, sandbox lifecycle, workspace transfer, IDE
intervention, and Git handoff behavior must not depend on which harness is
selected. The adapter translates one harness's session identity, tool schemas,
lifecycle events, and project-resource controls to those shared operations. It
also derives the logical group name supplied to the management API; the manager
does not know that the name represents a harness session.

The two candidate tracks are mutually exclusive MVP alternatives:

- **Pi track:** an explicitly loaded TypeScript extension replaces Pi's
  guest-local built-ins through its exported operation interfaces, handles
  interactive `user_bash`, names groups from Pi-owned session identifiers using
  a `pi:` namespace, and launches Pi with project-controlled
  resource discovery disabled. Pi core has no built-in MCP client, so
  sandbox-local MCP remains extension-owned and post-MVP.
- **OpenCode track:** a TypeScript plugin replaces OpenCode's guest-local
  built-ins, names groups from OpenCode-owned session identifiers using an
  `opencode:` namespace, sets
  `OPENCODE_DISABLE_PROJECT_CONFIG=true`, and disables native LSP, formatters,
  and other unredirected local execution.

The choice must be made before productizing the first harness vertical slice.
Selection criteria include replacement fidelity, complete suppression of
host-local execution, session lifecycle behavior, upgrade compatibility,
distribution complexity, and preservation of the harness's normal user
experience. See
[Harness integration comparison.md](Harness%20integration%20comparison.md) for
the evidence and track-specific adapter components.

### OpenCode-specific LSP and MCP scope

If the OpenCode track is selected, OpenCode v1.17.4 scopes its built-in LSP and
MCP clients by workspace directory, not by session. Tiny Cosmos scopes sandboxes
by session. Two sessions from one host project therefore cannot safely use
directly injected native OpenCode LSP or MCP clients for different guests.

OpenCode's built-in LSP client also reads host files and sends host paths even
when its language-server command is replaced by a stdio proxy. Merely launching
the language-server process in the guest is not sufficient.

The preferred future LSP integration is a session-aware plugin tool that
preserves OpenCode's model-facing `lsp` operations while the Tiny Cosmos manager
and guest perform LSP communication against guest-native files and paths.
Replacement file tools can return guest diagnostics after edits.

Harness-global MCP servers remain ordinary OpenCode MCP integrations on the
host. OpenCode rebuilds its model-visible tool map before every agent step, so a
sandbox-changing tool can wait for guest MCP discovery and make new tools
available to the immediately following inference. No tool change is required
during an inference already in progress.

Sandbox-local MCP still requires a session-aware adapter because OpenCode's
native MCP clients are directory-scoped. The preferred design is a small
session-aware dynamic-tool hook in OpenCode: the Tiny Cosmos plugin acts as the
MCP client and returns typed tools for only the current session on each tool
resolution. A no-patch fallback can export a namespaced union catalog through a
manager-owned MCP proxy, hide other sandboxes with default-deny session
permissions, and enforce ownership again at execution time. A generic plugin
tool remains the simplest compatibility fallback.

See [OpenCode sandbox LSP and MCP feasibility.md](OpenCode%20sandbox%20LSP%20and%20MCP%20feasibility.md)
for the source-level analysis and alternatives.

## Workspace model

The host workspace can seed a sandbox, but it is not mounted, mirrored, or
continuously synchronized. Project templates and agent-assisted image
preparation are post-MVP capabilities.

After creation, the host and guest copies are independent and may diverge.
Information crosses the boundary only through explicit operations such as:

1. Seed or import content into a sandbox.
2. Work independently inside the guest.
3. Inspect changes relative to the seed or another known revision.
4. Export a patch, commit, archive, selected files, or build artifacts.
5. Apply selected changes to the host as the invoking user.

Git should be a useful transport, not a requirement. Non-Git directories must
remain first-class inputs.

### Path independence

Host and guest project roots may differ. A conventional default might be:

- Linux guest: `/workspace/<project>`
- Windows guest: `C:\workspace\<project>`

Matching the host path may be offered as a compatibility option, but it is not
an invariant.

Workspace metadata should distinguish:

- Workspace identity
- Optional host seed location
- Guest root and guest OS
- Seed revision or import generation
- Optional export destination

Diagnostics, debugger locations, and artifacts may require boundary path
translation. Translation should not imply that host and guest share storage.

### Git worktree handoff

For Git repositories, the harness should provide a first-class command that
exports the current sandbox repository state into a new host-local Git worktree.
This is the primary transition from agent-driven work to sustained manual
development.

The command should:

1. Identify the sandbox repository and its relationship to the host repository.
2. Capture the current guest repository state, including work not yet committed.
3. Create a new host-local branch and worktree using a predictable,
   collision-safe name and location.
4. Materialize the captured state in that worktree using the invoking user's
   filesystem permissions.
5. Return or open the resulting host path.

This is a point-in-time export, not the start of synchronization. The original
sandbox and exported worktree may diverge immediately afterward. Repeating the
operation should create a new handoff or require an explicit update mode rather
than silently merging two independently modified trees.

The reverse direction requires no special synchronization mechanism. Opening a
new harness session from the host worktree follows the normal flow: the worktree
seeds a new, independent sandbox for that session.

The exact representation of dirty state remains open. The handoff must preserve
tracked modifications and untracked files that belong to the working tree.
Ignored files, nested repositories, submodules, Git LFS content, and repositories
whose original host source is unavailable require explicit policy.

## Non-root host operation

The harness and integration adapter must run without root or administrator
privileges.

Some hypervisor and networking operations may require a root/system daemon. If
so, that service must expose narrow domain operations such as creating an
approved sandbox, not general privileged host primitives.

Membership or access to the manager must not become equivalent to
"Docker group equals root." In particular, callers must not be able to request:

- Arbitrary host bind mounts or filesystem paths
- Arbitrary disks, kernels, images, or devices
- Custom hypervisor arguments
- Arbitrary TAP interfaces or network configuration
- Host command execution or lifecycle hooks
- Access to another user's sandboxes

Images, networks, and resource profiles should be manager-approved objects
referenced through opaque identifiers.

### Host file access

The per-user manager reads explicitly selected host seed paths and writes
explicitly selected export destinations using the interactive user's ordinary
OS permissions. It performs Git worktree handoff directly. Host paths are
mutable group metadata and never identify or authorize a group.

The manager confines exports to a newly created or explicitly selected
destination and treats all guest-provided paths, archives, manifests, and Git
objects as untrusted.

If a privileged daemon must access a host path, it must authenticate the caller
using the OS identity, validate access as that user, avoid unsafe symlink and
path-replacement races, and reject special or manager-owned paths.

Every group, sandbox, and operation is associated with an authenticated OS user.
Commands, transfers, exports, IDE connections, and other active work use
operation-scoped leases.

## Guest communication

A small guest supervisor provides only the bootstrap and out-of-band control
plane needed to establish and manage a sandbox:

- Boot identity, version, readiness, and lifecycle reporting
- Initial installation and later rotation or revocation of sandbox-scoped SSH
  credentials
- Reporting the SSH endpoint and host-key identity through the out-of-band
  channel
- Graceful filesystem flush and shutdown

The supervisor does not implement ordinary command execution, terminal
streaming, file operations, archive transfer, or port relaying. Those operations
should use mature guest-native facilities, with SSH as the default data plane
for Linux guests.

The manager may keep one or more persistent, multiplexed SSH connections to a
sandbox. A harness or CLI connection can carry multiple concurrent command,
PTY, SFTP, and transfer channels; VS Code Remote may establish a separate
persistent SSH connection. Command lifetime is independent of connection
lifetime, and Tiny Cosmos must not assume one SSH connection per command.

Cancellation is a best-effort guest operation. The manager starts a command in
its own process group when practical and uses another SSH channel to signal that
group. It does not treat SSH disconnect as cancellation or claim that in-guest
process scoping constrains guest root. If guest control becomes unreliable, the
manager's reliable authority is to stop the VM through the hypervisor; the
sandbox can then be restarted with its retained disks or explicitly recreated.

The preferred out-of-band supervisor transport is `vsock` on Linux hypervisors
and Hyper-V sockets on Windows. It is deliberately separate from the SSH data
plane.

The guest supervisor is not itself a security boundary. Guest root may kill or
replace it; the host manager must retain out-of-band authority to terminate or
reset the VM.

The common execution API should not assume Unix semantics. Windows and Linux may
differ in paths, shells, signals, PTYs, process trees, environment variables, and
file locking.

## Sandbox groups

A harness session maps to one sandbox group. The group has an immutable
manager-assigned identifier and a mutable integration-owned logical name. It
contains:

- One primary development VM
- Post-MVP, optional child VMs for databases, clusters, cross-platform tests, or
  similar workloads
- A network allocation scoped to that group

The agent may receive restricted post-MVP tools to create and manage children,
but it must not receive the manager socket or arbitrary VM configuration.

Child capabilities may inherit or reduce parent policy, never broaden it.
Enforce limits on VM count, spawn depth, CPU, memory, disk, lifetime, and network
exposure. Children are normally destroyed with their parent.

Execution state belongs to sandbox members, not to the group. A future
non-primary member can be started directly by sandbox identifier. Group-level
convenience operations resolve the current primary member and never guess or
promote a different member.

Deleting a harness session deletes its sandbox group and resources.
Archiving a harness session is non-destructive. Where a harness exposes a simple
archive hook, the adapter may store an `archivedAt` metadata value for display or
future cleanup prioritization without changing lifecycle behavior.

Forking a harness session creates a new group identifier, logical name, primary
sandbox identifier, and network allocation. The parent group identifier is
provenance only. A full-state fork briefly stops the parent, sparse-copies both
persistent disks, starts the parent again, and starts the independent child.

A nonrecoverable primary failure is replaced within the same group using a new
primary sandbox identifier. The failed member remains linked as `Retired`
history and cannot start. Tiny Cosmos does not mount or repair failed disks on
the host. The replacement seeds from the current host workspace metadata and
reports that guest-only changes were not recovered.

## Lifecycle and scale

The number of retained sessions will grow over time, so an inactive session
must not imply a permanently running VM.

The adapter and manager should automatically:

1. Resolve or create the session's group through the dedicated get-or-create
   request before issuing the first operation. That request carries any required
   creation metadata, and the adapter retains the returned immutable group
   identifier for subsequent operations.
2. Start an existing stopped sandbox whenever a command, file operation,
   transfer, export capture, guest transport request, or IDE action requires it.
   Operations never create a missing group.
3. Detect inactivity using leases and observable use rather than agent
   cooperation alone.
4. Stop inactive VMs while preserving their writable disks and immutable
   identities.
5. Retain stopped and failed groups until explicit deletion in the MVP.

The normal user experience should not include manual VM power management.
Starting may cause a short delay. The manager emits structured state-change
progress separately from guest output, and clients present workspace readiness
rather than leaving the user or agent guessing.

The MVP durable sandbox states are `Preparing`, `Starting`, `Ready`, `Stopping`,
`Stopped`, `Failed`, `Deleting`, `Deleted`, and `Retired`. Disk-preserving idle
shutdown and explicit stop both end in `Stopped`; an optional reason records why.
Operations need leases or equivalent coordination so background stop cannot
race an active command, IDE session, file transfer, child VM operation, or
export.

Compatible requests against a preparing or starting sandbox join the existing
operation. Explicit start of a ready sandbox returns `AlreadyRunning` or
`NotModified`. Start or guest work during `Stopping` returns a lifecycle
conflict. Failed, deleting, deleted, and retired sandboxes reject start.

Client readiness waits default to 30 seconds and are client-configurable. A
timed-out or cancelled command is discarded, but startup continues to avoid
thrashing. The manager enforces a separate configurable hard startup limit,
defaulting to five minutes. On hard timeout it force-terminates the VM, records a
transient error, and restores `Stopped`. It does not retry automatically.

Other transient startup failures also leave the sandbox stopped and retryable.
Only nonrecoverable corruption, invalid configuration, or ownership failures
enter durable `Failed`; startup is never retried automatically.

Startup succeeds only after supervisor nonce validation, control-protocol
negotiation, workspace mounting, SSH host-key verification, and a successful
SSH data-plane probe. If no request remains when background startup finishes,
readiness begins the normal 15-minute idle period. Merely keeping a harness
session open does not hold a lease; active operations and explicit pins prevent
idle stopping.

The MVP does not automatically delete retained groups. Under disk pressure it
offers explicit cleanup. Bulk cleanup can preview groups idle longer than a
user-supplied duration, estimate reclaimed space, require confirmation, support
an explicit noninteractive confirmation option, and exclude running or leased
sandboxes.

Post-MVP work may add weak retention priority so inactive or archived sessions
become earlier stop or cleanup candidates under pressure. Exact
heuristics remain intentionally open.

## IDE intervention

The first-class **Open in VS Code** action is an intervention mechanism for the
current agent sandbox. It exists for cases where the user needs to inspect,
debug, or make a focused change directly in the agent's environment. It is not
the product's primary development workflow and does not turn Tiny Cosmos into a
user development-environment manager.

The action should open the guest-native workspace root through an appropriate VS
Code Remote transport without exposing the host workspace as a shared mount.

The manager or adapter should handle connection details, automatic start of the
existing sandbox, host-key or endpoint setup, and workspace path selection. From
the user's perspective, clicking the action should wait for readiness and open
the project.

An active IDE connection should count as sandbox activity and prevent surprise
stopping. Closing the IDE does not destroy the sandbox; normal idle policy
resumes control.

The first implementation uses Remote SSH. VS Code may establish its own
persistent SSH connection rather than sharing the manager's harness/CLI
connection. The exact mechanism remains open for Windows guests and operation
without guest networking. IDE-specific credentials must be ephemeral and scoped
to one sandbox.

Other IDEs may be added later through the same concepts: resolve the sandbox,
start it if needed, establish a scoped connection, and open the guest workspace
root for intervention.

For a longer-term switch to manual development, the Git worktree handoff is the
preferred workflow. VS Code Remote edits the live agent sandbox; worktree export
creates an independent host-local development state.

## Networking

Network policy is deliberately deferred until the core MVP is complete and
proven.

### Initial MVP

- Provide ordinary NAT internet access.
- Do not inject credentials into the guest.
- Do not claim isolation from the host, LAN, VPN, metadata services, or sibling
  sandboxes through networking.
- Keep guest internet access conceptually separate from unrestricted local
  execution.

### Later direction

Add a host-side egress proxy or equivalent broker for some combination of:

- Per-sandbox allowlists and deny rules
- Auditing
- Credential brokering without placing raw credentials in the guest
- Offline mode
- Blocking host, LAN, metadata, and cross-session destinations
- Policy-controlled port publication

Published guest ports should default to host loopback. Detailed proxy design is
intentionally deferred.

## Platform direction

The manager should expose a backend-neutral lifecycle model while implementing
and proving one backend at a time.

### Linux host

The Linux MVP uses Firecracker as its only backend. Later Linux backends remain
open if broader guest, device, or platform requirements justify them.

### Windows host

Use the native Host Compute Service (HCS) and Host Compute Network (HCN) rather
than building a VMM directly or relying on the heavier Hyper-V management stack.
See [Hcs microvm notes.md](Hcs%20microvm%20notes.md) for detailed findings.

### Linux guests

Use small prepared images, direct kernel boot where supported, and per-sandbox
differencing disks or equivalent copy-on-write storage.

### Nested virtualization and performance

The machine hosting the harness and Tiny Cosmos manager may itself be a virtual
machine. Tiny Cosmos should support creating its sandbox VMs in that environment
through nested virtualization where the outer platform exposes the required
hardware virtualization capabilities.

Nested operation may reduce host density and make sandbox startup, compiles,
tests, and image builds slower. That is an acceptable tradeoff: user attention,
isolation, and workload fidelity matter more than agent wall-clock time. Low
overhead remains desirable, but nested deployments should not be excluded
merely because agent work takes longer.

### Windows guests

Windows guests are a best-effort product lane for Windows-native development.
Assume licensing is handled.

Useful headless workloads include:

- PowerShell and `cmd.exe`
- MSVC Build Tools and the Windows SDK
- .NET Framework and modern .NET
- MSBuild, CMake, and Ninja
- Windows-native language servers
- Windows services and command-line programs

Expected limitations include:

- Large images and higher memory use
- Slower boot, cloning, servicing, and teardown
- Reboot and first-boot specialization requirements
- More complicated warm-state and image maintenance
- Windows-specific process, path, PTY, and file-locking behavior
- Backend-dependent nested virtualization and Windows container support
- No promise of GUI-dependent Visual Studio workflows

Use serviced golden images, per-sandbox differencing disks, automated bootstrap,
and warm saved states where reliable. Windows guests should not be forced to
meet Linux density or startup-time targets.

## Suggested MVP sequence

The first vertical slice should prove:

1. Linux host and Linux guest.
2. The selected Pi or OpenCode harness remains on the host as a non-root
   process.
3. One VM is automatically associated with each selected-harness session.
4. Shell, file, build, and related local tools execute inside the VM.
5. The guest has root and can run Docker.
6. The workspace is seeded rather than mounted or synchronized.
7. Changes and artifacts return through explicit export/apply operations.
8. Code running inside one guest cannot observe or affect another session's
   filesystem or processes through Tiny Cosmos control surfaces. MVP networking
   is not part of this claim. Same-user management clients remain mutually
   trusted.
9. Destroying a guest does not damage the host workspace or harness.
10. Cleanup reliably removes the VM, writable storage, and session resources.
11. An inactive VM can stop and later start without losing session workspace
    continuity.
12. The user can open the guest workspace through a first-class VS Code Remote
    intervention action without manually configuring the connection.
13. For a Git repository, the user can export the sandbox's current repository
    state, including uncommitted work, into a new host-local worktree.
14. Starting a harness session from that worktree creates a new independent
    sandbox through the ordinary session startup flow.

Network filtering beyond basic NAT follows after this core architecture is
demonstrated.

Windows-host/Linux-guest support is a likely next platform milestone. Windows
guest work can proceed as a distinct lane without blocking validation of the
core model.

## MVP acceptance scenario

Using the selected Pi or OpenCode integration, start two sessions from the same
host project. Each receives a separate VM with root, Docker, and an independent
seeded workspace. Both may make conflicting changes or destroy their own guest
operating system.

Neither guest can directly modify the host workspace, inspect the other guest's
filesystem or processes through Tiny Cosmos control surfaces, or obtain
authority over the manager. MVP networking does not prevent access to reachable
host, LAN, or sibling services. Same-user harness adapters and CLI commands can
manage both sandboxes by design. The user can inspect and explicitly promote
results from either sandbox.

After one session becomes inactive, its VM is stopped automatically.
Returning to the session or opening it in VS Code starts the same workspace
without requiring the user to manage the VM.

When the user decides to take over sustained development, a harness command
exports the sandbox repository into a host-local worktree. Opening a new session
from that worktree seeds another independent sandbox without establishing
continuous synchronization with either the original sandbox or worktree.

## Non-goals

- Building a new agent harness
- Acting as a general-purpose user development-environment manager
- Continuous host/guest file synchronization
- Treating containers as the primary isolation boundary
- macOS support in the first wave
- GPU acceleration or broad device passthrough
- Running desktop or GUI applications inside the guest as a primary workflow
- Solving comprehensive network policy before the core MVP

## Open questions

- Pi versus OpenCode for the single MVP harness integration
- Exact minimal guest supervisor protocol and SSH credential recovery behavior
- Image distribution, updating, and trust for the single official MVP image.
  Project templates and agent-assisted preparation are post-MVP; see
  [VM image preparation.md](VM%20image%20preparation.md)
- Workspace seed and export formats for large or non-Git projects
- Branch naming, worktree placement, collision handling, and repeat-export
  semantics for Git handoff
- Preservation policy for ignored files, submodules, nested repositories, Git
  LFS content, and other nonstandard Git working-tree state
- Whether optional host/guest path parity provides enough practical benefit
- How diagnostics and debugger paths integrate with the host IDE
- The minimal safe API split between unprivileged manager components and a
  privileged helper
- Saved-state compatibility and weak retention priority after the MVP
- How active commands, IDE connections, and child VMs participate in lifecycle
  leases
- Exact VS Code Remote transport, bootstrap, and credential model for each
  host/guest combination
- Resource accounting, garbage collection, and crash recovery
- Scope and timing of Windows guest support
