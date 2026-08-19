using System.Text;
using Merkle.Adapters.DotNet;
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
        Assert.Contains("limit 10 bytes", error.Message, StringComparison.Ordinal);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Publish_DefaultLimitAcceptsReportAboveLegacySixteenMiBCeiling()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var report = Report("run-1") with
        {
            Warnings = [new string('x', 17 * 1024 * 1024)]
        };

        await store.PublishAsync(journal, report, default);

        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);
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
    public async Task Publish_RoundTripsGeneratedDotNetIndexWithEmptyProjectSemanticSignature()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var key = new IndexCompatibilityKey("repo-1", "head", 1, "sha256:1", "semantic:1", "dotnet", "1.0", "1", "1", "dotnet", "build:1");
        var snapshot = new RepositorySnapshot(
            new SnapshotIdentity("head", "HEAD", "git"),
            directory.Path,
            "repo-1",
            [
                new SnapshotFile(
                    "Merkle.slnx",
                    "solution-content-hash",
                    Encoding.UTF8.GetBytes("<Solution><Project Path=\"src/cli/Merkle.Cli.csproj\" /></Solution>")),
                new SnapshotFile(
                    "src/cli/Merkle.Cli.csproj",
                    "project-content-hash",
                    Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\" />"))
            ]);
        var index = await new DotNetAdapter().IndexAsync(new AdapterIndexRequest(snapshot, null), default);

        Assert.Contains(index.Units, unit =>
            unit.Kind == SourceUnitKind.Project &&
            unit.SemanticSignature == string.Empty);
        Assert.Contains(index.Units, unit =>
            unit.Kind == SourceUnitKind.File &&
            unit.SemanticSignature == string.Empty);

        await store.PublishAsync(
            journal,
            new StatePublication(Report("run-1"), [new PersistedAdapterIndex(key, index)]),
            default);

        var persisted = await ((IIndexStore)store).ReadIndexAsync(key, default);
        Assert.NotNull(persisted);
        var project = Assert.Single(persisted.Units, unit => unit.Kind == SourceUnitKind.Project);
        Assert.Equal(string.Empty, project.SemanticSignature);
    }

    [Theory]
    [InlineData(SourceUnitKind.Repository)]
    [InlineData(SourceUnitKind.Language)]
    [InlineData(SourceUnitKind.Path)]
    [InlineData(SourceUnitKind.Namespace)]
    [InlineData(SourceUnitKind.Type)]
    [InlineData(SourceUnitKind.Member)]
    public async Task Publish_RejectsEmptySemanticSignatureForKindsThatRequireSemanticEvidence(
        SourceUnitKind kind)
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var key = new IndexCompatibilityKey("repo-1", "head", 1, "sha256:1", "semantic:1", "dotnet", "1.0", "1", "1", "dotnet", "build:1");
        var index = new AdapterIndex(
            [new SourceUnit("unit:1", kind, "src/a.cs", "hash", string.Empty)],
            [],
            []);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await store.PublishAsync(
                journal,
                new StatePublication(Report("run-1"), [new PersistedAdapterIndex(key, index)]),
                default));

        Assert.Equal("IncompleteEvidence", error.Code);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    public static TheoryData<string?> InvalidOptionalSemanticSignatures => new()
    {
        null,
        " ",
        new string('s', 513)
    };

    [Theory]
    [MemberData(nameof(InvalidOptionalSemanticSignatures))]
    public async Task Publish_RejectsInvalidOptionalSemanticSignature(string? semanticSignature)
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var key = new IndexCompatibilityKey("repo-1", "head", 1, "sha256:1", "semantic:1", "dotnet", "1.0", "1", "1", "dotnet", "build:1");
        var index = new AdapterIndex(
            [new SourceUnit("unit:1", SourceUnitKind.Project, "src/a.csproj", "hash", semanticSignature!)],
            [],
            []);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await store.PublishAsync(
                journal,
                new StatePublication(Report("run-1"), [new PersistedAdapterIndex(key, index)]),
                default));

        Assert.Equal("IncompleteEvidence", error.Code);
        Assert.Null(await store.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task Publish_RoundTripsFailedTerminalReportWithoutMaskingOriginalError()
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var journal = await store.BeginRunAsync("run-1", default);
        var failure = Report("run-1") with
        {
            TerminalStatus = TerminalStatus.Failed,
            ErrorClass = ErrorClass.AnalysisError,
            ErrorCode = "InvalidProjectFile",
            IdentitySchemas = [],
            Warnings = ["The selected project file is invalid."]
        };

        await store.PublishAsync(journal, failure, default);

        var persisted = await store.ReadCurrentAsync(default);
        Assert.NotNull(persisted);
        Assert.Equal(TerminalStatus.Failed, persisted.TerminalStatus);
        Assert.Equal(ErrorClass.AnalysisError, persisted.ErrorClass);
        Assert.Equal("InvalidProjectFile", persisted.ErrorCode);
        Assert.Contains("The selected project file is invalid.", persisted.Warnings);
    }

    [Theory]
    [InlineData(TerminalStatus.Failed)]
    [InlineData(TerminalStatus.PolicyFailed)]
    public async Task Publish_RejectsEvidenceWithoutNegotiatedIdentitySchemasAndKeepsCurrentState(
        TerminalStatus terminalStatus)
    {
        using var directory = new TemporaryDirectory();
        var store = new LocalStateStore(directory.Path, ".merkle", "repo-1");
        var initialJournal = await store.BeginRunAsync("run-1", default);
        var initialKey = new IndexCompatibilityKey("repo-1", "head", 1, "sha256:1", "semantic:1", "dotnet", "1.0", "1", "1", "dotnet", "build:1");
        var initialIndex = new AdapterIndex(
            [new SourceUnit("unit:initial", SourceUnitKind.Member, "src/initial.cs", "hash", "signature")],
            [],
            []);
        var historyKey = new HistoryCompatibilityKey("repo-1", "1", "dotnet", "build:1");
        var initialHistory = new HistoricalRun(
            historyKey,
            HistoryProvenance.Local,
            HistoryRunStatus.Succeeded,
            false,
            DateTimeOffset.UnixEpoch,
            ["unit:initial"],
            []);
        await store.PublishAsync(
            initialJournal,
            new StatePublication(
                Report("run-1"),
                [new PersistedAdapterIndex(initialKey, initialIndex)],
                [initialHistory]),
            default);
        var attemptedJournal = await store.BeginRunAsync("run-2", default);
        var attemptedKey = initialKey with { SnapshotIdentity = "attempted" };
        var attemptedIndex = new AdapterIndex(
            [new SourceUnit("unit:attempted", SourceUnitKind.Member, "src/attempted.cs", "hash", "signature")],
            [],
            []);
        var attemptedHistory = new HistoricalRun(
            historyKey,
            HistoryProvenance.Local,
            HistoryRunStatus.Failed,
            false,
            DateTimeOffset.UnixEpoch,
            ["unit:attempted"],
            []);
        var failure = Report("run-2") with
        {
            TerminalStatus = terminalStatus,
            ErrorClass = terminalStatus == TerminalStatus.PolicyFailed
                ? ErrorClass.PolicyFailure
                : ErrorClass.AnalysisError,
            ErrorCode = "OriginalFailure",
            IdentitySchemas = []
        };

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await store.PublishAsync(
                attemptedJournal,
                new StatePublication(
                    failure,
                    [new PersistedAdapterIndex(attemptedKey, attemptedIndex)],
                    [attemptedHistory]),
                default));

        Assert.Equal("IncompleteEvidence", error.Code);
        Assert.Equal("run-1", (await store.ReadCurrentAsync(default))?.RunId);
        Assert.NotNull(await ((IIndexStore)store).ReadIndexAsync(initialKey, default));
        Assert.Null(await ((IIndexStore)store).ReadIndexAsync(attemptedKey, default));
        var histories = await ((IHistoryStore)store).ReadHistoryAsync(historyKey, default);
        Assert.Equal("unit:initial", Assert.Single(histories).ChangedUnitIdentities.Single());
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
