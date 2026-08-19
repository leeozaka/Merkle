using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Engine;
using Merkle.Core.Indexing;
using Merkle.Core.Snapshots;
using Merkle.Core.State;

namespace Merkle.Tests.Engine;

public sealed class ImpactEngineTests
{
    [Fact]
    public async Task Plan_BindsBothSnapshotsBeforeAdapterIndexingAndNeverExecutesTests()
    {
        var events = new List<string>();
        var snapshots = new RecordingSnapshotSource(events);
        var adapter = new RecordingAdapter(events);
        var engine = new ImpactEngine(
            snapshots,
            LanguageDetector.CreateDefault(),
            new AdapterRegistry([adapter]),
            new RecordingStateStore(events),
            TimeProvider.System);

        var result = await engine.PlanAsync(new PlanRequest(
            "main", "HEAD", [new LanguageSelection("dotnet", "minimal")],
            false, null), default);

        Assert.True(events.IndexOf("snapshots-bound") < events.IndexOf("index:base"));
        Assert.Equal(2, events.Count(item => item.StartsWith("index:", StringComparison.Ordinal)));
        Assert.DoesNotContain("execute", events);
        Assert.Equal(TerminalStatus.Succeeded, result.TerminalStatus);
        Assert.Equal(64 * 1024 * 1024, result.Limits?.ReportByteLimit);
    }

    [Fact]
    public async Task Plan_GoSelectionDoesNotReportDotNetOnlyDiscoveryLimitation()
    {
        var events = new List<string>();
        var engine = new ImpactEngine(
            new RecordingSnapshotSource(events, ["go.mod", "calc.go"]),
            LanguageDetector.CreateDefault(),
            new AdapterRegistry([new RecordingAdapter(events, "golang")]),
            new RecordingStateStore(events),
            TimeProvider.System);

        var result = await engine.PlanAsync(new PlanRequest(
            "main", "HEAD", [new LanguageSelection("golang", "minimal")],
            false, null), default);

        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains(".NET test targets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_MixedRepositoryPublishesFailureWithEveryDetection()
    {
        var events = new List<string>();
        var state = new RecordingStateStore(events);
        var engine = new ImpactEngine(
            new RecordingSnapshotSource(events, ["src/App.cs", "web/app.ts"]),
            LanguageDetector.CreateDefault(),
            new AdapterRegistry([new RecordingAdapter(events)]),
            state,
            TimeProvider.System);

        var result = await engine.PlanAsync(new PlanRequest(
            "main", "HEAD", [], false, null), default);

        Assert.Equal(TerminalStatus.Failed, result.TerminalStatus);
        Assert.Equal("MixedLanguagesRequireSelection", result.ErrorCode);
        Assert.Equal(["dotnet", "typescript"], result.Languages.Select(language => language.Language));
        Assert.Same(result, state.Published);
    }

    [Fact]
    public async Task Plan_ReportsMappedChangedUnitWhenAdapterRequestsATest()
    {
        var events = new List<string>();
        var adapter = new MappingAdapter(events);
        var engine = new ImpactEngine(
            new RecordingSnapshotSource(events),
            LanguageDetector.CreateDefault(),
            new AdapterRegistry([adapter]),
            new RecordingStateStore(events),
            TimeProvider.System);

        var result = await engine.PlanAsync(new PlanRequest(
            "main", "HEAD", [new LanguageSelection("dotnet", "minimal")], false, null), default);

        Assert.True(Assert.Single(result.ChangedUnits).Mapped);
        Assert.Null(Assert.Single(result.Tests).ImpactProbability);
        Assert.Null(Assert.Single(result.Tests).EvidenceConfidence);
    }

    [Fact]
    public async Task Plan_UsesBaselineEdgesToMapDeletedSource()
    {
        var events = new List<string>();
        var engine = new ImpactEngine(
            new DeletedSnapshotSource(events),
            LanguageDetector.CreateDefault(),
            new AdapterRegistry([new DeletedMappingAdapter()]),
            new RecordingStateStore(events),
            TimeProvider.System);

        var result = await engine.PlanAsync(new PlanRequest(
            "main", "HEAD", [new LanguageSelection("dotnet", "minimal")], false, null), default);

        var deleted = Assert.Single(result.ChangedUnits);
        Assert.Equal(ChangeKind.Deleted, deleted.ChangeKind);
        Assert.True(deleted.Mapped);
        Assert.Equal("test:a", Assert.Single(result.Tests).Identity);
    }

    private sealed class RecordingSnapshotSource(
        List<string> events,
        IReadOnlyList<string>? paths = null) : ISnapshotSource
    {
        public ValueTask<SnapshotPair> BindAsync(
            string? baselineReference, string? candidateReference, CancellationToken cancellationToken)
        {
            events.Add("snapshots-bound");
            return ValueTask.FromResult(new SnapshotPair(
                Snapshot("base", "old", paths), Snapshot("head", "new", paths)));
        }

        private static RepositorySnapshot Snapshot(
            string name,
            string content,
            IReadOnlyList<string>? paths)
        {
            var files = (paths ?? ["src/App.cs"])
                .Select(path => new SnapshotFile(path, $"{name}:{path}",
                    System.Text.Encoding.UTF8.GetBytes(content)))
                .ToArray();
            return new RepositorySnapshot(
                new SnapshotIdentity(name, name, "git"), "/repo", "repository", files);
        }
    }

    private sealed class RecordingAdapter(List<string> events, string language = "dotnet") : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => new(
            "1.0", language, "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);

        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            events.Add($"index:{request.Snapshot.Identity.Value}");
            var path = request.Snapshot.Files[0].Path;
            var unit = new SourceUnit($"{language}:file:{path}", SourceUnitKind.File,
                path, request.Snapshot.Files[0].ContentHash, string.Empty);
            return ValueTask.FromResult(new AdapterIndex([unit], [], []));
        }

        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
        {
            events.Add("map");
            return ValueTask.FromResult(new MappingResult([], request.ChangedUnits));
        }
    }

    private sealed class MappingAdapter(List<string> events) : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => new(
            "1.0", "dotnet", "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);

        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            events.Add($"index:{request.Snapshot.Identity.Value}");
            var unit = new SourceUnit("dotnet:file:src/App.cs", SourceUnitKind.File,
                "src/App.cs", request.Snapshot.Files[0].ContentHash, string.Empty);
            return ValueTask.FromResult(new AdapterIndex([unit], [], []));
        }

        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
        {
            var changed = Assert.Single(request.ChangedUnits);
            var reason = new ImpactReason(EvidenceKind.StaticDependency, changed.Identity,
                [changed.Identity, "test:a"]);
            return ValueTask.FromResult(new MappingResult(
                [new RequestedTest("test:a", "A", "xunit", [reason])], []));
        }
    }

    private sealed class DeletedSnapshotSource(List<string> events) : ISnapshotSource
    {
        public ValueTask<SnapshotPair> BindAsync(
            string? baselineReference, string? candidateReference, CancellationToken cancellationToken)
        {
            events.Add("snapshots-bound");
            return ValueTask.FromResult(new SnapshotPair(
                new RepositorySnapshot(
                    new SnapshotIdentity("base", "main", "git"), "/repo", "repository",
                    [new SnapshotFile("src/Deleted.cs", "old", [1])]),
                new RepositorySnapshot(
                    new SnapshotIdentity("head", "HEAD", "git"), "/repo", "repository",
                    [new SnapshotFile("tests/StillHere.cs", "same", [2])])));
        }
    }

    private sealed class DeletedMappingAdapter : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => new(
            "1.0", "dotnet", "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);

        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            var test = new TestDescriptor("test:a", "A", "xunit");
            if (request.Snapshot.Identity.Value == "base")
            {
                var unit = new SourceUnit(
                    "dotnet:file:src/Deleted.cs", SourceUnitKind.File, "src/Deleted.cs", "old", string.Empty);
                return ValueTask.FromResult(new AdapterIndex(
                    [unit],
                    [new ImpactEdge(unit.Identity, test.Identity, EvidenceKind.StaticDependency)],
                    [test]));
            }

            return ValueTask.FromResult(new AdapterIndex([], [], [test]));
        }

        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
        {
            var tests = request.Index.Tests.ToDictionary(test => test.Identity, StringComparer.Ordinal);
            return ValueTask.FromResult(new ImpactIndex(request.Index.Edges)
                .FindRequestedTests(request.ChangedUnits, tests));
        }
    }

    private sealed class RecordingStateStore(List<string> events) : IStateStore
    {
        public Merkle.Core.Reporting.TerminalReport? Published { get; private set; }

        public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken)
        {
            events.Add("journal");
            return ValueTask.FromResult(new RunJournal(runId, string.Empty));
        }

        public ValueTask PublishAsync(RunJournal journal, Merkle.Core.Reporting.TerminalReport report,
            CancellationToken cancellationToken)
        {
            events.Add("publish");
            Published = report;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Merkle.Core.Reporting.TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<Merkle.Core.Reporting.TerminalReport?>(null);

        public ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StateStatus("fake", 1, 0, null, false));

        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
