using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.State;

namespace Merkle.Tests.History;

public sealed class HistoryImportServiceTests
{
    [Fact]
    public async Task Import_PublishesImportedHistoryAndIsIdempotent()
    {
        var store = new MemoryStateStore();
        var service = new HistoryImportService(store, "repo", FixedTime.Instance);
        var source = Report();

        var imported = await service.ImportAsync(source, default);
        var duplicate = await service.ImportAsync(source, default);

        Assert.NotEqual(source.RunId, imported.RunId);
        Assert.Same(source, duplicate);
        Assert.Single(store.History);
        Assert.Equal(HistoryProvenance.Imported, store.History[0].Provenance);
        Assert.Equal(HistoricalTestOutcome.Passed, Assert.Single(store.History[0].Tests).Outcome);
    }

    [Theory]
    [InlineData(2, "repo", "IncompatibleImportSchema")]
    [InlineData(1, "other", "ImportRepositoryMismatch")]
    public async Task Import_RejectsIncompatibleReport(int schema, string repository, string code)
    {
        var report = Report() with { SchemaVersion = schema, RepositoryIdentity = repository };
        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await new HistoryImportService(new MemoryStateStore(), "repo", FixedTime.Instance).ImportAsync(report, default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Import_RejectsMissingExecutionsAmbiguousAdapterAndInvalidOutcome()
    {
        var service = new HistoryImportService(new MemoryStateStore(), "repo", FixedTime.Instance);
        var noExecutions = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await service.ImportAsync(Report() with { Executions = [] }, default));
        var ambiguous = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await service.ImportAsync(Report() with { Adapters = [Adapter(), Adapter() with { Language = "go" }] }, default));
        var invalidOutcome = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await service.ImportAsync(Report() with { Executions = [Execution("Mystery")] }, default));

        Assert.Equal("ImportHasNoExecutions", noExecutions.Code);
        Assert.Equal("ImportAdapterAmbiguous", ambiguous.Code);
        Assert.Equal("InvalidImportedOutcome", invalidOutcome.Code);
    }

    [Fact]
    public async Task Import_RequiresHistoryCapablePublicationProvider()
    {
        var service = new HistoryImportService(new BasicStateStore(), "repo", FixedTime.Instance);
        var error = await Assert.ThrowsAsync<CapabilityException>(async () => await service.ImportAsync(Report(), default));
        Assert.Equal("HistoryImportUnavailable", error.Code);
    }

    private static TerminalReport Report() => TerminalReportFactory.Success("source", new("base", "main", "git"), new("head", "HEAD", "git"), "repo") with
    {
        Adapters = [Adapter()],
        Executions = [Execution("Passed")],
        BuildContext = new ReportBuildContext("App.sln", "Debug", "AnyCPU", "observe"),
        EvidenceCutoff = FixedTime.Now
    };

    private static ReportAdapter Adapter() => new("dotnet", "merkle", "1", "1", "u1", "t1", [], [], []);
    private static ReportTestExecution Execution(string outcome) => new("test:a", outcome, true, 12, ["unit:a"], true, null);

    private class BasicStateStore : IStateStore
    {
        public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken) => ValueTask.FromResult(new RunJournal(runId, ""));
        public ValueTask PublishAsync(RunJournal journal, TerminalReport report, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<TerminalReport?>(null);
        public ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new StateStatus("memory", 1, 0, null, false));
        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class MemoryStateStore : BasicStateStore, IStatePublicationStore, IHistoryStore
    {
        public List<HistoricalRun> History { get; } = [];
        public ValueTask PublishAsync(RunJournal journal, StatePublication publication, CancellationToken cancellationToken)
        {
            History.AddRange(publication.PersistedHistoryRuns);
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<HistoricalRun>> ReadHistoryAsync(HistoryCompatibilityKey compatibility, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<HistoricalRun>>([.. History.Where(run => run.Compatibility.Matches(compatibility))]);
    }

    private sealed class FixedTime : TimeProvider
    {
        public static readonly FixedTime Instance = new();
        public static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
