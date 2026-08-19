---
name: develop-merkle-adapters
description: Add or change a Merkle language adapter across Protocol 1.0, stable identities, indexing and mapping, deep host capabilities, source-build registration, packaging, and conformance tests. Use for new language support, adapter worker changes, detect/index/map behavior, capability descriptors, build catalog entries, worker artifacts, manifests, or deep adapter operations. Do not use for planning policy or runtime observation changes that do not alter an adapter contract.
---

# Develop Merkle adapters

Build adapters as bounded translators from one language ecosystem into Merkle's shared domain. Keep policy in the core and language semantics in the adapter.

## Choose the adapter path

1. Use a process worker for a minimal or semantic adapter. Implement one bounded Protocol 1.0 request on stdin and one response on stdout.
2. Add a typed C# host when the adapter needs build, discovery, selected execution, or observation. Compose the host with the process worker when static analysis remains language-native.
3. Treat `detect`, `index`, and `map` as the minimum useful contract. Advertise only operations that the current runtime can perform.
4. Use `golang` as the canonical Go identifier. Treat aliases as CLI/build parsing concerns, not stored identity.

Read `docs/adapter-authoring.md`, `CONTEXT.md`, specification sections 4 and 6b, ADR-0005, and the closest existing adapter. Then read:

- [references/integration-map.md](references/integration-map.md) for every repository seam an adapter can touch.
- [references/known-contract-gaps.md](references/known-contract-gaps.md) before copying examples from the docs.

## Define the contract first

1. Choose stable lowercase language and producer identifiers.
2. Set protocol, adapter, source-unit identity, and test-identity versions explicitly.
3. Define repository-relative identities that use `/`, distinguish overloads or parameterization, and exclude absolute machine paths.
4. Define fallbacks for constructs that cannot be modeled safely. Return a coarser unit and warning instead of invented precision.
5. Define deterministic ordinal ordering for units, edges, tests, mappings, warnings, and artifacts.
6. Define blind spots and unsupported operations before implementing deep behavior.

A protocol response must echo `protocolVersion`, `requestId`, and `operation`, then return either a successful payload or a structured error. Keep stdout protocol-only and stderr diagnostic-only.

## Implement static analysis

1. Implement `describe` with accurate capabilities, profiles, targets, and platforms.
2. Implement detection evidence without deciding repository ownership or mixed-language scope.
3. Implement indexing with stable units, semantic signatures or hashes, containment, dependency edges, invalidation inputs, tests, and warnings.
4. Implement mapping with at least one explanation reason per requested test and explicit unmapped changed units.
5. Keep runtime budgets, confidence thresholds, and full-suite decisions out of the adapter.
6. Bound request, response, diagnostics, collections, and process output. Honor cancellation and close child processes.

Use `src/core/Adapters/ProcessLanguageAdapter.cs` as the host behavior to satisfy, including its size limits, validation, and noisy-stdout rejection.

## Add deep capabilities

Implement only the interfaces needed from `src/core/Adapters/DeepAdapterContracts.cs`:

- `IBuildPreparer`
- `ITestDiscoverer`
- `ISelectedTestResolver`
- `ISelectedTestExecutor`
- `ITestObserver`

Bind every build fingerprint to the snapshot, selected scope, configuration, platform, toolchain, adapter and observer versions, targets, and artifact hashes. Make `--no-build` reject absent, stale, moved, or tampered artifacts. Resolve stable test identities against the discovered runtime catalog; never fabricate a selector for an unresolved test.

Use `$change-merkle-observation-hooks` for observation completeness, serial attribution, hook mechanics, and evidence admission.

## Integrate the repository

1. Add detection patterns in `LanguageDetector` without confusing detection with supported adapter availability.
2. Add CLI alias normalization only when the language has an accepted alias.
3. Add the runtime artifact lookup and adapter registration in `src/cli/Program.cs`.
4. Add a `BuildAdapterBase` implementation and register it in `AdapterBuildCatalog`.
5. Preflight the toolchain, build into staging, run a Protocol 1.0 smoke request, hash artifacts, and return their profile metadata.
6. Package artifacts only below `workers/<adapter-id>/` and include them in schema-1 `adapters.json`.
7. Add manifest, source-build, package smoke, and composition-root tests. Do not stop after worker unit tests pass.
8. Update adapter documentation and add an ADR when accepting a new first-party support boundary or changing the protocol or identity compatibility contract.

Never install a toolchain or adapter from repository content. Never edit the repository under analysis, its source, manifests, lockfiles, dependency graph, or `.gitignore`.

## Verify

Read [references/verification.md](references/verification.md). Cover malformed envelopes, bounds, deterministic output, identity edge cases, direct and transitive mapping, cycles, unmapped units, cancellation, build and smoke failures, manifest inclusion, capability negotiation, and any deep fingerprint or outcome behavior.

Report the supported profile, tested platforms and toolchains, protocol and identity versions, package layout, blind spots, and checks not run.
