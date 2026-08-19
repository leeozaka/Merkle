# Planning flow

## End-to-end path

1. `CliApplication` parses command values and overlays them on `.merkle.yml`.
2. `GitSnapshotSource` binds immutable baseline and candidate snapshots.
3. `LanguageDetector` returns deterministic evidence. `ImpactEngine` resolves or validates explicit selections.
4. `AdapterRegistry` checks Protocol 1.0, identity versions, profile membership, and `detect/index/map` capabilities.
5. `ImpactEngine` reads or builds compatible baseline and candidate indexes.
6. `MerkleIndex.Compare` finds changed source units.
7. The adapter maps changes against candidate units plus baseline and candidate edges so deleted relationships remain available.
8. `ImpactEngine` merges requested tests and explicit unmapped units across adapters.
9. `HistoryModel` adds compatible probability, confidence, and duration estimates. Incompatible runs are counted, not coerced.
10. `PlanPolicy` validates values, orders candidates, applies confidence action and savings floor, and returns a recommendation.
11. `TerminalReport` records candidates, exclusions, warnings, economics, effective policy, recommendation, and decisive reason.
12. State publication exposes the complete terminal result atomically.
13. `DeepExecutionEngine` plans first. It executes only `selected` or `full-suite`; `plan-only`, `decision-not-configured`, or a failed plan stops execution.

## Main types

| Concern | Type |
|---|---|
| Request | `PlanRequest` |
| Candidate | `TestCandidate` |
| Effective policy | `PolicyConfiguration` |
| Policy output | `PlanDecision` and `PlanRecommendation` |
| Public output | `TerminalReport`, `ReportPolicy`, `ReportEconomics` |
| Unmapped enforcement | `UnmappedBehavior` and `PolicyException` |
| Historical estimates | `HistoryModel`, `HistoryCompatibility`, `HistoryTestEstimate` |

## Policy decision cases

| Condition | Expected recommendation or result |
|---|---|
| Mandatory mappings only, no automatic policy | `plan-only` |
| Discretionary candidates, incomplete automatic policy | `decision-not-configured` |
| Complete policy, candidates meet confidence, savings meets floor or is unavailable | `selected` |
| Low confidence and action `plan-only` | `plan-only` |
| Low confidence and action `full-suite` | `full-suite` |
| Low confidence and action `fail` | policy failure |
| Comparable savings below floor under automatic policy | `full-suite` |
| Unmapped source with `warn` | successful plan plus warning |
| Unmapped source with `fail` | policy failure with complete plan context |
