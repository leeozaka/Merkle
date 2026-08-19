---
name: change-merkle-planning-rules
description: Change Merkle's language-detection rules, candidate construction, history estimates, planning order, confidence and savings policy, unmapped behavior, configuration, and plan reporting. Use for `LanguageDetector`, `ImpactEngine`, `HistoryModel`, `PlanPolicy`, `.merkle.yml` policy fields, pedantic mode, `decision-not-configured`, full-suite fallback, or planning-related acceptance tests. Do not use for adapter-owned mapping semantics or runtime observation mechanics.
---

# Change Merkle planning rules

Keep planning advisory, explainable, deterministic, and explicitly configured where it can authorize execution.

## Locate the rule

Read specification sections 7 through 12 and 15, plus ADR-0001, ADR-0010, and ADR-0011. Read `CONTEXT.md` before changing names or report language.

Then classify the change:

- Language evidence and mixed-repository selection: `LanguageDetector`
- Snapshot-to-candidate orchestration: `ImpactEngine`
- Static explanation paths: `ImpactIndex` and the active adapter
- Historical probability, confidence, and runtime: `HistoryModel`
- Selection order and recommendation: `PlanPolicy`
- Reviewed defaults and validation: `MerkleConfigurationLoader`
- CLI overrides and execution gate: `CliApplication` and `DeepExecutionEngine`
- Terminal schema and text: `TerminalReport` and report renderers

Read [references/planning-flow.md](references/planning-flow.md) for the end-to-end data path and [references/current-gaps.md](references/current-gaps.md) before adding a new rule type or profile behavior.

## Preserve the planning contract

1. Keep adapter-requested mandatory tests selected regardless of runtime cost.
2. Keep impact probability, evidence confidence, and expected duration as separate nullable values.
3. Require both `confidenceThreshold` and `onLowConfidence` before policy can authorize selected execution.
4. Return `decision-not-configured` when discretionary candidates exist without a complete automatic policy. Make `run` stop without executing.
5. Apply the savings floor only when automatic policy is configured and selected/full durations are comparable.
6. Warn and continue for unmapped source by default. Convert it to `PolicyFailure:UnmappedSource` only when pedantic or reviewed configuration says `fail`.
7. Treat selected-only absence as censored history, never a negative label.
8. Keep ordering stable: mandatory, probability, confidence, duration, then ordinal test identity unless an accepted contract change says otherwise.
9. Keep typed error class and code stable. Do not infer behavior from display text.
10. Preserve every excluded candidate and its reason in the terminal report.

## Change detection rules

1. Add manifest or source patterns to `LanguageDetector.CreateDefault` with a canonical language identifier.
2. Normalize paths before matching and keep evidence order deterministic.
3. Do not equate detection with adapter availability. TypeScript is detected today without a registered first-party adapter.
4. Preserve the mixed-language rule: no implicit selection when more than one language is detected.
5. Update CLI alias parsing, configuration parsing, and registry setup separately when a new canonical identifier is supported.
6. Test casing, duplicate evidence, manifest classification, selected-but-not-detected failures, and mixed-language diagnostics.

## Change policy or history behavior

1. Write boundary tests for every new range, null case, equality edge, and invalid value.
2. Trace configuration file value, CLI override, effective `PolicyConfiguration`, `PlanDecision`, rendered report, exit code, and deep-execution gate.
3. Keep repository-owned policy separate from adapter mapping. Adapters request tests and give reasons; the core decides recommendation and execution authority.
4. Keep full-suite calibration provenance and compatibility requirements intact when changing history estimates.
5. Update the specification for observable semantics. Add or supersede an ADR when changing the advisory guarantee, probability/confidence model, default savings floor, or required confidence action.

## Verify

Read [references/verification.md](references/verification.md). Run focused policy, engine, configuration, history, CLI, and reporting tests based on the changed path. Add an acceptance-style test when the behavior crosses more than one module.

In the handoff, state the effective rule, its configuration source, behavior at null and boundary values, recommendation and exit-code effects, evidence used, and any intentionally unimplemented rule surface.
