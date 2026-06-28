# OpenCode Sandbox LSP and MCP Feasibility

## Status

Investigated on June 13, 2026 against OpenCode v1.17.4, released on June
12, 2026.

This note evaluates whether Tiny Cosmos can keep OpenCode on the host while
making OpenCode use LSP and MCP servers that run inside a per-session sandbox.

## Decision summary

- Running an LSP process in the guest and relaying its stdio to OpenCode is
  technically easy, but it does not make OpenCode's built-in LSP integration
  correct for Tiny Cosmos. OpenCode's LSP client still reads host files, uses
  host paths in `file://` URIs, and shares LSP state by workspace directory
  rather than by session.
- A session-aware plugin tool named `lsp` is feasible. It can replace
  OpenCode's experimental built-in `lsp` tool and send operations to a
  guest-side LSP client through the Tiny Cosmos manager. Replacement file tools
  can also return guest LSP diagnostics after writes and edits.
- OpenCode can connect to a sandbox-running MCP server through a host stdio
  proxy or a loopback HTTP relay. Direct configuration is straightforward when
  one OpenCode workspace directory maps to one sandbox.
- OpenCode rebuilds the model-visible tool map before every agent step. MCP
  `tools/list_changed` notifications refresh its cached catalog, so tools can
  appear or disappear after a tool call and before the next inference. They
  cannot change during an already-running inference, which Tiny Cosmos does not
  need: sandbox lifecycle and capability changes occur through tool calls.
- OpenCode's supported plugin API does not currently let a plugin register or
  remove typed tools for one session at runtime. Native dynamic MCP registration
  is scoped by workspace directory, not session.
- A no-patch proxy is possible by exporting a directory-wide union catalog,
  namespacing tools by sandbox, and applying default-deny OpenCode session
  permissions so each session sees only its own tools. The proxy and plugin must
  still enforce ownership at execution time.
- The cleaner integration is a small OpenCode extension that asks plugins for
  additional tools during per-step tool resolution. The Tiny Cosmos plugin can
  then act as the MCP client and return only the current session's typed tools.
- OpenCode's experimental local workspace adapters could provide a unique
  directory-scoped OpenCode instance for each sandbox. This is promising for
  future native MCP integration, but the API and user flow are experimental and
  should not be an MVP dependency.
- The MVP should continue to disable OpenCode's built-in host LSP and formatter
  execution. Harness-global MCP servers remain on the host. Sandbox-local LSP
  and MCP integration should be added only through explicitly session-aware
  adapters.

## Relevant OpenCode behavior

### LSP

OpenCode v1.17.4 supports custom LSP entries with:

- A command and arguments
- File extensions
- Environment variables
- Initialization options

The configured command is spawned on the host with the detected host project
root as its working directory.

The built-in LSP client:

- Caches clients in instance state keyed by OpenCode's workspace directory.
- Computes roots from host filesystem paths.
- Sends the host root as `rootUri` and `workspaceFolders`.
- Reads document text from the host filesystem before `didOpen` and
  `didChange`.
- Uses host paths in LSP requests and diagnostic maps.
- Is called directly by OpenCode's built-in `read`, `write`, `edit`,
  `apply_patch`, and experimental `lsp` tools.

A stdio relay can move the language-server process into the VM, but it cannot
by itself change these host filesystem and path assumptions.

### MCP

OpenCode supports:

- Local MCP servers launched as child processes over stdio.
- Remote MCP servers over Streamable HTTP or SSE.
- Dynamic MCP addition through `POST /mcp`.
- MCP tools, prompts, and resources.

Local MCP commands are started on the host. Their default working directory is
the OpenCode workspace directory.

MCP clients and their discovered tool catalogs are stored in instance state
keyed by workspace directory. The MCP client receives the MCP method and
arguments, but not the OpenCode session identifier. The plugin
`tool.execute.before` hook does receive a session identifier, but prompt and
resource paths do not pass through that hook.

OpenCode does, however, resolve tools again before each model step. Its MCP
client caches tool definitions and refreshes that cache when a server sends
`notifications/tools/list_changed`. A tool call that creates, resumes,
reconfigures, or deletes a sandbox can therefore use this sequence:

```text
model inference
  -> lifecycle or guest tool call
  -> manager changes sandbox capabilities
  -> proxy connects, disconnects, or refreshes its MCP catalog
  -> tool call returns after the catalog update is committed
  -> OpenCode resolves tools for the next agent step
  -> next model inference receives the new tool set
```

There is no need to alter the tools supplied to an inference already in
progress. If a provider emits several parallel tool calls in one response, all
of those calls use the tool set from the beginning of that response; catalog
changes become visible on the following model step.

### Plugins and isolation controls

Plugin tools receive a tool context containing `sessionID`, `directory`, and
`worktree`. A custom tool with the same name as a built-in tool wins because
plugin tools are registered after built-ins.

Plugin-defined tools are collected when OpenCode initializes the
directory-scoped tool registry. The public plugin API has no supported
session-aware runtime registration operation. The `tool.definition` hook can
modify an existing tool's description and schema, but it cannot add a tool and
does not receive a session identifier.

Plugin config hooks can mutate the resolved OpenCode configuration before lazy
LSP and MCP state is initialized. This is useful for disabling host LSPs or
injecting a manager-owned proxy.

Tiny Cosmos must launch OpenCode with
`OPENCODE_DISABLE_PROJECT_CONFIG=true`. Otherwise a project can supply local
plugins, LSP commands, MCP commands, formatters, or other configuration that
executes in the trusted host process. The project configuration may be imported
as untrusted data by Tiny Cosmos and selectively re-executed inside the guest,
but OpenCode must not execute it directly on the host.

## Scope mismatch

Tiny Cosmos currently assigns:

```text
OpenCode session -> one sandbox VM
```

OpenCode v1.17.4 assigns LSP and MCP state approximately as:

```text
OpenCode workspace directory -> one LSP/MCP instance state
```

Two sessions opened from the same host project therefore share OpenCode's LSP
clients and MCP clients even though Tiny Cosmos gives them different VMs and
independent filesystems.

This is not only a lifecycle problem. It can route a tool request to the wrong
sandbox and expose one session's project-local service state to another
session.

## LSP integration options

### Custom OpenCode LSP command with a stdio bridge

Flow:

```text
OpenCode LSP client
  -> host tinycosmos LSP stdio proxy
  -> manager
  -> guest supervisor
  -> guest language server
```

Assessment: insufficient under the current workspace model.

The proxy would need to rewrite all host and guest file URIs, replace document
content read from the host, translate diagnostic and symbol locations, emulate
filesystem-dependent LSP requests, and choose a sandbox without receiving a
session identifier. A continuously synchronized host mirror could address some
of this, but it conflicts with the independent workspace model and introduces a
new synchronization and host attack surface.

### Session-aware plugin `lsp` tool

Flow:

```text
OpenCode custom lsp tool
  -> Tiny Cosmos manager using tool-context sessionID
  -> guest LSP broker
  -> guest language server
```

Assessment: feasible and recommended when first-class guest LSP support is
implemented.

The custom tool can preserve the argument and response shape of OpenCode's
experimental `lsp` tool. The replacement `read`, `write`, `edit`, and
`apply_patch` tools can explicitly warm the guest LSP and append diagnostics
from the guest after a mutation.

The guest LSP broker needs to:

- Discover or accept configured language-server commands.
- Pool servers by sandbox, server identity, and guest project root.
- Perform LSP initialization and shutdown.
- Send guest-native paths and document contents.
- Support diagnostics and the navigation operations exposed by the tool.
- Translate returned guest paths only when presenting them to OpenCode.
- Stop all servers when the sandbox suspends or is deleted.

This does not populate OpenCode's native `GET /lsp` status or reuse its internal
LSP client. That is an acceptable product limitation if the model receives the
same useful diagnostics and navigation operations.

### Experimental local OpenCode workspace per sandbox

OpenCode plugins can register an experimental workspace adapter whose target is
a local directory. A Tiny Cosmos adapter could create a manager-owned anchor
directory per sandbox and bind one OpenCode session to that workspace. OpenCode
would then create separate directory-keyed LSP and MCP state.

Assessment: promising but incomplete.

This solves the session-scoping problem, but not the built-in LSP client's need
to read files from the local target directory. It is more immediately useful
for MCP than LSP. It also needs a reliable way to bind the session to the
workspace before its first agent turn; current plugin event delivery is not an
awaited session-creation gate.

### Remote OpenCode workspace in the guest

Running an OpenCode server in the guest would make its built-in file, LSP, and
MCP assumptions naturally match the guest filesystem.

Assessment: rejected for the current architecture.

OpenCode's remote workspace routing moves substantial harness behavior to the
remote server. It would place conversation execution, provider access, plugin
loading, or equivalent delegated authority inside the untrusted VM unless Tiny
Cosmos implemented a much larger split OpenCode runtime. That contradicts the
current trust boundary.

## MCP integration options

### One sandbox per OpenCode workspace directory

With a one-to-one mapping, the plugin can inject a local MCP configuration such
as a manager-owned stdio proxy. The proxy starts the real MCP server inside the
guest and relays MCP frames. OpenCode retains its normal MCP client, tool
catalog, permissions, prompts, and resources.

Assessment: feasible, but it does not match the current per-session mapping
when several sessions share one directory.

### Generic session-aware plugin tool

A plugin can expose operations such as:

- List sandbox MCP tools.
- Call a sandbox MCP tool.
- List or read sandbox MCP resources.
- List or expand sandbox MCP prompts.

Every call includes the trusted plugin tool context's `sessionID`, so routing is
unambiguous.

Assessment: robust but less native.

OpenCode does not receive each MCP tool as a separately typed model tool. The
agent instead uses a generic discovery and call interface. This is suitable as
a fallback or for a small number of optional guest capabilities.

### Dynamic union-catalog MCP proxy

A single manager-owned MCP proxy can be registered once with OpenCode for the
host workspace directory. The proxy acts as an MCP client for each live
sandbox, then exports the union of their tool catalogs to OpenCode.

Each exported name must include an opaque, collision-safe sandbox capability
prefix. OpenCode session permissions use ordered wildcard rules:

```text
deny  tinycosmos_sandbox_*
allow tinycosmos_sandbox_<current-capability>_*
```

Because the general deny also matches tools added in the future, sibling
sessions do not start seeing a newly created sandbox's tools when the proxy
sends `tools/list_changed`. OpenCode filters denied tools before constructing
the provider request.

The plugin's `tool.execute.before` hook receives the trusted OpenCode
`sessionID`. It must reject a call unless the exported tool's sandbox capability
belongs to that session. The proxy must independently validate the capability
before forwarding the MCP request. Tool-name opacity and model-visible filtering
are not authorization boundaries.

Assessment: feasible without an OpenCode fork, but operationally awkward.

The sandbox-changing tool must wait for the proxy catalog update before
returning. Tool removal uses the same barrier. The design also needs:

- Session permission rules to be installed before the first model step.
- Permission cleanup without overwriting user-authored rules.
- Bounded catalog retention and cleanup on session deletion.
- Provider-compatible names despite the added capability prefix.
- Tests proving filtered union catalogs do not leak schemas across sessions.

MCP prompts and resources are still directory-wide OpenCode surfaces. The proxy
should expose sandbox prompts and resources through session-scoped tools, or
defer them, rather than using OpenCode's native prompt and resource endpoints.

### Session-aware dynamic plugin tool provider

The clean design adds a small OpenCode plugin hook at the end of
`SessionTools.resolve`. The hook receives the session identifier and may add or
remove typed tools for that one agent step. The Tiny Cosmos plugin becomes the
MCP client:

```text
OpenCode SessionTools.resolve(sessionID)
  -> Tiny Cosmos plugin dynamic-tool provider
  -> manager catalog for sessionID
  -> typed OpenCode tool wrappers
  -> proxy call to the matching guest MCP server
```

Assessment: preferred.

OpenCode already invokes tool resolution on every step, so the extension does
not need a new scheduler or mid-stream mutation mechanism. The lifecycle tool
waits for MCP discovery, returns, and the next resolution receives the changed
catalog. Tool wrappers close over the trusted session identifier and can route
calls without model-visible metadata or a directory-wide union.

This likely requires a small maintained patch or an accepted upstream API. The
hook should:

- Run after built-in and native MCP tool resolution and before provider
  filtering.
- Receive `sessionID`, agent, and model context.
- Accept JSON Schema tool definitions plus an execution callback.
- Preserve normal OpenCode permission, cancellation, attachment, truncation,
  and plugin before/after behavior.
- Be awaited so catalog discovery is deterministic.

Prompts and resources can be represented as typed session tools initially. A
future hook can expose them as native OpenCode surfaces if that becomes useful.

### Argument-routing MCP stdio proxy

A single host MCP proxy can expose a stable tool catalog to OpenCode and manage
one real MCP connection per Tiny Cosmos session. For tool calls, the plugin's
`tool.execute.before` hook could add reserved routing metadata to the argument
object; the proxy would authenticate, remove that metadata, and forward the
call to the correct guest.

Assessment: superseded by the union-catalog design for a no-patch
implementation.

MCP prompts and resources bypass the tool execution hook and still lack session
identity. This option is therefore tool-only unless OpenCode changes or Tiny
Cosmos replaces those surfaces as well.

### Experimental local workspace per sandbox

A unique local OpenCode workspace target per sandbox would give each sandbox
its own directory-keyed MCP state. The config hook could then inject a normal
local MCP stdio proxy with no multiplexing.

Assessment: viable for native MCP behavior without an OpenCode fork, but less
direct than per-step session tools and dependent on an experimental workspace
lifecycle.

## Capability classification

MCP protocol use does not determine trust placement. Tiny Cosmos should
distinguish:

| MCP class | Examples | Process location | Credential policy |
|---|---|---|---|
| Harness-global | GitHub, issue trackers, cloud APIs, memory | Host | Existing OpenCode permissions and host credential storage |
| Sandbox-local | Browser automation against a guest app, guest database inspection, project-local services | Guest | No implicit host environment or credentials |
| Boundary-crossing | Artifact publication, host import/export, port publication | Host manager or a narrowly scoped proxy | Explicit Tiny Cosmos policy |

A project-provided local MCP command is sandbox-local by default. Tiny Cosmos
may offer an explicit trusted override, but must not silently execute it on the
host.

## Recommended implementation direction

### MVP

- Set `OPENCODE_DISABLE_PROJECT_CONFIG=true`.
- Load the Tiny Cosmos integration from a manager-controlled host profile and
  preserve only separately trusted harness-global configuration.
- Set OpenCode `lsp` and `formatter` to `false` in the plugin config hook.
- Keep user-approved harness-global MCP servers in OpenCode on the host.
- Do not advertise sandbox-local MCP injection as an MVP feature.
- Continue to let the agent install and invoke language servers, linters, and
  type checkers through guest shell commands.
- Include a narrow compatibility spike for per-step dynamic tools so
  sandbox-local MCP can be promoted without revisiting the control-plane
  architecture.

### First guest LSP increment

- Add a session-aware custom `lsp` tool backed by a small guest LSP broker.
- Add optional guest diagnostics to the replacement write/edit/patch tools.
- Keep native OpenCode LSP disabled.
- Treat failure or absence of a language server as a normal optional capability,
  not a failed file operation.

### First sandbox-local MCP increment

Prefer, in order:

1. A session-aware dynamic plugin tool-provider hook in OpenCode, backed by the
   Tiny Cosmos plugin as MCP client.
2. A dynamic union-catalog MCP proxy with default-deny session permissions and
   execution-time ownership checks.
3. A generic session-aware plugin MCP tool.
4. A stable OpenCode local workspace per Tiny Cosmos sandbox, followed by a
   normal stdio MCP proxy.

Do not use a remote OpenCode server in the guest merely to obtain native MCP or
LSP behavior.

## Upstream capabilities that would simplify integration

Tiny Cosmos should consider proposing or tracking these OpenCode changes:

- Scope LSP and MCP state by session or workspace identifier, not only by local
  directory.
- Add an awaited session-aware dynamic-tool provider hook during
  `SessionTools.resolve`.
- Pass session and tool-call metadata to MCP transports without adding it to
  model-visible tool arguments.
- Allow plugins to register an MCP transport or client factory.
- Allow plugins to register an LSP transport plus virtual filesystem and path
  mapping callbacks.
- Expose LSP operations to plugins through the supported plugin API.
- Add an awaited hook that can assign a workspace before the first session
  turn.

## Required compatibility spikes

Before enabling either integration:

1. Verify `OPENCODE_DISABLE_PROJECT_CONFIG=true` prevents project LSP, MCP,
   formatter, command, and plugin execution on the host.
2. Verify the Tiny Cosmos config hook runs before lazy LSP and MCP
   initialization for every supported OpenCode version.
3. Verify custom tools continue to override the built-in file and `lsp` tools.
4. Run two concurrent sessions from one host project and prove all custom LSP
   operations route by session.
5. Prove a sandbox-spawning tool can commit an MCP catalog change before
   returning and that the new typed tools appear on the immediately following
   agent step.
6. Run two concurrent sessions with conflicting MCP schemas and state. Prove
   that neither model receives the other session's tool schemas and that direct
   execution cannot cross-route.
7. Prove tool removal is visible on the immediately following agent step after
   sandbox deletion or suspension.
8. If testing experimental workspaces, prove the session is bound before its
   first model turn and survives OpenCode restart.

## Verified references

- [OpenCode v1.17.4 release](https://github.com/anomalyco/opencode/releases/tag/v1.17.4)
- [OpenCode LSP documentation](https://opencode.ai/docs/lsp/)
- [OpenCode MCP documentation](https://opencode.ai/docs/mcp-servers/)
- [OpenCode plugin documentation](https://opencode.ai/docs/plugins/)
- [OpenCode LSP runtime at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/lsp/lsp.ts)
- [OpenCode LSP client at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/lsp/client.ts)
- [OpenCode MCP runtime at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/mcp/index.ts)
- [OpenCode MCP tool wrapper at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/mcp/catalog.ts)
- [OpenCode per-step session tool resolution at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/tools.ts)
- [OpenCode session loop at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/prompt.ts)
- [OpenCode provider tool filtering at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/llm/request.ts)
- [OpenCode plugin API at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/plugin/src/index.ts)
- [OpenCode instance bootstrap ordering at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/project/bootstrap.ts)
- [OpenCode instance state at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/effect/instance-state.ts)
- [OpenCode workspace adapter types at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/control-plane/types.ts)
- [OpenCode configuration paths at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/config/paths.ts)
