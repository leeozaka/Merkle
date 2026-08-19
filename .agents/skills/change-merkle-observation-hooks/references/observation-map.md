# Observation map

| Stage | Shared seam | .NET implementation | Go implementation |
|---|---|---|---|
| Plan gate | `src/core/Engine/DeepExecutionEngine.cs` | Same | Same |
| Contracts | `src/core/Adapters/DeepAdapterContracts.cs` | Same | Same |
| Adapter facade | `ILanguageAdapter` plus deep interfaces | `src/adapters/dotnet/DotNetAdapter.cs` | `src/adapters/go/host/GoAdapter.cs` |
| Build preparation | `IBuildPreparer` | `DotNetDeepOperations.PrepareBuildAsync` | `GoDeepOperations.PrepareBuildAsync` |
| Discovery | `ITestDiscoverer` | `dotnet test --list-tests` | `go list` and `go test -list` |
| Stable identity resolution | `ISelectedTestResolver` | Exact identity, project fallback, or discovered selector match | Exact catalog identity |
| Selected execution | `ISelectedTestExecutor` | One `dotnet test` process and TRX parsing | Prepared test binary through `go tool test2json` |
| Runtime hook | `ITestObserver` | `src/adapters/dotnet/observer/StartupHook.cs` via `DOTNET_STARTUP_HOOKS` | Temporary `-test.coverprofile` from the prepared artifact |
| Evidence mapping | `DynamicObservation` | Loaded assembly matched to fingerprinted artifact, then project identity | Positive coverage block normalized to `golang:file:<path>` |
| Completeness | `ObservationScope` | Observation file exists and maps to at least one repository artifact | Valid nonempty attributable `mode: set` profile |
| Publication | `IStatePublicationStore` and terminal report | Complete scopes only | Complete scopes only |
| Package | source-build adapter and `adapters.json` | Worker and observer DLLs under `workers/dotnet/` | Native Go worker under `workers/go/`; system Go still required for deep target analysis |

## Test ownership

- Shared orchestration: `tests/Merkle.Tests/Engine/DeepExecutionEngineTests.cs`
- .NET behavior: `tests/Merkle.Tests/Adapters/DotNetDeepOperationsTests.cs`
- Go behavior: `tests/Merkle.Tests/Adapters/GoDeepOperationsTests.cs`
- Package presence and manifests: `tests/Merkle.Tests/Build/`
- State/history publication: `tests/Merkle.Tests/State/` and infrastructure state tests

There is no direct cross-platform integration test of `StartupHook` against a real test host today. Do not claim that coverage from fake-process unit tests.
