# Adapter verification

Run the native tests for the changed worker, then the C# host and packaging tests.

## Focused .NET tests

```bash
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~ProcessLanguageAdapterTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~AdapterRegistryTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~ContractConformanceTests
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~AdapterBuild
dotnet test tests/Merkle.Tests/Merkle.Tests.csproj --filter FullyQualifiedName~AdapterManifestContractTests
```

Add the adapter's host and deep-operation test classes to this set when applicable.

## Native worker tests

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

## Protocol smoke

Send exactly one descriptor request to the built worker:

```json
{"protocolVersion":"1.0","requestId":"smoke","operation":"describe","payload":{}}
```

Assert exit code zero, no stdout prefix or suffix outside the JSON response, matching request and operation values, canonical language, accurate capabilities, and bounded stderr.

## Build and package

```bash
./build build --adapters <adapter-id> --adapter-policy strict --test
./build publish --adapters <adapter-id> --adapter-policy strict --runtime <current-rid>
```

Inspect `adapters.json`, every declared checksum, executable mode where needed, and the final CLI artifact lookup. Run `--adapters all` under strict policy when shared catalog or packaging code changed and all toolchains are available.
