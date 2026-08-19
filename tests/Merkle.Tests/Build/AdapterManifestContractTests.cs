using System.Security.Cryptography;
using System.Text.Json;
using Merkle.Build;

namespace Merkle.Tests.Build;

public sealed class AdapterManifestContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"merkle-manifest-tests-{Guid.NewGuid():N}");

    public AdapterManifestContractTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("", "not relative")]
    [InlineData("/absolute/adapter", "not relative")]
    [InlineData("workers/../escape", "unsafe")]
    [InlineData("payload/adapter", "beneath 'workers/'")]
    [InlineData("workers/java/missing.jar", "missing")]
    public async Task ValidateFile_RejectsUnsafeOrMissingArtifactPaths(string artifactPath, string expectedDiagnostic)
    {
        var manifestPath = Path.Combine(_root, Guid.NewGuid().ToString("N"), "adapters.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = new
        {
            schemaVersion = 1,
            merkleVersion = "1.0.0",
            configuration = "Debug",
            runtimeIdentifier = "osx-arm64",
            adapters = new[]
            {
                new
                {
                    id = "java",
                    version = "1.0.0",
                    protocolVersion = "1.0",
                    profile = "minimal",
                    artifacts = new[]
                    {
                        new { path = artifactPath, sha256 = new string('a', 64) }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        var error = Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(manifestPath));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateFile_RejectsNullDocumentAndWrongSchema()
    {
        var emptyPath = Path.Combine(_root, "null.json");
        await File.WriteAllTextAsync(emptyPath, "null");

        Assert.Contains(
            "empty",
            Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(emptyPath)).Message,
            StringComparison.OrdinalIgnoreCase);

        var schemaPath = await WriteManifestAsync(ValidManifest() with { SchemaVersion = 2 });

        Assert.Contains(
            "schema",
            Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(schemaPath)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("configuration")]
    [InlineData("runtime")]
    public async Task ValidateFile_RejectsMissingBuildIdentity(string field)
    {
        var manifest = ValidManifest();
        manifest = field switch
        {
            "version" => manifest with { MerkleVersion = " " },
            "configuration" => manifest with { Configuration = "" },
            "runtime" => manifest with { RuntimeIdentifier = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var path = await WriteManifestAsync(manifest);

        var error = Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(path));

        Assert.Contains("build identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateFile_RejectsEmptyAndDuplicateAdapterIds()
    {
        var manifest = ValidManifest();
        var emptyIdPath = await WriteManifestAsync(manifest with
        {
            Adapters = [manifest.Adapters[0] with { Id = " " }]
        });
        var duplicateIdPath = await WriteManifestAsync(
            manifest with { Adapters = [manifest.Adapters[0], manifest.Adapters[0]] },
            createArtifact: true);

        Assert.Contains(
            "non-empty and unique",
            Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(emptyIdPath)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "non-empty and unique",
            Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(duplicateIdPath)).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("protocol")]
    [InlineData("profile")]
    [InlineData("artifacts")]
    public async Task ValidateFile_RejectsIncompleteAdapterMetadata(string field)
    {
        var manifest = ValidManifest();
        var adapter = manifest.Adapters[0];
        adapter = field switch
        {
            "version" => adapter with { Version = "" },
            "protocol" => adapter with { ProtocolVersion = " " },
            "profile" => adapter with { Profile = "" },
            "artifacts" => adapter with { Artifacts = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var path = await WriteManifestAsync(manifest with { Adapters = [adapter] });

        var error = Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(path));

        Assert.Contains("incomplete metadata", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ValidateFile_RejectsInvalidChecksumSyntax(string checksum)
    {
        var manifest = ValidManifest(checksum);
        var path = await WriteManifestAsync(manifest, createArtifact: true);

        var error = Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(path));

        Assert.Contains("invalid SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateFile_RejectsChecksumMismatch()
    {
        var path = await WriteManifestAsync(ValidManifest(new string('0', 64)), createArtifact: true);

        var error = Assert.Throws<InvalidDataException>(() => AdapterManifestContract.ValidateFile(path));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> WriteManifestAsync(TestManifest manifest, bool createArtifact = false)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "adapters.json");
        Directory.CreateDirectory(directory);
        if (createArtifact)
        {
            var artifact = Path.Combine(directory, "workers", "java", "adapter.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            await File.WriteAllTextAsync(artifact, "payload");
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    private static TestManifest ValidManifest(string? checksum = null) =>
        new(
            1,
            "1.0.0",
            "Debug",
            "osx-arm64",
            [new TestAdapter(
                "java",
                "1.0.0",
                "1.0",
                "minimal",
                [new TestArtifact(
                    "workers/java/adapter.jar",
                    checksum ?? Convert.ToHexString(SHA256.HashData("payload"u8.ToArray())).ToLowerInvariant())])]);

    private sealed record TestManifest(
        int SchemaVersion,
        string MerkleVersion,
        string Configuration,
        string RuntimeIdentifier,
        TestAdapter[] Adapters);

    private sealed record TestAdapter(
        string Id,
        string Version,
        string ProtocolVersion,
        string Profile,
        TestArtifact[] Artifacts);

    private sealed record TestArtifact(string Path, string Sha256);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
