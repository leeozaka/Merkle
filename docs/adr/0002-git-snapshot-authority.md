---
status: accepted
---

# Git supplies comparison snapshots; content defines identity

Merkle analyzes the difference between explicit code snapshots. Git is the initial snapshot provider, pull requests default to target versus current head, and content defines identity.

## Context

The original idea described feature branches nested beneath development branches. That shape is useful as a human convention but becomes fragile in rebases, detached heads, shallow clones, merge queues, local dirty trees, and CI systems that synthesize merge commits. Historical branch names may also disappear while the underlying content remains analyzable.

The user wants PR-first operation, a looser local mode, and the ability to restart local learned state when development becomes messy.

## Decision

Model every analysis as `base snapshot -> head snapshot`. The Git adapter resolves commits, PR metadata, merge bases, and working-tree content into immutable snapshot identities before the analysis engine runs.

For a pull request, the default base is the target branch snapshot and the default head is the current PR snapshot. Local commands may use explicit refs, a merge base, or a working-tree snapshot. Branch conventions can help resolve defaults but do not appear in semantic node or observation identity.

CI and local observations retain distinct provenance. If the adapter cannot resolve one base, it returns an analysis error instead of guessing from a branch name.

## Alternatives considered

- **Branch hierarchy as the product model.** Simple for one workflow, but unreliable across rebases, forks, detached CI jobs, and different hosting providers.
- **Commit IDs as the only identity.** Stable inside one repository history, but insufficient for dirty working trees and equivalent content reached through different histories.
- **Filesystem timestamps.** Fast but nondeterministic across checkouts and build environments.
- **Require rich history for every run.** Predictable, but unfriendly to shallow CI clones and local detached analysis.

## Rationale

Snapshot resolution keeps Git complexity at the boundary. Content-derived identity supports deterministic comparison, while Git provenance remains available for audit and history correlation.

## Consequences

- The engine accepts resolved snapshot manifests and leaves branch semantics to the Git adapter.
- The CLI must clearly report the resolved base and head.
- Working-tree snapshots need deterministic treatment of tracked, untracked, generated, and ignored files.
- Shallow or history-free environments need explicit-base guidance.
- Shared history records both content identity and source provenance.

## Reevaluation conditions

Revisit if a non-Git source becomes a first-class input or if a CI provider supplies a stronger immutable change-set contract. The `base -> head` model should remain even if the provider changes.
