# ADR-0017: Ship a first-party deep Go adapter

- **Status:** Accepted
- **Date:** 2026-08-17
- **Decision owners:** Merkle maintainers

## Context

The repository now contains a Go worker and a host implementation. The worker provides deterministic static detection, indexing, and mapping over a process protocol. The host can use the system Go toolchain for build preparation, test discovery, selected execution, and file-level coverage observation. Keeping these operations behind the adapter boundary preserves the core's C#/.NET 10 implementation while adding a second ecosystem without changing the planner's language-neutral contract.

## Decision

Go is a first-party deep adapter. Its canonical language identifier is `golang`; the CLI accepts `go` as an input alias and normalizes it to `golang`.

The worker process owns `detect`, `index`, and `map`. The host owns Go-toolchain operations for build, discovery, execution, and observation. Source builds require Go 1.22 or newer. Packaged Merkle releases bundle the worker executable for the supported platform, while deep analysis invokes the target repository's system `go` command.

The adapter supports multi-module repositories and `go.work` scopes, including nested `go.mod` files. It uses deterministic source and test identities and immutable snapshot/fingerprint validation. No-build operation is strict: missing, stale, or incompatible manifests or artifacts are analysis errors.

Observation is serial and file-level, based on Go cover profiles. The adapter must disclose blind spots including runtime-only subtests, standard-library and file-level coverage limits, interface/reflection and dynamic behavior, generated code, plugins, subprocesses, cgo/native code, and build-tag-dependent behavior.

## Alternatives considered

- **Keep Go deferred.** Rejected because the implementation now supplies the worker, host seam, build contract, and test coverage needed for a supported first-party adapter.
- **Ship only a minimal Go mapper.** Rejected because the host supports build, discovery, execution, and observation with explicit limits.
- **Move Go orchestration into the core.** Rejected because the process boundary keeps Go toolchain behavior and dependencies in the adapter.

## Consequences

- Mixed-language selections can request `golang:minimal` or `golang:deep` when the corresponding capabilities are available.
- Reports identify the `golang` adapter, negotiated capabilities, build fingerprint, and observation completeness.
- File-level coverage and runtime-only discovery remain weaker evidence than member-level static analysis; the planner must preserve those limits in confidence and warnings.
- Go becomes part of the first-party support matrix and no longer belongs in the deferred backlog.

## Re-evaluation conditions

Revisit this decision if the worker protocol, supported platforms, Go toolchain requirements, or observation boundary changes enough to require a new identity or capability contract. A later ADR must supersede this record rather than rewriting its history.
