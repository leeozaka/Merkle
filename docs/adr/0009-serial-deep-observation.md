---
status: accepted
---

# Run deep observation serially in V1

Execute and observe deep per-test work serially in the first version. The user put it plainly: “Go for serially! Not in a hurry for deep specialization.”

## Context

Parallel test execution can reduce first-run cost, but it complicates attribution when multiple tests execute overlapping code in the same process or when test frameworks reuse workers. It also adds coordination around profiler buffers, process lifecycles, external dependencies, and atomic observation publication.

Serial execution is the explicit starting point. Deep specialization speed can wait.

## Decision

The V1 deep adapter schedules one observation unit at a time and closes its attribution boundary before beginning the next. The unit may be a single test or the smallest isolated test-platform unit needed for reliable identity; reports name any coarser unit.

The planner may analyze indexes concurrently internally when determinism is preserved, but per-test dynamic observation is serialized. A user-provided `timeoutMs` may bound the run; no additional default latency policy is introduced.

## Alternatives considered

- **Use native test-runner parallelism immediately.** Faster cold starts, but risks ambiguous execution-to-test attribution.
- **Run isolated tests in parallel processes.** Better attribution, but significantly increases process and build/test-host overhead and resource contention.
- **Observe the full suite as one aggregate.** Cheap to implement, but does not produce per-test mappings.
- **Disable dynamic observation until parallelism is solved.** Simpler, but removes a primary accepted evidence source.

## Rationale

Serial execution gives the project trustworthy mappings and a reference dataset for future parallel designs. It also keeps cancellation and publication rules understandable.

## Consequences

- Cold-start and refresh runs may be slow on large suites.
- Cost-aware planning and explicit timeouts are important even before parallelism.
- Test-order dependence may surface; the runner should record order and environment fingerprints.
- Dynamic observations must distinguish test failures from analysis/observer failures.
- Performance benchmarks should measure both accuracy and throughput so a later parallel implementation has a trustworthy baseline.

## Reevaluation conditions

Revisit after the serial observer is stable and measured on representative repositories. Parallelism is acceptable only if attribution remains deterministic and conformance tests show no material loss of mapping quality.
