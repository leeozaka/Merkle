# Observation verification

Use focused tests first. Omit `--no-build` unless current binaries already match the worktree.

## Shared orchestration

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~DeepExecutionEngineTests
```

Cover plan failure before deep work, missing capability, complete and incomplete history, full-suite and selected resolution, unresolved identities, every normalized outcome, cancellation, redaction, and atomic publication fallback.

## .NET observation

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~DotNetDeepOperationsTests
```

Cover build/no-build fingerprints, one process per test, no implicit timeout, explicit timeout, stable English discovery output, exact selector resolution, hook environment, complete assembly match, empty or unmatched hook output, diagnostic bounds, TRX outcomes, and temporary workspace behavior.

When changing `StartupHook.cs`, add or run a real macOS/Linux test-host integration check when possible. State clearly when only fake-process tests were run.

## Go observation

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~GoDeepOperationsTests
```

```bash
cd src/adapters/go/worker
test -z "$(gofmt -l .)"
go vet ./...
go test ./...
```

Cover modules and workspaces, exact identities, immutable artifact hashes, no-build tampering, test2json outcomes, benchmark and fuzz selectors, positive coverage, malformed and zero profiles, outside-repository paths, timeout, cleanup, and blind-spot warnings.

Lock the admission boundary with explicit header-only and all-zero cases: both remain incomplete, contain no observed units, and enter no dynamic history.

## Package and broad checks

Run the adapter-specific source build with `--test`, then publish for the current runtime when worker or observer payloads changed. Run the full Release .NET test project and `dotnet format --verify-no-changes` after changing shared contracts, reports, state publication, or engine flow.
