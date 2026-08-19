# Product specification

Status: Adopted schema 1 specification  
Applies to: Initial Merkle CLI and official .NET and Go adapters

Related: [System design](system-design.md), [domain context](../CONTEXT.md), [implementation guide](implementation-guide.md)

The words **must**, **should**, and **may** are normative. **Accepted**, **Proposed**, and **Deferred** labels keep suggestions from becoming accidental guarantees.

## 1. Product contract

Merkle accepts a baseline snapshot, candidate snapshot, language selections, available evidence, and repository policy. It returns a terminal, explainable test plan. Executing that plan does not strengthen its guarantee.

**Accepted:** Merkle is advisory. It must not state or imply that omitted tests cannot fail, that mapped tests prove coverage completeness, or that a selected plan replaces the repository's full regression suite.

```mermaid
sequenceDiagram
    participant U as User or CI
    participant M as Merkle
    participant A as Language adapter
    participant S as State provider
    U->>M: Plan request
    M->>M: Freeze baseline and candidate
    M->>A: Negotiate capabilities
    A-->>M: Units, tests, impact evidence
    M->>S: Read compatible terminal history
    S-->>M: Evidence and timing statistics
    M->>M: Rank and apply repository policy
    M-->>U: Terminal plan and warnings
```

## 2. Supported initial scope

| Area | Status | Requirement |
|---|---|---|
| Repository history | Accepted | Git history is required for initial baseline/candidate comparison. |
| PR default | Accepted | Compare the target merge base with PR head. |
| Local comparison | Accepted | Permit explicit/configured branch, commit, or frozen working-tree candidates. |
| Languages | Accepted | Mixed-language repositories require explicit selections and list detections when missing. |
| Official adapter | Accepted | Provide a deep adapter for .NET 6+. |
| Go adapter | Accepted | First-party deep adapter; canonical language identifier is `golang`, with `go` accepted as a CLI alias. |
| Analysis scope | Accepted | Resolve one .NET solution or Go repository/workspace scope per deep run; ambiguity is an error. |
| Operating systems | Accepted | Support macOS and Linux; WSL is best-effort; native Windows is out of scope. |
| State service | Accepted | No hosted service is provided. Team-owned remote storage is optional. |
| Implementation language | Accepted | Build the core in C# on .NET 10; managed executables default to Native AOT publishing. |

## 3. Snapshot and change requirements

- **S-001:** A run must bind immutable baseline and candidate identities before indexing or execution.
- **S-002:** A working-tree candidate must include a digest of all included dirty and untracked inputs.
- **S-003:** In recognized PR context, absent explicit refs, the baseline must be the target merge base and the candidate the PR head.
- **S-004:** In local context, refs must be supplied by CLI/configuration unless an unambiguous local policy is configured.
- **S-005:** Missing shallow-clone history or merge base is an analysis error with a remediation message.
- **S-006:** Repositories without usable Git ancestry are unsupported initially. A manually written change description is not equivalent evidence.
- **S-007:** A changed configuration or build input that can alter a complete project must invalidate at least that project scope.
- **S-008:** Merkle comparison must be deterministic for identical inputs, adapter versions, and configuration.

## 4. Language and adapter requirements

- **A-001:** Language detection must return every detected language and its evidence.
- **A-002:** When more than one language is detected and no explicit/configured selection exists, analysis must fail and list the detections.
- **A-003:** Each selected adapter must advertise a protocol version, identity version, and supported capabilities before work begins.
- **A-004:** Requesting an unavailable capability must fail with a machine code and the human message `Function not available for: <language>` or an equivalent localized form.
- **A-005:** Minimal adapters must map changed source units to stable requested-test identities and reasons.
- **A-006:** Deep adapters may additionally discover, observe, execute, and report tests.
- **A-007:** The core project does not guarantee a third-party adapter. Reports must identify its producer and version.
- **A-008:** An adapter must not silently emulate an unsupported deep capability with a weaker operation.

See [Adapter authoring](adapter-authoring.md) for protocol 1.0.

## 5. Index and impact requirements

- **I-001:** The index must separate change detection from test-impact evidence.
- **I-002:** Merkle nodes must use canonical identities, canonical child ordering, domain-separated encoding, and an identified hash scheme version.
- **I-003:** Comparing compatible roots must descend only unequal branches and return a change frontier at the available adapter granularity.
- **I-004:** The reverse impact index must distinguish containment, static dependency, dynamic observation, and historical association evidence.
- **I-005:** Cyclic dependency groups must terminate safely and produce deterministic candidate ordering.
- **I-006:** Exact member-level evidence should outrank containing-file or project fallback evidence.
- **I-007:** If deep mapping is unavailable, the engine may expand to the smallest semantic ancestor with mapped tests.
- **I-008:** Every requested test must include at least one explainable path from a changed source unit or an explicit configured rule.
- **I-009:** A changed source unit with no known test relationship must be emitted as unmapped. It must not be described as safe or untested.

## 6. .NET deep adapter requirements

- **D-001:** The official adapter must support repositories targeting .NET 6 or later within its published compatibility matrix.
- **D-002:** It must use the system `dotnet` resolver and honor normal repository SDK selection, including `global.json` behavior.
- **D-003:** It must not add a NuGet package, Node dependency, source file, project reference, or other instrumentation dependency to the target repository.
- **D-004:** It must resolve one configured or unambiguous solution. Multiple candidates are a configuration error.
- **D-005:** It must build by default.
- **D-006:** `--no-build` must prove compatible artifacts exist for the candidate snapshot and effective build configuration. Missing, stale, or incompatible artifacts are an analysis error.
- **D-007:** A build or compilation failure is an analysis error, not a test failure.
- **D-008:** Initial per-test deep observation must execute serially for unambiguous attribution.
- **D-009:** Each observation must be tied to a test identity, source-unit identity, build fingerprint, adapter version, and terminal run.
- **D-010:** A test failure caused by an external dependency is an ordinary test failure. Environment provisioning is outside Merkle.
- **D-011:** No timeout is imposed unless `--timeout-ms` or `timeoutMs` is explicitly configured.
- **D-012:** Crossing an expected runtime mean alone must not stop, fail, or broaden a run.

## 6a. Go deep adapter requirements

- **G-001:** The official adapter must require Go 1.22 or newer for source builds and invoke the repository's system `go` toolchain.
- **G-002:** The worker must use the versioned process protocol for `detect`, `index`, and `map`; the host must provide build, discovery, execution, and observation operations.
- **G-003:** Build and discovery must support `go.mod`, nested modules, and `go.work` module scopes with deterministic selection and explicit ambiguity errors.
- **G-004:** Go source and test identities must be deterministic and use the canonical `golang` namespace.
- **G-005:** No-build validation must reject missing, stale, or incompatible manifests and artifacts.
- **G-006:** Observation must preserve immutable snapshot/fingerprint boundaries and disclose runtime-only subtests, file-level coverage, standard-library coverage, reflection/dynamic behavior, generated code, plugins, subprocesses, cgo/native code, and build-tag limits.

ADR-0016 selects a managed startup hook. Observation is complete only at assembly/project granularity and must report the member, reflection, native, child-process, pre-hook-load, and identity-correlation blind spots.

## 6b. Merkle source-build requirements

These requirements govern building Merkle itself. They do not change the analysis-build contract in sections 6 and 6a.

- **B-001:** A repository-owned .NET helper, launched through `./build`, must be the canonical interface for configurable adapter builds. It must expose `build` and `publish` commands.
- **B-002:** The adapter catalog initially contains `dotnet`, `golang`, `python`, and `java`. The helper must accept `go` as an alias for `golang`. `all` must expand from catalog membership, independent of local toolchain availability.
- **B-003:** A local build with no explicit adapter selection must request only `dotnet`. The default adapter build policy must be `strict`.
- **B-004:** The .NET SDK selected by `global.json` is a host prerequisite for every invocation. The .NET language adapter must remain selectable.
- **B-005:** Preflight must inspect every selected adapter before compilation. A missing executable or unsupported tool version makes that adapter unavailable and must report the required and detected versions when known.
- **B-006:** Strict policy must stop before compilation when any selected adapter is unavailable. After successful preflight, it must stop at the first adapter build, test, artifact, or smoke failure.
- **B-007:** Best-effort policy must skip unavailable adapters and continue after adapter-scoped failures. It must fail without building the host when no selected adapter succeeds.
- **B-008:** Missing prerequisites are `skipped`. Compiler, selected test, missing expected artifact, launch, or protocol smoke failures are `failed`. Disk, permission, helper, manifest, host, and packaging failures are global.
- **B-009:** A selected adapter counts as `built` only when the current invocation creates its expected artifact and the artifact passes a protocol smoke check. Pre-existing output cannot satisfy the current invocation.
- **B-010:** `--test` must run helper/host tests and tests owned by each selected adapter. Helper and host test failures are global; adapter test failures follow the adapter build policy. Smoke checks remain mandatory without `--test`.
- **B-011:** Adapter execution must default to sequential scheduling. `--builds parallel` must use bounded concurrency. Strict parallel failure must cancel in-flight adapter work, start no queued work, and prevent host publication.
- **B-012:** The host must build after the successful adapter set is known. A completed output must be promoted atomically; a failed or cancelled run must preserve any previous helper-owned destination.
- **B-013:** A custom non-empty output directory that is not recognizably helper-owned must be rejected. Cleanup must remain inside helper-owned intermediate, run-report, or output paths.
- **B-014:** Each package must contain a deterministic `adapters.json` listing only bundled adapters, their canonical IDs, versions, protocol/profile metadata, relative artifact paths, and checksums. Local paths and wall-clock timestamps are forbidden in the package manifest.
- **B-015:** Every helper invocation that passes argument validation must write a build report and per-adapter logs outside the package. `--no-warnings` affects console warnings only.
- **B-016:** Adapter terminal states are `built`, `skipped`, `failed`, `cancelled`, and `not-run`. Overall states are `success`, `partial-success`, `failed`, and `cancelled`.
- **B-017:** Helper exit codes are 0 for complete or accepted partial success, 2 for invalid invocation, 3 for strict adapter failure or zero successful adapters, 4 for helper/host/packaging failure, and 130 for interruption.
- **B-018:** Text output must retain a final summary when warnings are hidden. `--format json` must be non-interactive and emit one structured result on standard output; diagnostics belong on standard error.
- **B-019:** Interactive mode must show every catalog adapter and its readiness, keep unavailable adapters selectable, and let a user revise a strict selection that cannot succeed. Non-interactive mode must never prompt.
- **B-020:** `build` defaults to Debug. `publish` defaults to Release and the current supported runtime identifier. Publish targets must match the builder's operating system and architecture so every bundled artifact can run its smoke check.
- **B-021:** Plain `dotnet build` and `dotnet publish` must not invoke optional adapter toolchains and must produce the default .NET-only application. Direct publication must still emit a .NET-only `adapters.json`.
- **B-022:** The helper must not install toolchains. It resolves standard commands and ecosystem variables, derives version requirements from repository sources, and reports installation guidance for unavailable selections.
- **B-023:** The helper may leave compiler intermediates after a failed run. It must never include stale artifacts in the promoted package.
- **B-024:** Official releases must name `dotnet,golang,python,java` explicitly and use strict policy. CI must also exercise the expanding `all` catalog and controlled strict, best-effort, zero-success, cancellation, and stale-artifact cases.

The four acceptance cases that selected this contract are:

| Selection | Policy | Java toolchain unavailable | Required result |
|---|---|---|---|
| `all` | strict | Detected during preflight | Exit 3; build no adapter or host output |
| `all` | best effort | Java is skipped | Build .NET, Go, and Python; report partial success; exit 0 |
| `java` | strict | Detected during preflight | Exit 3; build no host output |
| `java` | best effort | Java is skipped and no adapter remains | Exit 3; build no host output |

## 6c. Local container installation requirements

These requirements govern the optional local installation used by development agents. They do not change adapter capabilities or make Merkle a full-suite replacement.

- **L-001:** `./install` must build only from `https://github.com/leeozaka/merkle.git`. An omitted ref selects the highest stable `vMAJOR.MINOR.PATCH` tag and falls back to the default branch only when no stable tag exists. The resolved commit must be recorded.
- **L-002:** The supported hosts are macOS, Linux, and WSL2 on its Linux filesystem, using the local Docker Engine or Docker Desktop through Compose v2. Native Windows, remote Docker contexts, alternate container engines, and Windows-mounted WSL repositories are outside the supported contract.
- **L-003:** Installation and runtime execution must use one parameterized Compose service. The host wrapper must hide Compose from callers and use an ephemeral container for each Merkle command.
- **L-004:** The default installation must request `dotnet,golang,python,java`. `--adapters` may request any nonempty subset; aliases, ordering, and duplicates must normalize before the adapter set becomes part of installation identity.
- **L-005:** A selected adapter must bring the compiler, SDK, runtime, and common build tools required by that adapter's documented scope. This does not provision target-specific databases, browsers, services, credentials, or native libraries.
- **L-006:** An installation variant is immutable and identified by resolved commit, Docker architecture, and normalized adapter set. Variants may coexist. A `current` selection provides the user-wide default, and `.merkle-version` may pin an exact variant for one repository.
- **L-007:** Installation must build and smoke-test before atomic promotion. Failure must preserve the previous `current` variant. Repeating an unchanged unqualified request must reuse a healthy variant; a newer stable tag must install beside the old variant and become `current`.
- **L-008:** Versioned source and manifests must live beneath `${XDG_DATA_HOME:-~/.local/share}/merkle`; disposable build data belongs beneath `${XDG_CACHE_HOME:-~/.cache}/merkle`. The wrapper belongs in `~/.local/bin` unless explicitly overridden.
- **L-009:** The installation manifest must record repository URL, requested and resolved ref, resolved commit, architecture, runtime identifier, normalized adapters, image name and ID, and installation ID. It must not record secret values.
- **L-010:** Runtime execution must mount only the discovered Git working tree read-write plus exact external Git administration paths required by a linked worktree. Host paths must remain identical inside the container. Bare repositories are unsupported.
- **L-011:** Linux and WSL execution must use the invoking UID and GID. Git trust may add only the exact mounted repository to `safe.directory`; wildcard trust is forbidden.
- **L-012:** Runtime network access is enabled by default and may be disabled explicitly. Host credentials, environment variables, Docker sockets, and external paths must not be forwarded automatically.
- **L-013:** `.merkle-runtime.yml` may select a custom image or repository-relative Dockerfile and context, name environment variables to forward, and add repository-contained mounts. External or broad host mounts require separate explicit authorization.
- **L-014:** Docker-owned caches may persist target dependency downloads. Images, volumes, and containers created by Merkle must carry ownership and installation labels; cleanup may remove only exact matching resources.
- **L-015:** Merkle operations must serialize per repository with a visible, bounded wait. Different repositories may run concurrently. A live lock must never be broken automatically.
- **L-016:** The host wrapper must expose `install`, `doctor`, `list`, `use`, and `uninstall` management commands while delegating `plan`, `observe`, `run`, `state`, and `history` to the selected containerized CLI.
- **L-017:** The portable agent skill must announce image builds and cold-start observation, run one deep invocation per configured .NET or Go language, disclose every fallback, and run the repository's canonical full suite before completing a development task.
- **L-018:** Python and Java may be bundled for semantic planning and future capability growth, but the skill must not claim deep selected-test execution for an adapter that does not advertise it.
- **L-019:** Public Merkle images, automatic editor hooks, CI release automation, arbitrary fork installation, and automatic normal-use version checks are outside this local-only contract.

## 7. Historical and statistical requirements

- **H-001:** Only terminal runs may contribute historical evidence.
- **H-002:** Official CI and local samples must retain separate provenance.
- **H-003:** Remote history admission must be explicitly configured; local runs are not automatically promoted to official history.
- **H-004:** Compatibility must consider repository identity, schema, adapter, source-unit identity, test identity, and build fingerprint family.
- **H-005:** Unmatched samples must be counted and excluded, not coerced into the current model.
- **H-006:** A cold or incompatible remote store must warn with compatible and unmatched run counts.
- **H-007:** An unexecuted test in a selected-only run is censored data, not a negative relevance label.
- **H-008:** Impact probability and evidence confidence must be separate output fields.
- **H-009:** Runtime estimates must use comparable environments and disclose when a full-suite estimate is unavailable.
- **H-010:** A periodic full-suite run remains externally owned and is the preferred calibration source.

Schema 1 uses a beta-binomial estimate for each candidate test:

```text
p(A | C,t) = (positiveRuns + 1) / (eligibleRuns + 2)
```

`A` means “test `t` is relevant to change event `C`.” Confidence is separate and combines maturity, recency, provenance, candidate coverage, and compatibility coverage. Each run contributes at most one relevance label and one timing sample.

## 8. Planning and policy requirements

- **P-001:** Exact mandatory mappings must not be removed solely because a test is slow.
- **P-002:** Discretionary candidates may be ranked by impact probability, configured severity, novelty, and expected duration.
- **P-003:** The plan must estimate selected duration and comparable full-suite duration when evidence permits.
- **P-004:** The fallback `minSavingsPercent` is 30 when not configured and when comparable timing exists.
- **P-005:** Users may lower the saving threshold, including to a 10–20% margin.
- **P-006:** There is no universal confidence threshold or default low-confidence action.
- **P-007:** A repository that asks Merkle to choose automatically must configure the applicable confidence action.
- **P-008:** Without an automatic-choice policy, the tool returns a successful ranked plan with recommendation `decision-not-configured`; `run` does not execute it.
- **P-009:** Unmapped source warns and continues by default; pedantic policy converts it to a policy failure.
- **P-010:** The report must preserve candidates excluded by budget and explain the exclusion.

## 9. CLI contract

```text
merkle plan [--base <ref>] [--head <ref|WORKTREE>]
            --languages <language:profile,...>
            [--format text|json] [--pedantic]

merkle observe --languages dotnet:deep
               [--base <ref>] [--head <ref|WORKTREE>]
               [--no-build] [--timeout-ms <milliseconds>]

merkle run [plan options] [--no-build] [--timeout-ms <milliseconds>]

merkle state status
merkle state reset --local
merkle history import <terminal-report>
```

- `plan` must never execute tests.
- `observe` builds/discovers and gathers deep per-test evidence.
- `run` plans, builds by default, and executes the policy-approved selection.
- Command-line values override repository configuration for one run.
- A possible `--dry-run=<base-ref>` alias may expand to `plan --base <base-ref> --head WORKTREE`; the alias is **Proposed**, not accepted.

## 10. Configuration contract

The recommended reviewed file is `.merkle.yml`; runtime state is separate and ignored.

```yaml
schemaVersion: 1
repository:
  solution: Example.sln
  stateDirectory: .merkle
languages:
  dotnet:
    profile: deep
baseline:
  localRef: development
  prStrategy: merge-base
execution:
  build: true
  serialObservation: true
policy:
  minSavingsPercent: 30
  confidenceThreshold: null
  onLowConfidence: null
  unmapped: warn
history:
  provider: local
```

Unknown and duplicate fields fail validation so a misspelled safety policy cannot be ignored. Schema evolution must be versioned.

## 11. Terminal report contract

Text output must remain understandable without color. JSON must be versioned from its first release. A report must include:

```text
schemaVersion
runId, terminalStatus, errorClass?, errorCode?
baseline, candidate, repositoryIdentity
languages[], adapters[], capabilities[]
indexSchema, identitySchemas[], buildFingerprint?
changedUnits[] { identity, kind, changeKind, mapped }
tests[] {
  identity, displayName, selected,
  impactProbability, evidenceConfidence,
  expectedDurationMs?, reasons[], excludedBy?
}
unmappedUnits[], warnings[]
history { compatibleRuns, unmatchedRuns, provenanceTiers }
economics { selectedMeanMs?, fullMeanMs?, savingsPercent? }
policy { effectiveConfiguration, recommendation, decisiveReason }
```

The terminal report must redact secrets and bound persisted output. It should include enough version and evidence-cutoff information to explain a later replay difference.

## 12. Failure classification

| Class | Examples | Result |
|---|---|---|
| Configuration error | Mixed languages not selected, multiple solutions, invalid policy | No evidence admitted; actionable configuration message |
| Capability error | Deep operation requested from minimal adapter | Named language and missing capability |
| Analysis error | Missing merge base, build failure, stale `--no-build`, corrupt index | Failed terminal report; not a test failure |
| Test failure | Assertion, crash, explicit timeout, external dependency failure | Completed execution report with failed test |
| Policy failure | Pedantic unmapped unit, configured confidence gate | Complete plan plus decisive policy reason |
| Interrupted | Signal, runner loss, process crash | Recoverable journal; prior published state remains current |

Exit codes are 0 success, 2 configuration, 3 capability, 4 analysis/build, 5 test failure, 6 policy, and 130 interruption. Machine-readable reports retain the error class and code.

## 13. State and visibility requirements

- Local runtime state must live in an explicitly resolved repository subdirectory and should be Git-ignored.
- Merkle may warn that state is tracked/unignored but must not silently edit `.gitignore`.
- In-progress work must use isolated run journals.
- Other agents and processes must see the previous complete result until a new run reaches a terminal state.
- Successful and failed terminal reports must become visible atomically.
- Invalid partial evidence must not enter historical statistics.
- `state status` must identify provider, version, last compatible run, size, and rebuild need.
- `state reset --local` must validate its exact target and affect only local disposable state.
- Teams may implement remote storage, but the project provides no hosted infrastructure.

SQLite schema 2 is the accepted local provider behind the storage interfaces.

## 14. Security and privacy requirements

- Merkle must treat builds/tests as arbitrary code execution and must not claim to sandbox them.
- No data may leave the runner without an explicitly configured remote provider.
- History should store identities, hashes, provenance, aggregate statistics, and bounded/redacted outcomes rather than source content.
- Official remote writes must be authenticated; untrusted PRs should use read-only or isolated credentials.
- Environment values, tokens, connection strings, and configured secret patterns must be redacted from persistent reports.
- Adapter input/output must be bounded and validated.
- Third-party adapters must not be automatically installed or treated as trusted solely because a repository requests one.

## 15. Initial acceptance scenarios

1. **Member-specific shared code:** a change to a Currency member used only by Payments selects direct Currency/Payments tests but not unrelated Orders tests; reasons show the path.
2. **Shared member:** a change to a Currency member used by Payments and Orders reaches both branches.
3. **Mixed repository:** detection without `--languages` fails and lists every language; explicit selections proceed only with available capabilities.
4. **Unmapped source:** normal plan warns and continues; pedantic plan returns policy failure.
5. **Default build:** a valid .NET solution builds before deep execution.
6. **Compilation error:** the terminal report classifies it as analysis failure.
7. **No-build:** absent or incompatible artifacts cause analysis failure; compatible artifacts proceed.
8. **No timeout:** a slow test is not cancelled merely for exceeding its mean; an explicit timeout is enforced.
9. **External dependency:** a test's database/network failure is reported as a test failure.
10. **Cold server:** report gives compatible/unmatched history counts and weaker confidence.
11. **Concurrent agents:** a reader never observes another run's partial index or result.
12. **Economics:** a plan with less than configured savings recommends the full suite or follows the repository's explicit action.
13. **Censored history:** omitted tests from selected-only runs are not learned as negative labels.
14. **Advisory language:** text and JSON communicate limitations and never describe omissions as proven safe.
