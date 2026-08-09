---
status: accepted
---

# Keep observation dependencies out of target projects

Normal observation leaves target-project dependency graphs unchanged. Teams do not add a NuGet package, Node package, source annotation, or equivalent application dependency for Merkle.

## Context

Adoption drops when a developer tool requires edits to every application or test project. Added packages affect restore graphs, lockfiles, security review, version conflicts, and production artifacts. The user explicitly called this a low-acceptance path and asked to use the normal system toolchain for .NET 6+.

Deep observation still needs to learn which code units each test executes. That can require test-platform integration or runtime hooks, but it does not require a compile-time reference from the repository under test.

## Decision

Implement observation through external mechanisms such as CLI orchestration, standard test-platform interfaces, environment configuration, runtime profiling hooks, sidecar/helper processes, or generated temporary run settings. Keep all Merkle binaries and configuration outside target project manifests and dependency files.

The adapter may create disposable files in its state or temporary directory. Persistent changes to project files, package references, source code, or test code require a future explicit opt-in decision.

The exact .NET observation mechanism is recorded in ADR-0016; this ADR constrains its dependency and trust boundary.

## Alternatives considered

- **NuGet instrumentation package.** Easier application-level callbacks, but modifies user projects and creates restore/versioning/security friction.
- **Source rewriting committed to the repository.** Precise control, but invasive and difficult to make trustworthy.
- **Test-framework-specific plugin required in every project.** Can expose test boundaries, but repeats setup and couples adoption to framework packages.
- **Static analysis only.** Dependency-free, but cannot deliver the accepted dynamic per-test evidence.

## Rationale

External observation installs Merkle as infrastructure and keeps it out of source dependencies. This fits self-contained CI use and reduces the chance that analysis changes the code under test.

## Consequences

- The .NET adapter may need platform-specific native helpers or runtime-profiler integration.
- Test boundary correlation may be harder across different test platforms.
- Target-framework and runtime compatibility testing becomes part of adapter quality.
- The tool must disclose temporary environment variables and processes in diagnostic output.
- Community adapters must obey the same no-injection rule to claim first-party-compatible deep behavior; users may still choose unsupported external approaches at their own risk.

## Reevaluation conditions

Revisit only if standard external hooks cannot provide reliable per-test mapping for a required ecosystem. Any project-injected option must be explicit, optional, isolated from production artifacts, and documented as a different trust/deployment mode.
