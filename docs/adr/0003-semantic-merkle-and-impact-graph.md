---
status: accepted
---

# Separate the semantic Merkle tree from the reverse impact graph

Use a deterministic semantic Merkle tree to localize content changes and a separate reverse impact graph to expand those changes to dependent code and tests.

## Context

The initial concept used a Merkle-like tree to find the highest relevant changed parent and run tests below it. Hierarchy works for containment: repository, solution, project, namespace, type, and method. It does not represent shared dependencies. A currency function used by payments and orders must fan out to both features even if those features live in unrelated directory subtrees.

Dynamic per-test observations and historical correlations also create many-to-many relationships that are naturally graph-shaped.

## Decision

Maintain two coordinated structures:

1. The **semantic Merkle tree** contains adapter-defined semantic nodes, containment edges, deterministic child ordering, and content hashes. Comparing roots descends only into changed subtrees and yields changed semantic units plus their relevant ancestors.
2. The **reverse impact graph** contains directed dependency and observation edges from code units toward dependants and tests. It expands changed units across shared code, callers, feature ownership, dynamic execution, and historical evidence.

Nodes share stable adapter-defined identities across both structures. Tree hashes accelerate change localization; graph traversal determines affected tests. A matching hash prunes an unchanged subtree. The earlier phrase “hash collisions root” appears to mean the nearest divergent ancestor. Confirm it before freezing the domain model.

## Alternatives considered

- **Filesystem Merkle tree only.** Fast to build, but coarse and blind to method-level or cross-feature dependencies.
- **One universal graph with recursive hashes.** Expressive, but cycles and nondeterministic edge ordering complicate hashing and incremental invalidation.
- **Dynamic coverage map only.** Accurate for observed executions, but weak at cold start and unable to explain never-observed code.
- **Static call graph only.** Useful before observations exist, but incomplete for runtime dispatch and reflection.

## Rationale

Containment locates changed content. Impact finds affected code and tests. Separate structures keep each algorithm deterministic and testable while several evidence sources share the same stable identities.

## Consequences

- Adapters must define stable semantic identities and deterministic serialization.
- The indexer owns tree construction; the planner owns graph expansion.
- Method/function granularity is available only when an adapter supports it.
- File-level minimal adapters can still participate by emitting coarser nodes and mappings.
- Cycles belong in the impact graph and require bounded traversal or strongly connected component handling. Tree hashing remains acyclic.
- Hash algorithm version and semantic-normalization version become part of index compatibility.

## Reevaluation conditions

Revisit if production data shows that semantic hashing provides no material incremental benefit, or if a unified content-addressed graph can preserve determinism and substantially simplify storage without weakening adapter boundaries.
