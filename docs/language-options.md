# Implementation Language and Toolkit Analysis

Status: historical analysis; superseded for core selection by [ADR-0015](adr/0015-dotnet-10-native-aot-core.md)  
Research date: 2026-08-07  
Target operating systems: macOS and Linux; WSL may work but is not a supported target  
Initial language adapter: .NET 6 and later  
First-party adapter: Go

Explicitly out of the current official-adapter scope: TypeScript/Node.js

## 1. Accepted direction and validation result

ADR-0015 selected C# on .NET 10. The delivered topology uses a Native AOT CLI, a managed Roslyn worker, and the managed startup-hook observer accepted by ADR-0016. Language adapters retain protocol 1.0 as a bounded process seam.

The implementation gate covers:

1. Load representative .NET 6+ solutions under their own SDK-selection rules.
2. Discover stable test identities and execute selected tests with both `--no-build` and default-build behavior.
3. Record complete per-test assembly/project observations without modifying the target project, with finer-grained blind spots disclosed.
4. Incrementally update the Merkle/index state in SQLite and reproduce the same selection from the same snapshot.
5. Publish Native AOT artifacts for the supported OS/architecture matrix.

If the team later proves that a single small native core is more valuable than implementation cohesion, keep the adapter protocol and reconsider Go for the orchestration core or Rust for the performance-sensitive core and profiler. That seam lets the team change the core without rewriting the impact model, state schema, or CLI contract.

C# keeps more of the first spike in one ecosystem. Roslyn, MSBuild, VSTest, Microsoft.Testing.Platform, and the target programs are all .NET technologies. A non-.NET core still requires a C# semantic adapter and an unmanaged profiler, creating three implementation surfaces before the product has proved its selection model.

## 2. Constraints that affect the choice

### 2.1 Product constraints

- The tool is an advisory test-impact assistant. Scheduled full-suite execution remains necessary.
- The initial useful result is a list of affected tests; deeper modes may discover, observe, execute, and report.
- The official first adapter must support .NET 6 and later.
- The target repository must not need to add a NuGet, Node, or equivalent project dependency.
- The default .NET path builds first. `--no-build` may reuse an existing compatible build and must fail when the required output is absent.
- Compilation failure is an analysis error.
- Deep observation starts serially; per-test parallel execution is a later optimization.
- macOS and Linux are first-class. Native Windows support is not in scope.
- A mixed-language repository must explicitly choose adapters and depths, for example `--languages=dotnet:deep,go:minimal`.
- Missing mappings are advisory by default and become errors only under an explicit strict/pedantic policy.
- State is repository-local and should normally be ignored by Git. When a team needs shared persistence, it supplies the storage; Merkle has no hosted service.

### 2.2 Tool dependencies stay outside target repositories

The analyzed repository should not need to reference a Merkle package. The Merkle executable may depend on libraries and bundle them into its distribution. It can use Roslyn, a SQLite provider, a CLI parser, or an unmanaged profiler without changing the user's application dependency graph.

### 2.3 Keep Git behind a snapshot boundary

The core should ask for two immutable source snapshots and their change set. A Git adapter can resolve the default PR base with `git merge-base` and consume machine-safe diff output. Git documents `merge-base` as the best common ancestor operation, and its raw diff format supports NUL-delimited pathnames with `-z`, avoiding quoting ambiguities in arbitrary file names. [Git merge-base documentation](https://git-scm.com/docs/git-merge-base), [Git diff raw-format documentation](https://git-scm.com/docs/git-diff.html)

Starting by spawning the installed `git` CLI has two advantages across every language option:

- identical snapshot semantics and edge-case handling regardless of implementation language;
- no need to select and maintain a different embedded Git implementation per runtime.

An embedded Git provider can be added later behind the same snapshot port if startup or process overhead becomes measurable.

## 3. Deep .NET observation spans managed and native code

Even with a C# CLI, dependency-free runtime observation still needs unmanaged code. Microsoft describes a CLR profiler as an unmanaged DLL/shared library loaded by the runtime, and warns that the profiler itself must be completely unmanaged; analysis and user interface work should remain out of process. The profiling API can monitor JIT/function events and modify CIL before or during JIT recompilation. [Microsoft CLR profiling overview](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/profiling/profiling-overview)

Deep .NET v1 requires these implementation surfaces:

| Candidate core | Required implementation surfaces for deep .NET v1 |
|---|---|
| C#/.NET | Managed core + .NET adapter; unmanaged profiler helper |
| Go | Go core; C#/.NET semantic/test adapter; unmanaged profiler helper |
| Rust | Rust core; C#/.NET semantic/test adapter; Rust or C++ profiler binding/helper |
| Python | Python core; C#/.NET semantic/test adapter; unmanaged profiler helper |
| JVM/Kotlin | JVM core; C#/.NET semantic/test adapter; unmanaged profiler helper |

The unmanaged helper may be written in C++ against the official CLR profiling headers, or prototyped in Rust with a deliberately thin C ABI/COM layer. Rust does not remove the need to validate ABI correctness, callback lifetime, reentrancy, and runtime-version behavior.

## 4. Common architecture independent of language

Keep the implementation language behind these stable seams:

| Port | Responsibility | Recommended v1 mechanism |
|---|---|---|
| `SourceSnapshotProvider` | Resolve baseline/current snapshots and changed paths | Spawn Git with argument arrays; never invoke a shell |
| `LanguageDetector` | Detect candidate languages and solutions/modules | File/signature scan; require user selection in mixed repos |
| `LanguageAdapter` | Index symbols, dependencies, tests, and runner commands | Versioned JSON Lines process protocol |
| `ObservationCollector` | Associate executed symbols with stable test IDs | External profiler + serial test execution in v1 |
| `ImpactStore` | Persist hashes, symbols, edges, observations, and run history | SQLite in a Git-ignored directory |
| `ImpactPlanner` | Combine structural, dynamic, and historical evidence | Pure domain module, deterministic for a fixed snapshot/store |
| `TestExecutor` | Build, filter, execute, time out, and normalize results | Adapter-owned process execution |
| `Reporter` | Human text, JSON, and dry-run manifests | Stable output schema owned by the core |

The process protocol should be language-neutral even if core and first adapter initially share a binary. Treat the in-process implementation as an optimization of the same contract. That keeps official and third-party adapters independently releasable and makes unsupported capabilities explicit, such as `FUNCTION_NOT_AVAILABLE: language=go capability=deep-observation`.

## 5. Candidate comparison

### 5.1 Summary matrix

Scores are project-specific directional judgments from 1 (poor fit) to 5 (strong fit).

| Criterion | Weight | C#/.NET | Go | Rust | Python | JVM/Kotlin |
|---|---:|---:|---:|---:|---:|---:|
| .NET semantic/test integration | 30% | 5 | 2 | 2 | 2 | 2 |
| macOS/Linux distribution | 20% | 4 | 5 | 5 | 2 | 3 |
| indexing/hash throughput potential | 15% | 4 | 5 | 5 | 2 | 4 |
| path to unmanaged profiler | 15% | 3 | 2 | 3 | 1 | 1 |
| contributor accessibility | 10% | 4 | 4 | 3 | 5 | 4 |
| v1 implementation cohesion | 10% | 5 | 2 | 2 | 2 | 2 |
| Weighted directional score | 100% | **4.25** | **3.25** | **3.30** | **2.15** | **2.55** |

The score changes if priorities change. For example, giving single-native-binary distribution more weight would improve Go and Rust; making official Go analysis the first adapter would improve Go substantially.

### 5.2 Relative effort

These are t-shirt estimates for reaching the same deep .NET proof, not calendar commitments.

| Candidate | Relative v1 effort | Why |
|---|---|---|
| C#/.NET core | Medium; lowest of the candidates | One managed codebase covers orchestration, Roslyn/MSBuild, test discovery, planning, and reporting; native profiler remains separate |
| Go core | High | Adds a process protocol and C# adapter immediately; native profiler remains separate |
| Rust core | Very high | Same three-part split as Go plus a steeper ABI/unsafe and contributor-learning burden |
| Python core | Medium to prototype, high to harden | Fast orchestration work, but production packaging, startup, memory, concurrency, and native integration require extra hardening |
| JVM/Kotlin core | High | Adds a second managed runtime with no direct advantage for .NET semantics; native-image work adds another packaging dimension |

For a minimal-mode-only prototype, Python or Go becomes more attractive. For the stated full .NET adapter, the deep path drives the schedule more than hashing.

## 6. Option A: C#/.NET

### Toolkit

| Concern | Suggested toolkit |
|---|---|
| CLI | `System.CommandLine`, or a deliberately small internal parser if dependency minimization outweighs ergonomics |
| Process execution | `System.Diagnostics.Process` with argument lists, redirected streams, cancellation, and process-tree termination |
| Git | External `git` process using merge-base and raw NUL-delimited diff |
| Hashing/Merkle | `System.Security.Cryptography.SHA256` for durable content IDs; optionally a faster non-cryptographic prefilter only after profiling |
| Index storage | SQLite through `Microsoft.Data.Sqlite` inside the tool distribution |
| .NET semantic model | Roslyn compiler and Workspace APIs, `MSBuildWorkspace`, MSBuild SDK resolution |
| Test discovery/execution | VSTest-compatible `dotnet test` for .NET 6+; explicit Microsoft.Testing.Platform support as a separate capability |
| Runtime observation | Native CLR profiler helper loaded only into test host processes |
| Packaging | Native AOT publication per runtime identifier; isolate incompatible dynamic tooling behind the adapter process boundary |
| Project tests | MSTest, xUnit, or NUnit for managed modules; a native test framework for the profiler helper |

`System.CommandLine` provides parsing, help generation, validation, completion, and response-file support, and Microsoft documents it as trim-friendly and suitable for lightweight/AOT-capable CLIs. It is a dependency of the Merkle tool, not of target repositories. [System.CommandLine overview](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)

`Microsoft.Data.Sqlite` is Microsoft's lightweight ADO.NET provider for SQLite and can be used without Entity Framework. [Microsoft.Data.Sqlite overview](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)

### Why it fits the .NET-first scope

Roslyn exposes syntax trees, symbols, semantic models, compilations, and a Workspace layer that represents whole solutions. The adapter needs those APIs to distinguish a changed method from its containing type/project and to build reverse symbol dependencies with compiler fidelity. [Roslyn SDK object model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)

`dotnet test` applies to .NET 6 SDK and later, builds by default, and runs tests using VSTest or Microsoft.Testing.Platform depending on the SDK/repository configuration. Before .NET 10, VSTest is the available runner through `dotnet test`; runner selection through `global.json` begins with .NET 10. [dotnet test reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test)

VSTest-style filter expressions can select tests by fields such as fully qualified name, name, class, and framework-specific traits. The adapter must escape generated filter values rather than concatenating untrusted test names. [Selective unit-test documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/selective-unit-tests)

### SDK selection recommendation

Do not force “the highest installed SDK” globally. Run `dotnet` from the repository/solution context and let the .NET SDK resolver honor `global.json`; when no `global.json` exists, the CLI selects an installed SDK according to its normal rules. Microsoft notes that SDK selection is separate from the project's target runtime and provides `rollForward` for controlled compatibility. [global.json overview](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)

Honoring repository SDK rules preserves reproducibility in CI and still permits a recent SDK to build older target frameworks when the repository and installed targeting packs support it. The adapter should report the resolved SDK, target frameworks, runner, and build command in every analysis manifest.

### Packaging

.NET supports self-contained and single-file deployment, but publications are OS/architecture-specific. Produce separate signed/checksummed artifacts such as `osx-arm64`, `osx-x64`, `linux-x64`, and later `linux-arm64`. [Microsoft single-file deployment guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

Use Native AOT publication from the first executable and run publish validation continuously. Roslyn, MSBuild, test-platform discovery, native SQLite, and adapter loading all require explicit trimming/AOT verification. If a dependency cannot satisfy those constraints, isolate it in the versioned adapter process and record the exception; do not silently turn AOT off for the core CLI.

### Main risks

- The profiler helper is still native and must be built per supported OS/architecture.
- Roslyn/MSBuild packages increase distribution size and may complicate trimming.
- `MSBuildWorkspace` must be tested against multi-targeted projects, custom SDKs, source generators, analyzers, conditional compilation, and repositories pinned by `global.json`.
- VSTest and Microsoft.Testing.Platform have different execution models and CLI capabilities; do not pretend one implementation transparently covers both.
- Stable IDs for parameterized/generated tests and compiler-generated methods require explicit normalization rules.

## 7. Option B: Go

### Toolkit

| Concern | Suggested toolkit |
|---|---|
| CLI | Standard `flag` for a small interface or Cobra for nested commands/completion |
| Process execution | Standard `os/exec` |
| Git | External `git` process; `go-git` only if an embedded provider is later justified |
| Hashing/Merkle | Standard `crypto/sha256`; byte buffers and streaming I/O |
| Index storage | Standard `database/sql` plus a SQLite driver such as the CGo-free `modernc.org/sqlite` |
| Adapter protocol | JSON Lines over stdin/stdout with `encoding/json` |
| Go analysis adapter | `go list -deps -json`, `go test -list`, `go test -json`, and optional serial coverage profiles |
| .NET analysis | Separate C# adapter process plus native profiler helper |
| Packaging | Native binaries per `GOOS`/`GOARCH`; `CGO_ENABLED=0` when all chosen dependencies permit it |
| Project tests | Standard `testing`, golden files, fuzz tests, integration fixtures |

Go's standard `os/exec` invokes external programs without automatically invoking a shell, which is a good default for safe Git and adapter execution. The standard library also supplies SHA-256 and a generic SQL interface, although SQLite itself requires a driver. [Go `os/exec`](https://pkg.go.dev/os/exec), [Go `crypto/sha256`](https://pkg.go.dev/crypto/sha256), [Go `database/sql`](https://pkg.go.dev/database/sql)

The `modernc.org/sqlite` driver documents a CGo-free SQLite port and current Darwin/Linux architecture support. It reduces native-build friction for the core, but it is a substantial generated dependency that still needs supply-chain, size, and performance review. [modernc.org/sqlite package documentation](https://pkg.go.dev/modernc.org/sqlite)

The standard Go tool builds executable commands, emits JSON for machine processing, tests packages, and exposes build/test caching and coverage. The first-party Go adapter calls those commands through its host boundary. [Go command reference](https://pkg.go.dev/cmd/go)

### Strengths

- Simple native deployment for a small CLI and background analyzer.
- Fast startup and straightforward concurrency for indexing and adapter orchestration.
- The first-party Go adapter reuses the core runtime boundary and standard toolchain.
- The language is approachable to systems and application contributors.

### Costs in this project

- The first release immediately becomes a Go core plus C# adapter plus native profiler project.
- Roslyn symbols and test identities must cross a process boundary before the contract is mature.
- Rich .NET diagnostics are harder to preserve through an early protocol.
- A CGo-free core does not make the CLR profiler CGo-free.

### When Go becomes the better choice

Choose Go for the core if the spike demonstrates all of the following:

- core distribution size/startup is a binding product requirement;
- the process protocol can express .NET symbols, evidence, and failures without leaky abstractions;
- the team accepts maintaining three implementation surfaces from v1;
- the Go adapter remains behind its documented capability and platform boundary.

## 8. Option C: Rust

### Toolkit

| Concern | Suggested toolkit |
|---|---|
| CLI | `clap` derive or builder API |
| Process execution | Standard `std::process::Command` |
| Git | External `git`; optional `git2`/libgit2 provider later |
| Hashing/Merkle | RustCrypto `sha2`; optionally BLAKE3 for internal acceleration if the format does not expose it as a permanent ID |
| Index storage | `rusqlite` with bundled SQLite |
| Adapter protocol | `serde`/`serde_json`, JSON Lines over stdio |
| Runtime observation | Rust ABI bindings or a thin C++ shim around the CLR profiling interfaces |
| .NET analysis | Separate C# process using Roslyn/MSBuild/TestPlatform |
| Packaging | Cargo release builds per target triple; cross-build/linker validation in CI |
| Project tests | Built-in test runner, property tests, fixture repositories, ABI tests |

Rust's standard `Command` offers fine-grained process construction and passes arguments literally rather than through a shell. [Rust `std::process::Command`](https://doc.rust-lang.org/std/process/struct.Command.html)

`clap` provides a mature typed CLI, while `rusqlite` can compile and link a bundled SQLite, reducing reliance on a system SQLite version. [clap documentation](https://docs.rs/clap/latest/clap/), [rusqlite documentation](https://docs.rs/crate/rusqlite/latest)

Cargo builds for explicit target triples, but native dependencies still require suitable linkers/toolchains and cannot be assumed to cross-compile by setting `--target` alone. [Cargo build reference](https://doc.rust-lang.org/cargo/commands/cargo-build.html)

### Strengths

- Suited to high-throughput indexing, compact immutable structures, and bounded memory.
- Can potentially own both the native core and much of the profiler implementation.
- Produces native binaries and exposes low-level control over binary format and I/O.
- The type system can encode schema versions and evidence rules.

### Costs in this project

- Roslyn and test-platform integration still require a C# adapter.
- CLR profiling uses COM-style native interfaces and runtime callbacks; the unsafe surface remains substantial even when written in Rust.
- Contributor onboarding and build troubleshooting are harder than C# or Go.
- SQLite, CLI, JSON, hashing, and Git support are ecosystem crates rather than standard-library facilities, increasing dependency review work.

### Appropriate role

Rust fits the profiler helper or a later optimized indexing engine. It is a higher-risk choice for the entire v1 because it does not remove the managed adapter.

## 9. Option D: Python

### Toolkit

| Concern | Suggested toolkit |
|---|---|
| CLI | Standard `argparse`, or Typer/Click if richer UX is worth an external dependency |
| Process execution | Standard `subprocess` with argument arrays and timeouts |
| Git | External `git` process |
| Hashing/Merkle | Standard `hashlib` |
| Index storage | Standard `sqlite3` |
| Adapter protocol | Standard `json`, newline-delimited messages |
| .NET analysis | Separate C# adapter and native profiler helper |
| Packaging | `zipapp` when a Python runtime is acceptable; PyInstaller per target OS for a bundled executable |
| Project tests | `pytest`, Hypothesis, fixture repositories |

Python's standard `argparse` is the recommended standard-library option for basic command-line applications and generates help and validation behavior. [Python `argparse`](https://docs.python.org/3/library/argparse.html)

`zipapp` creates executable Python archives but still relies on an interpreter and cannot directly load C extensions from the archive. [Python `zipapp`](https://docs.python.org/3/library/zipapp.html)

PyInstaller can bundle the interpreter and dependencies into a folder or a single executable for macOS/Linux, but the one-file form unpacks at startup and must be built/tested for each target platform. [PyInstaller operating model](https://www.pyinstaller.org/en/stable/operating-mode.html)

### Strengths

- Low-friction way to test planner math, schema evolution, CLI semantics, and report formats.
- Works well for research notebooks, fixture generation, and statistical analysis.
- Broad contributor accessibility.

### Costs in this project

- Still needs C# and native components for deep .NET mode.
- Distribution and reproducibility are weaker than Go/Rust/.NET self-contained artifacts.
- Large repository scans need careful native extensions, multiprocessing, or later rewrites.
- Dynamic packaging can hide missing imports/data until a platform-specific build runs.

### Appropriate role

Use Python for model experiments, corpus evaluation, calibration, and migration utilities. If the first milestone is deliberately a minimal dry-run prototype, Python can be a temporary core; otherwise, keep it out of the production core.

## 10. Option E: JVM with Java or Kotlin

### Toolkit

| Concern | Suggested toolkit |
|---|---|
| CLI | Picocli |
| Process execution | `ProcessBuilder`/Process API |
| Git | External `git` or JGit |
| Hashing/Merkle | `MessageDigest` and NIO channels |
| Index storage | JDBC plus Xerial SQLite JDBC |
| Adapter protocol | Jackson or kotlinx.serialization over JSON Lines |
| .NET analysis | Separate C# adapter and native profiler helper |
| Packaging | JVM distribution, `jlink`/`jpackage`, or GraalVM Native Image |
| Project tests | JUnit 5, property testing, fixture repositories |

Java's `ProcessBuilder` creates and configures operating-system processes. [Java `ProcessBuilder`](https://docs.oracle.com/en/java/javase/26/docs/api/java.base/java/lang/ProcessBuilder.html)

Picocli supports rich Java/Kotlin CLIs, generated help/completion, exit-code handling, and GraalVM configuration. [Picocli documentation](https://picocli.info/)

Xerial's SQLite JDBC bundles native SQLite libraries for major macOS and Linux architectures into its distribution and documents GraalVM Native Image support. [Xerial SQLite JDBC](https://github.com/xerial/sqlite-jdbc)

GraalVM Native Image can turn JVM bytecode into a standalone executable, but reflection, JNI, resources, and dynamic adapter discovery require reachability metadata and target-specific builds. [GraalVM Native Image basics](https://www.graalvm.org/jdk24/reference-manual/native-image/basics/)

### Strengths

- Mature tooling and broad contributor base.
- Strong process, concurrency, storage, and parser ecosystems.
- Kotlin can express the domain model concisely while sharing Java libraries.

### Costs in this project

- Adds a second managed runtime without reducing .NET adapter or profiler work.
- Shipping a JVM conflicts with the desired low-friction CLI; Native Image trades that for build/configuration complexity.
- No clear first-adapter advantage compared with C# and no single-binary simplicity advantage compared with Go/Rust.

### Appropriate role

Keep the JVM as a future third-party/official adapter target, not the recommended v1 core.

## 11. TypeScript/Node.js position

TypeScript is removed from the current official adapter roadmap. It should not appear in v1 examples such as `--languages=dotnet:deep,typescript:minimal`.

A TypeScript orchestration core would add a Node runtime or bundling concern while still requiring the C# adapter and native profiler. It offers no decisive advantage over C# for the first adapter or Go for native distribution. Revisit only if contributor demand or an official TypeScript adapter becomes a concrete roadmap item.

Third-party adapters remain user-chosen and explicitly unsupported by the engine maintainers unless promoted through a documented compatibility process.

## 12. .NET adapter design implications

### 12.1 Minimal mode

Minimal mode should require no build and no runtime instrumentation. It should:

1. Resolve baseline and current snapshots.
2. Load the solution/project graph if possible.
3. Map changed files and symbols to test files/test symbols through static reverse dependencies and naming/project relationships.
4. Return stable test IDs, reasons, confidence components, and uncovered changed symbols.

If solution loading fails, report an analysis error rather than silently degrading to path heuristics unless the user explicitly requests a heuristic-only mode.

### 12.2 Deep mode

Deep mode should:

1. Build by default; treat a compile failure as an analysis error.
2. With `--no-build`, validate that compatible test assemblies and metadata exist; otherwise fail.
3. Discover tests using the repository's actual runner.
4. Execute discovery/observation serially in v1.
5. Launch only test host processes with the profiler environment enabled.
6. Record test ID → executed method IDs, duration, outcome, snapshot hash, SDK, target framework, runner, and instrumentation version.
7. Normalize compiler-generated/async/lambda methods back to source symbols when Roslyn/PDB data permits it.
8. Store only identifiers, edges, aggregate statistics, and necessary diagnostics by default. Exclude source content.

### 12.3 VSTest and Microsoft.Testing.Platform

Model VSTest and Microsoft.Testing.Platform as separate executor capabilities. `dotnet test` can use either, and their CLI behavior differs. Microsoft describes MTP as a lightweight, portable alternative embedded in test projects, while VSTest remains the traditional platform for the .NET 6+ range. [Microsoft.Testing.Platform overview](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-intro), [dotnet test reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test)

Recommended rollout:

- v1: VSTest discovery/filter/execution for .NET 6+ projects.
- v1.x: detect MTP and provide minimal/static mode plus a precise unsupported-deep error until implemented.
- later: implement an MTP executor after stable test-ID and extension-loading behavior is verified.

### 12.4 Per-test observation without a target package

Serial v1 observation permits a package-free strategy:

- discover test IDs;
- execute one test identity (or one safe indivisible test case group) per test-host run;
- let the profiler aggregate executed method IDs for that process;
- associate the completed process observation with the selected test ID.

This is expensive on the first run, but it avoids injecting a context-propagation library into test code. After the model is proved, optimize with a shipped VSTest/MTP extension that observes test start/end inside one host process while remaining externally loaded rather than project-referenced. That optimization must prove correctness for parallel tests and asynchronous continuations before becoming default.

## 13. Packaging and release matrix

Start with the host combinations most aligned with the stated audience:

| Artifact | Priority | Notes |
|---|---|---|
| macOS arm64 | P0 | Apple Silicon development machines |
| Linux x64 glibc | P0 | Most CI runners and servers |
| macOS x64 | P1 | Intel Macs and compatibility environments |
| Linux arm64 glibc | P1 | ARM CI/server growth |
| Linux x64 musl | Later | Requires explicit native-profiler and SQLite validation |
| WSL/Linux x64 | Best effort | Covered only through Linux behavior; no Windows guarantees |
| Native Windows | Out of scope | Do not advertise or block design on it |

Every distribution should include:

- core executable;
- .NET adapter/runtime assets when not compiled into the core;
- profiler shared library for the exact OS/architecture;
- schema/protocol version manifest;
- third-party notices, checksums, and reproducible build metadata;
- no requirement to edit target project files.

## 14. Recommended spike plan

### Spike A: C# end-to-end vertical slice

Build the smallest real pipeline:

1. `SourceSnapshotProvider` shells out safely to Git.
2. C# adapter loads one multi-project .NET 6+ solution with Roslyn/MSBuild.
3. Index files, declarations, calls/references, project edges, and tests into SQLite.
4. Change one method and produce affected test IDs with evidence.
5. Build and discover tests through VSTest.
6. Run tests serially under a minimal native profiler on macOS arm64 and Linux x64.
7. Re-run after a second change and prove incremental updates touch only changed Merkle branches and affected graph rows.

Exit criteria:

- no target-project package/reference changes;
- deterministic selection for the same snapshots and state;
- failure classes distinguish source-resolution, build, discovery, observation, selection, and test failures;
- stale/missing state is visible and recoverable with a reset command;
- distribution starts on clean macOS/Linux machines with only Git and the repository-required .NET SDK available.

### Spike B: adapter protocol and Go-core counterfactual

Implement a small protocol probe that:

- starts the same .NET adapter out of process;
- negotiates protocol/capability versions;
- streams one solution summary, changed-symbol set, and affected-test set;
- propagates cancellation, timeouts, logs, and structured errors;
- measures cold start, throughput, memory, and diagnostic fidelity.

The existing Go worker is the protocol probe and the first-party adapter implementation. Keep the C# core and use the process boundary for Go toolchain operations; reconsider the core language only through a new ADR.

### Profiler language spike

Compare a minimal C++ implementation against Rust plus a thin C-compatible layer on:

- ABI/header coverage and runtime callbacks;
- macOS/Linux build and signing;
- callback overhead;
- crash isolation and diagnostics;
- symbol/module ID fidelity;
- contributor maintainability.

Choose the profiler language independently of the core. Using one language across these runtime boundaries would expose more complexity through a single module.

## 15. Decision triggers

Do not finalize the core language until the following facts are measured:

| Trigger | Favors |
|---|---|
| Roslyn/MSBuild dominates implementation and diagnostics | C# core |
| Process adapter protocol is clean and native CLI footprint is critical | Go core |
| Profiler and index throughput dominate, and systems expertise is available | Rust core/helper |
| Only a disposable minimal-mode proof is funded | Python prototype |
| JVM adapter demand becomes a committed first-class requirement | Re-evaluate JVM, still unlikely for .NET-first v1 |

Do not choose the core based on theoretical hash speed. SHA-256 and SQLite are available in every option, and repository hashing is unlikely to dominate before semantic loading, test discovery, profiler correctness, and first-run observation work. Benchmark before introducing specialized hash algorithms or custom databases.

## 16. Recommended technology baseline if the C# spike succeeds

This table is a starting baseline pending spike results:

| Layer | Baseline |
|---|---|
| Managed runtime | Lowest supported tool TFM compatible with the distribution policy; test against target repositories using .NET 6+ |
| CLI | `System.CommandLine` |
| Git | External Git CLI through safe argument-array process execution |
| Durable hash | SHA-256 with domain-separated node encodings and canonical path normalization |
| Local store | SQLite via `Microsoft.Data.Sqlite`; WAL only after concurrent-reader behavior is tested |
| Semantic analysis | Roslyn Workspaces + MSBuild-backed solution loading |
| Test execution | VSTest through `dotnet test` first; MTP capability detected separately |
| Runtime observation | External unmanaged CLR profiler, serial test association first |
| Interop | Versioned JSON Lines process protocol; length-prefixed binary protocol only if profiling proves JSON overhead material |
| Packaging | Self-contained, single-file, non-AOT per RID plus adjacent profiler shared library if bundling/extraction is fragile |
| Configuration | Repository YAML/TOML/JSON chosen after CLI/config ergonomics spike; secrets excluded |
| State directory | Repository-local, Git-ignored by recommendation; explicit reset/rebuild commands |

## 17. Sources

Primary and official technical sources consulted:

- [Git: `git merge-base`](https://git-scm.com/docs/git-merge-base)
- [Git: `git diff` raw output and `-z`](https://git-scm.com/docs/git-diff.html)
- [Microsoft: Roslyn SDK compiler model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)
- [Microsoft: `dotnet test`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test)
- [Microsoft: selective unit-test filters](https://learn.microsoft.com/en-us/dotnet/core/testing/selective-unit-tests)
- [Microsoft: Microsoft.Testing.Platform overview](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-intro)
- [Microsoft: CLR profiling overview](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/profiling/profiling-overview)
- [Microsoft: `global.json` SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)
- [Microsoft: `System.CommandLine`](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)
- [Microsoft: `Microsoft.Data.Sqlite`](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
- [Microsoft: .NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Go: `os/exec`](https://pkg.go.dev/os/exec)
- [Go: `crypto/sha256`](https://pkg.go.dev/crypto/sha256)
- [Go: `database/sql`](https://pkg.go.dev/database/sql)
- [Go: `cmd/go`](https://pkg.go.dev/cmd/go)
- [modernc.org/sqlite package documentation](https://pkg.go.dev/modernc.org/sqlite)
- [Rust: `std::process::Command`](https://doc.rust-lang.org/std/process/struct.Command.html)
- [Rust Cargo: `cargo build`](https://doc.rust-lang.org/cargo/commands/cargo-build.html)
- [clap documentation](https://docs.rs/clap/latest/clap/)
- [rusqlite documentation](https://docs.rs/crate/rusqlite/latest)
- [Python: `argparse`](https://docs.python.org/3/library/argparse.html)
- [Python: `zipapp`](https://docs.python.org/3/library/zipapp.html)
- [PyInstaller operating model](https://www.pyinstaller.org/en/stable/operating-mode.html)
- [Oracle: Java `ProcessBuilder`](https://docs.oracle.com/en/java/javase/26/docs/api/java.base/java/lang/ProcessBuilder.html)
- [Picocli documentation](https://picocli.info/)
- [Xerial SQLite JDBC](https://github.com/xerial/sqlite-jdbc)
- [GraalVM Native Image basics](https://www.graalvm.org/jdk24/reference-manual/native-image/basics/)
