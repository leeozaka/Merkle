---
status: accepted
---

# Merkle is an advisory test selector, not a completeness guarantee

Merkle recommends an evidence-backed test plan to shorten commit and pull-request feedback loops. Teams still own periodic full-suite validation, and unselected tests can fail.

## Context

Test-impact analysis trades breadth for speed. Static dependency information is incomplete in the presence of reflection, generated code, runtime dispatch, external systems, and imperfect test coverage. Dynamic observations and historical correlations improve the selection but cannot prove that an unobserved dependency does not exist.

The product helps developers and coding agents iterate quickly. The user called its posture “loose strength” and expects teams to keep their own full-suite policy, such as a nightly run.

## Decision

Treat every result as an advisory plan with explainable evidence. Reports must show changed scopes, selected tests, unmapped changes, evidence maturity, estimated cost, expected savings, and any reason for falling back to the full suite.

Describe the output as a recommendation with known gaps. CI owners decide whether a Merkle plan is informative, gating, or supplemented by a full suite.

## Alternatives considered

- **Sound conservative selector.** Run every test that could possibly be affected. This is attractive as a guarantee but infeasible for dynamic behavior and often collapses to the full suite.
- **Coverage replacement.** Make Merkle responsible for proving test coverage. This duplicates established coverage and quality tools and changes the product's purpose.
- **Mandatory selective gate.** Enforce the selected set as the only required PR validation. This assigns a risk policy that belongs to each team.

## Rationale

The advisory boundary fits imperfect evidence and preserves the speed objective. The same engine can serve loose local development and controlled CI pipelines while recording their different authority.

## Consequences

- Explainability is a product feature, not optional diagnostics.
- Unmapped changed code is reported and continues by default; a strict mode may fail.
- Teams retain full-suite, coverage, and release-policy ownership.
- Documentation and CLI output must avoid guarantee language.
- A full-suite fallback is a normal plan outcome.

## Reevaluation conditions

Revisit only if a future product mode can make a formally defined guarantee for a restricted environment. That mode would need its own soundness model and separate command semantics.
