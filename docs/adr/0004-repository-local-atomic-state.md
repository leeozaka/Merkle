---
status: accepted
---

# Keep local state repository-local, Git-ignored, resettable, and atomically published

Persist local indexes and observations in a generated repository-local state directory. Recommend ignoring it in Git, expose a safe reset workflow, and publish a run's learned evidence only when the run concludes.

## Context

Local development should work without a hosted service, while teams may later point the same engine at user-owned shared storage. The user strongly prefers generated state to stay out of version control and wants local state to be disposable when experiments, rebases, or concurrent agent work make it confusing.

The user also decided that agents need to see results only after a run ends, whether the test result passes or fails. Partially collected mappings should not leak into planning as if they were complete.

## Decision

Place local generated state under one configurable hidden directory rooted in the repository. Documentation recommends a `.gitignore` entry. The tool leaves ignore files unchanged unless the user asks it to edit them.

Write each observation run into a staging transaction. When the run concludes, atomically publish its outcome, timings, mappings, provenance, and completion state. A failed test run may still produce a completed observation. Interrupted or structurally incomplete runs stay outside trusted evidence.

Provide explicit inspect, reset, and rebuild operations. Resets must target only the resolved Merkle state directory and must be safe against broad path deletion.

## Alternatives considered

- **Commit the index and observations to Git.** Portable, but creates churn, merge conflicts, repository growth, and potential leakage of paths or test metadata.
- **User-global cache only.** Avoids repository files, but makes repository isolation, CI caching, and reset behavior harder to understand.
- **Publish observations incrementally.** Enables live consumers, but exposes partial mappings and complicates trust under cancellation.
- **Require a shared server.** Centralizes state, but contradicts local-first and self-hosted-by-choice goals.

## Rationale

Repository locality makes ownership and cache lifecycle clear. Completion-atomic publication gives concurrent tools one rule: visible evidence represents a concluded run. Resetability supports the deliberately looser local trust model.

## Consequences

- Local state is a generated cache and evidence store. It does not need a Git commit.
- CI cache configuration can persist the directory explicitly when desired.
- Readers need snapshot isolation or an equivalent mechanism that hides half-published runs.
- The state schema must preserve run provenance and completion status.
- Failed tests and failed external dependencies remain normal completed outcomes; process interruption and analysis failure remain distinct.
- The exact local database technology is decided separately in ADR-0012.

## Reevaluation conditions

Revisit if repository-local state creates unacceptable disk duplication across worktrees, or if distributed teams require a shared store as the default. Any replacement must preserve local operation, explicit provenance, atomic visibility, and resetability.
