# Adapter integration map

## Shared seams

| Concern | Files | Required result |
|---|---|---|
| Domain contracts | `src/core/Adapters/AdapterContracts.cs`, `src/core/Domain/DomainModels.cs` | Shared units, edges, tests, mappings, descriptors, and evidence kinds remain language-neutral |
| Process host | `src/core/Adapters/ProcessLanguageAdapter.cs` | One bounded request/response, validated descriptor and payload, bounded diagnostics |
| Deep contracts | `src/core/Adapters/DeepAdapterContracts.cs` | Build, discovery, resolution, execution, and observation remain separate capabilities |
| Registry | `src/core/Adapters/AdapterRegistry.cs` | Protocol, identity versions, profile, and required capabilities are checked before work |
| Detection | `src/core/Adapters/LanguageDetector.cs` | File evidence uses a canonical language identifier; detection does not imply support |
| CLI parsing | `src/cli/CommandLineParser.cs` | Accepted aliases normalize to the canonical identifier |
| Composition | `src/cli/Program.cs`, `src/cli/Merkle.Cli.csproj` | Worker lookup, host wrapper, registry entry, and development/package artifact paths agree |
| Source build | `src/build/Adapters/`, `AdapterBuildCatalog.cs` | Preflight, optional native tests, staged build, smoke, hashes, and metadata |
| Package output | `src/build/Packaging/BuildOutputPublisher.cs`, `AdapterManifestContract.cs` | Owned atomic output, safe `workers/` paths, lowercase SHA-256, schema-1 manifest |
| Conformance | `tests/Merkle.Tests/Conformance/`, `tests/Merkle.Tests/Adapters/` | Protocol, identity, mapping, host failure, and compatibility behavior are locked down |

## Existing adapter patterns

| Adapter | Static path | Deep path | Package |
|---|---|---|---|
| .NET | `DotNetAdapter` with `DotNetProcessAnalysisWorker` when present | `DotNetDeepOperations` and startup-hook observer | Managed worker and observer under `workers/dotnet/` |
| Go | Process-backed Go worker behind `GoAdapter` | `GoDeepOperations` using prepared test binaries and coverage | Native `workers/go/merkle-adapter-go` artifact; canonical ID `golang` |
| Python | `src/adapters/python/merkle_adapter/` process worker | None | Deterministic `.pyz`, minimal/semantic |
| Java | `src/adapters/java/` process worker | None | Maven shaded JAR, minimal/semantic |

Choose the pattern with the closest trust and runtime boundary. Do not copy an adapter's ecosystem-specific identities or packaging assumptions.

## Registration checklist

- Worker source and native tests
- Canonical descriptor and versions
- Detection pattern
- CLI alias, if accepted
- Runtime artifact lookup
- `AdapterRegistry` composition
- Build adapter definition and catalog registration
- Build preflight and smoke request
- Package path and manifest entry
- Process-host, conformance, build, package, and CLI tests
- Adapter documentation and accepted support boundary
