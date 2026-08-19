---
name: merkle
description: Install the official Docker-backed Merkle CLI or use Merkle to select faster tests during development. Use when a user asks to install github.com/leeozaka/merkle, make Merkle available to an agent, use the Merkle helper, establish Merkle observations, or shorten iterative test runs while retaining the repository's full suite as the final gate.
---

# Merkle

Use Merkle for the tight development loop. Finish every task with the repository's canonical full suite.

Read [references/commands.md](references/commands.md) when installing, managing versions, pinning a repository, or configuring container access.

## Install

1. Check for Git and local Docker Compose v2. Stop with installation guidance when either is unavailable. Treat WSL paths outside its Linux filesystem as best effort.
2. If `merkle doctor` succeeds, reuse the installation. Run `merkle install` when the user requested the newest stable version.
3. Otherwise clone `https://github.com/leeozaka/merkle.git` into a temporary directory and run its `./install`. Use all adapters unless the prompt names a subset. Pass an explicit tag or commit through `--ref`.
4. Tell the user before cloning, building the image, or performing a cold-start observation. The first local build is large and may take several minutes.
5. Run `merkle doctor`. Installation is complete when the selected immutable variant and its local image pass verification.

When the request comes from chat, make the command available to the current agent session; do not add repository instructions unless the user asks. When repository instructions invoke this skill, follow the scope and persistence those instructions define.

## Prepare a repository

1. Inspect the repository instructions and its canonical targeted and full-suite commands.
2. If `.merkle.yml` is absent, explain the smallest valid configuration for the supported deep language. Create it and add `.merkle/` to `.gitignore` only when the user explicitly asks to prepare that repository. Merkle currently executes deep observations for .NET and Go; use the repository's normal tests for other languages.
3. Run `merkle state status`. When compatible complete-suite evidence is absent, announce the cold start and run one `merkle observe` per configured deep language.
4. If configuration, dependencies, mounts, or credentials remain ambiguous, ask before running. Forward only the named environment variables and mounts that the user authorizes.

The repository is ready when configuration is valid and compatible observation evidence exists, or when the limitation and fallback are explicit.

## Iterate

After each meaningful code-change batch, run `merkle run` against `WORKTREE` once per configured deep language. Use `merkle plan` when inspection is requested without execution.

Report whether Merkle selected tests, recommended the full suite, or could not decide. If Merkle cannot support the repository or its container dependencies, explain the exact reason and use the repository's normal targeted tests. Never present omitted tests as safe.

## Finish

Run the repository's canonical full suite outside Merkle. A passing Merkle-selected run does not replace this gate. If the full suite fails, keep working or report the failure; do not declare the task complete.

The task is complete only when iterative tests or their stated fallback pass, the canonical full suite passes, and no installation or container failure remains hidden.
