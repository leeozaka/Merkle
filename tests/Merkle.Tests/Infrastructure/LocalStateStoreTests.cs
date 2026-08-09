using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Adapters;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.State;
using Merkle.Infrastructure.State;

namespace Merkle.Tests.Infrastructure;

public sealed class LocalStateStoreTests
{
    [Fact]
    public async Task Publish_KeepsJournalInvisibleUntilTerminalReportIsPublished()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);

        Assert.Null(await store.ReadCurrentAsync(default));

        var report = Report("run-1");
        await store.PublishAsync(journal, report, default);

        Assert.Equal(report.RunId, (await store.ReadCurrentAsync(default))?.RunId);
        Assert.False(Directory.Exists(journal.JournalPath));
    }

    [Fact]
    public async Task Publish_ReplacesCurrentOnlyAfterSecondReportIsComplete()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var first = await store.BeginRunAsync("run-1", default);
        await store.PublishAsync(first, Report("run-1"), default);
        var second = await store.BeginRunAsync("run-2", default);

        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);

        await store.PublishAsync(second, Report("run-2"), default);
        Assert.Equal("run-2", (await store.ReadCurrentAsync(default))?.RunId);
    }

    [Fact]
    public void Constructor_RejectsStateDirectoryOutsideRepository()
    {
        using var directory = new TemporaryDirectory();
        var outside = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory.Path, "..", "outside"));

        var error = Assert.Throws<ConfigurationException>(() =>
            new LocalStateStore(directory.Path, outside, "repo-1"));

        Assert.Equal("UnsafeStatePath", error.Code);
    }

    [Fact]
    public async Task Status_ReturnsProviderVersionSizeAndLastRun()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        await store.PublishAsync(journal, Report("run-1"), default);

        var status = await store.GetStatusAsync(default);

        Assert.Equal("sqlite", status.Provider);
        Assert.Equal(2, status.SchemaVersion);
        Assert.Equal("run-1", status.LastCompatibleRunId);
        Assert.True(status.SizeBytes > 0);
        Assert.True(status.RebuildRequired);
    }

    [Fact]
    public async Task ReadCurrent_UsesTransactionalPointerRatherThanMutableReportArtifact()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        await store.PublishAsync(journal, Report("run-1"), default);
        var reportPath = System.IO.Path.Combine(directory.Path, ".merkle", "reports", "run-1.json");
        File.WriteAllText(reportPath, new JsonReportRenderer().Render(Report("run-1") with
        {
            SchemaVersion = 2
        }));

        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);
    }

    [Fact]
    public async Task Reset_RemovesOnlyMarkedLocalStateDirectory()
    {
        using var directory = new TemporaryDirectory();
        var keep = System.IO.Path.Combine(directory.Path, "keep.txt");
        File.WriteAllText(keep, "keep");
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        _ = await store.BeginRunAsync("run-1", default);

        await store.ResetAsync(default);

        Assert.False(Directory.Exists(System.IO.Path.Combine(directory.Path, ".merkle")));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task ReadCurrent_IgnoresLegacyPointerFiles()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        _ = await store.BeginRunAsync("run-1", default);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, ".merkle", "current"), "missing");

        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task BeginRun_RejectsPathBearingRunIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await store.BeginRunAsync("../escape", default));

        Assert.Equal("InvalidRunIdentity", error.Code);
        Assert.False(Directory.Exists(System.IO.Path.Combine(directory.Path, "escape")));
    }

    [Fact]
    public async Task BeginRun_DoesNotClaimExistingUnmarkedDirectory()
    {
        using var directory = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(directory.Path, ".merkle");
        Directory.CreateDirectory(statePath);
        File.WriteAllText(System.IO.Path.Combine(statePath, "keep.txt"), "user data");
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await store.BeginRunAsync("run-1", default));

        Assert.Equal("InvalidStateDirectory", error.Code);
        Assert.True(File.Exists(System.IO.Path.Combine(statePath, "keep.txt")));
    }

    [Fact]
    public async Task Publish_RejectsReportFromAnotherRun()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await store.PublishAsync(journal, Report("run-2"), default));

        Assert.Equal("RunIdentityMismatch", error.Code);
    }

    [Fact]
    public async Task Publish_RejectsReportAboveConfiguredByteLimit()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1", maxReportBytes: 10);
        var journal = await store.BeginRunAsync("run-1", default);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await store.PublishAsync(journal, Report("run-1"), default));

        Assert.Equal("ReportSizeLimitExceeded", error.Code);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Status_ReportsExistingUnmarkedDirectoryWithoutClaimingIt()
    {
        using var directory = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(directory.Path, ".merkle");
        Directory.CreateDirectory(statePath);
        File.WriteAllText(System.IO.Path.Combine(statePath, "unknown"), "data");
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");

        var status = await store.GetStatusAsync(default);

        Assert.Equal("unrecognized-local-directory", status.Provider);
        Assert.Equal(0, status.SchemaVersion);
        Assert.True(status.RebuildRequired);
    }

    [Fact]
    public async Task BeginRun_RejectsSymlinkedRunsDirectory()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var first = await store.BeginRunAsync("run-1", default);
        await store.PublishAsync(first, Report("run-1"), default);
        var runs = System.IO.Path.Combine(directory.Path, ".merkle", "runs");
        Directory.Delete(runs);
        Directory.CreateSymbolicLink(runs, outside.Path);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
                await store.BeginRunAsync("run-2", default));

            Assert.Equal("UnsafeStatePath", error.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        }
        finally
        {
            if (Directory.Exists(runs))
            {
                Directory.Delete(runs, recursive: false);
            }
        }
    }

    [Fact]
    public async Task ReadCurrent_IgnoresPathBearingLegacyPointerValue()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        _ = await store.BeginRunAsync("run-1", default);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, ".merkle", "current"), "../../outside");

        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Publish_RoundTripsCompatibleIndexAndTerminalHistory()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var key = new IndexCompatibilityKey("repo-1", "head", 1, "sha256:1", "semantic:1", "dotnet", "1.0", "1", "1", "dotnet", "build:1");
        var index = new AdapterIndex(
            [new SourceUnit("unit:1", SourceUnitKind.Member, "src/a.cs", "hash", "signature")],
            [],
            [new TestDescriptor("test:1", "Test", "xunit")]);
        var historyKey = new HistoryCompatibilityKey("repo-1", "1", "dotnet", "build:1");
        var history = new HistoricalRun(historyKey, HistoryProvenance.Local, HistoryRunStatus.Failed, false,
            DateTimeOffset.UnixEpoch, ["unit:1"], [new HistoricalTestExecution("test:1", true, HistoricalTestOutcome.Failed, 12, ["unit:1"])]);

        await store.PublishAsync(journal, new StatePublication(Report("run-1"), [new PersistedAdapterIndex(key, index)], [history]), default);

        Assert.Equal("unit:1", (await ((IIndexStore)store).ReadIndexAsync(key, default))?.Units.Single().Identity);
        var histories = await ((IHistoryStore)store).ReadHistoryAsync(historyKey, default);
        Assert.Equal(HistoryProvenance.Local, Assert.Single(histories).Provenance);
        var status = await store.GetStatusAsync(default);
        Assert.False(status.RebuildRequired);
    }

    [Fact]
    public async Task Publish_RejectsInterruptedHistoryWithoutChangingCurrent()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var first = await store.BeginRunAsync("run-1", default);
        await store.PublishAsync(first, Report("run-1"), default);
        var second = await store.BeginRunAsync("run-2", default);
        var history = new HistoricalRun(new HistoryCompatibilityKey("repo-1", "1", "dotnet", "build"), HistoryProvenance.Local,
            HistoryRunStatus.Interrupted, false, DateTimeOffset.UnixEpoch, [], []);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await store.PublishAsync(second, new StatePublication(Report("run-2"), HistoryRuns: [history]), default));

        Assert.Equal("IncompleteEvidence", error.Code);
        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);
    }

    [Fact]
    public async Task Reset_RefusesUnmarkedDirectory()
    {
        using var directory = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(directory.Path, ".merkle");
        Directory.CreateDirectory(statePath);
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () => await store.ResetAsync(default));

        Assert.Equal("InvalidStateDirectory", error.Code);
        Assert.True(Directory.Exists(statePath));
    }

    [Fact]
    public async Task ReadCurrent_RejectsStateOwnedByAnotherRepository()
    {
        using var directory = new TemporaryDirectory();
        var firstStore = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        _ = await firstStore.BeginRunAsync("run-1", default);
        var secondStore = new LocalStateStore(directory.Path, ".merkle", "repo-2");

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () => await secondStore.ReadCurrentAsync(default));

        Assert.Equal("IncompatibleState", error.Code);
    }

    [Fact]
    public async Task ReadIndex_RejectsInvalidCompatibilityBeforeOpeningState()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var invalid = new IndexCompatibilityKey("", "head", 0, "", "", "", "", "", "", "", null);

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await ((IIndexStore)store).ReadIndexAsync(invalid, default));

        Assert.Equal("InvalidCompatibilityKey", error.Code);
    }

    [Fact]
    public async Task BeginRun_RejectsDuplicateIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        _ = await store.BeginRunAsync("run-1", default);

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () => await store.BeginRunAsync("run-1", default));

        Assert.Equal("DuplicateRunIdentity", error.Code);
    }

    private static TerminalReport Report(string runId) => TerminalReportFactory.Success(
        runId,
        new SnapshotIdentity("base", "main", "git"),
        new SnapshotIdentity("head", "HEAD", "git"),
        "repo-1");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"merkle-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
