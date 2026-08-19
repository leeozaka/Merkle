# Current repository gaps

Do not assume these missing surfaces exist.

- The repository has no root `AGENTS.md`, `CONTRIBUTING.md`, `SECURITY.md`, `CODEOWNERS`, `.editorconfig`, or checked-in pre-commit hook setup. Use `Directory.Build.props`, CI, the documentation index, and the in-repository skills as current guidance.
- CI runs Go format, vet, and worker tests directly. It packages Python and Java but does not directly invoke their native unit-test commands. Run those suites for Python or Java changes.
- `README.md` and `docs/index.md` link to `QA-REPORT.md`, but that file is absent.
- The implementation guide's suggested source tree does not match the delivered layout. Follow the current `src/core`, `src/infrastructure`, `src/build`, and `src/adapters` directories.
- The release workflow starts from `v*` tags, but the repository has no checked-in release checklist and no explicit project `Version` or `VersionPrefix` property.
- There are no repository-local Git hooks beyond Git's sample hooks. Do not claim a local hook ran unless one was installed outside this repository.

Treat these as facts to report or tasks to address when in scope. Do not silently add contributor policy, release semantics, or security ownership as part of unrelated code work.
