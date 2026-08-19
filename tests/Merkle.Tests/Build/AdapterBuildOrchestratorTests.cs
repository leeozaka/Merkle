using System.Security.Cryptography;
using Merkle.Build;
using Merkle.Core.Errors;

namespace Merkle.Tests.Build;

public sealed class AdapterBuildOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"merkle-build-tests-{Guid.NewGuid():N}");

    public AdapterBuildOrchestratorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Run_StrictAllPreflightsEveryAdapterAndDoesNotBuildWhenJavaIsUnavailable()
    {
        var dotnet = FakeAdapter.Ready("dotnet");
        var go = FakeAdapter.Ready("golang");
        var python = FakeAdapter.Ready("python");
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = Orchestrator(dotnet, go, python, java);

        var report = await orchestrator.RunAsync(Request(["all"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(0, dotnet.BuildCalls + go.BuildCalls + python.BuildCalls + java.BuildCalls);
        Assert.All(new[] { dotnet, go, python, java }, adapter => Assert.Equal(1, adapter.PreflightCalls));
        Assert.Equal(AdapterBuildStatus.Skipped, Status(report, "java"));
    }

    [Fact]
    public async Task Run_BestEffortAllSkipsJavaAndBuildsRemainingAdapters()
    {
        var dotnet = FakeAdapter.Ready("dotnet");
        var go = FakeAdapter.Ready("golang");
        var python = FakeAdapter.Ready("python");
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = Orchestrator(dotnet, go, python, java);

        var report = await orchestrator.RunAsync(
            Request(["all"]) with { Policy = AdapterBuildPolicy.BestEffort }, default);

        Assert.Equal(BuildOutcome.PartialSuccess, report.Outcome);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(AdapterBuildStatus.Built, Status(report, "dotnet"));
        Assert.Equal(AdapterBuildStatus.Built, Status(report, "golang"));
        Assert.Equal(AdapterBuildStatus.Built, Status(report, "python"));
        Assert.Equal(AdapterBuildStatus.Skipped, Status(report, "java"));
    }

    [Fact]
    public async Task Run_StrictJavaOnlyWithUnavailableJdkProducesNoBuild()
    {
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = Orchestrator(java);

        var report = await orchestrator.RunAsync(Request(["java"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(0, java.BuildCalls);
        Assert.Equal(AdapterBuildStatus.Skipped, Status(report, "java"));
    }

    [Fact]
    public async Task Run_BestEffortJavaOnlyWithUnavailableJdkFailsWhenNoAdapterSucceeds()
    {
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = Orchestrator(java);

        var report = await orchestrator.RunAsync(
            Request(["java"]) with { Policy = AdapterBuildPolicy.BestEffort }, default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(AdapterBuildStatus.Skipped, Status(report, "java"));
    }

    [Fact]
    public async Task Run_StrictSequentialStopsAtFirstAdapterCompilationFailure()
    {
        var failing = FakeAdapter.Fails("dotnet", "compiler failed");
        var later = FakeAdapter.Ready("golang");
        var last = FakeAdapter.Ready("python");
        var orchestrator = Orchestrator(failing, later, last);

        var report = await orchestrator.RunAsync(Request(["dotnet", "golang", "python"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(1, failing.BuildCalls);
        Assert.Equal(0, later.BuildCalls);
        Assert.Equal(0, last.BuildCalls);
        Assert.Equal(AdapterBuildStatus.Failed, Status(report, "dotnet"));
        Assert.Equal(AdapterBuildStatus.NotRun, Status(report, "golang"));
        Assert.Equal(AdapterBuildStatus.NotRun, Status(report, "python"));
    }

    [Fact]
    public async Task Run_BestEffortContinuesAfterAdapterCompilationFailure()
    {
        var failing = FakeAdapter.Fails("dotnet", "compiler failed");
        var succeeding = FakeAdapter.Ready("golang");
        var last = FakeAdapter.Ready("python");
        var orchestrator = Orchestrator(failing, succeeding, last);

        var report = await orchestrator.RunAsync(
            Request(["dotnet", "golang", "python"]) with { Policy = AdapterBuildPolicy.BestEffort }, default);

        Assert.Equal(BuildOutcome.PartialSuccess, report.Outcome);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(AdapterBuildStatus.Failed, Status(report, "dotnet"));
        Assert.Equal(AdapterBuildStatus.Built, Status(report, "golang"));
        Assert.Equal(AdapterBuildStatus.Built, Status(report, "python"));
    }

    [Fact]
    public async Task Run_StrictParallelBoundsStartsAndCancelsInFlightAdaptersAfterFailure()
    {
        var failing = FakeAdapter.Fails("dotnet", "compiler failed");
        var blocked = FakeAdapter.BlocksUntilCancelled("golang");
        var queued = FakeAdapter.BlocksUntilCancelled("python");
        var orchestrator = Orchestrator(failing, blocked, queued);

        var report = await orchestrator.RunAsync(
            Request(["dotnet", "golang", "python"]) with
            {
                Scheduling = BuildScheduling.Parallel,
                MaxParallel = 2
            }, default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(1, failing.BuildCalls);
        Assert.Equal(1, blocked.BuildCalls);
        Assert.Equal(0, queued.BuildCalls);
        Assert.Equal(AdapterBuildStatus.Failed, Status(report, "dotnet"));
        Assert.Equal(AdapterBuildStatus.Cancelled, Status(report, "golang"));
        Assert.Equal(AdapterBuildStatus.NotRun, Status(report, "python"));
    }

    [Fact]
    public async Task Run_ExternalCancellationOverridesBestEffortPolicy()
    {
        var blocked = FakeAdapter.BlocksUntilCancelled("dotnet");
        var succeeding = FakeAdapter.Ready("python");
        var orchestrator = Orchestrator(blocked, succeeding);
        using var cancellation = new CancellationTokenSource();

        var run = orchestrator.RunAsync(
            Request(["dotnet", "python"]) with { Policy = AdapterBuildPolicy.BestEffort },
            cancellation.Token).AsTask();
        await blocked.BuildStarted;
        await cancellation.CancelAsync();

        var report = await run;

        Assert.Equal(BuildOutcome.Cancelled, report.Outcome);
        Assert.Equal(130, report.ExitCode);
        Assert.Equal(AdapterBuildStatus.Cancelled, Status(report, "dotnet"));
        Assert.Equal(AdapterBuildStatus.Cancelled, Status(report, "python"));
        Assert.False(Directory.Exists(Path.Combine(_root, "output")));
    }

    [Fact]
    public async Task Run_UnavailableDotNetSdkIsGlobalFailureAndPreservesToolDetails()
    {
        var dotnet = FakeAdapter.Unavailable(
            "dotnet",
            "The .NET SDK was not found.",
            requiredTool: "dotnet SDK 10.0+",
            detectedVersion: "not found");
        var go = FakeAdapter.Ready("golang", detectedVersion: "go1.24.1");
        var python = FakeAdapter.Ready("python", detectedVersion: "Python 3.12.4");
        var java = FakeAdapter.Unavailable(
            "java",
            "The JDK was not found.",
            requiredTool: "JDK 17+",
            detectedVersion: "not found");
        var orchestrator = Orchestrator(dotnet, go, python, java);

        var report = await orchestrator.RunAsync(Request(["all"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(4, report.ExitCode);
        Assert.All(new[] { dotnet, go, python, java }, adapter => Assert.Equal(1, adapter.PreflightCalls));
        Assert.All(new[] { dotnet, go, python, java }, adapter => Assert.Equal(0, adapter.BuildCalls));
        var result = Assert.Single(report.Adapters, adapter => adapter.AdapterId == "dotnet");
        Assert.Equal("dotnet SDK 10.0+", result.RequiredTool);
        Assert.Equal("not found", result.DetectedVersion);
        Assert.Equal(
            "go1.24.1",
            Assert.Single(report.Adapters, adapter => adapter.AdapterId == "golang").DetectedVersion);
        Assert.Equal(
            "Python 3.12.4",
            Assert.Single(report.Adapters, adapter => adapter.AdapterId == "python").DetectedVersion);
        Assert.Equal(
            "JDK 17+",
            Assert.Single(report.Adapters, adapter => adapter.AdapterId == "java").RequiredTool);
        Assert.Contains("Required tool: dotnet SDK 10.0+", report.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportFailureDoesNotReplaceExistingOutput()
    {
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "previous.txt");
        await File.WriteAllTextAsync(sentinel, "previous");
        var dotnet = FakeAdapter.Ready("dotnet");
        var orchestrator = new BuildOrchestrator(
            new FakeCatalog([dotnet]),
            new SuccessfulHostPublisher(),
            new FailingReportWriter(),
            new BuildRunWorkspaceFactory());

        var report = await orchestrator.RunAsync(Request(["dotnet"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(4, report.ExitCode);
        Assert.Null(report.ManifestPath);
        Assert.Equal("previous", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(output, "adapters.json")));
    }

    [Fact]
    public async Task Run_WorkspaceAcquisitionFailureWritesFallbackReport()
    {
        var dotnet = FakeAdapter.Ready("dotnet");
        var orchestrator = new BuildOrchestrator(
            new FakeCatalog([dotnet]),
            new SuccessfulHostPublisher(),
            new BuildReportWriter(),
            new FailingWorkspaceFactory());

        var report = await orchestrator.RunAsync(Request(["dotnet"]), default);

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.Equal(4, report.ExitCode);
        Assert.Equal(0, dotnet.PreflightCalls + dotnet.BuildCalls);
        Assert.NotNull(report.ReportPath);
        Assert.True(File.Exists(report.ReportPath));
        Directory.Delete(report.RunDirectory!, recursive: true);
    }

    private BuildOrchestrator Orchestrator(params FakeAdapter[] adapters) =>
        new(
            new FakeCatalog(adapters),
            new SuccessfulHostPublisher(),
            new BuildReportWriter(),
            new BuildRunWorkspaceFactory());

    private BuildRequest Request(IReadOnlyList<string> adapters) => new(
        BuildCommand.Build,
        adapters,
        OutputPath: Path.Combine(_root, "output"),
        ReportPath: Path.Combine(_root, "report.json"));

    private static AdapterBuildStatus Status(BuildReport report, string adapterId) =>
        Assert.Single(report.Adapters, adapter => adapter.AdapterId == adapterId).Status;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeCatalog(IReadOnlyList<FakeAdapter> adapters) : IBuildAdapterCatalog
    {
        public IReadOnlyList<IBuildAdapter> Adapters => adapters;

        public IReadOnlyList<IBuildAdapter> ResolveSelection(IReadOnlyList<string> names)
        {
            if (names.Contains("all", StringComparer.OrdinalIgnoreCase)) return Adapters;
            return names.Select(name => Adapters.FirstOrDefault(adapter =>
                    StringComparer.OrdinalIgnoreCase.Equals(adapter.Definition.Id, name) ||
                    adapter.Definition.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
                ?? throw new ConfigurationException("UnknownAdapter", $"Unknown adapter '{name}'."))
                .Distinct()
                .ToArray();
        }
    }

    private sealed class SuccessfulHostPublisher : IHostPublisher
    {
        public ValueTask<HostPublishResult> PublishAsync(
            HostPublishRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new HostPublishResult(true));
    }

    private sealed class FailingWorkspaceFactory : IBuildRunWorkspaceFactory
    {
        public ValueTask<IBuildRunWorkspace> AcquireAsync(
            BuildRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IBuildRunWorkspace>(new IOException("workspace unavailable"));
    }

    private sealed class FailingReportWriter : IBuildReportWriter
    {
        public ValueTask<string> WriteAsync(
            BuildReportRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<string>(new IOException("report unavailable"));
    }

    private sealed class FakeAdapter : IBuildAdapter
    {
        private readonly AdapterReadinessStatus _readiness;
        private readonly AdapterBuildStatus _buildStatus;
        private readonly string? _diagnostic;
        private readonly bool _blockUntilCancelled;
        private readonly string? _requiredTool;
        private readonly string? _detectedVersion;
        private readonly TaskCompletionSource _buildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeAdapter(
            string id,
            AdapterReadinessStatus readiness,
            AdapterBuildStatus buildStatus,
            string? diagnostic = null,
            bool blockUntilCancelled = false,
            string? requiredTool = null,
            string? detectedVersion = null)
        {
            Definition = new AdapterBuildDefinition(id, id == "golang" ? ["go"] : [], "1.0", ["linux-x64", "osx-arm64"]);
            _readiness = readiness;
            _buildStatus = buildStatus;
            _diagnostic = diagnostic;
            _blockUntilCancelled = blockUntilCancelled;
            _requiredTool = requiredTool;
            _detectedVersion = detectedVersion;
        }

        public AdapterBuildDefinition Definition { get; }
        public int PreflightCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public Task BuildStarted => _buildStarted.Task;

        public static FakeAdapter Ready(
            string id,
            string? requiredTool = null,
            string? detectedVersion = null) =>
            new(
                id,
                AdapterReadinessStatus.Ready,
                AdapterBuildStatus.Built,
                requiredTool: requiredTool,
                detectedVersion: detectedVersion);

        public static FakeAdapter Unavailable(
            string id,
            string reason,
            string? requiredTool = null,
            string? detectedVersion = null) =>
            new(
                id,
                AdapterReadinessStatus.Unavailable,
                AdapterBuildStatus.Skipped,
                reason,
                requiredTool: requiredTool,
                detectedVersion: detectedVersion);

        public static FakeAdapter Fails(string id, string reason) =>
            new(id, AdapterReadinessStatus.Ready, AdapterBuildStatus.Failed, reason);

        public static FakeAdapter BlocksUntilCancelled(string id) =>
            new(id, AdapterReadinessStatus.Ready, AdapterBuildStatus.Cancelled, blockUntilCancelled: true);

        public ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            return ValueTask.FromResult(new AdapterReadiness(
                Definition.Id,
                _readiness,
                _diagnostic,
                _requiredTool,
                _detectedVersion));
        }

        public async ValueTask<AdapterBuildResult> BuildAsync(
            AdapterBuildRequest request,
            CancellationToken cancellationToken)
        {
            BuildCalls++;
            _buildStarted.TrySetResult();
            if (_blockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new AdapterBuildResult(Definition.Id, AdapterBuildStatus.Cancelled, [], "cancelled");
                }
            }

            if (_buildStatus == AdapterBuildStatus.Built)
            {
                var artifactPath = Path.Combine(request.Context.StagingDirectory, "workers", Definition.Id, "adapter.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
                await File.WriteAllTextAsync(artifactPath, Definition.Id, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath, cancellationToken))).ToLowerInvariant();
                return new AdapterBuildResult(
                    Definition.Id,
                    AdapterBuildStatus.Built,
                    [new AdapterBuildArtifact(Definition.Id, $"workers/{Definition.Id}/adapter.bin", hash, Definition.Version, "1.0", "worker")]);
            }

            return new AdapterBuildResult(Definition.Id, _buildStatus, [], _diagnostic);
        }
    }
}
