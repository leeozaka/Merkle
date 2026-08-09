---
status: superseded
superseded-by: 0015-dotnet-10-native-aot-core
---

# Defer the core implementation language until focused spikes are complete

This deferral was superseded when the project selected C# on .NET 10 with Native AOT in [ADR-0015](0015-dotnet-10-native-aot-core.md).

Choose the engine implementation language after comparing candidate toolchains against the .NET deep-adapter boundary, distribution targets, storage, performance, and contributor model.

## Context

The current design is language-neutral at the engine boundary but .NET-first at the deep-adapter boundary. The product must run on macOS and Linux, avoid injecting target-project dependencies, build a fast incremental index, use an embedded local store, and welcome external adapter contributions.

These constraints pull in different directions. C# may reduce friction around Roslyn, MSBuild, test-platform, and .NET runtime APIs. Go may simplify cross-platform CLI distribution. Rust may offer strong performance and native integration with tighter safety guarantees but higher implementation complexity. A scripting-language prototype may accelerate learning but weaken the final distribution or profiling story. None has yet been validated against the complete slice.

## Decision

Defer selection of the production core language. Run bounded spikes that implement the same representative path:

1. Read an explicit base/head snapshot pair.
2. Index a small .NET solution into stable semantic nodes.
3. Persist hashes and reverse edges in the proposed local store.
4. Discover stable test identities.
5. Invoke one serial deep observation through the no-project-dependency boundary.
6. Return a versioned adapter response and ranked dry-run plan.
7. Package and run on supported macOS and Linux targets.

Evaluate candidates using written criteria: integration depth, incremental performance, cold-start time, memory, package size, cross-compilation/release burden, native-profiler interoperability, debugging, SQLite support, protocol tooling, contributor accessibility, and long-term maintenance. Keep Go as a possible future adapter or core candidate without promising it. TypeScript is outside the current first-party adapter scope and should not become the core through tooling convenience.

## Alternatives considered

- **Choose C# immediately because .NET is first.** Likely fastest for the adapter, but may couple engine and ecosystem or complicate a small self-contained distribution.
- **Choose Go immediately for a single CLI binary.** Attractive operations story, but pushes .NET semantics and runtime observation across a process boundary from day one.
- **Choose Rust immediately for performance and native control.** Strong systems fit, but may increase initial delivery and contribution effort before algorithms are validated.
- **Prototype indefinitely in a scripting language.** Fast experimentation, but risks a throwaway implementation becoming an accidental production architecture.
- **Use one language for every adapter.** Contradicts the versioned language-neutral contract and raises contribution barriers.

## Rationale

The observation boundary and the cost of keeping language-specific depth out of the planner are the hardest unknowns. A thin vertical spike can measure them. Deferral prevents premature lock-in and sets a decision checkpoint.

## Consequences

- Architecture documents describe module contracts without language-specific package names until selection.
- Spike code is disposable unless it meets production criteria and tests.
- The adapter protocol and canonical fixtures must be implementation-neutral.
- The first implementation milestone includes a language decision checkpoint.
- No public contribution guide should promise a core toolchain before the ADR is accepted.

## Reevaluation conditions

Replace this ADR with an accepted language decision when the spikes are measured and the team can explain why the chosen option wins for the end-to-end slice. Record rejected candidates and the evidence that ruled them out.
