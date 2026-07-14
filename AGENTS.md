# Repository Guidelines

## Project Structure & Module Organization

Tiny Cosmos is a .NET 10 solution organized by runtime role. Source projects live in `src/`: `TinyCosmos.Cli` for the `tinycosmos` command, `TinyCosmos.Manager` for per-user orchestration, `TinyCosmos.Broker` for privileged host operations, `TinyCosmos.Guest` for in-VM supervision, plus shared `Core`, `Linux`, and `Protocol` libraries. Tests live in `tests/TinyCosmos.Core.Tests` and `tests/TinyCosmos.Integration.Tests`. VM image assets are under `images/linux`, systemd and Debian packaging files under `packaging`, protocol examples under `integrations/protocol`, and design notes under `planning`.

## Build, Test, and Development Commands

- `./scripts/test.sh`: restores, builds, and runs all tests in `TinyCosmos.slnx`.
- `dotnet build TinyCosmos.slnx`: quick compile check during development.
- `dotnet test TinyCosmos.slnx --no-build`: rerun tests after a successful build.
- `./scripts/publish-native.sh linux-x64`: publishes native AOT binaries to `artifacts/publish/linux-x64`.
- `./scripts/package-deb.sh`: builds the `.deb` package in `artifacts/deb`.
- `./scripts/e2e-local.sh`: runs the local CLI/manager e2e flow; expect Linux platform prerequisites and, for full VM paths, KVM/root-capable setup.

## Coding Style & Naming Conventions

The solution uses nullable reference types, implicit usings, latest analysis, code style enforcement, AOT compatibility, and warnings-as-errors from `Directory.Build.props`. Follow existing C# style: file-scoped namespaces, 4-space indentation, `PascalCase` public types and members, `camelCase` locals and parameters, and `Async` suffixes for asynchronous methods. Keep protocol payloads explicit and JSON source-generation contexts near their owning assembly.

## Testing Guidelines

Tests use xUnit with method names that describe expected behavior, such as `LogicalGroupNameRejectsInvalidShape`. Put fast domain and safety coverage in `TinyCosmos.Core.Tests`; put CLI, manager, broker, image, and workspace flows in `TinyCosmos.Integration.Tests`. Run `./scripts/test.sh` before opening a PR. Use the e2e script when changes touch sandbox lifecycle, guest workspace seeding, packaging, or host integration.

## Commit & Pull Request Guidelines

Recent history uses short, imperative commit subjects, for example `Seed guest workspace during sandbox start` and `Relax KVM preflight for root broker`. Keep commits focused and avoid committing generated `artifacts/` output. Pull requests should summarize behavior changes, list validation commands, call out host/KVM assumptions, and link related issues or planning notes. Include screenshots or logs only when they clarify CLI output, package contents, or e2e failures.

## Security & Configuration Tips

Be conservative around host operations: broker, network namespace, systemd, Firecracker, and filesystem ownership changes can affect the machine outside the repo. Prefer dry-run paths where available, keep user-owned state under the manager paths, and document any new privileged prerequisite in scripts and CI.
