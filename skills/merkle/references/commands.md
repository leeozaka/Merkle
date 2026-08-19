# Merkle local commands

## Installation

From a temporary official checkout:

```sh
./install
./install --ref v1.2.3
./install --adapters dotnet,golang
```

After the first installation, `merkle install` delegates to the same installer retained in the selected official checkout and accepts the same options.

`all` selects `dotnet,golang,python,java`. `go` normalizes to `golang`. Installs are local, versioned, and immutable; an unqualified install selects the newest stable `vMAJOR.MINOR.PATCH` tag and falls back to the default branch when no stable tag exists.

Manage installed variants through the host wrapper:

```sh
merkle doctor
merkle list
merkle use <installation-id>
merkle use <installation-id> --project
merkle uninstall <installation-id|current>
```

Project pinning writes `.merkle-version` with the exact ref, commit, architecture, adapter set, and installation ID. It selects a runtime variant; it does not install the agent skill. Skill installation and runtime installation have separate lifecycles.

## Development loop

Use one deep language per invocation:

```sh
merkle state status
merkle observe --base main --head WORKTREE --languages dotnet:deep
merkle run --base main --head WORKTREE --languages dotnet:deep
merkle plan --base main --head WORKTREE --languages golang:deep
```

Repeat for each configured .NET or Go scope. Python and Java adapters currently provide semantic planning but not deep execution; use their repository-native tests.

## Runtime customization

The wrapper forwards no host secrets by default. A repository may add `.merkle-runtime.yml`:

```yaml
image: team/merkle-runtime:local
environment:
  - PRIVATE_FEED_TOKEN
mounts:
  - .tool-cache
```

Mounts resolve beneath the Git root. Absolute paths and paths escaping the repository require a separate explicit user decision, passed as `merkle --allow-external-mount <exact-path> <command>`. Filesystem root and home-directory mounts are rejected. Environment entries name variables to forward; values stay out of retained manifests and logs.

For a derived image, replace `image` with repository-relative `dockerfile` and `context` keys. The Dockerfile must accept `MERKLE_BASE_IMAGE`:

```dockerfile
ARG MERKLE_BASE_IMAGE
FROM ${MERKLE_BASE_IMAGE}
RUN apt-get update && apt-get install -y your-project-package
```

Use `merkle --offline <command>` to disable the runtime network after required dependencies are cached.
