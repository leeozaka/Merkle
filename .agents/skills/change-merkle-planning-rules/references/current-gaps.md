# Current planning gaps

Treat these as missing or incomplete seams, not hidden features.

- There is no implemented `path-only` profile or configured directory-rule evaluator.
- `EvidenceKind.ConfiguredRule` exists, but no parser or producer emits it.
- The specification and implementation guide mention explicit configured-rule explanations and conservative fallback rules. The checked-in configuration schema does not expose them.
- `semantic` is currently descriptor/profile metadata. `AdapterRegistry` checks membership, but `ImpactEngine` does not run a separate semantic-profile pipeline.
- `execution.serialObservation` is parsed and validated but not consumed by the CLI, engine, or adapters. Deep implementations remain serial by design.
- A single detected language with no explicit selection defaults to `minimal`; mixed-language repositories require explicit selection.
- TypeScript detection exists without a first-party TypeScript adapter.

Do not document these surfaces as working. To implement one, define schema and ownership, add typed contracts, update specification and ADRs as needed, cover configuration and report behavior, and test the full CLI-to-terminal path.
