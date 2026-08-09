---
status: accepted
---

# Build by default; make no-build strict; classify compilation failures as analysis errors

Deep .NET analysis builds the selected solution by default. `--no-build` reuses compatible prior outputs and fails if compatibility cannot be established. Compilation failures are analysis errors.

## Context

Semantic indexing and runtime observation must correspond to the code snapshot being planned. Reusing stale assemblies can create persuasive but incorrect mappings. At the same time, CI workflows may already have built the repository and need an explicit way to avoid duplicate work.

The user chose default build behavior, strict reuse under `--no-build`, and an explicit separation between compilation and test outcomes.

## Decision

Before deep observation:

1. Resolve one configured or unambiguous .NET solution and the repository's effective SDK policy.
2. Build by default using the repository-controlled toolchain.
3. With `--no-build`, validate that required outputs exist and are compatible with the analyzed snapshot and configuration.
4. If compatibility cannot be established, return an analysis error without running tests.
5. Classify compiler/MSBuild failure as an analysis error. Classify an executed test's failure, including a failure caused by an external dependency, as a normal test failure.

An explicit `--no-build` prevents a silent rebuild. Failed validation prevents use of stale output.

## Alternatives considered

- **Never build.** Fast and composable, but unsafe for local use and confusing when outputs are missing.
- **Always build with no override.** Reliable but wastes work in pipelines that already produce verified artifacts.
- **Trust any existing output timestamp.** Convenient, but timestamps do not establish snapshot or configuration compatibility.
- **Treat compilation as a failed test suite.** Collapses two actionable failure classes and corrupts test-history statistics.

## Rationale

Default build gives a standalone CLI the expected code snapshot. A strict opt-out lets pipelines reuse verified artifacts. Separate analysis and test failures keep selection history statistically meaningful.

## Consequences

- Build fingerprints must include enough snapshot, configuration, target-framework, and adapter/toolchain information to validate reuse.
- Reports and exit codes need distinct analysis-error and test-failure categories.
- Build duration belongs in total tool latency but not in individual test runtime estimates.
- External dependency failures are not reclassified by Merkle.
- SDK selection, including `global.json` behavior, must be printed for diagnosis.

## Reevaluation conditions

Revisit when CI artifact manifests provide a stronger portable compatibility proof or when a future minimal planning mode can operate entirely from source and intentionally skip runtime observation.
