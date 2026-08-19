---
status: accepted
---

# Build selected adapters through a dedicated helper

Merkle source builds use a .NET build helper, launched through `./build`, to select and package repository-owned language adapters. The helper keeps adapter packaging separate from runtime analysis, builds the Merkle host only after the successful adapter set is known, and publishes through a temporary destination so a failed invocation cannot replace the previous output.

## Decision

The helper exposes strict and best-effort adapter build policies. Strict policy rejects an unavailable selected toolchain before compilation and stops at the first attempted adapter failure. Best effort skips unavailable adapters and continues after adapter failures, but it fails when no requested adapter succeeds. Adapter-scoped failures never cover helper, host, filesystem, manifest, or packaging failures.

Plain `dotnet build` and `dotnet publish` produce the default .NET-only application. They do not invoke Go, Java, or Python. Configurable builds use the helper and a common adapter build interface. The adapter catalog contains repository-owned implementations; runtime discovery may still load compatible external adapters after publication.

Official releases pin `.NET`, Go, Python, and Java under strict policy. Local builds default to the .NET adapter under strict policy. A future adapter joins `all` when its repository build definition is registered, but it joins the pinned release set only through a deliberate release change.

## Consequences

- The .NET SDK remains mandatory because the host and helper are .NET projects, even when the .NET language adapter is not selected.
- Successful packages contain a deterministic adapter manifest. Run-specific failures, tool versions, paths, and logs remain outside the package.
- Adapter builds may run sequentially or with bounded parallelism. Runtime analysis and discovery keep their existing execution contract.
- Inert first-party support code may remain in the host when an adapter is unselected; only current-run successful payloads are bundled and registered.
- Cross-target publication is deferred because mandatory smoke checks require a runnable target.
