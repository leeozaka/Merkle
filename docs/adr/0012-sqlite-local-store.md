---
status: accepted
---

# Use SQLite locally behind a storage-provider boundary

## Context

Merkle needs to store semantic nodes, hashes, graph edges, test identities, run samples, runtime statistics, provenance, and schema versions. The store must support atomic visibility after a run concludes, repository-local operation, resetability, and efficient reverse queries. A team may later configure its own cloud or bare-metal shared store, but there is no first-party hosted service.

## Decision

Use a single-file SQLite database within the configurable Git-ignored state directory for local use. Access it only through versioned storage-provider interfaces owned by the engine's state module.

Use explicit schema migrations, foreign-key enforcement, transactions for run publication, and repository/snapshot namespaces. Treat the database as generated state: export or rebuild paths must exist, and deletion of only the validated state target is allowed during reset.

Define the remote provider contract from domain operations and immutable records. Do not expose SQLite files or SQL over the network. Remote history uses a separate HTTPS protocol; there is no first-party hosted server.

## Alternatives considered

- **Loose JSON/JSONL files.** Transparent and easy to inspect, but awkward for reverse edges, migrations, atomic multi-record publication, and concurrent readers.
- **Embedded key-value store.** Fast for known access paths, but graph queries and ad hoc diagnostics require more custom indexing.
- **Git-committed state.** Portable but creates merge conflicts, growth, and privacy concerns.
- **Mandatory remote relational database.** Strong shared access but contradicts local-first operation and creates infrastructure before it is needed.
- **One portable SQLite file shared over network storage.** Tempting but unsafe as a general remote concurrency strategy and couples remote design to local storage internals.

## Rationale

SQLite offers transactions, indexes, migrations, and local queries without provisioning a service. A provider boundary keeps this local choice from defining the shared-storage architecture.

The implementation spike passed atomic publication, migration, graph lookup, reset-boundary, Native AOT, and provider-substitution checks. Schema 2 stores terminal reports, indexes, and compatible history in one publication transaction. Release artifacts carry SQLite 3.53.3 through `SourceGear.sqlite3`; they do not carry the older `SQLitePCLRaw.lib.e_sqlite3` package affected by GHSA-2m69-gcr7-jv3q.

## Consequences

- The implementation language must have a mature SQLite integration and deployment story.
- Database schema version joins adapter/index compatibility in cache validation.
- Writers must use short, atomic publication transactions; long observation work occurs outside the commit transaction.
- Diagnostics should expose store path, size, schema version, and last complete run without leaking sensitive content by default.
- Remote history has separate authentication, compare-and-swap concurrency, retention, and provenance rules.

## Reevaluation conditions

Revisit if repository-scale measurements exceed the documented bounds, the native SQLite package cannot cover a supported target, or the local and remote provider contracts can no longer share domain records cleanly.
