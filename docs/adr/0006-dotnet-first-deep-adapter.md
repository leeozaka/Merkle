---
status: accepted
---

# Build the first deep adapter for .NET 6+ and exclude TypeScript from current first-party scope

The first complete, first-party deep adapter targets .NET 6 and newer on macOS and Linux. TypeScript is explicitly removed from the current official scope; Go remains only a possible later adapter.

## Context

The discussion initially considered a complete .NET adapter plus a minimal TypeScript/Jest adapter, with Go later. The user then cut that direction short: “Lol take those typescript away from now. What a mess!” The first release needs deep, credible per-test impact analysis before it adds more languages.

The user expects the installed/default .NET toolchain to build supported targets and does not currently require native Windows. Multiple solutions in a single analysis are also outside the initial expectation.

## Decision

Prioritize one first-party deep adapter with this initial support boundary:

- .NET 6 and newer target frameworks.
- macOS and Linux as supported platforms.
- Native Windows excluded; WSL is not a committed target.
- One configured or unambiguously discovered solution per invocation.
- Minimal, semantic, build, test execution, timing, and per-test observation capabilities.

Respect repository SDK controls and report the selected SDK. The detailed SDK selection and roll-forward behavior must be validated in the adapter spike rather than assumed.

Keep TypeScript off the current first-party roadmap. The generic adapter protocol still permits a community implementation. Reassess Go after the .NET adapter validates the protocol, with no promise before then.

## Alternatives considered

- **Ship .NET and TypeScript together.** Broader reach, but splits attention across unrelated build/test/runtime ecosystems before the protocol is proven.
- **Ship .NET and Go together.** Useful validation of language neutrality, but still delays depth and leaves two incomplete integrations likely.
- **Start with only minimal adapters.** Faster demonstration, but does not validate the dynamic per-test evidence that differentiates the product.
- **Support Windows from V1.** Expands runtime, path, process, and profiler validation beyond the user's target platforms.

## Rationale

A single deep vertical slice tests stable semantic identity, build integration, test discovery, runtime observation, persistence, and ranking. .NET matches the user's immediate focus. Deferring a second adapter leaves portability as something to prove.

## Consequences

- The repository architecture must still prevent .NET concepts from leaking into core planning.
- Initial conformance fixtures should cover several .NET 6+ target frameworks and test-project shapes.
- Ambiguous multiple-solution repositories fail with guidance.
- Documentation must remove historical TypeScript examples from current commands.
- Go support remains an open roadmap decision.

## Reevaluation conditions

Revisit platform and language scope after the .NET deep adapter reaches a stable alpha and real repositories validate the protocol. Add another first-party language only when it can test a specific portability risk without destabilizing the core.
