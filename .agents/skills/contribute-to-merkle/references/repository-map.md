# Repository map

Use this map to find the owning seam. Read the source before editing; paths describe the current layout, not a substitute for it.

| Area | Main entry points | Closest tests | Watch for |
|---|---|---|---|
| CLI and composition | `src/cli/Program.cs`, `CliApplication.cs`, `CommandLineParser.cs`, `Commands.cs` | `tests/Merkle.Tests/Cli/` | Exit codes, stdout versus stderr, CLI-over-config precedence, Native AOT composition |
| Planning orchestration | `src/core/Engine/ImpactEngine.cs` | `tests/Merkle.Tests/Engine/ImpactEngineTests.cs` | Snapshot binding, capability negotiation, deterministic merging, terminal publication |
| Deep execution | `src/core/Engine/DeepExecutionEngine.cs`, `src/core/Adapters/DeepAdapterContracts.cs` | `tests/Merkle.Tests/Engine/DeepExecutionEngineTests.cs` | Plan gate before execution, exact identity resolution, complete versus partial history |
| Adapter contracts | `src/core/Adapters/` | `tests/Merkle.Tests/Adapters/`, `tests/Merkle.Tests/Conformance/` | Protocol and identity versions, capabilities, bounded process I/O |
| Indexing | `src/core/Indexing/MerkleIndex.cs`, `ImpactIndex.cs`, `CanonicalHash.cs` | `tests/Merkle.Tests/Indexing/` | Canonical encoding, ordinal ordering, cycles, explanation paths |
| Planning policy | `src/core/Planning/PlanPolicy.cs` | `tests/Merkle.Tests/Planning/PlanPolicyTests.cs` | Mandatory tests, null estimates, configured confidence action, savings floor |
| History | `src/core/History/` | `tests/Merkle.Tests/History/` | Censored selected-only runs, compatibility keys, provenance, probability/confidence separation |
| Snapshots | `src/infrastructure/Snapshots/GitSnapshotSource.cs`, `src/core/Snapshots/ISnapshotSource.cs` | `tests/Merkle.Tests/Infrastructure/GitSnapshotSourceTests.cs`, `tests/Merkle.Tests/Domain/SnapshotTests.cs` | Merge-base semantics, frozen worktrees, repository-relative normalized paths |
| State | `src/core/State/`, `src/infrastructure/State/` | `tests/Merkle.Tests/State/`, `tests/Merkle.Tests/Infrastructure/*State*Tests.cs` | Exact reset target, atomic publication, schema compatibility, remote CAS |
| Reporting | `src/core/Reporting/` | `tests/Merkle.Tests/Reporting/` | Schema stability, redaction, bounded diagnostics, readable no-color text |
| Configuration | `src/core/Configuration/MerkleConfigurationLoader.cs` | `tests/Merkle.Tests/Configuration/` | Reject unknown and duplicate fields; never invent safety defaults |
| Source build | `src/build/`, root `build` helper | `tests/Merkle.Tests/Build/` | Strict versus best effort, staged publication, ownership markers, manifest hashes |
| .NET adapter | `src/adapters/dotnet/` | `tests/Merkle.Tests/Adapters/DotNet*Tests.cs` | One solution, no target package changes, startup-hook blind spots |
| Go adapter | `src/adapters/go/` | `tests/Merkle.Tests/Adapters/Go*Tests.cs`, Go worker tests | Canonical ID `golang`, module scope, immutable test artifacts, file-level observation |
| Python adapter | `src/adapters/python/` | `src/adapters/python/tests/` | One-request protocol worker, deterministic zipapp, minimal/semantic only |
| Java adapter | `src/adapters/java/` | `src/adapters/java/src/test/` | Maven shaded JAR, minimal/semantic only, stdout protocol isolation |

## Authority by change type

- Observable behavior: `docs/specification.md`
- Architecture and component ownership: `docs/system-design.md`
- Delivery and implementation guidance: `docs/implementation-guide.md`
- Build and operator behavior: `USAGE.md`, `docs/operations.md`
- Adapter contract: `docs/adapter-authoring.md`
- Accepted or superseded decisions: `docs/adr/README.md` and the linked ADR
- Historical rationale only: `docs/conversation-decisions.md`
