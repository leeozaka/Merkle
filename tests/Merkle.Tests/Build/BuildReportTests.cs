using System.Text.Json;
using Merkle.Build;

namespace Merkle.Tests.Build;

public sealed class BuildReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"merkle-report-tests-{Guid.NewGuid():N}");

    public BuildReportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WriteAsync_WritesEveryAdapterStatusToExternalReport()
    {
        var path = Path.Combine(_root, "run", "report.json");
        var dotnetLogPath = Path.Combine(_root, "run", "logs", "dotnet.log");
        Directory.CreateDirectory(Path.GetDirectoryName(dotnetLogPath)!);
        await File.WriteAllTextAsync(dotnetLogPath, "$ dotnet build\nstdout:\nworker compiled\n");
        var report = new BuildReport(
            BuildOutcome.PartialSuccess,
            0,
            [
                new AdapterBuildResult("dotnet", AdapterBuildStatus.Built, []),
                new AdapterBuildResult("java", AdapterBuildStatus.Skipped, [], "JDK unavailable"),
                new AdapterBuildResult("python", AdapterBuildStatus.Failed, [], "smoke failed")
            ]);

        var result = await new BuildReportWriter().WriteAsync(
            new BuildReportRequest(path, BuildCommand.Publish, "Release", "linux-x64", report),
            default);

        Assert.Equal(Path.GetFullPath(path), result);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("partial-success", document.RootElement.GetProperty("outcome").GetString());
        var statuses = document.RootElement.GetProperty("adapters")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("adapterId").GetString()!);
        Assert.Equal("built", statuses["dotnet"].GetProperty("status").GetString());
        Assert.Equal("skipped", statuses["java"].GetProperty("status").GetString());
        Assert.Equal("failed", statuses["python"].GetProperty("status").GetString());
        var dotnetLog = await File.ReadAllTextAsync(dotnetLogPath);
        Assert.Contains("$ dotnet build", dotnetLog, StringComparison.Ordinal);
        Assert.Contains("worker compiled", dotnetLog, StringComparison.Ordinal);
        Assert.Contains("status: built", dotnetLog, StringComparison.Ordinal);
        Assert.Contains("status: skipped", await File.ReadAllTextAsync(Path.Combine(_root, "run", "logs", "java.log")));
    }

    [Fact]
    public async Task WriteAsync_PreservesCancellationAndToolchainDetails()
    {
        var path = Path.Combine(_root, "cancelled", "report.json");
        var report = new BuildReport(
            BuildOutcome.Cancelled,
            130,
            [
                new AdapterBuildResult(
                    "java",
                    AdapterBuildStatus.Cancelled,
                    [new AdapterBuildArtifact("java", "workers/java/adapter.jar", new string('a', 64), "1.0", "1.0", "minimal")],
                    "cancelled",
                    ["compiler warning"],
                    "JDK 17+",
                    "OpenJDK 21"),
                new AdapterBuildResult("python", AdapterBuildStatus.NotRun, [])
            ]);

        await new BuildReportWriter().WriteAsync(
            new BuildReportRequest(path, BuildCommand.Build, "Debug", "osx-arm64", report),
            default);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("cancelled", document.RootElement.GetProperty("outcome").GetString());
        var log = await File.ReadAllTextAsync(Path.Combine(_root, "cancelled", "logs", "java.log"));
        Assert.Contains("required-tool: JDK 17+", log, StringComparison.Ordinal);
        Assert.Contains("detected-version: OpenJDK 21", log, StringComparison.Ordinal);
        Assert.Contains("warning: compiler warning", log, StringComparison.Ordinal);
        Assert.Contains("artifact: workers/java/adapter.jar", log, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
