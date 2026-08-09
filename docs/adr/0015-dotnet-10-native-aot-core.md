---
status: accepted
---

# Use C# on .NET 10 with Native AOT as the core toolchain

## Context

Merkle's first deep adapter analyzes .NET repositories and must integrate with SDK resolution, build/test tooling, semantic analysis, and an unmanaged CLR profiler. The earlier language study rated C# as the strongest end-to-end fit but deferred the production choice until spikes were complete. The project is now beginning implementation and needs one coherent toolchain and an executable distribution constraint.

Native AOT creates real constraints around reflection, dynamic assembly loading, Roslyn/MSBuild workspaces, test-platform integration, SQLite providers, and adapter discovery. Deferring those constraints would allow an implementation that only discovers incompatibility near release.

## Decision

Use C# targeting .NET 10 for the core, CLI, contracts, and first-party .NET adapter.

First-party managed executable projects must enable Native AOT publishing when they are created. CI must eventually build and smoke-test target-specific AOT artifacts for the supported release matrix. Shared libraries must remain trimming-safe, use explicit serialization metadata where required, and avoid reflection-based public contracts.

The CLI remains the composition root. Domain planning, identities, and policy stay in ordinary class libraries. Language adapters retain a versioned, process-capable protocol even when an official adapter initially runs in process.

If Roslyn, MSBuild, test-platform, storage, or another required dependency cannot operate correctly under Native AOT, isolate it behind that process boundary. Any first-party managed executable published without AOT requires a focused ADR documenting the incompatible surface, packaging impact, and removal criteria. The unmanaged CLR profiler remains a target-specific native helper and is not replaced by Native AOT.

This decision selects the implementation toolchain; it does not claim the observation, persistence, or complete packaging spikes have passed.

## Alternatives considered

- **Continue deferring the language.** Preserves optionality but prevents a production-shaped bootstrap and postpones the most relevant compatibility feedback.
- **Use C# with framework-dependent or self-contained JIT publication first.** Reduces early compatibility friction but lets reflection and dynamic-loading assumptions accumulate outside the intended release shape.
- **Use Go or Rust for the core.** Produces a native core but immediately requires a separate C# semantic adapter plus the unmanaged profiler, increasing the number of implementation surfaces before the product model is proven.
- **Require every component to live in one AOT process.** Conflicts with the accepted process-capable adapter seam and risks distorting the architecture around third-party dynamic tooling.

## Rationale

C# keeps orchestration and the first adapter in the ecosystem being analyzed. .NET 10 supplies the selected SDK baseline, while Native AOT makes startup, deployment, trimming, and dynamic-code constraints visible throughout development. The process-capable adapter boundary contains tooling that proves incompatible without weakening the AOT core or the language-neutral protocol.

## Consequences

- Development requires the .NET 10 SDK and honors the repository's `global.json`.
- Native AOT publish is a routine compatibility check, not a release-only task.
- Release artifacts are built per OS and architecture.
- Dynamic code generation, unbounded reflection, and runtime-only adapter discovery are prohibited in core contracts.
- Source-generated serialization and explicit registrations are preferred.
- Roslyn/MSBuild and SQLite choices remain subject to their existing spikes.
- Target repositories may still target .NET 6 or later; the tool's own `net10.0` target does not change adapter compatibility goals.

## Reevaluation conditions

Revisit only if a measured required capability cannot be isolated behind an adapter process, supported release targets cannot run the AOT artifacts, or the .NET support policy changes. Record a superseding ADR with the failing evidence and migration plan.
