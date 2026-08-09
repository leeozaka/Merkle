# CI and remote-state operations

Status: Schema 1 operational contract  
Audience: repository and CI maintainers

Merkle runs inside infrastructure the repository owner already controls. Builds and tests execute repository code with the runner's permissions; Merkle is not a sandbox.

## CI sequence

Use the same order on any provider:

1. Check out full Git history. Shallow history can hide the target merge base.
2. Restore/build Merkle separately from the repository under analysis.
3. Set `MERKLE_PR_BASE_REF` and `MERKLE_PR_HEAD_REF`, or pass `--base` and `--head`.
4. Run `merkle plan --format json` and keep the terminal report as an artifact.
5. Run `merkle observe` only on trusted branches or trusted pull requests.
6. Run the repository's full suite on its normal schedule and import the resulting Merkle report with `merkle history import`.

The checked-in GitHub workflow builds/tests on macOS and Linux, enforces the coverage gate, and publishes Native AOT smoke artifacts. Other CI systems can use the same commands; no GitHub API is required by the CLI.

Cache `.merkle/state.db` only within one repository identity and compatibility namespace. Do not share a path-derived repository identity between clones. A reviewed `repository.repositoryId` is required for portable history.

## Untrusted forks

Do not expose remote-state write credentials to untrusted fork jobs. Those jobs may plan with local state or an anonymous/read-only remote endpoint. Observation and selected execution run arbitrary repository code, so place them in the same trust boundary as the repository's normal test job.

Use a separate trusted workflow to publish official-CI history. Treat imported reports as data: Merkle checks schema, repository identity, adapter compatibility, bounds, and outcomes before admitting them.

## Remote history contract

The configured endpoint is an absolute HTTPS base URI without embedded credentials, query, or fragment. The client calls `history` beneath that base.

- `GET history` accepts repository, schema, adapter, build-family, cursor, and limit query values. A successful response includes an ETag and a schema-1 JSON page.
- `POST history` sends a schema-1 JSON publication with `Authorization: Bearer`, `If-Match`, `Idempotency-Key`, `X-Merkle-Protocol-Version: 1`, and `X-Merkle-History-Schema: 1`.
- The server returns 401/403 for authorization failures and 409/412 for compare-and-swap conflicts.
- Requests and responses are capped at 16 MiB. Only terminal history is accepted. Source content and environment values are outside the wire contract.

The repository supplies the token through the environment variable named by `history.tokenEnvironment`. Merkle never accepts the token itself in `.merkle.yml`.

## Release verification

Tag builds create self-contained macOS/Linux x64 and Arm64 archives, SHA-256 checksum files, and GitHub build-provenance attestations. Verify both before installation:

```bash
shasum -a 256 --check merkle-<rid>.tar.gz.sha256
gh attestation verify merkle-<rid>.tar.gz --repo <owner>/<repository>
```

The managed semantic worker and startup-hook observer ship beneath `workers/dotnet` next to the native CLI. The archive also carries the supported native SQLite library. These artifacts do not modify analyzed projects.
