#!/bin/sh
set -eu

mkdir -p "$HOME"
if [ -n "${MERKLE_REPO_ROOT:-}" ]; then
    git config --global --add safe.directory "$MERKLE_REPO_ROOT"
fi

exec /opt/merkle/Merkle.Cli "$@"

