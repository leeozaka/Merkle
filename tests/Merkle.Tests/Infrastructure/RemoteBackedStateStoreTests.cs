using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.State;
using Merkle.Infrastructure.State;

namespace Merkle.Tests.Infrastructure;

public sealed class RemoteBackedStateStoreTests
{
    [Fact]
    public async Task ReadHistory_PaginatesUntilTheRemoteCompletes()
    {
        using var directory = new TemporaryDirectory();
        var remote = new RecordingRemote(
            Page("v1", new RemoteHistoryCursor("page-2"), Run("first")),
            Page("v2", null, Run("second")));
        var store = Store(directory, remote);

        var result = await ((IHistoryStore)store).ReadHistoryAsync(Key(), default);

        Assert.Equal(["first", "second"], result.Select(run => run.ChangedUnitIdentities.Single()));
        Assert.Equal(2, remote.Reads.Count);
        Assert.Null(remote.Reads[0].Cursor);
        Assert.Equal("page-2", remote.Reads[1].Cursor!.Value);
        Assert.All(remote.Reads, read => Assert.Equal(1_000, read.MaximumRuns));
    }

    [Fact]
    public async Task ReadHistory_RemoteIsAuthoritativeInsteadOfLocalHistory()
    {
        using var directory = new TemporaryDirectory();
        var local = new LocalStateStore(directory.Path, ".merkle", "repo");
        var initial = await local.BeginRunAsync("local-run", default);
        await local.PublishAsync(initial, new StatePublication(Report("local-run"), HistoryRuns: [Run("local")]), default);
        var remote = new RecordingRemote(Page("v1", null, Run("remote")));
        var store = new RemoteBackedStateStore(local, remote);

        var result = await ((IHistoryStore)store).ReadHistoryAsync(Key(), default);

        Assert.Equal("remote", Assert.Single(result).ChangedUnitIdentities.Single());
    }

    [Fact]
    public async Task ReadHistory_RejectsMoreThanTenPages()
    {
        using var directory = new TemporaryDirectory();
        var pages = Enumerable.Range(0, 10)
            .Select(index => Page($"v{index}", new RemoteHistoryCursor($"next-{index}"), Run($"run-{index}")))
            .ToArray();
        var remote = new RecordingRemote(pages);
        var store = Store(directory, remote);

        var error = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await ((IHistoryStore)store).ReadHistoryAsync(Key(), default));

        Assert.Equal("RemoteHistoryLimitExceeded", error.Code);
        Assert.Equal(10, remote.Reads.Count);
    }

    [Fact]
    public async Task Publish_RemoteSucceedsBeforeLocalReportBecomesVisible()
    {
        using var directory = new TemporaryDirectory();
        var local = new LocalStateStore(directory.Path, ".merkle", "repo");
        var remote = new RecordingRemote(Page("v1", null));
        var store = new RemoteBackedStateStore(local, remote);
        var journal = await store.BeginRunAsync("run-1", default);
        remote.OnPublish = _ =>
        {
            Assert.Null(local.ReadCurrentAsync(default).AsTask().GetAwaiter().GetResult());
            return "v2";
        };

        await ((IStatePublicationStore)store).PublishAsync(
            journal,
            new StatePublication(Report("run-1"), HistoryRuns: [Run("history")]),
            default);

        Assert.Equal("run-1", (await local.ReadCurrentAsync(default))?.RunId);
        Assert.Single(remote.Publications);
    }

    [Fact]
    public async Task Publish_RetriesCasAndKeepsTheSameDeterministicIdempotencyKey()
    {
        using var directory = new TemporaryDirectory();
        var remote = new RecordingRemote(Page("v1", null), Page("v2", null))
        {
            PublishFailuresRemaining = 1
        };
        var store = Store(directory, remote);
        var journal = await store.BeginRunAsync("run-1", default);

        await ((IStatePublicationStore)store).PublishAsync(journal, new StatePublication(Report("run-1"), HistoryRuns: [Run("history")]), default);

        Assert.Equal(2, remote.Publications.Count);
        Assert.Equal("v1", remote.Publications[0].ExpectedVersion);
        Assert.Equal("v2", remote.Publications[1].ExpectedVersion);
        Assert.Equal(remote.Publications[0].IdempotencyKey, remote.Publications[1].IdempotencyKey);
        Assert.Matches("^[0-9a-f]{64}$", remote.Publications[0].IdempotencyKey);
    }

    [Fact]
    public async Task Publish_FailsAfterThreeCasConflictsWithoutPublishingLocalState()
    {
        using var directory = new TemporaryDirectory();
        var remote = new RecordingRemote(Page("v1", null), Page("v2", null), Page("v3", null))
        {
            PublishFailuresRemaining = 3
        };
        var store = Store(directory, remote);
        var journal = await store.BeginRunAsync("run-1", default);

        var error = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await ((IStatePublicationStore)store).PublishAsync(journal, new StatePublication(Report("run-1"), HistoryRuns: [Run("history")]), default));

        Assert.Equal("RemoteConcurrencyConflict", error.Code);
        Assert.Equal(3, remote.Publications.Count);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Publish_ReportOnlyDoesNotContactRemoteAndDelegatesStateOperations()
    {
        using var directory = new TemporaryDirectory();
        var remote = new RecordingRemote();
        var store = Store(directory, remote);
        var journal = await store.BeginRunAsync("run-1", default);

        await ((IStatePublicationStore)store).PublishAsync(journal, new StatePublication(Report("run-1")), default);

        Assert.Empty(remote.Reads);
        Assert.Empty(remote.Publications);
        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);
        Assert.Equal("remote-history+sqlite", (await store.GetStatusAsync(default)).Provider);
        await store.ResetAsync(default);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Publish_RejectsSchemaLessFailureEvidenceBeforeContactingRemote()
    {
        using var directory = new TemporaryDirectory();
        var local = new LocalStateStore(directory.Path, ".merkle", "repo");
        var initialJournal = await local.BeginRunAsync("run-1", default);
        await local.PublishAsync(initialJournal, Report("run-1"), default);
        var remote = new RecordingRemote(Page("v1", null));
        var store = new RemoteBackedStateStore(local, remote);
        var attemptedJournal = await store.BeginRunAsync("run-2", default);
        var failure = Report("run-2") with
        {
            TerminalStatus = TerminalStatus.Failed,
            ErrorClass = ErrorClass.AnalysisError,
            ErrorCode = "OriginalFailure",
            IdentitySchemas = []
        };

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await ((IStatePublicationStore)store).PublishAsync(
                attemptedJournal,
                new StatePublication(failure, HistoryRuns: [Run("attempted")]),
                default));

        Assert.Equal("IncompleteEvidence", error.Code);
        Assert.Empty(remote.Reads);
        Assert.Empty(remote.Publications);
        Assert.Equal("run-1", (await local.ReadCurrentAsync(default))?.RunId);
        Assert.Empty(await ((IHistoryStore)local).ReadHistoryAsync(Key(), default));
    }

    [Fact]
    public async Task EnvironmentTokenSource_ReadsConfiguredValueAndHonorsCancellation()
    {
        var name = "MERKLE_REMOTE_TOKEN_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "token-value");
        try
        {
            var source = new EnvironmentRemoteStateTokenSource(name);
            Assert.Equal("token-value", await source.GetTokenAsync(default));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await source.GetTokenAsync(cancellation.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static RemoteBackedStateStore Store(TemporaryDirectory directory, RecordingRemote remote) =>
        new(new LocalStateStore(directory.Path, ".merkle", "repo"), remote);

    private static HistoryCompatibilityKey Key() => new("repo", "1", "dotnet", "build");

    private static HistoricalRun Run(string changedUnit) => new(
        Key(), HistoryProvenance.Local, HistoryRunStatus.Succeeded, true,
        DateTimeOffset.UnixEpoch, [changedUnit], []);

    private static RemoteHistoryPage Page(string version, RemoteHistoryCursor? cursor, params HistoricalRun[] runs) =>
        new([.. runs.Select((run, index) => new RemoteHistoricalRun($"{version}-{index}", run))], cursor, version);

    private static TerminalReport Report(string runId) => TerminalReportFactory.Success(
        runId,
        new SnapshotIdentity("base", "main", "git"),
        new SnapshotIdentity("head", "HEAD", "git"),
        "repo");

    private sealed class RecordingRemote(params RemoteHistoryPage[] pages) : IRemoteStateStore
    {
        private readonly Queue<RemoteHistoryPage> _pages = new(pages);

        public List<RemoteHistoryRead> Reads { get; } = [];
        public List<RemoteHistoryPublication> Publications { get; } = [];
        public int PublishFailuresRemaining { get; set; }
        public Func<RemoteHistoryPublication, string>? OnPublish { get; set; }

        public ValueTask<RemoteHistoryPage> ReadCompatibleTerminalHistoryAsync(RemoteHistoryRead read, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(read);
            return ValueTask.FromResult(_pages.Count > 0 ? _pages.Dequeue() : Page("v-empty", null));
        }

        public ValueTask<string> PublishTerminalHistoryAsync(RemoteHistoryPublication publication, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publications.Add(publication);
            if (PublishFailuresRemaining-- > 0)
            {
                throw new RemoteStateException(RemoteStateFailureKind.Concurrency, "CasConflict", "conflict");
            }

            return ValueTask.FromResult(OnPublish?.Invoke(publication) ?? "v-next");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"merkle-remote-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
