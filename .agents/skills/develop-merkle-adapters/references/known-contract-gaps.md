# Known contract gaps

Check current source and tests before changing these areas. Do not silently choose one side of an inconsistency.

- `docs/adapter-authoring.md` shows `adapterProtocol` in one descriptor example. Implementations and hosts use `protocolVersion`.
- Python and Java docs say JSON-lines. Current workers and `ProcessLanguageAdapter` exchange one bounded JSON document; a final newline is acceptable, a stream of messages is not.
- The authoring contract describes adapter `detect`, while `ILanguageAdapter` exposes only index and map. Repository language detection currently runs through `LanguageDetector`; process workers may still implement `detect` for protocol completeness.
- `AdapterCapability.Report` exists without a report-specific interface or process operation. Do not invent that seam as part of an unrelated adapter change.
- Supported-platform values differ between adapters and documents (`linux`/`macos` versus RID-like values). Preserve the current adapter's contract unless the task explicitly normalizes the schema and compatibility behavior.
- Python and Java advertise `semantic`, but the shared host does not run a separate semantic pipeline. Profile membership is capability negotiation metadata in the current core.
- Deep .NET can report a newer adapter version than minimal fixtures. Tie expectations to the configured capability path.
- `LanguageDetector` detects TypeScript, but the repository has no first-party TypeScript adapter. Detection evidence is not adapter support.
- The CLI and source-build parser accept `go` as an alias for `golang`; reports and reviewed configuration use `golang`. Verify configuration behavior before extending alias claims.
- The implementation guide's suggested source tree is historical guidance. Use the current `src/core`, `src/infrastructure`, `src/build`, and `src/adapters` layout.

Changing the protocol field names, transport shape, identity versions, platform vocabulary, or profile meaning requires compatibility analysis, conformance fixtures, specification updates, and usually an ADR.
