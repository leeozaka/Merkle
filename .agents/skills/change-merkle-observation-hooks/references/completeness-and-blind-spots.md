# Completeness and blind spots

## Shared admission rule

A test may finish with a valid execution result while its observation remains incomplete. Keep the execution outcome, but emit no observed unit identities and admit no dynamic edges for that scope.

Mark observation incomplete for missing output, empty evidence, invalid format, timeout before a complete boundary, cancellation, unmatched artifacts, stale fingerprints, or attribution outside the selected snapshot and scope.

Do not redefine completeness as “the test process finished.” `TestExecutionResult` already records that fact. A complete `ObservationScope` must contain at least one valid unit identity. Treat a header-only or all-zero Go cover profile as incomplete even when its syntax is valid.

If warning volume is the problem, add a more specific incomplete reason or aggregate repeated warnings. Do not admit an empty dynamic observation into history.

## .NET

Current granularity is loaded assembly and owning project. Disclose that it does not observe:

- member-level execution;
- reflection-only resolution;
- native code;
- child processes;
- assemblies loaded before the hook can see them; or
- runner identities that cannot be matched to stable tests.

Do not translate assembly evidence into member evidence. Do not call the result complete coverage.

## Go

Current granularity is repository file from a positive coverage block. Disclose blind spots for:

- runtime-only subtests as separate identities;
- standard-library coverage;
- interface dispatch and reflection;
- generated code;
- plugins and subprocesses;
- cgo and native code;
- build-tag-dependent variants; and
- packages outside instrumentation scope.

An empty or syntactically valid zero-count profile is incomplete. A profile path outside the materialized snapshot is not attributable evidence.

## Compatibility

Any change to granularity, artifact matching, identity mapping, observer version, selected scope, or toolchain inputs can invalidate stored evidence. Update fingerprint or identity versions deliberately and add compatibility tests instead of accepting old history by accident.
