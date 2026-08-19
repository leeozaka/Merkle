## Build for local development

Install the .NET SDK selected by `global.json`. It is required for every source build. Go, Python, and a Java JDK with Maven are needed only when you select their adapters.

From the Merkle source directory:

```bash
./build build
```

This defaults to the .NET adapter, strict failure handling, sequential execution, and a Debug build. With an interactive terminal, running `./build` with no arguments shows adapter readiness and prompts for the selection. Automation should pass arguments explicitly:

```bash
./build build \
  --adapters dotnet,golang,python \
  --adapter-policy best-effort \
  --builds parallel \
  --max-parallel 3 \
  --test \
  --format json
```

Run `./build --help` for the complete flag list. `--clean` removes only stale intermediate directories carrying a Merkle ownership marker; it does not remove the previous successful package or user-owned directories.

`--adapters all` selects every adapter registered in the repository even when its toolchain is missing. Strict policy preflights the whole selection and builds nothing if any selected toolchain is unavailable. Best effort skips unavailable adapters and continues after adapter failures; it still fails when no adapter succeeds. Exit codes are 0 for success or accepted partial success, 2 for invalid arguments, 3 for adapter-policy failure, 4 for helper/host/package failure, and 130 for cancellation.

Build reports and per-adapter logs stay beside the run directory, outside the promoted package. Default packages are written beneath `artifacts/build/<configuration>/<runtime>` or `artifacts/publish/<configuration>/<runtime>`. Use `--output` and `--report` to choose explicit locations.

Plain `dotnet build` and `dotnet publish` remain available for .NET-only output and do not invoke Go, Python, or Java. The test project enforces at least 80% aggregate line and branch coverage.

### Run the development build against a repository

Set the companion paths when the target repository is different from the Merkle source repository:

```bash
export MERKLE_SOURCE=/absolute/path/to/Merkle
export MERKLE_DOTNET_WORKER="$MERKLE_SOURCE/src/adapters/dotnet/worker/bin/Debug/net10.0/Merkle.Adapters.DotNet.Worker.dll"
export MERKLE_DOTNET_OBSERVER="$MERKLE_SOURCE/src/adapters/dotnet/observer/bin/Debug/net8.0/Merkle.Adapters.DotNet.Observer.dll"
export MERKLE_GO_ADAPTER="$MERKLE_SOURCE/src/cli/bin/Debug/net10.0/workers/go/merkle-adapter-go"

cd /absolute/path/to/repository-under-test
dotnet "$MERKLE_SOURCE/src/cli/bin/Debug/net10.0/Merkle.Cli.dll" --help
```

Run all later examples by replacing `merkle` with that `dotnet ...Merkle.Cli.dll` command, or publish a native package and invoke its executable directly.

## Publish a native package

The helper publishes for the current machine because every selected adapter must run its smoke check before packaging:

| Platform | Runtime identifier |
|---|---|
| Linux x64 | `linux-x64` |
| Linux Arm64 | `linux-arm64` |
| macOS Intel | `osx-x64` |
| macOS Apple silicon | `osx-arm64` |

For example, publish all current adapters on Apple silicon with:

```bash
export MERKLE_RID=osx-arm64

./build publish \
  --adapters all \
  --adapter-policy strict \
  --runtime "$MERKLE_RID" \
  --output "artifacts/$MERKLE_RID"
```

Smoke-test the package:

```bash
"artifacts/$MERKLE_RID/Merkle.Cli" --help
"artifacts/$MERKLE_RID/Merkle.Cli" state status
```

Deploy the entire output directory. Copying only `Merkle.Cli` omits selected adapter payloads and `adapters.json`.

The package is self-contained for the CLI. Deep analysis still invokes toolchains required by the repository being analyzed. Install Git and those project toolchains on the runtime machine.

On a CI runner, unpack the directory into a fixed location and invoke the executable by its absolute path. The examples below use `merkle` as shorthand for that path.

## Prepare the repository being analyzed

Merkle requires usable Git history. Run it from within the target repository and fetch enough history to resolve the baseline or pull-request merge base.

Add the state directory to `.gitignore`:

```gitignore
.merkle/
```

Create `.merkle.yml` in the repository root:

```yaml
schemaVersion: 1

repository:
  solution: Example.sln
  stateDirectory: .merkle
  # Generate once with uuidgen, review it, and share it only with trusted clones.
  repositoryId: 019fde48-89db-7230-b822-c9f25c100df8

languages:
  dotnet:
    profile: deep

baseline:
  localRef: main
  prStrategy: merge-base

execution:
  build: true
  serialObservation: true
  configuration: Release
  # timeoutMs is optional. Merkle has no default timeout.

policy:
  minSavingsPercent: 30
  confidenceThreshold: null
  onLowConfidence: null
  unmapped: warn

history:
  provider: local
```

Replace `Example.sln` with the target solution. Generate a new `repositoryId` with `uuidgen`; do not reuse the sample identity across unrelated repositories.

For a Go repository, select `golang` and point `repository.solution` at `go.work` or `go.mod`. Omit `solution` when the snapshot contains one unambiguous workspace or module scope. The CLI accepts `go` as an alias, but configuration and reports use `golang`.

```yaml
repository:
  solution: go.work
  stateDirectory: .merkle

languages:
  golang:
    profile: deep
```

The parser rejects unknown fields, duplicate fields, tabs, inline collections, and unsupported values. Copy [`examples/merkle.yml`](examples/merkle.yml) if you prefer to start from the checked-in example.

## Plan without executing tests

Compare `main` with the current commit:

```bash
merkle plan \
  --base main \
  --head HEAD \
  --languages dotnet:deep
```

Compare `main` with tracked and non-ignored working-tree changes:

```bash
merkle plan \
  --base main \
  --head WORKTREE \
  --languages dotnet:deep \
  --format json > merkle-report.json
```

`plan` never builds or executes tests. It writes the terminal report to standard output and diagnostics to standard error.

Use the same command shape for Go:

```bash
merkle plan --base main --head WORKTREE --languages go:deep
```

## Observe tests

`observe` builds by default, discovers the test catalog, and runs each discovered test serially with the startup-hook observer:

```bash
merkle observe \
  --base main \
  --head HEAD \
  --languages dotnet:deep \
  --timeout-ms 120000
```

Observation records complete assembly/project evidence. Member execution, reflection-only resolution, native code, child processes, assemblies loaded before the hook, and unmatched runner identities remain visible blind spots.

For Go, observation runs the discovered tests serially and admits only nonempty file-level cover profiles:

```bash
merkle observe \
  --base main \
  --head HEAD \
  --languages golang:deep \
  --timeout-ms 120000
```

Go subtests remain attached to their parent catalog entry. Interface dispatch, reflection, generated code, plugins, subprocesses, cgo/native code, build tags, and uninstrumented packages remain reported blind spots.

Use `--no-build` only after a compatible Merkle build has produced the exact artifact manifest for the same snapshot, solution, configuration, platform, and adapter version.

## Execute a policy-approved plan

`run` plans first. It executes tests only when the effective policy returns `selected` or `full-suite`. With no confidence threshold and low-confidence action, it returns `decision-not-configured` and does not execute tests.

Configure automatic behavior in `.merkle.yml`, for example:

```yaml
policy:
  minSavingsPercent: 30
  confidenceThreshold: 0.70
  onLowConfidence: full-suite
  unmapped: warn
```

Then run:

```bash
merkle run \
  --base main \
  --head HEAD \
  --languages dotnet:deep
```

CLI policy flags override the repository file for one run:

```bash
merkle run \
  --languages dotnet:deep \
  --min-savings-percent 20 \
  --confidence-threshold 0.70 \
  --on-low-confidence full-suite
```

Merkle remains advisory. Keep the repository's full regression suite in its normal scheduled or release workflow.

## Inspect, reset, and import state

Inspect repository-local state:

```bash
merkle state status
```

Reset only the validated local Merkle state directory:

```bash
merkle state reset --local
```

Import a schema-1 terminal report from a trusted complete-suite run:

```bash
merkle history import path/to/terminal-report.json
```

`history import` validates schema, repository identity, adapter compatibility, terminal status, bounds, and outcomes before admitting evidence.

## Pull-request and CI usage

Explicit `--base` and `--head` values take precedence. CI may instead set:

```bash
export MERKLE_PR_BASE_REF=origin/main
export MERKLE_PR_HEAD_REF=HEAD
merkle plan --languages dotnet:deep --format json
```

Merkle also recognizes supported GitHub and GitLab pull-request variables. Check out full history so the target merge base exists. Run `observe` and `run` only where you would trust the repository's normal build and test commands; they execute repository code with the runner's permissions.

## Remote history and serving

Merkle does not start an HTTP server. Teams that need shared history provide their own HTTPS service and configure the CLI as a client:

```yaml
history:
  provider: remote
  endpoint: https://history.example.com/merkle/
  tokenEnvironment: MERKLE_HISTORY_TOKEN
```

Set the named environment variable on trusted writers:

```bash
export MERKLE_HISTORY_TOKEN='value-from-your-secret-store'
```

The service implements `GET history` and `POST history` with ETags, compare-and-swap writes, idempotency keys, bearer authentication, schema headers, pagination, and a 16 MiB payload limit. The repository contains the client contract, not a reference server. See [CI and remote-state operations](docs/operations.md) for the wire contract and untrusted-fork rules.

## Exit codes

| Code | Meaning |
|---:|---|
| `0` | Success, including an advisory plan that is not authorized to execute |
| `2` | Configuration error |
| `3` | Missing or incompatible capability |
| `4` | Git, analysis, build, index, or state failure |
| `5` | Test failure |
| `6` | Policy failure |
| `130` | Interrupted operation |

## Common failures

- `DeepToolchainUnavailable`: deploy the complete publish directory, including `workers/dotnet`, or set both companion environment variables for a development build.
- `DeepProfileRequired`: use exactly `--languages dotnet:deep` with `observe` and `run`.
- `MixedLanguagesRequireSelection`: pass explicit `language:profile` selections for every language you want analyzed.
- `GitMergeBaseUnavailable`: fetch the target branch and enough Git history, then retry with explicit refs if needed.
- `SelectedTestsUnavailable`: the runtime test catalog could not match the planned stable identities. Keep the terminal report and use the full suite.
- `ArtifactsUnavailable`: remove `--no-build` so Merkle can create a compatible build.
- A successful plan with `decision-not-configured`: configure `confidenceThreshold` and `onLowConfidence` before expecting `run` to execute tests.
