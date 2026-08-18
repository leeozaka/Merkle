# Go adapter

Status: Accepted first-party deep adapter
Related: [Specification](specification.md), [Adapter authoring](adapter-authoring.md), [ADR-0017](adr/0017-first-party-go-deep-adapter.md)

Merkle's Go adapter is a first-party deep adapter for repositories using the Go toolchain. Its canonical language identifier is `golang`. The CLI normalizes `go` to `golang` when parsing language selections; reports and configuration use the canonical identifier.

## Capability boundary

The packaged worker is a Go process. It uses the versioned JSON process protocol and advertises `detect`, `index`, and `map`. The worker reads one bounded JSON request from standard input and writes one JSON response to standard output; diagnostics stay on standard error.

The host supplies deep operations when the system Go toolchain is available on Linux or macOS:

- build preparation compiles a coverage-capable `.test` artifact for each test-bearing package with `go test -c`;
- discovery uses `go list -mod=readonly -json ./...` and `go test -mod=readonly -list .`;
- execution wraps the prepared artifact with `go tool test2json` and passes an exact test-binary selector; and
- observation reuses that artifact with `-test.coverprofile`; coverage instrumentation is limited to packages in the owning module.

The host invokes these commands in a materialized immutable snapshot workspace, including for a frozen working-tree snapshot. It sets module mode on and uses the local Go toolchain (`GO111MODULE=on`, `GOTOOLCHAIN=local`).

## Repository scope

The adapter discovers `go.work` and `go.mod` files in the snapshot. A workspace selects the modules listed by its `use` directives. Without a workspace, all discovered modules are considered unless one `go.mod` is configured. Nested modules use the longest matching module root. More than one `go.work` requires explicit configuration.

Build and no-build validation include the snapshot identity, selected scope, Go version, module and package manifests, adapter versions, requested platform, effective `GOOS/GOARCH`, and artifact hashes. A no-build run fails when the manifest or any compiled test artifact is absent, stale, or incompatible, and selected execution runs that prepared artifact directly. The adapter does not modify the analyzed repository's source, module files, or project configuration.

## Stable identities and snapshots

Source identities use the `golang:` namespace and include repository-relative file, package, type, and function or method coordinates. Test identities include the import path and test function name, for example `golang:example.com/payments/currency:TestRound`. Unit and test ordering is deterministic for identical snapshot inputs and adapter versions.

The host binds a snapshot before materializing files, indexing, building, discovering, executing, or observing. State and artifacts are keyed by that snapshot and their compatibility fingerprint; a later snapshot cannot reuse them accidentally.

## Observation boundary

Coverage is mapped to file units from nonempty, valid cover profiles. It is not member-level coverage. Go subtests are discovered only when the selected parent test runs, so they are not separate catalog entries. File-level profiles do not establish standard-library coverage and can miss code reached through dynamic behavior.

Results can be incomplete for interface dispatch, reflection, generated code, plugins, subprocesses, cgo/native code, build tags, and other configuration-dependent builds. The adapter reports these blind spots and does not treat an empty or invalid profile as admitted observation evidence.

## Toolchain and packaging

Source builds require Go 1.22 or newer. Release packages bundle the Go worker executable for the supported target platform; users do not need to build the worker separately. Deep analysis still needs the target repository's system `go` command and its dependencies. Build and test commands execute repository code with the runner's permissions.
