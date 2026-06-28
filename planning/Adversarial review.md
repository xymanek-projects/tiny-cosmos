# Tiny Cosmos: Adversarial Review

## Purpose

This review is split by delivery stage:

- [MVP adversarial proof.md](MVP%20adversarial%20proof.md) defines what the MVP
  must prove about control-plane configuration, access, and handoff behavior.
- [Production security requirements.md](Production%20security%20requirements.md)
  defines security-orchestration work deferred until a full production release.

The documents reflect decisions made on June 13, 2026. No implementation exists
yet, so all controls remain design requirements.

## Agreed trust model

Tiny Cosmos is not itself a security or isolation mechanism. It is a control
plane and orchestrator for dedicated enforcement components such as
Firecracker/KVM, the Firecracker jailer, host kernel resource controls, and
future firewalls and egress proxies.

Tiny Cosmos assumes an agent may be confused, over-eager, or prompt-injected and
may use arbitrary guest root capabilities. Its responsibility is to:

- Select approved enforcement components and policy profiles.
- Configure them correctly without exposing bypass primitives.
- Associate the right sandbox and policy with the right user and session.
- Mediate access to control sockets, disks, guest communication, and lifecycle.
- Fail or report degradation when the requested enforcement configuration
  cannot be established or verified.
- Provide destination-confined handoffs across the guest/host boundary.

The external components are responsible for enforcing VM, process, and kernel
resource isolation. Production networking components will later enforce network
policy. Tiny Cosmos is responsible for integration and configuration defects,
but does not claim to prevent vulnerabilities or escapes inside those
enforcement components.

The following are trusted:

- The host harness and Tiny Cosmos harness plugin
- The per-user manager and privileged broker
- The interactive host user
- Explicit actions initiated through the trusted harness or CLI
- The harness's model, memory, MCP, credential, and permission behavior

Guest root, guest processes, the guest supervisor, guest files, guest protocol
data, and guest output are untrusted.

Tiny Cosmos is harness-agnostic and does not defend arbitrary harness-model
interactions. Use of harness-global tools is outside the sandbox security
boundary.

## Trust transitions

Two operations intentionally leave strict execution containment:

- **Git worktree handoff:** the user chooses to materialize untrusted guest code
  on the host. Tiny Cosmos confines where export writes, but does not claim the
  exported code is safe to build or run.
- **Open in VS Code:** the user chooses to connect host IDE behavior to the
  guest. Tiny Cosmos scopes the connection, but normal VS Code Remote,
  extension, workspace, credential, and user behavior is outside the
  containment guarantee.

## Finding disposition

| # | Finding | MVP | Production |
|---:|---|---|---|
| 1 | Harness-global authority | Out of scope | Harness responsibility |
| 2 | Internet exfiltration and abuse | Document limitation | Orchestrate an egress enforcement component |
| 3 | Git handoff host execution | Explicit transition; use `--no-checkout` | Same trust model |
| 4 | Export path and filesystem escape | Prove destination confinement | Retain and harden limits |
| 5 | Malformed guest protocol | Prove pessimistic fail-closed behavior | Retain and continuously fuzz |
| 6 | Host filesystem repair | Do not repair; retire and replace | Same policy |
| 7 | Incomplete selected-harness interception | Disable the integration if an intended path fails | Maintain compatibility coverage |
| 8 | Plugin and CLI authority | Not a trust boundary | Same trust model |
| 9 | DNS forwarder exposure | Document limitation | Orchestrate a hardened network/DNS component |
| 10 | Resource exhaustion | Configure bounded VM profiles | Configure aggregate host controls |
| 11 | Host/LAN/sibling network reachability | Document that MVP NAT provides no isolation | Orchestrate best-effort filtering |
| 12 | VS Code Remote exposure | Explicit transition | Same trust model |
| 13 | Firecracker jail details | Prove correct basic jailer configuration | Configure advanced jail hardening |
| 14 | Pinned vulnerable versions | Start pinned; accept risk | Same unless policy changes |
| 15 | Host environment leakage | Prove no ambient sharing | Retain invariant |
| 16 | Terminal and log controls | Prove structured sanitization | Retain and broaden coverage |
| 17 | Ambiguous group or sandbox binding | Prove immutable-ID routing and exact logical-name lookup | Retain invariant |
| 18 | Idempotency and replay | IDs grant no authority | Retain invariant |
| 19 | Mutable metadata rebinds resources | Prove names and paths cannot alter immutable resources | Retain invariant |
| 20 | Conflicting lifecycle requests | Prove compatible coalescing and explicit conflicts | Retain invariant |

## Product claims

The MVP may claim:

> Tiny Cosmos creates and mediates an independent VM execution environment
> whose isolation is enforced by Firecracker/KVM and the configured host
> controls. Tiny Cosmos gives the guest no ambient host filesystem, process,
> credential, manager, broker, or sibling filesystem/process access through
> Tiny Cosmos control surfaces. Guest communication fails closed, and explicit
> exports are confined to their selected destination.

The MVP must also state:

> Tiny Cosmos does not defend arbitrary harness-model interactions, prevent
> internet exfiltration, provide comprehensive denial-of-service protection, or
> make exported code and VS Code Remote sessions safe. MVP networking provides
> ordinary NAT and does not isolate guests from reachable host, LAN, VPN,
> metadata, or sibling services. Hypervisor, kernel, jailer, firewall, and proxy
> enforcement is provided by selected external components, not implemented by
> Tiny Cosmos.

An unsupported version of the selected harness may continue after a clear
degraded or unverified warning. If any intended Tiny Cosmos replacement tool or
interactive shell path cannot function through the manager, the integration
must disable itself. Unknown future harness-native tools remain under the
trusted harness and its permission model rather than an interception claim by
Tiny Cosmos.
