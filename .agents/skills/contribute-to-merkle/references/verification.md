# Verification

Choose checks that cover the changed seam. Run commands from the repository root unless a working directory is shown.

## Fast checks

Run a focused .NET test:

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~TypeOrMethodName
```

Run all .NET tests with the repository coverage threshold:

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --configuration Release
```

Verify C# formatting:

```bash
dotnet format Merkle.slnx --verify-no-changes
```

Build with warnings as errors:

```bash
dotnet build Merkle.slnx --configuration Release --warnaserror
```

## Adapter-native checks

CI runs the Go checks directly. It does not directly run the Python or Java unit suites, so run the commands below for changes in those adapters.

```bash
cd src/adapters/go/worker
test -z "$(gofmt -l .)"
go vet ./...
go test ./...
```

```bash
cd src/adapters/python
python3 -m unittest discover -s tests
```

```bash
cd src/adapters/java
mvn test
```

## Source-build and packaging checks

Build the default strict .NET package:

```bash
./build build
```

Build one adapter and run its native test hook when supported:

```bash
./build build --adapters <adapter-id> --adapter-policy strict --test
```

Verify the full adapter catalog only when all four toolchains are available:

```bash
./build publish --adapters all --adapter-policy strict --runtime <current-rid> --output artifacts/<current-rid>
```

Use `best-effort` only when a missing optional toolchain is an accepted part of the test. Never present best-effort success as proof that every selected adapter built.

## CI-equivalent order

For broad or release-sensitive changes, follow `.github/workflows/ci.yml`:

1. Run Go format, vet, and tests.
2. Restore the solution and runtime-specific CLI.
3. Check transitive packages for known vulnerabilities.
4. Build Release with warnings as errors.
5. Verify `dotnet format` has no changes.
6. Run the Release test project and its coverage threshold.
7. Publish all adapters under strict policy for the current runtime.
8. Smoke the CLI, worker artifacts, and `adapters.json`.

Do not claim the cross-platform gate passed from one operating system. CI covers Ubuntu x64 and macOS Arm64.
