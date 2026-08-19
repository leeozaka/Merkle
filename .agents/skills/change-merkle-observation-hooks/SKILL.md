---
name: change-merkle-observation-hooks
description: Change Merkle's deep execution and runtime observation path, including build fingerprints, strict no-build validation, test discovery and identity resolution, serial execution, the .NET startup hook, Go cover-profile observation, completeness, blind spots, history admission, and observer packaging. Use for `DeepExecutionEngine`, `DotNetDeepOperations`, `StartupHook`, `GoDeepOperations`, observation reports, timeouts, or runtime evidence tests. Do not use for static adapter mapping or planning policy alone.
---

# Change Merkle observation hooks

Preserve one reliable attribution boundary per test and admit runtime evidence only when the adapter can prove the scope is complete under its stated granularity.

## Read the accepted boundary

Read ADR-0007, ADR-0008, ADR-0009, and ADR-0016 for every observation change. Also read ADR-0017 and `docs/go-adapter.md` for Go. Use `docs/adapter-authoring.md` sections 8 through 10 for the shared capability contract.

Read:

- [references/observation-map.md](references/observation-map.md) to locate the owning process, artifact, and report seam.
- [references/completeness-and-blind-spots.md](references/completeness-and-blind-spots.md) before changing evidence admission.

## Trace the complete operation

1. `DeepExecutionEngine` produces a successful plan before any build or test work.
2. It requires exactly one `<language>:deep` selection and negotiates the needed capabilities.
3. The adapter prepares a build or validates `--no-build`, discovers the runtime catalog, and resolves selected stable identities.
4. `observe` runs the full catalog; selected execution follows policy unless the plan requires the full suite.
5. The adapter executes or observes tests serially and returns normalized outcomes.
6. Only complete observations expose unit identities and enter historical evidence.
7. Terminal report and admitted history publish together when the state store supports atomic publication.

Keep build failure, missing or stale artifacts, test failure, explicit timeout, observer incompleteness, and cancellation as distinct outcomes.

## Reject unsafe evidence admission

Do not implement a request that relabels missing, empty, all-zero, malformed, outside-scope, or unattributable observation as complete merely to reduce warnings. The current contract defines complete observation as valid evidence attributable to at least one repository unit at the adapter's stated granularity. Process completion is already represented by the test execution outcome; it is not observation completeness.

When a request conflicts with that rule:

1. State the conflict with the accepted completeness and history-admission contract.
2. Keep the scope incomplete and preserve the execution outcome.
3. Offer a separate diagnostic category or warning wording if the goal is to distinguish “test ran but reached no attributable unit” from malformed or missing observer output.
4. Require an explicit product decision, specification and ADR review, compatibility analysis, observer-version change, confidence-model review, and backtest before changing what counts as admitted evidence.

## Change the .NET startup hook

1. Keep the hook dependency-free and outside the repository under analysis.
2. Attach it through `DOTNET_STARTUP_HOOKS`; pass only the host-owned `MERKLE_OBSERVATION_FILE` destination.
3. Record already loaded and later loaded assemblies without throwing into the test process.
4. Keep records bounded and deduplicated. Preserve the current 4,096-record cap unless the task explicitly changes the protocol or resource bound.
5. Write only to the provided state or temporary path. Never edit target source, projects, solutions, package references, lockfiles, or `.gitignore`.
6. Match a loaded assembly to a fingerprinted artifact by canonical path or by filename plus hash.
7. Mark a scope complete only when the hook output exists and maps to at least one repository artifact.
8. Keep one `dotnet test` process per discovered test, stable English tool output, exact fully qualified selector, and explicit timeout behavior.
9. Keep hook failure observational. A write or reflection failure in `StartupHook.Initialize` must not change the test result.
10. Package and smoke the observer alongside the managed analysis worker for every supported release runtime.

## Change Go observation

1. Build an immutable coverage-capable test binary per test-bearing package with module-local instrumentation.
2. Include snapshot, selected scope, configuration, platform, effective toolchain, module and package manifests, adapter and observer versions, and artifact hashes in the fingerprint.
3. Execute the prepared artifact through `go tool test2json` with an exact test, benchmark, fuzz, or example selector.
4. Produce a temporary `mode: set` cover profile for one test and delete it after parsing.
5. Accept only valid, nonempty profiles with positive blocks attributable to repository-relative files in the selected modules.
6. Reject zero, malformed, outside-repository, stale, tampered, or unattributable evidence as incomplete.
7. Keep Go observations at file granularity and retain the blind-spot warning in every complete or incomplete scope.

## Preserve cross-cutting invariants

- Keep observation serial until an accepted design proves unambiguous parallel attribution.
- Impose no default timeout. Apply a deadline only when the user supplies one.
- Never admit partial or empty observations after timeout, cancellation, crash, missing output, zero attributable coverage, or identity mismatch.
- Never fabricate a runtime selector when stable identity resolution fails.
- Keep `ObservationCompleteness`, execution outcome, duration, warnings, and observed unit identities separate.
- Store no raw source in history. Redact and bound diagnostics.
- Treat build and test commands as arbitrary code execution under the runner's permissions.
- Do not advance published state with half of a report/history pair.
- Preserve the previous terminal state when the operation is interrupted.

## Verify

Read [references/verification.md](references/verification.md). Add tests at the adapter seam and at `DeepExecutionEngine` when completeness, outcome, recommendation gating, or publication changes. Run package verification whenever the observer or worker artifact changes.

Report the observed granularity, attachment mechanism, fingerprint inputs, completeness rule, tested outcomes and platforms, blind spots, and checks not run.
