# VM Image Preparation

## Status

This post-MVP design note explores how Tiny Cosmos can provide a simple
first-run experience without maintaining every possible combination of
operating system, language, toolchain, framework, dependency set, and future
non-development workload.

The proposed direction is layered images with agent-assisted local template
creation as an adaptive fallback. Exact formats, invalidation rules, and
checkpoint mechanisms remain open.

The Linux MVP ships one authenticated Ubuntu base image only. Capability
templates, project templates, environment detection, and agent-assisted
preparation described below are explicitly post-MVP.

## Design pressures

Image preparation must balance:

- A user experience that does not require manual VM administration
- Fast sandbox startup after initial preparation
- A very large and changing set of development toolchains
- Future workloads that are not necessarily software development
- Support for both Linux and best-effort Windows guests
- Reasonable image download, disk, and maintenance costs
- Reproducibility and explainability
- Safe handling of source code, credentials, and thread-specific state
- Operation without internet access once the necessary local state exists

Tiny Cosmos cannot realistically ship a prebuilt image for every useful
combination. A completely empty base also creates a poor first-run experience
and repeats expensive setup for every thread.

## Layered image model

### Trusted base image

A trusted base is the smallest supported, bootable guest environment maintained
by Tiny Cosmos.

It should contain:

- The guest supervisor and its required transport
- Basic shell and filesystem utilities
- Git and archive/file-transfer utilities where appropriate
- Certificate roots
- Networking and time synchronization support
- Bootstrap and cleanup machinery
- Enough diagnostics to recover from failed setup

The number of official bases should remain deliberately small, organized around
supported guest OS and version combinations rather than language ecosystems.

Base images are versioned, signed or otherwise authenticated, and treated as
immutable inputs. A sandbox receives a copy-on-write child rather than modifying
the base.

### Capability template

A capability template adds a reusable workload or toolchain layer, such as:

- Rust
- Node.js
- Python
- JVM tooling
- .NET
- MSVC Build Tools and Windows SDK
- Docker or Kubernetes tooling
- A database or other non-development runtime

Capability templates may eventually be official, community-provided,
user-created, or agent-created. Tiny Cosmos should maintain only a modest set of
high-value official templates rather than a combinatorial catalog.

Templates must identify their base image and compatibility requirements. They
should not contain a project checkout or thread-specific mutable state.

### Project template

A project template is a locally cached environment prepared for a particular
project or environment definition.

It may contain:

- Required language and compiler versions
- System libraries
- Package-manager caches
- Restored dependencies
- Built helper tools
- Project-specific services or runtime prerequisites

It should ideally not contain the mutable project checkout. Normal thread
creation clones the project template and then imports an independent workspace.

Separating environment and workspace state enables multiple threads to share the
expensive preparation result without sharing source mutations.

## Template resolution

When creating a sandbox, the integration should resolve an environment in this
general order:

1. Use an explicitly selected template when one is provided.
2. Detect known environment declarations and choose a compatible local template.
3. Reuse a valid project template created from the same relevant inputs.
4. Run agent-assisted template preparation.
5. Fall back to the trusted base and allow the normal thread agent to configure
   its own disposable sandbox.

Potential environment signals include:

- Nix flakes
- Dev Container metadata
- `mise`, `asdf`, or language-version files
- Package manifests and lockfiles
- Toolchain files
- Project bootstrap scripts
- CI workflow definitions
- Existing Tiny Cosmos environment metadata

Detection should guide template selection and agent context. Tiny Cosmos should
not attempt to become a complete parser and package manager for every ecosystem.

## Agent-assisted preparation

An agent is a useful adaptive fallback when deterministic metadata does not
fully describe the required environment. Preparation should be a distinct
template-build operation, not hidden activity inside a normal development
thread.

### Proposed flow

1. Select a trusted base or compatible capability template.
2. Start a disposable builder VM.
3. Import only the project and environment inputs needed for setup.
4. Invoke a setup agent with a focused environment-preparation task.
5. Allow it to install tools and dependencies inside the builder.
6. Run declared or inferred validation commands.
7. Remove the project checkout and known transient or sensitive state.
8. Shut the builder down cleanly.
9. Capture its disk as an immutable, content-addressed local template.
10. Create normal thread sandboxes as copy-on-write children of that template.

The harness may invoke this flow automatically and present it simply as
"Preparing project environment." It should still disclose that model usage,
downloads, and a potentially lengthy setup operation are occurring.

### Setup-agent capabilities

The setup agent may have:

- Root or administrator access inside the disposable builder
- Internet access during the preparation phase
- Access to the minimum project/environment inputs needed for detection and
  validation
- The normal guest-local shell and file tools

It should not receive by default:

- Arbitrary host filesystem access
- Cross-thread memory
- General MCP tools or remote service integrations
- Long-lived user credentials
- Authority over the host sandbox manager
- Access to unrelated sandboxes or templates

The build should have configurable time, CPU, memory, disk, and download limits.
Boundary-crossing capabilities remain subject to harness and manager policy.

### Transparent invocation

Automatic invocation should be coordinated by the harness integration:

1. Sandbox creation determines that no suitable local template exists.
2. The harness starts a separate preparation turn or narrowly scoped internal
   agent task.
3. The preparation receives an explicit goal, relevant environment files, and
   validation expectations.
4. Progress is surfaced through a simple readiness state.
5. Successful output is sanitized and checkpointed.
6. The original user thread starts or resumes against a clone of the result.

The setup task should not inherit the full conversation merely for convenience.
It should receive a concise generated brief so unrelated user data and
cross-thread context do not leak into the template or influence setup.

Whether this is represented as a visible harness thread, a child task, or a
special system operation depends on harness capabilities. The important
properties are isolation, explicit accounting, bounded authority, and a clear
audit trail.

## Checkpointing and sanitization

Template state, workspace state, and runtime state must remain distinct:

- **Template state:** reusable tools, dependencies, and caches
- **Workspace state:** thread-specific source and edits
- **Runtime state:** running processes, containers, logs, sockets, and temporary
  data

Checkpointing an arbitrary development sandbox as a reusable template risks
preserving source code, credentials, machine identity, running services, shell
history, and thread-specific debris.

Before capture, the preparation flow should:

- Remove the imported project checkout or replace it with an empty workspace
- Stop services and containers unless they are intentionally part of the
  template contract
- Clear temporary files, logs, shell history, package-manager credentials, and
  setup-agent artifacts
- Remove injected SSH keys, tokens, and transient certificates
- Regenerate or clear guest identity that must be unique per clone
- Flush filesystem writes and shut down cleanly
- Record what sanitization was attempted and whether it succeeded

Known injected credentials should be tracked by the manager and scrubbed
explicitly. Scanning for unknown secrets can provide defense in depth, but
cannot prove that a template is free of sensitive data.

Templates created from sensitive source may need to remain private to the local
user even after cleanup. Export or sharing should be a separate, explicit
operation with stronger validation.

## Provenance

Every generated template should include machine-readable provenance:

- Template identifier and creation time
- Parent base/template identity and digest
- Guest OS and architecture
- Relevant environment input paths and hashes
- Requested capabilities
- Setup recipe, command log, or equivalent action summary
- Setup agent and model identity where available
- Network use and downloaded artifact information where practical
- Validation commands and results
- Sanitization actions and results
- Compatibility and invalidation metadata

Provenance supports debugging, user trust, cache reuse, and future deterministic
rebuilds. It should not contain secret values or unnecessary source content.

## Offline behavior

The realistic offline goal is:

> Once a resolved template and its required artifacts exist locally, Tiny Cosmos
> can create new sandboxes from it without internet access.

This does not guarantee that every future revision of the project can build
offline. Project changes may introduce new tools or dependencies.

Agent-assisted preparation initially requires:

- A locally available inference model, or network access to a remote model
- Locally available package artifacts, or network access to download them
- A locally available base image

To improve offline support over time, Tiny Cosmos may retain content-addressed
downloads, package caches, base images, templates, and preparation recipes.
Later rebuilds could first attempt a network-disabled preparation using these
local inputs, then request network access only if validation fails.

Offline capability should be reported as a property of a particular resolved
template and artifact set, not as a universal property of the project.

## Invalidation and reuse

A project template may become stale when relevant inputs change, including:

- Base image or capability template
- Toolchain/version declarations
- Package manifests or lockfiles
- Environment and bootstrap scripts
- Selected guest OS or architecture
- Requested capabilities
- Preparation or sanitization implementation

Invalidation should usually mean "do not select automatically without
revalidation," not immediate deletion. Existing sandboxes and templates may
remain useful for old revisions or offline work.

Relevant inputs should be hashed into a resolution key. Because it is difficult
to infer every important file, users and agents need a way to mark additional
inputs or explicitly request a rebuild.

Template reuse should favor explainability over a falsely precise compatibility
guess. A fast validation command may allow reuse even when the resolution key is
not an exact match.

## Failure and fallback

Agent preparation will sometimes choose the wrong tools, fail validation,
exhaust resources, or require interactive credentials.

On failure, the product should:

- Preserve useful logs and provenance
- Explain the failed validation at a user-appropriate level
- Allow the normal thread to start from the base or partial environment
- Permit the thread agent to continue setup in its disposable sandbox
- Avoid publishing a failed build as a reusable template by default
- Offer an explicit retry with changed inputs or permissions

The image system must not make sandbox creation depend on successful autonomous
environment inference.

## Recommended post-MVP initial scope

After the single-image Linux MVP is stable:

1. Build on the authenticated trusted base shipped by the Linux MVP.
2. Support one simple local template format and copy-on-write cloning.
3. Detect a small set of common environment files.
4. Provide an agent-assisted builder with root, bounded internet access, minimal
   host inputs, validation, and basic sanitization.
5. Record provenance and cache the resulting project template.
6. Support explicit template rebuild and deletion.
7. Fall back cleanly to per-thread setup when preparation fails.

Capability-template distribution, community templates, sophisticated secret
scanning, and broad deterministic environment support can follow after the
basic preparation-to-clone loop is proven.

Windows should use the same conceptual layers but may require different capture,
specialization, servicing, reboot, and sanitization mechanisms. It should not
block validation of the Linux model.

## Open questions

- Which environment declarations are detected in the first release?
- Is agent preparation a visible harness thread, an internal child task, or a
  manager-owned operation mediated by the harness?
- How is model usage disclosed and attributed?
- What project content does the setup agent receive, and is it mounted,
  imported, or represented through selected files?
- How are validation commands selected and prevented from hanging indefinitely?
- What secrets can the manager reliably track and scrub before capture?
- Can package downloads be intercepted into a reusable content-addressed cache?
- What image and snapshot formats work across the selected hypervisors?
- How portable are templates across host kernel, CPU, hypervisor, and manager
  versions?
- Should project templates preserve dependency caches whose paths embed the
  removed workspace location?
- How should trusted, local-private, community, and official templates be
  distinguished?
- What is the user experience when preparation needs authentication or a
  license-protected dependency?
- How are templates garbage-collected without breaking offline expectations?
- Can preparation be reproduced from provenance alone, or is the initial agent
  result inherently an opaque local artifact?
