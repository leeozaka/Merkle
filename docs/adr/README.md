# Architecture Decision Records

These records cover decisions that are expensive to reverse, surprising without context, or central to Merkle's trust model. **Deferred** leaves the choice open. **Proposed** marks an engineering recommendation awaiting evidence and maintainer acceptance.

| ADR | Decision | Status |
|---|---|---|
| [ADR-0001](0001-advisory-test-selector.md) | Merkle is an advisory selector, not a test-completeness guarantee | Accepted |
| [ADR-0002](0002-git-snapshot-authority.md) | Git supplies comparison snapshots; branch shape is not domain identity | Accepted |
| [ADR-0003](0003-semantic-merkle-and-impact-graph.md) | Separate the semantic Merkle tree from the reverse impact graph | Accepted |
| [ADR-0004](0004-repository-local-atomic-state.md) | Keep local state repository-local, Git-ignored, resettable, and atomically published | Accepted |
| [ADR-0005](0005-versioned-adapter-protocol.md) | Use a versioned, capability-negotiated language-adapter protocol | Accepted |
| [ADR-0006](0006-dotnet-first-deep-adapter.md) | Build the first deep adapter for .NET 6+; exclude TypeScript from current first-party scope | Accepted |
| [ADR-0007](0007-dependency-free-observation.md) | Do not inject observation dependencies into target projects | Accepted |
| [ADR-0008](0008-build-and-analysis-contract.md) | Build by default; make no-build strict; classify compilation failures as analysis errors | Accepted |
| [ADR-0009](0009-serial-deep-observation.md) | Run deep observation serially in V1 | Accepted |
| [ADR-0010](0010-probability-cost-ranking.md) | Keep impact confidence separate from mean-runtime cost | Accepted |
| [ADR-0011](0011-savings-and-confidence-policy.md) | Default the savings floor to 30%; do not default the confidence threshold | Accepted |
| [ADR-0012](0012-sqlite-local-store.md) | Use SQLite locally behind a provider boundary | Accepted |
| [ADR-0013](0013-core-language-deferred.md) | Defer the core implementation language until focused spikes are complete | Superseded by ADR-0015 |
| [ADR-0014](0014-second-official-adapter-deferred.md) | Consider Go later, but promise no second first-party adapter yet | Superseded |
| [ADR-0015](0015-dotnet-10-native-aot-core.md) | Use C# on .NET 10 with Native AOT as the core toolchain | Accepted |
| [ADR-0016](0016-startup-hook-observation.md) | Use a startup hook for dependency-free coarse .NET observation | Accepted |
| [ADR-0017](0017-first-party-go-deep-adapter.md) | Ship a first-party deep Go adapter | Accepted |
| [ADR-0018](0018-selectable-adapter-builds.md) | Build selected adapters through a dedicated helper | Accepted |

## Maintenance rules

- Preserve old records when a choice changes; mark them superseded and link the replacement ADR.
- Leave routine implementation details out of ADRs.
- Keep accepted user decisions distinct from proposed engineering recommendations.
- A missing confidence threshold never authorizes an invented one.
