---
status: accepted
---

# Default savings to a 30% floor and leave confidence unset

Use a configurable minimum expected-savings floor with a 30% default. The user sets any impact-confidence acceptance threshold.

## Context

Selective execution is pointless when it costs nearly as much as the full suite. The user chose a 30% minimum improvement when no savings setting is supplied. Separately, the user declined to choose a universal level of acceptable miss risk and said that decision belongs to the team using the tool.

Conflating these values would turn a cost preference into a correctness claim. A plan can be cheap but weakly supported, or well-supported but barely faster.

## Decision

Calculate expected savings as a comparison between predicted selected-plan cost and predicted full-suite cost. Let users configure the minimum. If they do not, use **30% expected savings** as the floor; below that floor, recommend the full suite.

Ship no default impact-confidence threshold. Any workflow that makes an execution decision from confidence needs an explicit user/team policy or an explicitly chosen planning/full-suite mode. The CLI and configuration schema keep savings and confidence settings separate.

The informal 10–20% margin cannot supply a threshold because its meaning remains unresolved.

## Alternatives considered

- **Always run the selected set regardless of savings.** Wastes optimization overhead when little time is saved.
- **Use no default savings floor.** Forces configuration before the tool can make a cost-aware recommendation.
- **Use 30% as both savings and confidence.** Gives one number two incompatible meanings.
- **Choose a conservative default confidence threshold.** Still imposes the tool author's risk tolerance on users and suggests unjustified universal calibration.
- **Always fall back to full suite when confidence is unconfigured.** Safe but is still a default policy; the user has not selected it as the universal behavior.

## Rationale

The user chose 30% as an efficiency preference. Confidence expresses organizational risk tolerance and depends on a probability model that is still open. The configuration keeps those decisions separate.

## Consequences

- Plan output must show predicted selected cost, full cost, savings percentage, and applied floor.
- The command/config validator must detect when a requested selective-execution policy lacks an explicit confidence decision.
- Dry-run planning can still display confidence evidence without applying a universal cutoff.
- Documentation presents 30% only as an expected-savings floor.
- An organization can choose any confidence policy once the model's semantics are defined.

## Reevaluation conditions

Revisit the 30% savings default only after real usage shows it systematically prevents useful plans or produces negligible value. Establish a default confidence threshold only through a new explicit product decision backed by a defined, calibrated model.
