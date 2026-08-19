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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
