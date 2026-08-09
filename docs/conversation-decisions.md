# Merkle Test-Impact Assistant: Conversation and Decision Record

Status: Working design record  
Scope: All product decisions and open questions captured in the discussion to date  
Working name: **Merkle**

## Decision labels

The chronology uses three labels:

- **User decision** means the product owner explicitly chose, rejected, or constrained an option.
- **Accepted recommendation** means the assistant proposed an option and the user explicitly endorsed it.
- **Open question** means the user intentionally deferred the decision or did not yet have a preference.

The available record contains the user's side of several exchanges without the assistant's exact question. Those prompts are paraphrased and marked. Missing recommendations are left missing.

## Product thesis

Merkle is a self-hosted, advisory test-impact analysis tool for Git-based development. It observes changes between two code snapshots, identifies the smallest meaningful code scope affected by those changes, maps that scope to tests, and recommends or runs a ranked test selection. Its purpose is to shorten repeated commit and pull-request feedback loops for developers and coding agents. The project's full test suite remains under team policy.

The design combines:

1. A content-addressed semantic tree, inspired by Merkle trees, to find where a codebase changed.
2. A reverse impact index from code units to features, callers, and tests.
3. Dynamic per-test observations to learn which tests execute which code units.
4. Historical correlation to improve predictions when static or dynamic evidence is incomplete.
5. Cost-aware ranking so the tool can decline to optimize when the selected set is not materially cheaper than the full suite.

The product is a helping assistant with deliberately loose strength. Teams remain responsible for their CI policy and periodic full-suite runs.

## Chronological conversation record

### 1. Initial idea: semantic change localization for SDD-heavy repositories

**User proposal.** Specification-driven development can create many test files and cause excessive processing between commits. The tool should observe the whole codebase through a fast reverse index and a Merkle-like tree, locate the branch of the code tree where a change begins, and select the relevant tests. In a feature tree with several features and subparts, a changed worktree node and its parents should determine whether to run a whole feature suite or only a lower subtree.

The model must go below changed files and represent nested scopes such as repository, solution, project, namespace/module, type, method/function, feature, and test.

### 2. Advisory posture and guarantee boundary

**User decision.** The selection may have what the user called “loose strength.” Its job is to improve commit-sequence speed for many developers and agents working in parallel. A well-run company can execute the full suite periodically, for example nightly. The team's CI policy still decides what is required.

### 3. Git history, local use, and CI pull requests

**User position.** A repository with no useful commit history is unlikely to provide enough information to understand changes, although a helper file describing changes since the previous run could be considered. The main use case should be a CI/CD pull-request pipeline rather than only local execution. The discussion nevertheless made a detached/local run worth considering.

Git provides the first baseline source. Local snapshots and persisted observations remain part of the design.

### 4. CLI and dry-run comparison

**User proposal.** Start with a CLI that assumes common options. A proposed form such as `--dry-run:{branchname}` would compare one branch with another and print the expected affected tests; a deeper mode could perform more analysis.

**Open question.** The exact CLI grammar remains open. The colon form is a product idea awaiting a stable contract.

### 5. Open adapter architecture and progressive capability levels

**User decision.** Follow dependency-inversion principles: language integrations are adapters, and language-specific code stays out of the engine. An adapter should be developable against a shared conformance test suite. Capability should be progressive:

1. A **minimal** adapter emits an affected-test file/list that machines and LLMs can consume.
2. A richer semantic or LSP-backed adapter can provide deeper language knowledge.
3. A **deep** adapter can run the language's test suite and report observations.

The initial thought was a complete .NET adapter, a minimal TypeScript/Jest adapter, and later Go support. That initial TypeScript direction was explicitly corrected later; see decision 27.

### 6. Minimum viable output

**User decision.** The bare minimum is to detect changed files and list the tests affected by those changes.

Planning and execution are separate. An adapter can be useful when it discovers and identifies tests without running them.

### 7. Merkle-like ancestry and affected subtree selection

**User proposal.** The built tree should reveal the root/common point where hashes diverge and where the change starts. The tool should find the last relevant parent of the changes and run every applicable test beneath it.

**Open question.** The phrase “hash collisions root” appears to mean the nearest common or divergent ancestor in the semantic tree. Confirm that reading before freezing the data model.

### 8. Shared code must fan out to all dependent features

**User decision.** If a `currency` component is used by both payments and orders, a currency change should test both. At a finer level, if function X is used only by payments, changing X should test X's currency tests and payments; if function Y is used by both, changing Y should test currency plus both consuming features.

Directory ancestry cannot capture this fan-out. Impact needs graph traversal over semantic dependencies and code-to-test evidence, with function/method granularity where supported.

### 9. Exploration of existing approaches

**Open question at that point.** The user did not know which existing suite-selection approaches to use and asked for options.

**Accepted recommendation.** Dynamic per-test observation and historical correlation were identified as the most promising approaches and were explicitly favored for likely primary use.

### 10. Cold start and learning loop

**User position.** A local first run may execute the full suite and improve subsequent selections. Another local entry point may compare a feature branch with its parent development branch, where branch structure can be configured. Cloud/CI execution is easier to reason about, but the design should avoid making local and cloud operation depend on fragile Git workflows.

This requires a cold-start strategy, explicit baseline resolution, and resettable local state. Branch names may help resolve a baseline, but they cannot define identity.

### 11. Different trust levels for cloud and local observations

**User decision.** In cloud/CI, only official pipeline requests should update trusted observations. Local runs can be looser, and users must be able to start the learned state again if development becomes messy.

Observation provenance must distinguish authoritative CI evidence from local evidence. State reset/rebuild is a supported workflow.

### 12. No hosted service and user-owned storage

**User decision.** Merkle has no first-party hosted or monetized service plan. Teams fetch it and operate it themselves, providing cloud or bare-metal storage if they need shared history. The CLI writes its state within the repository working area.

This rules out a first-party hosted control plane.

### 13. Repository-local state should be ignored by Git

**User decision.** Strongly recommend keeping generated state in a Git-ignored location. Users may choose to commit it, but that is not recommended.

The default local store must live under a predictable hidden directory, and the documentation should provide a `.gitignore` entry.

### 14. Observation visibility and concurrent agents

**User decision.** Observations may become visible only when a run concludes, whether the run passes or fails. Agents can consume the completed result. No incremental visibility during a run is required.

**User position on conflicts.** Agents are expected to own the completeness of their changes. If parallel changes cause tests to fail, the final pipeline is expected to reveal that the combined code does not work.

V1 can publish an observation transaction when a run ends. Merkle does not need to resolve semantic merge conflicts between agents.

### 15. Confidence policy was initially open

**User status.** The correct behavior when evidence is uncertain was not decided at first, and the user asked for suggestions.

### 16. Rank by expected value and avoid pointless optimization

**Accepted direction.** The product's loose-strength posture calls for ranking the best tests. If the selected subtree offers little savings, prefer the full suite.

Selection needs an impact/confidence score and an estimated cost. The smallest test count can still be the wrong plan.

### 17. Pull-request defaults and history maturity warnings

**User decision.** In a PR, the default comparison is the target branch versus the current branch. Simpler or deeper analysis is configurable, particularly when a project has configured shared server-side storage. If shared storage exists but has insufficient historical matches, Merkle should warn that a number of runs were not matched in the codebase and that confidence will be weaker until more evidence accumulates.

Every plan should expose evidence maturity and unmatched-history counts alongside its score.

### 18. Minimum worthwhile speed improvement

**User decision.** The cost/savings boundary should be configurable. If the user does not configure it, fall back to a minimum expected improvement of **30%**.

If the selected plan is not expected to save at least 30% relative to the full suite, prefer the full suite.

### 19. Probability and statistical evidence

**User direction.** When pre-execution confidence is strong, described as a good probability `P(A)`, run the calculated tests instead of the full suite. The intended scoring is statistical, using probability and mean calculations over observed runs.

**User tolerance.** Higher confidence is better, but an estimated margin in the 10–20% range can still be useful.

**Open question.** The exact event A, estimator, confidence interval, smoothing method, sample-size correction, and treatment of correlated tests are not yet defined.

### 20. No universal default confidence threshold

**User decision.** The confidence/risk cutoff should be deferred to the actual user and exposed through configuration. No universal default threshold should be assumed.

The **30% default expected-savings floor** is settled. A default **confidence acceptance threshold** remains open.

### 21. No project-injected dependencies

**User decision.** Most users do not want to add a NuGet package or Node dependency to their production/test projects. Merkle should avoid requiring application-level dependencies.

Observation should use the installed toolchain, test-platform hooks, environment variables, sidecar processes, or runtime profiling. It should not require source-package instrumentation.

### 22. Initial platform target

**User decision.** Focus on .NET 6 and newer and use the system's normal/default .NET installation.

**User decision.** Support macOS and Linux. Native Windows is out of scope; WSL may work incidentally but is not a target.

### 23. Third-party adapter contract

**User decision.** Third-party adapters are required only to implement the minimal capability: list the requested/affected tests. Contributors may implement deep execution at their discretion. The CLI exposes common capability flags, but asking an adapter for an unsupported capability must exit with an error such as `function not available for: {language}`.

**User decision.** The project does not guarantee third-party adapters. Users decide which external adapter to trust and operate; the first-party project guarantees only its engine and official adapters.

### 24. Mixed-language repositories require explicit configuration

**User decision.** If multiple languages are detected and the user has not configured which ones to analyze, fail and list the detected languages. The user then selects explicit adapter modes, illustrated during the discussion as `--languages=dotnet:deep,typescript:minimal`.

**Correction.** TypeScript was later removed from official scope. Future examples should use a generic third-party language or Go, such as `--languages=dotnet:deep,go:minimal`. The earlier TypeScript string stays here as a historical example.

### 25. Unmapped changes and pedantic mode

**User decision.** If a changed code unit has no mapped tests, continue by default. A strict flag such as `--pedantic` may make this an error. Coverage completeness is assumed to be handled by existing CI tools such as GitLab coverage facilities or Sonar, not by Merkle.

Unmapped code must appear in diagnostics. It does not block the run by default.

### 26. First-party adapters under consideration

**User position.** The project should ship a complete .NET adapter and was considering either TypeScript or Go as an additional adapter.

### 27. TypeScript removed from current scope

**User correction.** “Lol take those typescript away from now. What a mess!” TypeScript was explicitly rejected from the current first-party plan.

Official V1 planning is .NET-first. Go remains a candidate for a later first-party adapter. The open contract permits a community TypeScript adapter, but the project does not promise one.

### 28. Scope-triage table accepted

**User action.** The user requested a table of information before deciding what to ignore and then accepted the assistant's suggested items.

The exact rows of that assistant table are absent from the available user-side transcript. This record does not reconstruct them. A scope item is authoritative only when an explicit decision here supports it or the full transcript recovers it.

### 29. Timeout behavior

**User decision.** There is no default special behavior when analysis or execution exceeds expected latency. A user may set a `timeoutMs` flag.

**Open question.** The precise timeout exit code and whether completed partial observations are discarded still need a decision. The completion-only visibility rule implies that partial evidence should not be trusted by default.

### 30. Build and analysis failures

**User decision.** A compilation failure is an analysis error.

**User decision.** Build by default. If the user supplies `--no-build`, use the available previous build; error if a usable prior build does not exist.

### 31. .NET SDK selection

**User position.** No detailed SDK-selection policy was chosen. The likely direction is to use the highest available/selected SDK because newer .NET SDKs commonly build projects targeting older supported frameworks.

**Open question.** This recommendation still needs validation. The implementation must respect repository controls such as `global.json` and should report the selected SDK.

### 32. Solution scope

**User decision.** Multiple .NET solutions in one analysis are not expected in the initial version.

V1 may require exactly one configured or discovered solution and return a clear ambiguity error.

### 33. Integration tests and external dependencies

**User decision.** Teams configure integration-test infrastructure outside Merkle. If an external dependency fails unexpectedly during a selected test, report a normal test failure.

Merkle reports process and test outcomes as received. V1 does not orchestrate services or reclassify their failures.

### 34. Serial deep observation

**User decision.** “Go for serially! Not in a hurry for deep specialization.”

V1 favors deterministic, trustworthy mappings. Parallel observation can wait for a later roadmap item.

## Consolidated requirements inventory

### Functional requirements

| ID | Requirement | Source status | Initial priority |
|---|---|---|---|
| FR-001 | Compare two Git/snapshot references and identify changed files. | User decision | Must |
| FR-002 | Build a deterministic content-addressed semantic tree for the analyzed codebase. | User proposal | Must |
| FR-003 | Maintain a reverse impact graph from changed code units to dependent code scopes and tests. | User proposal/decision | Must |
| FR-004 | Support method/function-level impact when an adapter can provide it. | User decision via shared-currency example | Must for .NET deep; optional for minimal adapters |
| FR-005 | Emit a plain list of affected test identities without requiring execution. | User decision | Must |
| FR-006 | Offer dry-run branch/snapshot comparison. | User proposal | Must |
| FR-007 | Default a PR comparison to target branch versus current branch. | User decision | Must |
| FR-008 | Run a full suite to establish local cold-start observations when requested or required. | User direction | Must |
| FR-009 | Collect dynamic per-test code observations in the .NET deep adapter. | Accepted recommendation | Must |
| FR-010 | Use historical correlation as evidence in selection. | Accepted recommendation | Should for first useful release |
| FR-011 | Rank tests using impact likelihood/confidence and expected execution cost. | Accepted direction | Must |
| FR-012 | Prefer the full suite when predicted savings are below a configurable floor; default the floor to 30%. | User decision | Must |
| FR-013 | Leave the confidence acceptance threshold user-configurable with no universal default. | User decision | Must |
| FR-014 | Report confidence/evidence maturity and unmatched historical runs. | User decision | Must when history is used |
| FR-015 | Distinguish authoritative CI observations from looser local observations. | User decision | Must |
| FR-016 | Publish learned state only after the run concludes, whether pass or fail. | User decision | Must |
| FR-017 | Allow local learned state to be reset/rebuilt. | User decision | Must |
| FR-018 | Persist CLI state in a repository-local, Git-ignored directory by default. | User decision | Must |
| FR-019 | Permit user-owned shared/cloud/bare-metal history storage without requiring a hosted service. | User decision | Should; protocol first, server later |
| FR-020 | Detect multiple languages, fail if unconfigured, and list them. | User decision | Must |
| FR-021 | Accept per-language capability selection, for example `dotnet:deep,go:minimal`. | User decision, corrected example | Must once mixed-language support exists |
| FR-022 | Define a minimal third-party adapter contract for affected-test listing. | User decision | Must |
| FR-023 | Fail clearly when a requested adapter capability is unavailable. | User decision | Must |
| FR-024 | Continue on unmapped changed code by default, report it, and fail under `--pedantic`. | User decision | Must |
| FR-025 | Build before analysis by default. | User decision | Must |
| FR-026 | Support `--no-build`; fail if compatible prior outputs do not exist. | User decision | Must |
| FR-027 | Classify compilation failure as an analysis error. | User decision | Must |
| FR-028 | Support a user-provided `timeoutMs`; do not invent an additional default latency policy. | User decision | Must |
| FR-029 | Treat external dependency failures during tests as normal test failures. | User decision | Must |
| FR-030 | Begin with one .NET solution per analysis. | User decision | Must |
| FR-031 | Execute deep observation serially in the initial version. | User decision | Must |

### Adapter capability contract

| Capability | Minimal adapter | Semantic adapter | Deep adapter |
|---|---:|---:|---:|
| Detect owned source and test files | Required | Required | Required |
| Discover stable test identities | Required | Required | Required |
| Map changed files to candidate tests | Required | Required | Required |
| Emit a plain affected-test list | Required | Required | Required |
| Parse language semantics below file level | Optional | Required | Required |
| Emit code-unit dependency edges | Optional | Required | Required |
| Build and invoke the native test platform | Optional | Optional | Required |
| Observe code units executed by each test | Optional | Optional | Required |
| Report timings and outcomes | Optional | Optional | Required |

An unsupported requested capability is an explicit error. The core should negotiate capabilities before doing expensive work.

### CLI surface captured from the conversation

The exact command names and grammar remain subject to specification, but these concepts are required:

| Concept | Historical/example spelling | Required behavior |
|---|---|---|
| Compare without executing | `--dry-run:{branchname}` | Resolve base/head, analyze impact, print proposed tests and evidence. |
| Select adapter depth | `--languages=dotnet:deep,go:minimal` | Fail on unconfigured mixed-language repositories; validate capabilities. |
| Enforce unmapped-code strictness | `--pedantic` | Convert unmapped changed units from diagnostics into failure. |
| Skip compilation | `--no-build` | Reuse compatible outputs or fail clearly. |
| Bound runtime | `--timeoutMs=<n>` | Stop according to an explicit user-provided duration. |
| Configure minimum savings | Name unresolved | Default to 30%; fall back to the full suite below the floor. |
| Configure confidence/risk tolerance | Name unresolved | No universal default value. |
| Reset local state | Name unresolved | Discard/rebuild local observations safely. |

### Data and evidence requirements

The persisted model needs at least:

- Snapshot identity independent of branch name, plus optional Git commit/ref provenance.
- Semantic nodes with stable adapter-defined identities, content hashes, parent relations, and source locations.
- Dependency edges between code units.
- Stable test identities and their adapter/test-platform provenance.
- Per-test observation sets: executed code units, duration, outcome, timestamp, snapshot, and environment fingerprint.
- Historical correlations between changed units and test outcomes, with sample counts.
- Evidence provenance: authoritative CI, local, imported, or community adapter.
- Planner output: changed units, expanded impacts, selected tests, unmapped units, confidence inputs, predicted selected/full costs, expected savings, warnings, and fallback reason.
- Atomic run state so incomplete observations are not made visible as trusted history.

### Non-functional requirements

| ID | Requirement | Rationale |
|---|---|---|
| NFR-001 | Analysis must be deterministic for the same snapshots, configuration, adapter versions, and trusted history. | Developers and agents need reproducible plans. |
| NFR-002 | Incremental indexing must avoid rereading/recomputing unchanged semantic subtrees when possible. | Commit-loop speed is the core value proposition. |
| NFR-003 | The engine and adapter protocol must be language-neutral. | New languages should not require modifying core policy. |
| NFR-004 | No NuGet/package dependency may be injected into the user's projects for normal operation. | Explicit user constraint and adoption concern. |
| NFR-005 | Local state is private, user-owned, resettable, and Git-ignored by default. | Self-hosting and safe local experimentation. |
| NFR-006 | Reports must explain why each test was selected and why a full-suite fallback occurred. | Advisory decisions must be inspectable. |
| NFR-007 | macOS and Linux are first-class platforms. | Explicit platform scope. |
| NFR-008 | Native Windows behavior is not promised; WSL is best-effort only. | Explicit non-goal. |
| NFR-009 | Adapter capabilities and versions must be discoverable before analysis. | Prevent late “feature unavailable” surprises. |
| NFR-010 | The initial deep observation path favors correctness and serial determinism over parallel speed. | Explicit user choice. |

## Current scope boundaries

### In scope for the first implementation track

- A language-neutral planner and state model.
- Git/working-tree snapshot comparison for local and PR contexts.
- Minimal affected-test list output.
- A complete first-party .NET adapter for .NET 6+ repositories.
- Dynamic per-test observation and historical correlation.
- Cost-aware fallback to the full suite.
- Local Git-ignored state, plus an interface for user-owned shared storage.
- macOS and Linux.
- One .NET solution per invocation.
- Serial deep observation.

### Explicitly out of scope or not guaranteed

- Replacing a project's complete validation policy or periodic full-suite run.
- A first-party hosted service or managed storage.
- Native Windows support.
- Multiple .NET solutions in one initial analysis.
- Coverage completeness enforcement; existing coverage tools own that concern.
- Provisioning databases, queues, containers, or other external integration-test dependencies.
- Special classification of external dependency failures.
- Requiring NuGet, Node, or other application-project dependencies.
- Guaranteeing community adapters.
- A first-party TypeScript adapter in the current plan.
- Parallel deep test observation in V1.

## User decisions versus assistant recommendations

### Explicit user-owned decisions

- Advisory posture; no completeness guarantee.
- CI/PR-first default with a supported local mode.
- Self-hosted/user-owned operation and storage.
- Git-ignored repository-local state.
- Completed-run visibility only.
- Dynamic function-level fan-out across all dependent features.
- Default 30% minimum expected savings before optimization is worthwhile.
- User-controlled confidence threshold with no universal default.
- No project-injected packages.
- .NET 6+, macOS, and Linux focus.
- Explicit mixed-language configuration.
- Continue on unmapped code unless pedantic.
- TypeScript removed from current first-party scope.
- Build by default; `--no-build` requires compatible outputs.
- Compilation failures are analysis errors.
- No implicit timeout policy; explicit `timeoutMs` only.
- One solution and serial deep observation initially.
- External test infrastructure remains external.

### Assistant options explicitly accepted by the user

- Dynamic per-test observation as a primary evidence source.
- Historical correlation as a primary evidence source.
- Ranking the best test set and using the full suite when selection does not produce meaningful savings.
- An unspecified scope recommendation table was accepted, but its unavailable rows are not asserted here.

### Assistant recommendations still requiring ratification

These items are consistent with the conversation but were not explicitly frozen by the user:

- The exact probability model and definition of `P(A)`.
- The exact command grammar and exit-code taxonomy.
- The use of the repository-selected/newest compatible .NET SDK after respecting `global.json`.
- The concrete state store, wire protocol, and shared-storage server shape.
- Go as the next first-party adapter after .NET.
- The engine implementation language.

## Open questions and decision backlog

| ID | Question | Why it matters | Suggested decision point |
|---|---|---|---|
| OQ-001 | What precisely is event A in `P(A)`: a test executing changed code, a test detecting a regression, or the selected set containing every relevant test? | Different events require different evidence and calibration. | Before implementing the planner. |
| OQ-002 | How is uncertainty represented: posterior probability, confidence interval, calibrated score, or conservative bound? | Users configure risk tolerance, so the score must have stable semantics. | During the statistics spike. |
| OQ-003 | What does the 10–20% “margin” refer to: uncertainty width, tolerated miss risk, or savings variance? | It cannot become configuration until defined. | Statistics spike with examples. |
| OQ-004 | What are the final CLI names and precedence rules for base/head, PR metadata, language modes, timeout, savings, and confidence? | Required for adapter and CI stability. | Before public alpha. |
| OQ-005 | Which state store is used locally, and what protocol enables user-owned shared storage? | Affects concurrency, portability, migrations, and privacy. | Architecture decision before persistence work. |
| OQ-006 | How are authoritative CI observations authenticated or distinguished from local uploads? | Prevents weak/local evidence from silently becoming trusted. | Shared-storage design. |
| OQ-007 | What is the exact stable identity for overloaded/generic .NET methods and parameterized tests? | Historical mappings are useless if identities drift. | .NET adapter spike. |
| OQ-008 | Which .NET test platforms are supported first, and how are VSTest/Microsoft.Testing.Platform differences isolated? | Determines execution and observation hooks. | .NET adapter design. |
| OQ-009 | How are generated code, source generators, reflection, dependency injection, and runtime dispatch represented? | Static graphs alone will miss important edges. | Deep-observation conformance cases. |
| OQ-010 | When no merge base/history exists, may explicit snapshots or a helper manifest establish the baseline? | Needed for shallow CI clones and imported source archives. | Git/snapshot ADR. |
| OQ-011 | Should timed-out runs publish failed-test outcomes but discard incomplete mapping evidence? | Completion-only visibility does not fully specify cancellation semantics. | Runner lifecycle ADR. |
| OQ-012 | Is Go the second official adapter, or only an example community adapter? | Affects repository structure and roadmap promises. | After .NET alpha. |
| OQ-013 | Which implementation language should the engine use? | Influences deployment, contribution ergonomics, profiler integration, and delivery cost. | After targeted language/toolkit spikes. |
| OQ-014 | Does “hash collision root” mean nearest changed semantic ancestor or a different aggregation rule? | Prevents encoding ambiguous terminology in the domain model. | Domain-model review. |
| OQ-015 | What exact recommendations were contained in the accepted but unavailable scope table? | Required to claim a fully faithful record. | Recover from original assistant transcript if available. |

## Recommended wording for the product promise

> Merkle recommends a fast, evidence-backed test plan for a code change. It explains the affected code, selected tests, estimated savings, confidence inputs, and any unmapped areas. It can fall back to the full suite when selection is not worthwhile. Unselected tests can fail, so periodic full validation remains part of the team's policy.

## Change control for this record

Future conversations should append or amend decisions by ID. A later explicit choice supersedes an earlier idea; keep the old entry as history and point it to the replacement. The early TypeScript idea stays in the chronology and points to its removal in decision 27.
