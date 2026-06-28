# Harness Integration Comparison

## Status

Prepared on June 13, 2026.

This document records integration surfaces relevant to Tiny Cosmos for:

- Pi
- OpenCode
- Claude Code
- Gemini CLI
- Codex

It records documented behavior, inspected source behavior, and the adapter
components implied by the Tiny Cosmos boundary. It does not select or rank a
harness.

Pi and OpenCode are the two candidate MVP integration tracks. The choice is
deferred, and the MVP will implement and ship exactly one of them. Claude Code,
Gemini CLI, and Codex remain comparison inputs and possible post-MVP targets.

The reviewed snapshots are:

| Harness | Reviewed snapshot |
|---|---|
| Pi | Repository commit [`6f29450e1339b37582796775749de3957936cd8d`](https://github.com/earendil-works/pi/tree/6f29450e1339b37582796775749de3957936cd8d) |
| OpenCode | Release [`v1.17.4`](https://github.com/anomalyco/opencode/releases/tag/v1.17.4), released June 12, 2026 |
| Claude Code | Official documentation retrieved June 13, 2026 and repository commit [`ca9f6045fc90c8244f9e787fb57d54b380f9a27c`](https://github.com/anthropics/claude-code/tree/ca9f6045fc90c8244f9e787fb57d54b380f9a27c) |
| Gemini CLI | Official documentation retrieved June 13, 2026 and repository commit [`9e5599c323f12df36672794332df5ae16e6125d5`](https://github.com/google-gemini/gemini-cli/tree/9e5599c323f12df36672794332df5ae16e6125d5) |
| Codex | Official app-server documentation retrieved June 13, 2026 and repository commit [`f297b9f07de10c7d8b9ed284b674d06cc5ff7723`](https://github.com/openai/codex/tree/f297b9f07de10c7d8b9ed284b674d06cc5ff7723) |

## Tiny Cosmos boundary

The comparison uses the requirements recorded in
[Current plan.md](Current%20plan.md) and
[MVP technical plan.md](MVP%20technical%20plan.md):

- The harness process, model credentials, conversation state, and
  harness-global integrations remain on the host.
- One harness thread or session maps to one independent sandbox.
- Model-initiated shell and guest-filesystem operations execute in the
  sandbox.
- Project-controlled configuration does not execute in the host harness
  process.
- The host workspace seeds the sandbox. It is not mounted or continuously
  synchronized.
- Retained sandbox workspace state survives harness and VM restarts.
- Import, export, port publication, and other boundary-crossing operations
  remain manager-controlled.

## Cross-harness facts

| Area | Pi | OpenCode | Claude Code | Gemini CLI | Codex |
|---|---|---|---|---|---|
| Primary reviewed integration surface | TypeScript or JavaScript extension | TypeScript or JavaScript plugin | CLI controls or Agent SDK with MCP tools | Extension, hooks, MCP, or ACP | App-server JSON-RPC API, dynamic tools, or remote environment |
| Built-in tool suppression | `--no-builtin-tools`; `--tools`; `--exclude-tools` | Plugin tools can shadow selected built-ins; configuration can disable LSP and formatters | `--tools ""` disables all built-in tools | `tools.core` is a built-in allowlist; extension `excludeTools` removes named tools | An empty experimental `environments` list removes environment-backed shell, patch, and image tools |
| Same-name built-in replacement | `registerTool()` replaces a built-in with the same name | A plugin tool with the same name takes precedence | Agent SDK custom tools use MCP names such as `mcp__server__tool`; built-ins can be disabled separately | MCP tools are separately registered; hooks can deny or rewrite a call but do not supply a pre-execution tool result | Core tools are registered before extension and dynamic tools; duplicate names are skipped |
| Manager-backed tool mechanism | Built-in operation interfaces or replacement tools | Plugin tool handlers | CLI-connected MCP server or in-process Agent SDK MCP server | MCP server supplied by extension, settings, or ACP client | Dynamic tool callbacks or an exec-server remote environment |
| Harness session identifier | Explicit `--session-id`; extension reads `getSessionId()` | Plugin context includes `sessionID` | Explicit UUID with `--session-id`; Agent SDK result returns a session ID | Session UUID in stored session data and resume interfaces | App-server thread ID; `thread.sessionId` identifies the session tree root |
| Resume | Session manager and CLI session options | OpenCode session events and persisted session state | `--resume` by ID or name; Agent SDK `resume` option | `--resume` by latest, index, or full UUID | `thread/resume` |
| Fork or branch | Session fork lifecycle event | Not recorded in the reviewed OpenCode source notes | `--fork-session` with resume flows | Named chat checkpoints and separate sessions; Git worktrees documented for parallel sessions | `thread/fork` creates a new thread ID and preserves the root `sessionId` |
| Runtime tool catalog change | `registerTool()` can refresh the active session tool catalog | Plugin tools are collected in directory-scoped plugin state; MCP tools are resolved again before each model step | MCP tool discovery supplies custom tools | MCP discovery and `BeforeToolSelection` filtering change the model-visible set | Experimental `dynamicTools` are attached to a thread and restored on resume |
| Project resource control | Discovery flags and project trust event | `OPENCODE_DISABLE_PROJECT_CONFIG=true` | `--bare`, `--safe-mode`, and `--setting-sources` control different resource classes | Trusted Folders and workspace setting precedence control project resources | App-server accepts a thread `cwd` and configuration overrides; project instructions are resolved from the selected working directory |
| User-entered shell path | `user_bash` event covers `!` and `!!` | Separate from model-facing `bash`; included in the host-process audit in the Tiny Cosmos plan | Built-in Bash remains available in `--bare` unless removed with `--tools` | `!` invokes `run_shell_command` | `thread/shellCommand` runs outside the sandbox with full host access |
| Built-in MCP client | No MCP in Pi core | Yes | Yes | Yes | Yes |
| MCP scoping fact | Extension-owned | Native clients are keyed by OpenCode workspace directory | MCP configuration can be restricted to explicit `--mcp-config` input | Extension and settings MCP servers load at startup; ACP clients can provide an MCP server during initialization | MCP servers are configured in Codex; required MCP startup failure can fail thread start or resume |
| Built-in LSP fact | The reviewed Gondolin extension does not add LSP routing | Native LSP state is keyed by workspace directory and uses host paths/files | LSP servers are a customization class disabled by `--safe-mode` | The official built-in tools list does not list an LSP tool | The reviewed app-server tool surface does not expose a built-in LSP client |

## Shared process placement

For each integration described here, the harness remains a host process. The
Tiny Cosmos manager remains a separate trusted host process. The guest runs the
project shell, filesystem, package managers, builds, language tools, and local
services.

The following operations remain separate from guest-local model tools:

- Harness authentication and model API access
- Harness conversation persistence
- Harness-global MCP servers and other credentialed integrations
- Host workspace seed reads
- Explicit export to a host path
- IDE launch and connection setup
- Manager and broker lifecycle operations

## MVP integration tracks

The core MVP is shared through guest execution, workspace transfer, lifecycle,
recovery, IDE intervention, and Git handoff. Only the harness adapter is split
into mutually exclusive Pi and OpenCode tracks.

### Shared adapter contract

Whichever track is selected must:

1. Derive one stable opaque caller key from its harness-owned session and
   workspace context, add a clear harness-specific prefix, and supply it to the
   management API.
2. Replace or suppress every model-facing shell and filesystem operation that
   could otherwise execute against the host project.
3. Keep the harness, model credentials, conversation state, and harness-global
   integrations on the host.
4. Prevent project-controlled plugins, commands, formatters, LSP servers, MCP
   servers, hooks, or equivalent executable configuration from running in the
   trusted host process.
5. Forward cancellation, timeouts, output limits, and path semantics through
   the shared manager protocol.
6. Support create, resume, fork or replacement where applicable, shutdown, and
   lazy first-tool recovery without changing sandbox identity unexpectedly.
7. Expose manager-owned seed, export, VS Code intervention, and deletion
   actions.
8. Pass a per-supported-version conformance suite covering every enabled tool
   and non-tool host filesystem or process path.

### Candidate differences

| Area | Pi MVP track | OpenCode MVP track |
|---|---|---|
| Adapter | Explicitly loaded TypeScript extension | TypeScript plugin |
| Guest-local replacement | Built-in operation interfaces plus direct `grep` | Same-name plugin tools |
| Replacement set | `read`, `write`, `edit`, `bash`, `grep`, `find`, `ls` | `bash`, `read`, `write`, `edit`, `grep`, `glob`, `apply_patch` |
| Interactive user shell | Handle `user_bash` and prevent default host execution | Audit and redirect or disable every separate user-shell path |
| Caller-key convention | `pi:<workspace-digest>:<session-id>` | `opencode:<workspace-digest>:<session-id>` |
| Project safety | Explicit extension only; disable project resource discovery with CLI flags and trust controls | Set `OPENCODE_DISABLE_PROJECT_CONFIG=true`; disable native LSP, formatters, and other unredirected local execution |
| LSP | No reviewed Pi core LSP surface to redirect | Native client is directory-scoped and uses host files/paths, so disable it for MVP |
| MCP | No core MCP; extension-owned sandbox MCP is post-MVP | Harness-global MCP may stay host-side; native sandbox MCP is unsafe across same-directory sessions and is post-MVP |
| Dynamic tools | Extension can register tools and refresh the active catalog | Public plugin tools are directory-scoped; session-aware dynamic tools need a source change or compatibility fallback |
| Primary compatibility risk | Pi operation interfaces or lifecycle events change | OpenCode built-in schemas, precedence, or hidden host-I/O paths change |

### Selection gate

The choice is required before the productized harness vertical slice. The
decision should use a pinned-version spike and recorded pass/fail evidence for:

1. Tool replacement fidelity and normal model-facing result rendering.
2. Complete prevention of unintended host shell, file, formatter, LSP, MCP, and
   helper-process execution.
3. Guest-plane isolation for two sessions opened from one host project.
4. Create, resume, fork or replacement, restart, and shutdown lifecycle
   behavior.
5. Cancellation and interactive command behavior.
6. Packaging, launch, and upgrade compatibility.

Selection of one track moves the other out of the MVP. Shared manager and guest
contracts must not contain Pi- or OpenCode-specific types.

The management API itself does not know about harness sessions or these
candidate differences. Harness adapters are expected to be its primary clients,
but the CLI and other authorized clients can invoke the same sandbox operations
directly. The manager treats the adapter-provided caller key as an opaque value
scoped to the authenticated operating-system user. It is a lookup handle, not a
credential or authorization token.

All management clients under one UID are assumed cooperative and share
authority over that user's sandboxes. An exact caller-key collision aliases the
existing sandbox. The manager does not track creating-client ownership or
protect one same-user harness, adapter, or command from another. Harness prefixes
such as `pi:` and `opencode:` are adapter conventions for reducing accidental
confusion, not enforced namespaces.

The CLI is not an adapter and has no caller-key prefix. It is a general
management interface that can list and operate all sandboxes owned by the
invoking UID, including those created through any harness adapter. It targets a
sandbox by Tiny Cosmos sandbox identifier or exact caller key and forwards an
explicit key unchanged when creating or resuming.

## Pi

### Extension loading

Pi loads TypeScript and JavaScript extensions through `jiti`. Extensions receive
the Pi extension API and register tools, commands, providers, event handlers,
renderers, and other session behavior.

The following CLI controls affect extension and resource discovery:

| Option | Documented behavior |
|---|---|
| `--no-extensions` | Disables extension discovery; explicitly supplied `-e` extension paths still load |
| `--no-builtin-tools` | Disables built-in tools while retaining extension and custom tools |
| `--tools` | Applies an allowlist to tools |
| `--exclude-tools` | Applies a denylist to tools |
| `--no-skills` | Disables skill discovery and loading |
| `--no-prompt-templates` | Disables prompt-template discovery and loading |
| `--no-themes` | Disables theme discovery and loading |
| `--no-context-files` | Disables `AGENTS.md` and `CLAUDE.md` discovery and loading |
| `--no-approve` | Declines project-local resources for the process |

Pi resolves `project_trust` before project resources load.

### Tool replacement

Calling `registerTool()` with a built-in tool name replaces the built-in
registration for that name.

The Gondolin example replaces:

- `read`
- `write`
- `edit`
- `bash`
- `grep`
- `find`
- `ls`

Pi exports operation interfaces used by its built-in tool constructors:

- `ReadOperations`
- `WriteOperations`
- `EditOperations`
- `BashOperations`
- `FindOperations`
- `LsOperations`

The Gondolin extension constructs tools with Pi's built-in names, schemas,
metadata, and rendering. It supplies VM-backed operation implementations.
`grep` is implemented directly against the VM filesystem.

A Tiny Cosmos Pi adapter can implement these operation interfaces with manager
protocol calls. This preserves Pi's built-in tool names and result rendering
while changing the process and filesystem backend.

### User shell

Pi emits `user_bash` for interactive `!` and `!!` commands. An extension can
return replacement output and prevent the default command execution.

The Gondolin example handles `user_bash`, runs the command in its VM, and
returns output and exit status to Pi.

### Session identity and lifecycle

The Pi CLI accepts `--session-id <id>` and uses that exact project session
identifier, creating the session when it does not exist.

Extensions receive a read-only session manager with:

- `getSessionId()`
- `getSessionFile()`
- `getCwd()`
- `getHeader()`
- `getEntries()`
- `getBranch()`

Pi emits `session_start` for startup, reload, new, resume, and fork operations.
For session replacement, Pi emits `session_shutdown` for the old extension
runtime, recreates the extension runtime for the replacement session, and then
emits `session_start`.

The Pi adapter derives a stable opaque caller key from the Pi session identifier
and relevant workspace context using the `pi:` prefix. The manager stores only
that opaque value.
`session_start` and `session_shutdown` provide adapter lifecycle events for
lease acquisition and release.

### Dynamic tools and MCP

Pi core does not include MCP. Its documentation identifies MCP as extension
functionality.

An extension can act as an MCP client and register discovered MCP tools as Pi
tools. Calling `registerTool()` after startup refreshes the active tool catalog.
Pi also exposes `setActiveTools()` for changing the enabled set.

### Workspace behavior in the Gondolin example

The Gondolin example uses `RealFSProvider` to expose a host directory to the
micro-VM at `/workspace`. Tiny Cosmos uses host-to-guest seed transfer and
manager-controlled export instead of that mount behavior.

### Pi adapter components

The described mapping contains:

- Explicitly loaded Pi extension
- Managed launch profile disabling discovered resources and unredirected
  built-in tools
- Opaque caller-key derivation from Pi session and workspace context
- Manager client
- Pi filesystem operation implementations
- Pi command operation implementation
- `grep` implementation using manager filesystem calls
- `user_bash` event handler
- Sandbox create, resume, suspend, and release handlers
- Host seed and explicit export commands
- Host-path to guest-path translation
- Optional extension-owned MCP client for sandbox-local MCP

## OpenCode

### Plugin loading

OpenCode plugins are TypeScript or JavaScript modules. Plugin-defined tools are
added to the OpenCode tool registry.

A plugin tool registered with the same name as a built-in tool takes precedence
over that built-in registration.

The Tiny Cosmos MVP plan names replacements for:

- `bash`
- `read`
- `write`
- `edit`
- `grep`
- `glob`
- `apply_patch`

Each replacement retains the corresponding OpenCode argument and result shape
and forwards execution to the Tiny Cosmos manager.

### Session context

Plugin tool context contains:

- `sessionID`
- `directory`
- `worktree`

The OpenCode adapter derives a stable opaque caller key from the OpenCode
session identifier and canonical host workspace identity using the `opencode:`
prefix. The manager stores only that opaque value. Session events can initialize
the sandbox. The first guest-local tool call can also create or resume it.

### Project configuration

The documented Tiny Cosmos launch environment contains:

```text
OPENCODE_DISABLE_PROJECT_CONFIG=true
```

The plugin configuration hook can set `lsp` and `formatter` to `false`.
Project-provided OpenCode plugins, local MCP commands, LSP commands, formatters,
and related executable configuration are then not loaded from project
configuration by the host OpenCode process.

### LSP

OpenCode's native LSP implementation:

- Caches client state in OpenCode instance state keyed by workspace directory
- Reads files through the host filesystem
- Sends host paths to the language server
- Is called by built-in file tools for diagnostics and document updates

Replacing only the language-server process with a guest stdio relay does not
change the host file reads or host paths used by the native client.

A plugin-defined `lsp` tool can forward model-facing LSP operations to the
manager and guest. This tool does not populate OpenCode's native LSP status or
reuse its internal LSP client.

### MCP

OpenCode includes local stdio and remote HTTP or SSE MCP clients.

Native MCP client and catalog state are keyed by OpenCode workspace directory,
not by OpenCode session ID. Two sessions using one OpenCode workspace directory
share that native MCP state.

OpenCode rebuilds the provider tool map before every model step. An MCP
`tools/list_changed` notification can affect the following model step.

The public plugin API at `v1.17.4` collects plugin tools in
directory-scoped state. It does not expose a session-aware callback for
registering a different set of typed plugin tools for each session during tool
resolution.

The researched session-specific MCP representations are:

- One OpenCode workspace identity per sandbox
- A manager-owned MCP proxy exporting a namespaced union catalog
- A generic plugin tool that accepts a sandbox-local MCP tool name and
  arguments
- A source change adding a session-aware plugin tool-provider callback during
  per-step tool resolution

### Other host process and filesystem paths

The Tiny Cosmos plan includes an audit of:

- User-entered shell paths
- File attachment and explicit host-file paths
- Snapshots and change tracking
- Helper processes
- Formatters
- LSP processes and filesystem access
- MCP process startup
- Every enabled built-in and plugin tool

### OpenCode adapter components

The documented mapping contains:

- TypeScript plugin
- Opaque caller-key derivation from OpenCode session and workspace context
- Manager client
- Seven replacement guest-local tools
- Configuration hook disabling native LSP and formatters
- Launcher environment disabling project configuration
- Session lifecycle handling and lazy first-tool initialization
- Host seed and explicit export commands
- Path translation
- Per-supported-version tool schema and host-I/O conformance tests

## Claude Code

### CLI controls

Claude Code exposes the following CLI controls:

| Option | Documented behavior |
|---|---|
| `--tools ""` | Disables all built-in tools |
| `--tools "Bash,Edit,Read"` | Restricts the enabled built-in tools |
| `--mcp-config` | Loads MCP servers from supplied JSON files or strings |
| `--strict-mcp-config` | Uses only MCP servers from `--mcp-config` |
| `--disallowedTools` | Removes tools matching supplied permission-rule patterns; `"mcp__*"` matches MCP tools |
| `--setting-sources` | Selects `user`, `project`, and `local` setting sources |
| `--settings` | Supplies settings that override the same keys from settings files for the session |
| `--bare` | Skips automatic hooks, skills, plugins, MCP servers, auto memory, and `CLAUDE.md`; Bash and file tools remain enabled |
| `--safe-mode` | Disables customizations including MCP and LSP servers while retaining built-in tools and permissions |
| `--plugin-dir` | Loads an explicit plugin directory or archive for the session |
| `--disable-slash-commands` | Disables all skills and commands for the session |
| `--session-id` | Uses an exact valid UUID for the conversation |
| `--resume` | Resumes by session ID or name |
| `--fork-session` | Creates a new session ID when resuming instead of reusing the original |

`--tools` affects built-in tools. MCP tools are controlled separately with MCP
configuration or `--disallowedTools`.

### Settings precedence

Claude Code documents this settings precedence, from highest to lowest:

1. Managed settings
2. Command-line arguments
3. Local project settings in `.claude/settings.local.json`
4. Shared project settings in `.claude/settings.json`
5. User settings in `~/.claude/settings.json`

Project-scoped resources include settings, subagents, MCP configuration,
plugins, and `CLAUDE.md` files. `--setting-sources` controls whether the user,
project, and local settings layers are loaded. `--bare` and `--safe-mode`
disable different sets of customization resources.

### Agent SDK custom tools

The Claude Agent SDK defines custom tools with `tool()` in TypeScript or
`@tool` in Python. The tools are wrapped in `createSdkMcpServer` or
`create_sdk_mcp_server` and passed through the SDK `mcpServers` option.

The SDK MCP server runs in the embedding application process rather than as a
separate process.

Custom tool names are exposed with MCP names such as:

```text
mcp__server_name__tool_name
```

The SDK can disable built-in tools while retaining custom MCP tools. Custom
tools do not replace the built-in `Read`, `Edit`, `Write`, or `Bash` names.

### Session identity

The CLI accepts an exact UUID through `--session-id`. It resumes a stored
session through `--resume`.

The Agent SDK returns `session_id` in its result message. Passing that ID in the
SDK `resume` option restores the conversation context.

### Manager tool mappings

Two documented Claude Code surfaces can carry Tiny Cosmos operations:

| Surface | Tool transport | Built-in control |
|---|---|---|
| Claude Code CLI | Manager-owned MCP server supplied through `--mcp-config` | `--tools ""` removes built-ins; `--strict-mcp-config` limits MCP sources |
| Claude Agent SDK | In-process SDK MCP server | SDK tool options remove built-ins while MCP tools remain separately registered |

In both mappings, model-facing guest operations use MCP tool names rather than
the built-in Claude Code file and shell tool names. Diff rendering, checkpoint
integration, and any built-in-specific file-edit display are not supplied by
the MCP naming mechanism itself.

### Claude Code adapter components

The described CLI mapping contains:

- Claude Code launcher arguments
- Opaque caller-key derivation from the exact session UUID and project context
- Manager-owned MCP server
- Built-in tool suppression
- Strict explicit MCP configuration
- Selected settings sources or bare-mode configuration
- Host seed and explicit export commands
- Path translation in MCP handlers

The described Agent SDK mapping contains:

- SDK host application
- Opaque caller-key derivation from the SDK session ID and project context
- In-process SDK MCP server
- Manager client
- Built-in tool suppression through SDK options
- Host seed and explicit export operations
- Path translation in custom tool handlers

## Gemini CLI

### Product notice

On June 13, 2026, the official Gemini CLI documentation displayed a notice that
Gemini CLI will be replaced by Antigravity CLI on June 18, 2026 for unpaid-tier
and Google One users.

### Extensions

Gemini CLI loads installed extensions from:

```text
<home>/.gemini/extensions
```

Each extension has a `gemini-extension.json` manifest. The manifest can define:

- MCP servers
- A context file
- Excluded tools
- Settings
- A plan directory

Extensions can also contain commands, hooks, skills, subagents, and policy
rules.

Extension MCP servers load at startup. When an extension and `settings.json`
define the same MCP server name, the settings-defined server takes precedence.
Workspace configuration takes precedence when extension configurations are
merged. Extension management changes take effect after the CLI session
restarts.

Sensitive environment variables are not passed to extensions or MCP servers by
default. Extension settings declare environment variables that the extension
receives.

### Built-in tools

The official tool references list these guest-local built-ins:

- `run_shell_command`
- `glob`
- `grep_search`
- `list_directory`
- `read_file`
- `read_many_files`
- `replace`
- `write_file`

The `tools.core` setting acts as an allowlist for built-in tools. If it is set,
only the listed built-ins are enabled.

An extension manifest `excludeTools` array removes named tools from the model.
MCP server configuration also supports server-specific `includeTools` and
`excludeTools`.

Interactive `@` file inclusion invokes `read_many_files`. Interactive `!`
commands invoke `run_shell_command`.

### MCP and ACP

Gemini CLI discovers MCP tools from configured stdio, SSE, and Streamable HTTP
servers and registers them in its tool registry.

ACP mode starts with:

```text
gemini --acp
```

ACP uses JSON-RPC 2.0 over stdio. During ACP initialization, the client can
provide connection details for its own MCP server. Gemini CLI connects to that
server, discovers its tools, and exposes them to the model.

A Tiny Cosmos ACP client can therefore expose manager-backed MCP tools through
the documented ACP initialization mechanism. A Gemini extension or explicit
settings file can expose the same manager MCP server through Gemini CLI's MCP
configuration.

### Hooks

Gemini CLI hook events include:

- `SessionStart`
- `SessionEnd`
- `BeforeAgent`
- `AfterAgent`
- `BeforeModel`
- `AfterModel`
- `BeforeToolSelection`
- `BeforeTool`
- `AfterTool`
- `PreCompress`
- `Notification`

`BeforeToolSelection` can set tool mode to `AUTO`, `ANY`, or `NONE` and can
supply an allowed function-name list.

`BeforeTool` can deny a tool call or merge replacement fields into its input. It
does not return a replacement execution result.

`AfterTool` runs after the selected tool executes. It can hide or replace the
result visible to the model, add context, or request a tail tool call.

`SessionStart` fires on startup, resume, and clear. The CLI does not wait for
`SessionEnd` to complete.

### Session identity and storage

Gemini CLI automatically records conversation history and tool executions.
Sessions are stored under:

```text
~/.gemini/tmp/<project_hash>/chats/
```

The project hash is based on the project root. Session history is
project-specific.

`--resume` supports:

- The most recent session
- A displayed session index
- A full session UUID

### Trusted Folders

Trusted Folders is disabled by default. When enabled, an untrusted folder runs
with these documented restrictions:

- Workspace settings are ignored.
- Project `.env` files are ignored.
- Extension management is restricted.
- Every tool call prompts.
- Automatic memory loading is disabled.
- MCP servers do not connect.
- Custom commands are not loaded.

In headless mode, an untrusted workspace with Trusted Folders enabled causes a
`FatalUntrustedWorkspaceError`. `--skip-trust` and
`GEMINI_CLI_TRUST_WORKSPACE=true` trust the workspace for that process.

Because untrusted mode disables all MCP connections, a Tiny Cosmos mapping that
uses manager MCP tools uses either a trusted manager-owned working directory or
a process-level trust override. Project discovery then occurs relative to the
working directory supplied to Gemini CLI.

### Gemini CLI adapter components

The described extension mapping contains:

- Gemini extension manifest
- Manager MCP server configuration
- Built-in tool exclusion
- Opaque caller-key derivation from the session UUID and project context
- Session lifecycle hooks
- Host seed and explicit export commands
- Path translation in MCP handlers
- Working-directory and trust configuration

The described ACP mapping contains:

- ACP client process
- JSON-RPC session control
- Client-provided manager MCP server
- Opaque caller-key derivation from the session UUID and project context
- Built-in tool configuration
- Host seed and explicit export operations
- Path translation in MCP handlers

## Codex

### App-server protocol

`codex app-server` is the protocol used by Codex rich clients. It uses
bidirectional JSON-RPC 2.0. The default transport is newline-delimited JSON over
stdio. The reviewed documentation also lists WebSocket and Unix-socket
transports, with WebSocket marked experimental and unsupported.

The app-server API exposes:

- Thread start, resume, fork, read, list, archive, and unsubscribe operations
- Turn start, steer, interrupt, and completion events
- Streaming item lifecycle and output notifications
- Approvals
- Configuration
- MCP state
- Filesystem and process client APIs
- Command execution APIs

### Session identity and lifecycle

`thread/start` creates a thread. `thread/resume` restores an existing thread by
ID. `thread/fork` copies stored history into a new thread ID.

`thread.sessionId` identifies the live session-tree root. A root thread uses its
own thread ID as the session ID. A fork receives a new thread ID and retains the
root thread's session ID.

App-server automatically subscribes the client to events for a started or
resumed thread. Thread and turn notifications carry thread and turn IDs.

### Dynamic tools

The experimental `dynamicTools` field on `thread/start` accepts client-supplied
tool specifications. Experimental app-server APIs require the client to enable
the experimental capability during initialization.

Codex sends dynamic tool calls to the app-server client with the thread ID,
turn ID, call ID, tool name, and arguments. The client returns content items and
a success value.

Dynamic tool definitions are persisted in thread rollout metadata. On
`thread/resume`, Codex restores the persisted definitions when the resume
request does not supply replacements.

Codex registers core tool sources before extension and dynamic tool sources.
Extension tool names already present in the registry are skipped. During
model-visible tool-spec construction, later duplicate names are omitted after
the first registration. Dynamic tools therefore use names distinct from
registered core tools.

### Environments and exec-server

The experimental `environments` field on `thread/start` has these source-defined
semantics:

- Omitted: select the default environment when environment access is enabled
- Empty list: disable environment access for turns without an override
- Non-empty list: select the first listed environment for the current turn

When no environment is available, Codex does not add model-facing shell tools,
`apply_patch`, or `view_image`.

Codex `EnvironmentManager` supports local and remote environments. A remote
environment uses:

- A remote process backend
- A remote filesystem backend
- A remote HTTP client
- Lazy connection to an exec-server transport

`CODEX_EXEC_SERVER_URL=none` disables default environment access. Codex can also
load named environments from `CODEX_HOME/environments.toml`.

The experimental app-server environment API can add or replace a named remote
environment with an exec-server endpoint. The remote environment routes Codex
process and filesystem operations through the exec-server protocol.

A Tiny Cosmos exec-server adapter can translate that protocol to manager calls.
In this mapping, Codex retains its shell and patch tool names while the selected
environment supplies remote process and filesystem backends.

### App-server dynamic-tool mapping

An alternative mapping starts a thread with no environment and supplies
manager-backed dynamic tools. In that mapping:

- Environment-backed core shell, patch, and image tools are absent.
- Manager-backed tools use distinct dynamic tool names.
- Tool execution requests arrive at the app-server client.
- The app-server client forwards requests to the Tiny Cosmos manager.

### User-entered shell

`thread/shellCommand` is documented as a user-initiated shell command API. It
runs outside the Codex sandbox with full host access and does not inherit the
thread sandbox policy.

A Tiny Cosmos client that treats user-entered shell as guest-local does not use
`thread/shellCommand` for that operation. It exposes a separate client action
that calls the manager.

### Working directory and project resources

`thread/start` and `turn/start` accept a `cwd`. App-server also accepts
configuration and sandbox-policy overrides. Codex resolves workspace and
project context from the active working directory.

Using a manager-owned host directory as `cwd` causes host-side project discovery
to occur under that directory. The original host project can remain a separate
seed source supplied to the Tiny Cosmos manager.

### Codex adapter components

The remote-environment mapping contains:

- App-server client
- Opaque caller-key derivation from Codex thread, session-tree, and workspace
  context
- Remote environment registration
- Exec-server protocol adapter
- Manager process and filesystem forwarding
- Manager-owned host working directory
- Host seed and explicit export commands
- User-entered guest shell client action
- Per-version app-server schema generation

The dynamic-tool mapping contains:

- App-server client
- Thread start with an empty environment list
- Dynamic manager-backed tools
- Dynamic tool call and result handling
- Opaque caller-key derivation from Codex thread, session-tree, and workspace
  context
- Manager-owned host working directory
- Host seed and explicit export commands
- User-entered guest shell client action
- Per-version app-server schema generation

## Tool-name behavior

| Harness | Fact |
|---|---|
| Pi | Same-name `registerTool()` registration replaces a built-in tool |
| OpenCode | Same-name plugin tool registration takes precedence over a built-in tool |
| Claude Code | Custom Agent SDK tools are MCP tools with `mcp__...` names; built-ins are disabled separately |
| Gemini CLI | Built-ins can be excluded or omitted from `tools.core`; manager tools are separately registered MCP tools |
| Codex | Core names are registered first; colliding extension and dynamic names are skipped |

## Project-resource behavior

| Harness | Host-side project resource controls |
|---|---|
| Pi | CLI flags disable individual discovery classes; explicit `-e` paths still load; project trust runs before project resources |
| OpenCode | `OPENCODE_DISABLE_PROJECT_CONFIG=true` disables project configuration; the plugin config hook can set LSP and formatters to `false` |
| Claude Code | `--setting-sources`, `--bare`, and `--safe-mode` load different documented resource sets; explicit MCP and plugin inputs have separate flags |
| Gemini CLI | The working directory controls workspace discovery; Trusted Folders controls project resources; workspace configuration takes precedence over extension configuration |
| Codex | App-server `cwd` controls workspace discovery; app-server accepts configuration overrides; a separate host path can supply the Tiny Cosmos seed |

## Adapter caller-key inputs

These inputs remain inside each adapter. They are used to derive an opaque
caller key and are not fields in the management API. Each adapter adds a clear
client prefix.

| Client | Prefix | Client-owned key inputs |
|---|---|---|
| Pi | `pi:` | Pi session ID and working directory |
| OpenCode | `opencode:` | OpenCode `sessionID`, directory, and worktree |
| Claude Code | `claude-code:` | CLI or Agent SDK session UUID and selected project directory |
| Gemini CLI | `gemini:` | Session UUID and project-root-derived session scope |
| Codex | `codex:` | Thread ID, root `sessionId`, and app-server working directory |

## Manager protocol reuse

The following Tiny Cosmos manager operations are independent of the harness
adapter:

- List and inspect every sandbox owned by the authenticated UID
- Create or resume a sandbox using an optional opaque caller key
- Acquire, renew, and release an active lease
- Seed a guest workspace from a host source
- Execute a process with streaming output, cancellation, and PTY support
- Read, list, search, write, edit, and patch guest files
- Query guest metadata and health
- Suspend, resume, stop, and destroy a sandbox
- Export a patch, commit, archive, selected files, or artifacts
- Publish a port under manager policy
- Translate host presentation paths and guest execution paths
- Discover and call sandbox-local MCP servers
- Route guest LSP requests

Each harness adapter translates its tool schemas and event model to these
manager operations and derives an opaque caller key from its own session
semantics. The manager does not know or persist the source fields or their
meaning. The CLI can call these operations directly without a harness adapter.
The CLI can list all same-user sandboxes and address one by its Tiny Cosmos
identifier or by reusing another client's exact caller key.

## Cross-harness observable checks

The researched integrations expose different tool registries and host-side
features. The following checks produce observable integration results:

1. The model-visible tool catalog after adapter startup.
2. The execution location of every enabled shell and filesystem tool.
3. The execution location of interactive user-shell shortcuts.
4. The setting and project-resource sources loaded by the harness.
5. Session create, resume, fork, clear, and shutdown behavior.
6. Sandbox identity when two sessions use the same host project.
7. Cancellation and timeout propagation.
8. Host seed behavior and the absence of a continuous host mount.
9. Explicit export behavior.
10. Harness-global MCP availability on the host.
11. Sandbox-local MCP isolation by session.
12. Guest LSP path and filesystem behavior.
13. Behavior after harness, manager, broker, and VM restart.
14. Caller-key prefix clarity and stability across restart.
15. Same-user exact-key aliasing and cross-UID key separation.
16. CLI listing and debugging access to sandboxes created by each adapter.

## References

### Tiny Cosmos

- [Current plan.md](Current%20plan.md)
- [MVP technical plan.md](MVP%20technical%20plan.md)
- [OpenCode sandbox LSP and MCP feasibility.md](OpenCode%20sandbox%20LSP%20and%20MCP%20feasibility.md)

### Pi

- [Pi coding agent README at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/README.md)
- [Pi extension documentation at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/docs/extensions.md)
- [Pi CLI arguments at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/src/cli/args.ts)
- [Pi session manager at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/src/core/session-manager.ts)
- [Pi extension API types at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/src/core/extensions/types.ts)
- [Pi Gondolin extension at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/examples/extensions/gondolin/index.ts)
- [Pi tool override example at the reviewed commit](https://github.com/earendil-works/pi/blob/6f29450e1339b37582796775749de3957936cd8d/packages/coding-agent/examples/extensions/tool-override.ts)

### OpenCode

- [OpenCode plugin documentation](https://opencode.ai/docs/plugins/)
- [OpenCode built-in tools](https://opencode.ai/docs/tools/)
- [OpenCode LSP documentation](https://opencode.ai/docs/lsp/)
- [OpenCode MCP documentation](https://opencode.ai/docs/mcp-servers/)
- [OpenCode plugin API at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/plugin/src/index.ts)
- [OpenCode LSP runtime at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/lsp/lsp.ts)
- [OpenCode LSP client at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/lsp/client.ts)
- [OpenCode MCP runtime at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/mcp/index.ts)
- [OpenCode MCP catalog at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/mcp/catalog.ts)
- [OpenCode per-step tool resolution at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/tools.ts)
- [OpenCode session loop at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/prompt.ts)
- [OpenCode provider tool filtering at v1.17.4](https://github.com/anomalyco/opencode/blob/v1.17.4/packages/opencode/src/session/llm/request.ts)

### Claude Code

- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-reference)
- [Claude Code settings](https://code.claude.com/docs/en/settings)
- [Claude Agent SDK custom tools](https://code.claude.com/docs/en/agent-sdk/custom-tools)
- [Claude Agent SDK sessions](https://code.claude.com/docs/en/agent-sdk/sessions)
- [Claude Agent SDK TypeScript reference](https://platform.claude.com/docs/en/agent-sdk/typescript)
- [Claude Code repository snapshot](https://github.com/anthropics/claude-code/tree/ca9f6045fc90c8244f9e787fb57d54b380f9a27c)

### Gemini CLI

- [Gemini CLI extension reference](https://geminicli.com/docs/extensions/reference/)
- [Gemini CLI configuration reference](https://geminicli.com/docs/reference/configuration/)
- [Gemini CLI tools reference](https://geminicli.com/docs/reference/tools/)
- [Gemini CLI filesystem tools](https://geminicli.com/docs/tools/file-system/)
- [Gemini CLI shell tool](https://geminicli.com/docs/tools/shell/)
- [Gemini CLI MCP servers](https://geminicli.com/docs/tools/mcp-server/)
- [Gemini CLI hooks reference](https://geminicli.com/docs/hooks/reference/)
- [Gemini CLI session management](https://geminicli.com/docs/cli/session-management/)
- [Gemini CLI headless mode](https://geminicli.com/docs/cli/headless/)
- [Gemini CLI Trusted Folders](https://geminicli.com/docs/cli/trusted-folders/)
- [Gemini CLI ACP mode](https://geminicli.com/docs/cli/acp-mode/)
- [Gemini CLI repository snapshot](https://github.com/google-gemini/gemini-cli/tree/9e5599c323f12df36672794332df5ae16e6125d5)

### Codex

- [Codex app-server documentation](https://developers.openai.com/codex/app-server)
- [Codex app-server README at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/app-server/README.md)
- [Codex app-server thread protocol at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/app-server-protocol/src/protocol/v2/thread.rs)
- [Codex app-server environment protocol at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/app-server-protocol/src/protocol/v2/environment.rs)
- [Codex tool planning at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/core/src/tools/spec_plan.rs)
- [Codex tool configuration at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/tools/src/tool_config.rs)
- [Codex environment manager at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/exec-server/src/environment.rs)
- [Codex exec-server README at the reviewed commit](https://github.com/openai/codex/blob/f297b9f07de10c7d8b9ed284b674d06cc5ff7723/codex-rs/exec-server/README.md)
