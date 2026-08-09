---
status: accepted
---

# Use a startup hook for dependency-free coarse .NET observation

## Context

ADR-0007 requires runtime observation without changing the analyzed repository's project files or package graph. A CLR profiler can provide member-level evidence, but it adds native binaries for every runtime identifier, a COM-style activation boundary, and substantially more unsafe and platform-specific code. Phase 3 needs a shippable observation path on macOS and Linux while preserving an adapter seam for later precision work.

## Decision

Launch each discovered test in its own bounded `dotnet test` process and attach a Merkle-managed assembly through `DOTNET_STARTUP_HOOKS`. The hook records assemblies loaded during that test process. The adapter converts only complete observations into assembly and project evidence; partial scopes do not enter the impact index or history.

Treat these as explicit blind spots: member-level execution, reflection-only resolution, native code, child processes, assemblies loaded before the hook, and a runner identity that cannot be matched to a stable test identity. Reports expose the reduced precision rather than implying member-level coverage.

The observer is a companion managed artifact outside the Native AOT CLI. It never writes to the target repository or changes its manifests. Test execution stays serial under ADR-0009.

## Alternatives considered

- **Native CLR profiler.** More precise, but creates a larger platform and security surface before the planning and evidence model is established.
- **Test-framework packages.** Easier test-boundary callbacks, but violates the no-injection boundary and alters restore graphs.
- **Static analysis only.** Simpler, but cannot contribute runtime evidence.
- **Whole-suite observation.** Faster to orchestrate, but cannot attribute evidence to one stable test identity.

## Rationale

The startup-hook design uses a supported runtime boundary, keeps analyzed projects untouched, and isolates dynamic tooling from the AOT core. Process-per-test execution gives a clear attribution boundary. Coarse, disclosed evidence is preferable to unverified precision.

## Consequences

- Observation is slower than shared-process execution.
- Assembly and project changes can select more tests than member-level evidence would.
- The managed observer must ship beside every CLI artifact.
- Runtime and test-platform compatibility need macOS/Linux integration coverage.
- The adapter protocol and evidence model keep room for a future profiler without changing core planning contracts.

## Reevaluation conditions

Revisit when measured over-selection prevents the configured savings target, process-per-test cost is unacceptable at repository scale, or a supported runtime blocks startup hooks. A replacement must keep the no-injection boundary and prove identity correlation, completeness, cancellation, and packaging across the release matrix.
