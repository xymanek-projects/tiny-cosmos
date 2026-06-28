# Sandbox Integration Examples Comparison

## Status

Prepared on June 13, 2026.

This document compares three existing integrations that keep an agent harness
on the host while redirecting coding operations into a sandbox:

1. Pi's Gondolin example extension
2. Daytona's OpenCode plugin
3. The community `pi-daytona` extension

The comparison focuses on what each implementation demonstrates for Tiny
Cosmos, what should be adopted, and what should be rejected. It does not treat
any of the three implementations as satisfying the Tiny Cosmos security or
workspace model without changes.

## Sources reviewed

- [Pi Gondolin extension](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/examples/extensions/gondolin/index.ts)
- [Pi containerization guidance](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/docs/containerization.md)
- [Daytona OpenCode plugin](https://github.com/daytonaio/daytona/tree/db180b92ded599b3d7129fce5f55f7759b322f32/libs/opencode-plugin)
- [Daytona OpenCode plugin guide](https://github.com/daytonaio/daytona/blob/db180b92ded599b3d7129fce5f55f7759b322f32/apps/docs/src/content/docs/en/guides/opencode/opencode-plugin.mdx)
- [`pi-daytona`](https://github.com/richardanaya/pi-daytona)

## Executive assessment

The three integrations demonstrate complementary parts of the desired design:

| Integration | Strongest evidence |
|---|---|
| Pi with Gondolin | Pi provides a clean and faithful tool-backend replacement API. |
| OpenCode with Daytona | A session-oriented sandbox integration can provide durable lifecycle, reconnection, previews, and host handoff. |
| Pi with Daytona | Pi's operation-provider pattern works with a remote sandbox, not only an in-process local VM. |

No implementation should be copied wholesale:

- Gondolin exposes the host workspace to the guest through a writable mount.
- Daytona continuously synchronizes sandbox commits into host Git branches and
  reimplements OpenCode tools with incomplete semantics.
- `pi-daytona` fails open to local execution and has no automatic workspace
  transfer.

The best Tiny Cosmos design combines Pi/Gondolin's adapter shape with
OpenCode/Daytona's lifecycle concepts, while retaining Tiny Cosmos's independent
workspace and explicit export model.

## Summary matrix

| Area | Pi and Gondolin | OpenCode and Daytona | Pi and Daytona |
|---|---|---|---|
| Ownership | Official Pi example | Official Daytona integration | Community extension |
| Sandbox location | Local QEMU micro-VM | Daytona cloud sandbox | Daytona cloud sandbox |
| Harness location | Host | Host | Host |
| Model credentials | Host | Host | Host |
| Tool strategy | Inject alternate operations into Pi tool implementations | Reimplement OpenCode tools as plugin tools | Primarily inject Daytona operations into Pi tools |
| Model-facing tool fidelity | High for redirected native tools | Uneven | High for read, write, edit, and bash; custom for search tools |
| Interactive shell shortcut | Redirects `!` and `!!` | Not demonstrated | Redirects `!` and `!!` |
| Session identity | Available, but example does not persist a binding | OpenCode session ID is durably mapped to a sandbox | Pi session ID keys in-memory state |
| Sandbox persistence | VM closes on session shutdown | Retained until OpenCode session deletion | Named sandboxes persist; binding is not durable |
| Workspace transfer | Writable host mount | Initial Git push and automatic reverse sync | No automatic seed or export |
| Independent workspace | No | Yes | Yes, but initially empty |
| Background commands | No dedicated product flow | Supported through Daytona exec sessions | No dedicated background flow |
| Preview or port action | No | Daytona preview URL tool | No |
| LSP | Not provided | Registered tool is a stub | Not provided |
| Patch tool | Not applicable to Pi's default set | Registered tool is a stub | Not provided |
| Failure mode | VM errors prevent routed operation | Sandbox errors fail the tool | Falls back to host tools |
| Test coverage in integration | None found | None found | Package test script is a placeholder |
| Tiny Cosmos value | Best adapter reference | Best lifecycle reference | Best remote-Pi feasibility reference |

## Pi with Gondolin

### Architecture

The Pi process, provider authentication, conversation state, and extension
runtime remain on the host. The extension starts a local Gondolin micro-VM and
routes Pi's coding tools into it.

The extension uses Pi's exported tool constructors and operation interfaces:

- `createReadTool` with `ReadOperations`
- `createWriteTool` with `WriteOperations`
- `createEditTool` with `EditOperations`
- `createBashTool` with `BashOperations`
- `createFindTool` with `FindOperations`
- `createLsTool` with `LsOperations`

It directly implements guest-backed `grep` behavior and handles Pi's
`user_bash` event so interactive `!` commands also run in the VM.

### Strengths

- Preserves Pi's native tool names, schemas, validation, output rendering, edit
  presentation, and truncation behavior where operation injection is used.
- Redirects both model-initiated shell operations and interactive user shell
  shortcuts.
- Keeps the sandbox backend behind small operation interfaces rather than
  reproducing the harness tool layer.
- Uses session lifecycle events and system-prompt rewriting through documented
  extension APIs.
- Demonstrates that the harness and its provider credentials do not need to
  enter the VM.
- Is compact enough to audit as an integration example.

### Limitations

- `RealFSProvider` mounts the host working directory at `/workspace`.
- Guest writes under `/workspace` immediately modify the host.
- The VM exists only in extension memory.
- `session_shutdown` closes the VM instead of releasing a lease on a retained
  sandbox.
- It has no durable session-to-VM mapping, crash recovery, suspension,
  reconnection, explicit export, port publication, or IDE handoff.
- Search behavior is partly recreated in the extension rather than delegated to
  a guest-native search service.

### Tiny Cosmos interpretation

Gondolin is strong evidence for Pi as a harness target, but not for the Tiny
Cosmos workspace model. Tiny Cosmos should preserve the extension structure and
replace both `VM` and `RealFSProvider` with calls to the Tiny Cosmos manager.

## OpenCode with Daytona

### Architecture

OpenCode and its model credentials remain on the host. Plugin tools use the
Daytona SDK to operate on a separate cloud sandbox for each OpenCode session.

The plugin:

- Maps OpenCode session IDs to Daytona sandbox IDs.
- Persists mappings in project-specific JSON files.
- Lazily creates or reconnects to a sandbox on a tool call.
- Starts a retained sandbox when necessary.
- Deletes a sandbox when its OpenCode session is deleted.
- Auto-commits guest changes when a session becomes idle.
- Fetches those commits into numbered host branches.
- Provides a preview URL tool for guest services.

### Strengths

- Provides the most complete session-oriented product workflow of the three.
- Demonstrates durable session mapping and reconnection across harness
  processes.
- Uses one independent sandbox per OpenCode session.
- Handles lazy creation, retained sandbox startup, deletion, notifications,
  logging, and recovery from partial local state.
- Demonstrates background command sessions.
- Demonstrates an explicit product surface for exposing a guest service.
- Provides a practical Git-based route from sandbox work back to the host.

### Limitations

The plugin recreates OpenCode tools individually instead of supplying alternate
operations to OpenCode's native implementations. This produces behavioral
gaps:

- `patch` returns a not-implemented message.
- `lsp` returns a not-implemented message.
- `edit` uses a single JavaScript string replacement and does not reject a
  missing or ambiguous match.
- `multiedit` applies sequential string replacements but is not atomic.
- `read` omits offsets, line limits, byte limits, image handling, and
  truncation metadata.
- Foreground `bash` does not provide streaming output, PTY behavior,
  cancellation, or the full expected timeout and environment semantics.
- Search tools expose reduced schemas and result behavior.

Its workspace behavior also differs from Tiny Cosmos:

- Only committed host state is initially pushed; dirty host state is not
  faithfully seeded.
- Guest changes are automatically committed.
- A local `opencode/N` branch is continuously updated.
- If that generated branch is checked out, the plugin may run
  `git reset --hard` in the host worktree.
- This is synchronization, not explicit point-in-time handoff.

The documented installation through project OpenCode configuration also does
not establish Tiny Cosmos's stronger rule that project-provided harness
configuration must never execute on the host.

### Tiny Cosmos interpretation

Daytona is the best reference for lifecycle and user workflow, but not for tool
implementation or workspace transfer. Its session manager should be studied as
product prior art, while Tiny Cosmos keeps lifecycle authority in its manager
and uses explicit seeding and worktree export.

## Pi with Daytona

### Architecture

The community `pi-daytona` package keeps Pi and Daytona credentials on the host
and replaces Pi's coding tools with Daytona-backed implementations.

It uses Pi's native operation-provider pattern for:

- `read`
- `write`
- `edit`
- `bash`

It implements `grep`, `find`, and `ls` as custom tools using Daytona filesystem
APIs. It also redirects `user_bash`, adds sandbox list and delete tools, and
keys active state by `sessionManager.getSessionId()`.

### Strengths

- Proves Pi's operation-provider pattern works over a remote SDK boundary.
- Supports separate active sandboxes for concurrent Pi sessions.
- Preserves native Pi behavior for the tools built through Pi's constructors.
- Redirects interactive `!` commands.
- Supports named retained sandboxes and reconnection by name or ID.
- Keeps the Daytona API key outside the sandbox.
- Provides simple sandbox discovery and deletion actions.

### Limitations

- The package is small, community-maintained, and currently version `0.0.2`.
- It has no real test suite.
- It has no automatic host-to-sandbox workspace seed.
- It has no guest-to-host export or Git handoff.
- The Pi session-to-sandbox relationship is held only in process memory.
- A resumed Pi session does not automatically recover its previous sandbox
  unless the user supplies the same sandbox name.
- Shell output is delivered after Daytona command completion rather than
  genuinely streamed.
- Cancellation cannot necessarily stop a command already running remotely.
- `grep`, `find`, and `ls` implement only part of their advertised schemas.
- It deliberately reads local `SKILL.md` files outside the sandbox.
- It imports the older `@mariozechner/pi-coding-agent` package name; current Pi
  compatibility should be verified.

The most serious issue is its failure policy:

- If sandbox mode is not active, replacement tools invoke local Pi tools.
- Sandbox initialization failure can leave local tools available.
- Deleting the active sandbox detaches the session to local execution.

This behavior is convenient for an optional sandbox extension but unacceptable
for Tiny Cosmos. A missing, failed, or deleted sandbox must make guest-local
operations unavailable until a valid sandbox is restored.

### Tiny Cosmos interpretation

`pi-daytona` is useful proof that a Pi adapter does not need direct access to a
VM object. It is the closest small implementation to the desired host-harness
and remote-execution split, but it should be treated as a feasibility prototype
and negative security reference rather than a production foundation.

## What Tiny Cosmos should adopt

### Adopt from Pi and Gondolin

1. Use the harness's native tool constructors and inject alternate operation
   implementations whenever the harness supports that model.
2. Preserve native tool schemas, rendering, validation, truncation, and
   cancellation semantics.
3. Redirect interactive shell shortcuts as well as model-facing shell tools.
4. Use documented session start, resume, fork, switch, reload, and shutdown
   events.
5. Rewrite the harness-visible working-directory context to the guest path.
6. Keep the adapter thin and place lifecycle, storage, security, and transfer
   policy in the Tiny Cosmos manager.

### Adopt from OpenCode and Daytona

1. Persist a durable harness-session-to-sandbox binding.
2. Lazily create or resume a sandbox on the first operation as a fallback for
   missed lifecycle events.
3. Reconcile retained sandbox state after harness or manager restart.
4. Separate session deletion from ordinary harness shutdown.
5. Provide visible readiness, resume, failure, and cleanup status.
6. Support background activity through manager-owned command and port leases.
7. Provide a first-class, policy-controlled way to open or expose guest
   services.
8. Make sandbox cleanup an explicit lifecycle operation rather than relying on
   extension process teardown.

### Adopt from Pi and Daytona

1. Treat the harness session identifier as the primary adapter routing key.
2. Prove that the adapter can work through an asynchronous remote manager API,
   not only an in-process VM object.
3. Provide agent-visible sandbox inspection actions where they are useful and
   narrowly authorized.
4. Keep provider and sandbox-manager credentials in the host control plane.

## What Tiny Cosmos should adapt

### Session identity

Persist a generic binding rather than provider-specific extension state:

```text
harness kind
harness installation or instance identity
harness session identifier
canonical host workspace identity
sandbox group identifier
optional parent or fork relationship
```

Session shutdown should release active leases. Session deletion or an explicit
Tiny Cosmos delete action may begin retention-policy-controlled destruction.

### Tool adapters

Define canonical manager operations for:

- Process execution, cancellation, timeout, and PTY input
- File read, write, edit, and atomic replacement
- Directory listing, glob, and search
- Workspace seed and export
- Port relay and IDE access
- Health, capabilities, and lifecycle

Each harness adapter should translate native harness tool contracts into these
operations. It should not place sandbox lifecycle logic inside individual tool
implementations.

### Workspace transfer

Use neither a writable mount nor continuous branch synchronization:

1. Seed an independent guest workspace from the host.
2. Record the seed revision and dirty-state manifest.
3. Let host and guest diverge.
4. Export only through an explicit operation.
5. Materialize Git work into a new branch and worktree without changing an
   existing host branch or worktree.

### Project resources

Start the harness with project extension, plugin, hook, formatter, LSP, MCP, and
context discovery disabled where possible. Tiny Cosmos may parse selected
project configuration as untrusted input and reproduce approved behavior inside
the guest.

Explicit user attachments and trusted harness-global integrations should remain
separate from model-initiated guest file access.

## What Tiny Cosmos should reject

- Writable host workspace mounts.
- Automatic continuous host/guest synchronization.
- Silent commits made only to transport agent changes.
- Force-updating or resetting an existing host worktree.
- Local execution fallback after sandbox failure.
- Model-visible tools that can disable isolation or detach to host execution.
- Project-provided extension or plugin execution in the trusted host harness.
- Adapter-owned credentials inside the guest.
- Tool replacements that advertise unsupported behavior.
- Treating extension process memory as the source of lifecycle truth.
- Deleting a retained sandbox merely because the harness process exits or an
  extension reloads.

## Rankings

### Adapter architecture

1. **Pi and Gondolin**

   Best use of native harness abstractions and smallest semantic gap between
   local and sandbox-backed tools.

2. **Pi and Daytona**

   Confirms the same operation-provider architecture over a remote SDK, but
   partially recreates search tools and has unsafe fallback behavior.

3. **OpenCode and Daytona**

   Functional, but requires broad tool reimplementation and currently exposes
   incomplete substitutes.

### Product lifecycle

1. **OpenCode and Daytona**

   The only integration with durable automatic mapping, reconnection, lazy
   startup, session deletion cleanup, background commands, previews, and host
   handoff.

2. **Pi and Daytona**

   Retains named remote sandboxes and supports concurrent sessions, but lacks a
   durable automatic binding and workspace transfer.

3. **Pi and Gondolin**

   Creates and destroys one in-memory VM with the extension runtime.

### Security alignment with Tiny Cosmos

This ranking considers architecture after removing each integration's known
workspace and failure-policy violations.

1. **Pi and Gondolin adapter model**

   Pi exposes the clearest route to complete guest-local tool replacement,
   interactive shell interception, and fail-closed launch configuration.

2. **OpenCode and Daytona session model**

   OpenCode provides usable session context and the Daytona plugin proves the
   lifecycle, but complete host-execution suppression still needs a broader
   audit and adapter.

3. **Pi and Daytona implementation**

   The intended split is close to Tiny Cosmos, but its local fallback and
   model-visible detach behavior directly violate the isolation contract.

### Value as Tiny Cosmos references

1. **Pi and Gondolin:** primary adapter reference.
2. **OpenCode and Daytona:** primary lifecycle and product reference.
3. **Pi and Daytona:** remote-adapter feasibility and failure-policy reference.

## Recommended synthesis

The preferred Tiny Cosmos integration has this shape:

```text
host harness
  -> thin session-aware adapter
  -> native harness tool contracts
  -> Tiny Cosmos manager protocol
  -> durable session and lifecycle state
  -> guest supervisor
  -> independent guest workspace
```

The adapter should resemble Pi/Gondolin:

- Native tool constructors where available
- Alternate filesystem and process operations
- Session events
- Interactive shell interception
- Guest-path prompt context

The manager and product workflow should resemble the strongest parts of
OpenCode/Daytona:

- Durable mapping
- Lazy creation and reconnection
- Retained sandboxes
- Explicit deletion
- Status and failure feedback
- Background-command and service leases
- IDE and port actions

The workspace and security behavior must remain Tiny Cosmos-specific:

- No host mount
- No continuous sync
- Explicit seed and export
- New-worktree handoff
- No host-local fallback
- No project-controlled host execution
- Fail closed whenever the sandbox binding is unavailable

## Recommendation

For an initial harness spike, Pi is the lower-risk adapter target because its
operation-provider APIs preserve native tool behavior and its `user_bash` event
covers an additional host-execution path explicitly.

OpenCode remains a credible integration target and Daytona proves that its
session model can support a useful sandbox product. It requires more adapter
code and a larger conformance audit because replacement tools reproduce native
behavior individually and OpenCode has additional host-scoped LSP, MCP,
formatter, and project-configuration behavior.

A practical evaluation should therefore:

1. Implement a minimal Pi adapter against the Tiny Cosmos manager protocol.
2. Implement the equivalent OpenCode replacements for the MVP tool set.
3. Run the same fail-closed conformance suite against both.
4. Compare semantic fidelity, unredirected host behavior, maintenance burden,
   and session lifecycle reliability.

Until those spikes are complete, the evidence favors Pi for adapter simplicity
and OpenCode/Daytona as the stronger reference for lifecycle and user workflow.
