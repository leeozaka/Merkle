---
status: accepted
---

# Use a versioned, capability-negotiated language-adapter protocol

Keep the engine language-neutral by integrating language support through a versioned contract with an explicit capability handshake. Minimal adapters list affected tests; deeper adapters may add semantics, execution, and per-test observation.

## Context

The user wants contributors to add languages without coupling their implementation to engine internals. Different ecosystems cannot deliver equal depth immediately. A community adapter may only know changed files and tests, while the first-party .NET adapter is expected to build, execute, and observe code at method-level granularity.

The CLI exposes common feature requests. Unsupported behavior must fail clearly. The project does not guarantee third-party adapter correctness or maintenance.

## Decision

Define a versioned logical adapter protocol with:

- A startup handshake containing protocol version, adapter identity/version, supported languages, supported target versions, platforms, and capabilities.
- Stable request and response envelopes with machine-readable error codes.
- Capability negotiation before expensive analysis begins.
- Stable source-unit and test identities owned by the adapter contract.
- Conformance fixtures and tests for every declared capability.

Capability levels describe concrete behavior:

- **Minimal:** detect owned files, discover stable test identities, map changed files or coarse units to candidate tests, and emit a plain affected-test list.
- **Semantic:** add sub-file semantic units and dependency edges.
- **Deep:** add build/test execution, per-test dynamic observations, timings, and outcomes.

If a requested capability is missing, exit with an explicit error equivalent to `function not available for: {language}`. Silent substitution would hide a weaker analysis. The contract allows adapters and the core to use different implementation languages; transport remains a separate choice.

## Alternatives considered

- **Compile adapters into the core.** Simple calls and debugging, but creates language coupling and forces core releases for community changes.
- **One all-or-nothing adapter interface.** Uniform on paper, but prevents useful minimal contributions and encourages false capability claims.
- **Infer support by trying commands.** Avoids a handshake, but fails late and makes CI behavior unpredictable.
- **Use an LSP as the entire contract.** Reuses semantic tooling, but LSP does not define test discovery, per-test runtime observation, or Merkle-specific identities.

## Rationale

Versioning and negotiation make partial ecosystem support explicit and testable. A small mandatory core lowers contribution cost. Early capability errors protect users who request deeper behavior. A separate transport decision leaves the core language open.

## Consequences

- Protocol evolution needs compatibility rules and fixtures.
- Adapter identity/version is part of cached-evidence compatibility.
- Mixed-language repositories must specify desired adapters/modes when detection is ambiguous.
- Third-party adapter output must be labeled with provenance and is user-trusted, not first-party guaranteed.
- The core owns planning and policy; adapters own language semantics and native toolchain integration.
- A future adapter SDK stays outside target-project dependencies.

## Reevaluation conditions

Revisit the capability partition after at least two materially different adapters exist. Revisit the transport if profiling shows it dominates analysis time or if sandbox/security requirements demand stronger process isolation.
