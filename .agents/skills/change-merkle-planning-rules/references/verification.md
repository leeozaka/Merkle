# Planning verification

Use a filter without `--no-build` unless the solution has already been built from the current worktree. If RTK hides failure details, rerun the same `dotnet test` command directly or write a TRX log.

## Focused tests

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~PlanPolicyTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~ImpactEngineTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~HistoryModelTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~HistoryCompatibilityTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~MerkleConfigurationLoaderTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~CliApplicationTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~ReportRendererTests
```

Add `LanguageDetectorTests` for detection rules and `DeepExecutionEngineTests` when a recommendation changes execution authority.

## Required boundary cases

- Minimum savings at `0`, `100`, just below, and just above the configured floor
- Confidence at `0`, `1`, equal to threshold, below threshold, and null
- NaN, infinity, negative runtime, and out-of-range configuration
- Mandatory versus discretionary candidates
- Comparable and unavailable full-suite timing
- All three low-confidence actions
- Complete and incomplete automatic policy
- Unmapped warn and fail behavior
- Selected-only censored history
- Deterministic tie ordering by identity
- CLI override versus repository configuration
- Text and JSON decisive reasons and stable error code

Run the full Release test project and formatting check after changing shared domain records, configuration, report schema, or engine orchestration.
