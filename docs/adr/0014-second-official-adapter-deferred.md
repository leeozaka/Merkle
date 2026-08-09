# ADR-0014: Defer any second first-party adapter

- **Status:** Deferred
- **Date:** 2026-08-07
- **Decision owners:** Merkle maintainers

## Context

Contributors can implement language adapters. The initial product already carries substantial risk in its .NET 6+ deep adapter: semantic identity, package-free runtime observation, test discovery, build compatibility, and cross-platform behavior. The user removed TypeScript from current first-party scope with a blunt “What a mess!” Go has an attractive CLI distribution and toolchain, though that does not justify a maintained official adapter by itself.

## Decision

Merkle promises one first-party deep adapter, for .NET 6+, in the initial scope. Go remains the strongest candidate for a later official adapter. A commitment waits for evidence from the .NET path, adapter protocol, user demand, and maintainer capacity.

Third parties may implement minimal or deeper adapters against the versioned capability protocol. Their availability and correctness are not guaranteed by the core project.

## Alternatives considered

- **Ship .NET and Go together.** Rejected for V1 because it doubles integration and conformance work before the protocol stabilizes.
- **Ship TypeScript as the second adapter.** Rejected from current first-party scope by explicit product decision.
- **Declare that no second official adapter will ever exist.** Rejected because future demand and contributor capacity may make one worthwhile.

## Rationale

One high-quality deep adapter gives the protocol a real implementation before the project assumes another long-term compatibility burden.

## Consequences

- Roadmaps and examples label Go as a possible future candidate.
- Mixed-language syntax may use Go illustratively only when it is clear that an installed adapter is required.
- TypeScript may appear in the conversation history only as a superseded idea.
- A future official adapter requires its own acceptance ADR and support matrix.

## Re-evaluation conditions

Revisit this decision after the .NET adapter and protocol conformance suite are stable, and only when user demand, maintainer capacity, packaging feasibility, and end-to-end effort have been measured.
