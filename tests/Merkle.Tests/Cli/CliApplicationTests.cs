using Merkle.Cli;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Engine;
using Merkle.Core.Reporting;
using Merkle.Core.Snapshots;
using Merkle.Core.State;
using Merkle.Core.History;

namespace Merkle.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Run_HelpWritesUsageAndSucceeds()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync(["--help"], default);

        Assert.Equal(0, exitCode);
        Assert.Contains("merkle plan", fixture.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.StandardError.ToString());
    }

    [Fact]
    public async Task Run_ParserErrorWritesClassAndCodeToStandardError()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync(["plan", "--unknown"], default);

        Assert.Equal(2, exitCode);
        Assert.Contains("ConfigurationError:UnknownOption", fixture.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_StateStatusPrintsRequiredFields()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync(["state", "status"], default);

        Assert.Equal(0, exitCode);
        Assert.Contains("Provider: fake", fixture.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Rebuild required: True", fixture.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_StateResetInvokesOnlyLocalStore()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync(["state", "reset", "--local"], default);

        Assert.Equal(0, exitCode);
        Assert.True(fixture.State.ResetCalled);
    }

    [Fact]
    public async Task Run_PlanRendersVersionedJsonWithoutExecutingTests()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync([
            "plan", "--base", "main", "--head", "HEAD", "--format", "json"], default);

        Assert.Equal(0, exitCode);
        using var document = System.Text.Json.JsonDocument.Parse(fixture.StandardOutput.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Succeeded", document.RootElement.GetProperty("terminalStatus").GetString());
    }

    [Fact]
    public async Task Run_ObserveInvokesDeepEngineAndHonorsNoBuild()
    {
        var deep = new FakeDeepEngine();
        var fixture = new ApplicationFixture(deepExecutionEngine: deep);

        var exitCode = await fixture.Application.RunAsync(["observe", "--no-build", "--timeout-ms", "12"], default);

        Assert.Equal(0, exitCode);
        Assert.NotNull(deep.Request);
        Assert.Equal(DeepExecutionMode.Observe, deep.Request!.Mode);
        Assert.True(deep.Request.NoBuild);
        Assert.Equal(TimeSpan.FromMilliseconds(12), deep.Request.Timeout);
    }

    [Fact]
    public async Task Run_DeepCommandWithoutToolchainReturnsCapabilityExitCode()
    {
        var fixture = new ApplicationFixture();

        var exitCode = await fixture.Application.RunAsync(["run"], default);

        Assert.Equal(3, exitCode);
        Assert.Contains("CapabilityError:DeepToolchainUnavailable", fixture.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_HistoryImportPassesParsedReportToImporter()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(path, new JsonReportRenderer().Render(TerminalReportFactory.Success(
            "source", new SnapshotIdentity("base", "base", "git"), new SnapshotIdentity("head", "head", "git"), "repository")));
        var importer = new FakeHistoryImporter();
        var fixture = new ApplicationFixture(historyImporter: importer);

        var exitCode = await fixture.Application.RunAsync(["history", "import", path], default);

        Assert.Equal(0, exitCode);
        Assert.Equal("source", importer.Source?.RunId);
    }

    [Fact]
    public async Task Run_UnknownThrownExceptionIsRedactedAndUsesAnalysisExitCode()
    {
        var fixture = new ApplicationFixture(deepExecutionEngine: new ThrowingDeepEngine());

        var exitCode = await fixture.Application.RunAsync(["run"], default);

        Assert.Equal(4, exitCode);
        Assert.Contains("AnalysisError:UnexpectedFailure", fixture.StandardError.ToString(), StringComparison.Ordinal);
    }

    private sealed class ApplicationFixture
    {
        public ApplicationFixture(IDeepExecutionEngine? deepExecutionEngine = null, IHistoryImportService? historyImporter = null)
        {
            State = new FakeStateStore();
            StandardOutput = new StringWriter();
            StandardError = new StringWriter();
            var engine = new ImpactEngine(
                new FakeSnapshotSource(),
                LanguageDetector.CreateDefault(),
                new AdapterRegistry([new FakeAdapter()]),
                State,
                TimeProvider.System);
            Application = new CliApplication(engine, State, StandardOutput, StandardError,
                deepExecutionEngine: deepExecutionEngine, historyImportService: historyImporter);
        }

        public FakeStateStore State { get; }

        public StringWriter StandardOutput { get; }

        public StringWriter StandardError { get; }

        public CliApplication Application { get; }
    }

    private sealed class FakeDeepEngine : IDeepExecutionEngine
    {
        public DeepExecutionRequest? Request { get; private set; }

        public ValueTask<TerminalReport> ExecuteAsync(DeepExecutionRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(TerminalReportFactory.Success(
                "deep", new SnapshotIdentity("base", "base", "git"), new SnapshotIdentity("head", "head", "git"), "repository"));
        }
    }

    private sealed class ThrowingDeepEngine : IDeepExecutionEngine
    {
        public ValueTask<TerminalReport> ExecuteAsync(DeepExecutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("untrusted value");
    }

    private sealed class FakeHistoryImporter : IHistoryImportService
    {
        public TerminalReport? Source { get; private set; }

        public ValueTask<TerminalReport> ImportAsync(TerminalReport source, CancellationToken cancellationToken)
        {
            Source = source;
            return ValueTask.FromResult(source);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"merkle-cli-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FakeSnapshotSource : ISnapshotSource
    {
        public ValueTask<SnapshotPair> BindAsync(
            string? baselineReference, string? candidateReference, CancellationToken cancellationToken)
        {
            var baseline = Snapshot("base", "old");
            var candidate = Snapshot("head", "new");
            return ValueTask.FromResult(new SnapshotPair(baseline, candidate));
        }

        private static RepositorySnapshot Snapshot(string identity, string content) => new(
            new SnapshotIdentity(identity, identity, "git"),
            "/repo",
            "repository",
            [new SnapshotFile("src/App.cs", identity, System.Text.Encoding.UTF8.GetBytes(content))]);
    }

    private sealed class FakeAdapter : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => new(
            "1.0", "dotnet", "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);

        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            var file = request.Snapshot.Files[0];
            return ValueTask.FromResult(new AdapterIndex(
                [new SourceUnit("dotnet:file:src/App.cs", SourceUnitKind.File, file.Path, file.ContentHash, string.Empty)],
                [],
                []));
        }

        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MappingResult([], request.ChangedUnits));
    }

    public sealed class FakeStateStore : IStateStore
    {
        public bool ResetCalled { get; private set; }

        public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RunJournal(runId, string.Empty));

        public ValueTask PublishAsync(
            RunJournal journal, TerminalReport report, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<TerminalReport?>(null);

        public ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StateStatus("fake", 1, 12, "run-1", true));

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            ResetCalled = true;
            return ValueTask.CompletedTask;
        }
    }
}
