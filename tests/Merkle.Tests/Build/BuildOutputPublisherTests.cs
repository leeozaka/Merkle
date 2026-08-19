using System.Security.Cryptography;
using System.Text.Json;
using Merkle.Build;

namespace Merkle.Tests.Build;

public sealed class BuildOutputPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"merkle-output-tests-{Guid.NewGuid():N}");

    public BuildOutputPublisherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Publish_CreatesOwnedOutputWithOnlyBuiltAdaptersAndDeterministicManifest()
    {
        var hostStaging = Path.Combine(_root, "staging", "host");
        var adapterStaging = Path.Combine(_root, "staging", "adapters");
        var output = Path.Combine(_root, "output");
        var artifact = Path.Combine(adapterStaging, "workers", "golang", "worker");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "go");
        var artifactHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifact))).ToLowerInvariant();
        var failedArtifact = Path.Combine(adapterStaging, "workers", "java", "failed.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(failedArtifact)!);
        await File.WriteAllTextAsync(failedArtifact, "failed smoke payload");
        Directory.CreateDirectory(hostStaging);
        await File.WriteAllTextAsync(Path.Combine(hostStaging, "stale.tmp"), "current-run-only");

        var request = new BuildOutputRequest(
            output,
            hostStaging,
            adapterStaging,
            "Release",
            "linux-x64",
            [
                new AdapterBuildResult(
                    "java",
                    AdapterBuildStatus.Failed,
                    [],
                    "compiler failed"),
                new AdapterBuildResult(
                    "golang",
                    AdapterBuildStatus.Built,
                    [new AdapterBuildArtifact("golang", "workers/golang/worker", artifactHash, "1.0", "1.0", "minimal")])
            ]);

        var result = await new BuildOutputPublisher().PublishAsync(request, default);

        Assert.Equal(Path.GetFullPath(output), result.OutputPath);
        Assert.True(File.Exists(Path.Combine(output, BuildOutputPublisher.OwnershipMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(output, "workers", "golang", "worker")));
        Assert.False(File.Exists(Path.Combine(output, "workers", "java", "failed.jar")));
        Assert.True(File.Exists(Path.Combine(output, "stale.tmp")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var adapters = manifest.RootElement.GetProperty("adapters").EnumerateArray().ToArray();
        var adapter = Assert.Single(adapters);
        Assert.Equal("golang", adapter.GetProperty("id").GetString());
        Assert.DoesNotContain(Path.GetFullPath(adapterStaging), await File.ReadAllTextAsync(result.ManifestPath), StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp", await File.ReadAllTextAsync(result.ManifestPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publish_RejectsNonEmptyUnownedOutput()
    {
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "existing.txt"), "keep");

        var request = new BuildOutputRequest(
            output,
            Path.Combine(_root, "staging"),
            Path.Combine(_root, "adapters"),
            "Debug",
            "portable",
            [new AdapterBuildResult("dotnet", AdapterBuildStatus.Built, [])]);

        await Assert.ThrowsAsync<IOException>(() => new BuildOutputPublisher().PublishAsync(request, default).AsTask());
    }

    [Fact]
    public async Task Publish_RejectsArtifactWhoseRecordedChecksumDoesNotMatchPackage()
    {
        var hostStaging = Path.Combine(_root, "staging", "host");
        var adapterStaging = Path.Combine(_root, "staging", "adapters");
        Directory.CreateDirectory(hostStaging);
        var artifact = Path.Combine(adapterStaging, "workers", "python", "adapter.pyz");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "python");
        var request = new BuildOutputRequest(
            Path.Combine(_root, "output"),
            hostStaging,
            adapterStaging,
            "Debug",
            "osx-arm64",
            [new AdapterBuildResult(
                "python",
                AdapterBuildStatus.Built,
                [new AdapterBuildArtifact("python", "workers/python/adapter.pyz", new string('0', 64), "1.0", "1.0", "minimal")])]);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new BuildOutputPublisher().PublishAsync(request, default).AsTask());

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(request.OutputPath));
    }

    [Fact]
    public async Task Publish_RejectsAdapterArtifactOutsideWorkersTree()
    {
        var hostStaging = Path.Combine(_root, "staging", "host");
        var adapterStaging = Path.Combine(_root, "staging", "adapters");
        Directory.CreateDirectory(hostStaging);
        var artifact = Path.Combine(adapterStaging, "unexpected.bin");
        Directory.CreateDirectory(adapterStaging);
        await File.WriteAllTextAsync(artifact, "payload");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifact))).ToLowerInvariant();
        var request = new BuildOutputRequest(
            Path.Combine(_root, "output"),
            hostStaging,
            adapterStaging,
            "Debug",
            "osx-arm64",
            [new AdapterBuildResult(
                "python",
                AdapterBuildStatus.Built,
                [new AdapterBuildArtifact("python", "unexpected.bin", hash, "1.0", "1.0", "minimal")])]);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new BuildOutputPublisher().PublishAsync(request, default).AsTask());

        Assert.Contains("workers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_DoesNotCopyUndeclaredFailedAdapterFileOutsideWorkers()
    {
        var hostStaging = Path.Combine(_root, "staging", "host");
        var adapterStaging = Path.Combine(_root, "staging", "adapters");
        Directory.CreateDirectory(hostStaging);
        Directory.CreateDirectory(adapterStaging);
        await File.WriteAllTextAsync(Path.Combine(hostStaging, "Merkle.Cli"), "host");
        await File.WriteAllTextAsync(Path.Combine(adapterStaging, "unexpected.bin"), "failed adapter payload");
        var request = new BuildOutputRequest(
            Path.Combine(_root, "output"),
            hostStaging,
            adapterStaging,
            "Debug",
            "osx-arm64",
            [new AdapterBuildResult("future", AdapterBuildStatus.Failed, [], "smoke failed")]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BuildOutputPublisher().PublishAsync(request, default).AsTask());

        Assert.False(File.Exists(Path.Combine(request.OutputPath, "unexpected.bin")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
