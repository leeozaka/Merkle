---
status: accepted
---

# Run local agent installations through a containerized Merkle runtime

Local agent installations build and run Merkle through one Docker Compose service exposed by a user-wide `merkle` wrapper. Versioned source checkouts and images remain local, while the wrapper mounts the active Git working tree, preserves linked-worktree metadata paths, and keeps credentials and external mounts opt-in.

## Decision

The official repository owns `./install`, the runtime image, Compose configuration, and the wrapper. Installations resolve an official tag or commit, bundle the requested adapter toolchains, pass smoke checks, and become immutable variants selected through a `current` pointer or repository pin. A portable agent skill orchestrates installation, cold-start observation, iterative selected tests, and the repository's canonical full suite.

Host-native toolchain installation was rejected because an adapter package still depends on its language runtime and target build tools. Public images were deferred because the first release is for local cloning, and separate agent-specific hooks were rejected in favor of one portable skill.

## Consequences

- Docker Engine with Compose v2 is the only runtime prerequisite beyond Git and a POSIX shell.
- The standard image is large when all adapters are selected; reduced adapter variants trade breadth for disk and build time.
- Target-specific services, system libraries, mounts, and secrets remain explicit repository runtime configuration.
- The Merkle CLI keeps its no-surprise-mutation boundary. The agent skill may create `.merkle.yml` or update `.gitignore` only when the user has asked to prepare that repository.
- The repository's native full suite remains the final acceptance gate.
