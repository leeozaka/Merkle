---
status: accepted
---

# Keep impact confidence separate from mean-runtime cost

Rank test plans with separate estimates for evidence-based impact confidence and execution cost derived from observed runtimes.

## Context

The user wants probability-based pre-execution analysis, historical means, and a ranked set of valuable tests. The selector should use the calculated set when evidence is strong, but it should prefer the full suite when selective execution is not materially cheaper.

Impact measures how strongly the evidence associates a changed unit with a test. Cost estimates how long the selected plan will take relative to the full suite.

The conversation uses `P(A)` but has not yet defined event A or the estimator. It also mentions a useful 10–20% margin without defining whether that is uncertainty, miss tolerance, or cost variance.

## Decision

Maintain separate planner fields for:

- Impact evidence and its calibrated confidence/uncertainty.
- Sample counts and evidence provenance.
- Per-test mean runtime and dispersion where available.
- Estimated selected-plan runtime.
- Estimated full-suite runtime.
- Expected savings and its uncertainty.

Static dependencies, semantic containment, dynamic per-test execution, and historical correlations contribute explicit, inspectable evidence. The exact probability event and statistical model remain undefined until a focused spike. An arbitrary heuristic cannot carry the label `P(A)`.

Selection policy consumes estimates calculated by the statistical and cost models. Reports expose confidence and cost, the reason each test was selected, and the cause of any full-suite fallback.

## Alternatives considered

- **One opaque relevance score.** Easy to sort, but impossible to calibrate or configure safely.
- **Select every observed impacted test without cost modeling.** Uses evidence but may save almost no time.
- **Select only the fastest tests.** Optimizes latency while ignoring impact.
- **Use only last-run duration.** Simple but unstable; means and sample context are more useful.
- **Define `P(A)` immediately without data.** Creates false precision before the event and evidence quality are understood.

## Rationale

Separate confidence and cost fields preserve meaning and allow team-specific risk policy. They also let the team measure impact calibration and runtime prediction independently.

## Consequences

- Observation storage retains duration samples alongside aggregates.
- Reports show evidence provenance and sample maturity.
- Missing runtime data needs an explicit value; zero would be false data.
- The planner can compare several candidate sets under the same policy.
- No numeric impact-confidence threshold is established by this ADR.

## Reevaluation conditions

Revisit the model after collecting representative observation histories and defining event A. Any adopted estimator must be evaluated for calibration, cold-start behavior, drift, and explainability before its output is used as probability.
