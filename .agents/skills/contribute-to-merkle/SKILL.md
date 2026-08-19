---
name: contribute-to-merkle
description: Guide repository-wide Merkle contributions through the authoritative design sources, module seams, targeted tests, CI-equivalent checks, and review handoff. Use for features, bug fixes, refactors, documentation or ADR changes, and work spanning the core, CLI, infrastructure, build, reporting, history, or state. Use the focused Merkle adapter, planning-rule, or observation-hook skill when one of those areas is the main subject.
---

# Contribute to Merkle

Keep changes inside Merkle's accepted product boundary and verify the narrow behavior before running broader gates.

## Start from authority

1. Read `docs/index.md` to resolve the source-of-truth order.
2. Read `CONTEXT.md` before naming domain concepts. Keep terms such as snapshot, source unit, requested test, impact probability, and evidence confidence distinct.
3. Read the relevant specification section and accepted ADRs before changing observable behavior or a hard-to-reverse seam.
4. Treat later accepted ADRs as overrides. Do not revive superseded choices from old design artifacts or `docs/conversation-decisions.md`.
5. Read [references/repository-map.md](references/repository-map.md) for the owning modules, entry points, and tests.
6. Read [references/current-gaps.md](references/current-gaps.md) when the task touches contributor tooling, CI coverage, documentation QA, or releases.

## Route focused work

- Use `$develop-merkle-adapters` for a new language, protocol worker, adapter capability, build catalog entry, or adapter package.
- Use `$change-merkle-planning-rules` for detection, candidate ranking, history estimates, policy decisions, unmapped behavior, or selection reports.
- Use `$change-merkle-observation-hooks` for deep build fingerprints, discovery, selected execution, runtime observation, startup hooks, or Go cover profiles.
- Combine the focused skill with this workflow when a change crosses one of those boundaries and the CLI, state, reporting, or packaging layers.

## Work through the seam

1. Inspect the worktree and preserve unrelated changes.
2. State the behavior being changed, its owner, and the contracts it crosses.
3. Trace one complete flow from CLI input to terminal report or build artifact before editing.
4. Add or update the closest test first when the behavior is easy to isolate.
5. Change the smallest owning module. Keep Git, storage, process execution, runtime observers, and rendering behind their existing interfaces.
6. Preserve these repository-wide constraints:
   - Keep Merkle advisory. Never claim omitted tests are proven safe.
   - Keep output and ordering deterministic for identical inputs.
   - Keep impact probability separate from evidence confidence.
   - Keep machine-readable error class and code separate from display text.
   - Publish terminal state atomically; never admit partial or incompatible evidence.
   - Redact secrets and bound persisted or process output.
   - Preserve Native AOT compatibility in the CLI and isolate dynamic tooling in companion processes.
   - Never modify a repository under analysis, its dependency graph, or its `.gitignore`.
7. Update documentation only when the public contract, architecture, operation, or accepted decision changed. Add an ADR for a costly, surprising, or trust-boundary decision; do not use an ADR for routine implementation detail.

## Verify proportionally

Read [references/verification.md](references/verification.md), then:

1. Run the narrow unit or adapter-native test for the changed behavior.
2. Run the owning project tests.
3. Run formatting and warnings-as-errors checks for changed languages.
4. Run the source-build or publish path when packaging, manifests, runtime artifacts, or adapter selection changed.
5. Compare the final diff with the specification, accepted ADRs, and `.github/pull_request_template.md`.

Do not weaken the 80% aggregate line and branch coverage threshold to make a change pass.

## Hand off

Report the behavior changed, the important files, tests and commands run, any checks not run, compatibility effects, and remaining blind spots. Do not commit or push unless the user asks explicitly.
