# Merkle documentation

Merkle has an implementation of roadmap phases 0–5. This directory records the product constraints, the .NET 10/Native AOT topology, and the deferred backlog.

## Start here

| Reader | Recommended path |
|---|---|
| Building or using Merkle | [Build and usage](../USAGE.md) → [CI operations](operations.md) → [Specification](specification.md) |
| Evaluating the idea | [README](../README.md) → [System design](system-design.md) → [Roadmap](roadmap.md) |
| Maintaining the implementation | [Specification](specification.md) → [Implementation guide](implementation-guide.md) → ADRs |
| Building a language adapter | [Adapter authoring](adapter-authoring.md) → [Domain context](../CONTEXT.md) → [Specification](specification.md) |
| Choosing the core language | [Language options](language-options.md) → [Implementation guide](implementation-guide.md) → relevant ADRs |
| Reviewing product decisions | [Conversation decisions](conversation-decisions.md) → [System design](system-design.md) → ADRs |

## Documents

- [Build and usage](../USAGE.md) covers source builds, Native AOT publication, deployment, commands, state, policy, CI, and the remote-history service boundary.
- [System design](system-design.md) is the complete architecture narrative and source of truth for component relationships.
- [Specification](specification.md) is normative for externally visible behavior, failure classification, and initial acceptance.
- [Implementation guide](implementation-guide.md) describes module seams, invariants, delivery order, and verification.
- [Roadmap](roadmap.md) defines phases and measurable exit criteria without calendar promises.
- [Adapter authoring](adapter-authoring.md) defines the capability-negotiated contributor contract.
- [CI and remote-state operations](operations.md) defines trusted-runner, cache, remote API, and release rules.
- [Language options](language-options.md) compares likely core implementation languages and toolchains.
- [Conversation decisions](conversation-decisions.md) preserves the evolution of the idea and the constraints agreed during discovery.
- [Domain context](../CONTEXT.md) defines the implementation-free vocabulary.
- [Architecture decision records](adr/README.md) contains one decision per file with its own accepted, proposed, or deferred status.
- [Documentation QA report](../QA-REPORT.md) records the final render, accessibility, privacy, and package checks.

## Status language

| Status | Meaning |
|---|---|
| **Accepted** | An explicit product or architecture decision. Implementation should follow it unless superseded by a later ADR. |
| **Proposed** | A recommended design awaiting a spike, review, or implementation evidence. |
| **Deferred** | Intentionally undecided until a stated decision gate. |
| **Out of scope** | Excluded from the initial product; not implied by missing implementation. |

## Source-of-truth order

When documents appear inconsistent:

1. a later accepted ADR overrides an earlier decision;
2. the specification governs observable behavior;
3. the system design governs architectural intent;
4. the implementation guide provides non-normative delivery guidance; and
5. the conversation record explains history but does not override a formal decision.

Resolve ambiguities in the specification. Record hard-to-reverse choices in ADRs instead of hiding them in implementation behavior.

## Scope summary

The initial target is one self-hosted CLI, one repository, one .NET solution, .NET 6+, macOS/Linux, serial deep observation, and explicit language selection for mixed repositories. Merkle gives advice. The team's existing CI/CD system still owns the full suite and external integration-test infrastructure.
