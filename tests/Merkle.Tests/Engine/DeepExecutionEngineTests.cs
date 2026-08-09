using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Engine;
using Merkle.Core.Errors;
using Merkle.Core.History;
using Merkle.Core.Indexing;
using Merkle.Core.Reporting;
using Merkle.Core.Snapshots;
using Merkle.Core.State;

namespace Merkle.Tests.Engine;

public sealed class DeepExecutionEngineTests
{
    [Fact]
    public async Task Observe_PublishesCompleteHistoryAndObservedUnits()
    {
        var state = new State();
        var adapter = new DeepAdapter();
        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        Assert.Equal(TerminalStatus.Succeeded, report.TerminalStatus);
        Assert.True(Assert.Single(report.Executions!).ObservationComplete);
        Assert.Equal(["unit:a"], Assert.Single(report.Executions!).ObservedUnitIdentities);
        Assert.Single(state.History);
        Assert.True(state.History[0].IsCompleteSuite);
    }

    [Fact]
    public async Task RunSelected_LeavesAdvisoryPlanUntouched()
    {
        var state = new State();
        var adapter = new DeepAdapter();
        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.RunSelected), default);
        Assert.Equal(PlanRecommendation.PlanOnly, report.Policy.Recommendation);
        Assert.Equal(0, adapter.PrepareCalls);
        Assert.Empty(state.History);
    }

    [Fact]
    public async Task Observe_PublishesCapabilityFailure()
    {
        var state = new State();
        var adapter = new DeepAdapter { PrepareError = new CapabilityException("BuildUnavailable", "no build") };
        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), default);
        Assert.Equal(TerminalStatus.Failed, report.TerminalStatus);
        Assert.Equal("BuildUnavailable", report.ErrorCode);
        Assert.NotNull(state.Report);
    }

    [Fact]
    public async Task Observe_MissingDeepCapabilityIsPublishedRatherThanRejectedByConstructor()
    {
        var state = new State();
        var adapter = new PlanningOnlyAdapter();
        var engine = new DeepExecutionEngine(
            new ImpactEngine(new Snapshots(), LanguageDetector.CreateDefault(), new AdapterRegistry([adapter]), state, FixedTime.Instance, "repo"),
            new Snapshots(), adapter, state, FixedTime.Instance);

        var report = await engine.ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        Assert.Equal(TerminalStatus.Failed, report.TerminalStatus);
        Assert.Equal("DeepToolchainUnavailable", report.ErrorCode);
        Assert.NotNull(state.Report);
    }

    [Fact]
    public async Task RunSelected_MissingResolverFailsBeforeBuildPreparation()
    {
        var state = new State { SeedHistory = true };
        var adapter = new ExecutingAdapterWithoutResolver();
        var engine = new DeepExecutionEngine(
            new ImpactEngine(new Snapshots(), LanguageDetector.CreateDefault(), new AdapterRegistry([adapter]), state, FixedTime.Instance, "repo"),
            new Snapshots(), adapter, state, FixedTime.Instance);

        var report = await engine.ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal("DeepToolchainUnavailable", report.ErrorCode);
        Assert.Equal(0, adapter.PrepareCalls);
    }

    [Fact]
    public async Task Observe_PropagatesCancellationWithoutPublication()
    {
        var state = new State();
        var adapter = new DeepAdapter { CancelObserve = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), new CancellationToken(true)));
        Assert.Null(state.Report);
    }

    [Theory]
    [InlineData(TestOutcome.Passed, TerminalStatus.Succeeded, HistoryRunStatus.Succeeded)]
    [InlineData(TestOutcome.Skipped, TerminalStatus.Succeeded, HistoryRunStatus.Succeeded)]
    [InlineData(TestOutcome.Failed, TerminalStatus.Failed, HistoryRunStatus.Failed)]
    [InlineData(TestOutcome.TimedOut, TerminalStatus.Failed, HistoryRunStatus.Failed)]
    [InlineData(TestOutcome.Crashed, TerminalStatus.Failed, HistoryRunStatus.Failed)]
    [InlineData(TestOutcome.Cancelled, TerminalStatus.Failed, HistoryRunStatus.Failed)]
    public async Task RunSelected_MapsEveryOutcomeAndPublishesHistory(
        TestOutcome outcome,
        TerminalStatus terminalStatus,
        HistoryRunStatus historyStatus)
    {
        var state = new State { SeedHistory = true };
        var adapter = new DeepAdapter { MapRequested = false, Outcome = outcome };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal(PlanRecommendation.Selected, report.Policy.Recommendation);
        Assert.Equal(terminalStatus, report.TerminalStatus);
        Assert.Equal(historyStatus, Assert.Single(state.History).Status);
        Assert.Equal(outcome.ToString(), Assert.Single(report.Executions!).Outcome);
        Assert.False(state.History[0].IsCompleteSuite);
    }

    [Fact]
    public async Task RunSelected_ExpandsProjectFallbackAndMarksFullSuite()
    {
        const string projectIdentity = "dotnet-project:App.csproj";
        var state = new State { SeedHistory = true, SeedTestIdentity = projectIdentity };
        var adapter = new DeepAdapter
        {
            MapRequested = false,
            PlanIdentity = projectIdentity,
            RuntimeCatalog = [new TestCatalogEntry("test:runtime", "Runtime test", "xunit", "App.csproj", "Runtime")]
        };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(30, 1, "full-suite", UnmappedBehavior.Warn)),
            default);

        Assert.Equal(PlanRecommendation.FullSuite, report.Policy.Recommendation);
        Assert.Equal("test:runtime", Assert.Single(adapter.LastSelected).Identity);
        Assert.True(Assert.Single(state.History).IsCompleteSuite);
    }

    [Fact]
    public async Task RunSelected_MapsStaticIdentityToDiscoveredFullyQualifiedNameSelector()
    {
        const string identity = "dotnet:test:v1:tests/Merkle.Tests/Merkle.Tests.csproj:dotnet:type:tests/Merkle.Tests/Merkle.Tests.csproj:Merkle.Tests.Planning.PlanPolicyTests`0:RecommendationsAreExplicit`0():method";
        var state = new State { SeedHistory = true, SeedTestIdentity = identity };
        var adapter = new DeepAdapter
        {
            MapRequested = false,
            PlanIdentity = identity,
            PlanDisplayName = "PlanPolicyTests.RecommendationsAreExplicit`0()",
            RuntimeCatalog =
            [
                new TestCatalogEntry(
                    "dotnet:runner-test:v1:tests/Merkle.Tests/Merkle.Tests.csproj:abc",
                    "Merkle.Tests.Planning.PlanPolicyTests.RecommendationsAreExplicit",
                    "xunit",
                    "tests/Merkle.Tests/Merkle.Tests.csproj",
                    "Merkle.Tests.Planning.PlanPolicyTests.RecommendationsAreExplicit")
            ]
        };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal(TerminalStatus.Succeeded, report.TerminalStatus);
        var selected = Assert.Single(adapter.LastSelected);
        Assert.Equal(identity, selected.Identity);
        Assert.Equal("tests/Merkle.Tests/Merkle.Tests.csproj", selected.ProjectPath);
        Assert.Equal("Merkle.Tests.Planning.PlanPolicyTests.RecommendationsAreExplicit", selected.Selector);
    }

    [Fact]
    public async Task RunSelected_DoesNotExecuteFabricatedSelectorWhenDiscoveryHasNoMatch()
    {
        const string identity = "dotnet:test:v1:App.csproj:dotnet:type:App.csproj:Example.Tests:Works():method";
        var state = new State { SeedHistory = true, SeedTestIdentity = identity };
        var adapter = new DeepAdapter
        {
            MapRequested = false,
            PlanIdentity = identity,
            RuntimeCatalog = []
        };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal(TerminalStatus.Failed, report.TerminalStatus);
        Assert.Equal(ErrorClass.CapabilityError, report.ErrorClass);
        Assert.Equal("SelectedTestsUnavailable", report.ErrorCode);
        Assert.Empty(adapter.LastSelected);
    }

    [Fact]
    public async Task RunSelected_DoesNotExecutePartialResolutionWhenOneSelectedIdentityIsMissing()
    {
        var state = new State { SeedHistory = true };
        var adapter = new DeepAdapter
        {
            AdditionalPlanIdentity = "test:missing",
            ResolveSingleFallback = false,
            RuntimeCatalog = [new TestCatalogEntry("test:a", "A(1)", "xunit", "App.csproj", "A")]
        };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal(TerminalStatus.Failed, report.TerminalStatus);
        Assert.Equal("SelectedTestsUnavailable", report.ErrorCode);
        Assert.Empty(adapter.LastSelected);
    }

    [Fact]
    public async Task RunSelected_ReportsUnavailableUnknownIdentity()
    {
        var state = new State { SeedHistory = true, SeedTestIdentity = "unknown:test" };
        var adapter = new DeepAdapter { MapRequested = false, PlanIdentity = "unknown:test", RuntimeCatalog = [] };

        var report = await Engine(adapter, state).ExecuteAsync(
            Request(DeepExecutionMode.RunSelected, new PolicyConfiguration(0, 0, "plan-only", UnmappedBehavior.Warn)),
            default);

        Assert.Equal("SelectedTestsUnavailable", report.ErrorCode);
    }

    [Fact]
    public async Task Observe_DropsPartialEvidenceAndDeduplicatesWarnings()
    {
        var state = new State();
        var adapter = new DeepAdapter
        {
            Completeness = ObservationCompleteness.Incomplete,
            Duration = null,
            ScopeWarnings = ["coarse", "coarse"]
        };

        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        var execution = Assert.Single(report.Executions!);
        Assert.False(execution.ObservationComplete);
        Assert.Empty(execution.ObservedUnitIdentities);
        Assert.Null(execution.DurationMs);
        Assert.Single(report.Warnings, warning => warning == "coarse");
        Assert.Empty(Assert.Single(state.History).Tests[0].ObservedUnitIdentities);
    }

    [Fact]
    public async Task Observe_PublishesUnexpectedFailureWithoutLeakingItsMessage()
    {
        var state = new State();
        var adapter = new DeepAdapter { PrepareError = new InvalidOperationException("token=secret") };

        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        Assert.Equal("UnexpectedExecutionFailure", report.ErrorCode);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_ReturnsPlanningFailureBeforeDeepCapabilities()
    {
        var state = new State();
        var adapter = new DeepAdapter { ProtocolVersion = "2.0" };

        var report = await Engine(adapter, state).ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        Assert.Equal("UnsupportedProtocol", report.ErrorCode);
        Assert.Equal(0, adapter.PrepareCalls);
    }

    [Fact]
    public async Task Observe_UsesBasicStateStoreWhenAtomicPublicationExtensionIsUnavailable()
    {
        var state = new BasicState();
        var adapter = new DeepAdapter();
        var engine = new DeepExecutionEngine(
            new ImpactEngine(new Snapshots(), LanguageDetector.CreateDefault(), new AdapterRegistry([adapter]), state, FixedTime.Instance, "repo"),
            new Snapshots(), adapter, state, FixedTime.Instance);

        var report = await engine.ExecuteAsync(Request(DeepExecutionMode.Observe), default);

        Assert.Equal(TerminalStatus.Succeeded, report.TerminalStatus);
        Assert.Equal(report, state.Report);
    }

    [Fact]
    public void Constructor_RejectsNullCollaborators()
    {
        var state = new State();
        var snapshots = new Snapshots();
        var adapter = new DeepAdapter();
        var planner = new ImpactEngine(snapshots, LanguageDetector.CreateDefault(), new AdapterRegistry([adapter]), state, FixedTime.Instance, "repo");

        Assert.Throws<ArgumentNullException>(() => new DeepExecutionEngine(null!, snapshots, adapter, state, FixedTime.Instance));
        Assert.Throws<ArgumentNullException>(() => new DeepExecutionEngine(planner, null!, adapter, state, FixedTime.Instance));
        Assert.Throws<ArgumentNullException>(() => new DeepExecutionEngine(planner, snapshots, null!, state, FixedTime.Instance));
        Assert.Throws<ArgumentNullException>(() => new DeepExecutionEngine(planner, snapshots, adapter, null!, FixedTime.Instance));
        Assert.Throws<ArgumentNullException>(() => new DeepExecutionEngine(planner, snapshots, adapter, state, null!));
    }

    private static DeepExecutionEngine Engine(DeepAdapter adapter, State state) => new(
        new ImpactEngine(new Snapshots(), LanguageDetector.CreateDefault(), new AdapterRegistry([adapter]), state, FixedTime.Instance, "repo"),
        new Snapshots(), adapter, state, FixedTime.Instance);

    private static DeepExecutionRequest Request(DeepExecutionMode mode, PolicyConfiguration? policy = null) => new(
        new PlanRequest("main", "HEAD", [new LanguageSelection("dotnet", "minimal")], false, null, policy), mode, false, null, ".merkle");

    private sealed class Snapshots : ISnapshotSource
    {
        public ValueTask<SnapshotPair> BindAsync(string? baselineReference, string? candidateReference, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SnapshotPair(Snapshot("base", "old"), Snapshot("head", "new")));
        private static RepositorySnapshot Snapshot(string id, string hash) => new(new SnapshotIdentity(id, id, "git"), "/repo", "repo", [new SnapshotFile("src/App.cs", hash, [1])]);
    }

    private sealed class DeepAdapter : ILanguageAdapter, IBuildPreparer, ITestDiscoverer, ISelectedTestResolver, ISelectedTestExecutor, ITestObserver
    {
        public int PrepareCalls { get; private set; }
        public Exception? PrepareError { get; init; }
        public bool CancelObserve { get; init; }
        public bool MapRequested { get; init; } = true;
        public string PlanIdentity { get; init; } = "test:a";
        public string? AdditionalPlanIdentity { get; init; }
        public string PlanDisplayName { get; init; } = "A(1)";
        public bool ResolveSingleFallback { get; init; } = true;
        public string ProtocolVersion { get; init; } = "1.0";
        public TestOutcome Outcome { get; init; } = TestOutcome.Passed;
        public TimeSpan? Duration { get; init; } = TimeSpan.FromMilliseconds(4);
        public ObservationCompleteness Completeness { get; init; } = ObservationCompleteness.Complete;
        public IReadOnlyList<string> ScopeWarnings { get; init; } = [];
        public IReadOnlyList<TestCatalogEntry>? RuntimeCatalog { get; init; }
        public IReadOnlyList<TestCatalogEntry> LastSelected { get; private set; } = [];
        public AdapterDescriptor Describe() => new(ProtocolVersion, "dotnet", "test", "1", "1", "1", [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map, AdapterCapability.Discover, AdapterCapability.Observe, AdapterCapability.Execute], ["minimal"]);
        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            var unit = new SourceUnit("unit:a", SourceUnitKind.File, "src/App.cs", request.Snapshot.Files[0].ContentHash, "");
            string[] identities = AdditionalPlanIdentity is null ? [PlanIdentity] : [PlanIdentity, AdditionalPlanIdentity];
            return ValueTask.FromResult(new AdapterIndex(
                [unit],
                [.. identities.Select(identity => new ImpactEdge("unit:a", identity!, EvidenceKind.StaticDependency))],
                [.. identities.Select(identity => new TestDescriptor(identity!, identity == PlanIdentity ? PlanDisplayName : "Missing()", "xunit"))]));
        }
        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
        {
            if (!MapRequested) return ValueTask.FromResult(new MappingResult([], []));
            var requested = new List<RequestedTest>
            {
                new(PlanIdentity, PlanDisplayName, "xunit", [], true)
            };
            if (AdditionalPlanIdentity is not null)
            {
                requested.Add(new RequestedTest(AdditionalPlanIdentity, "Missing()", "xunit", [], true));
            }
            return ValueTask.FromResult(new MappingResult(requested, []));
        }
        public ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            if (PrepareError is not null) throw PrepareError;
            return ValueTask.FromResult(new BuildPreparationResult(Fingerprint(), []));
        }
        public ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken) => ValueTask.FromResult(new DiscoveryCatalog(fingerprint, RuntimeCatalog ?? [Catalog()], []));
        public SelectedTestResolution ResolveSelectedTests(IReadOnlyList<SelectedTestReference> selectedTests, IReadOnlyList<TestCatalogEntry> catalog)
        {
            var result = new List<TestCatalogEntry>();
            var unresolved = new List<SelectedTestReference>();
            foreach (var selected in selectedTests)
            {
                var exact = catalog.FirstOrDefault(test => test.Identity == selected.Identity);
                if (exact is not null)
                {
                    result.Add(exact);
                    continue;
                }

                if (selected.Identity.StartsWith("dotnet-project:", StringComparison.Ordinal))
                {
                    if (catalog.Count == 0) unresolved.Add(selected);
                    else result.AddRange(catalog);
                    continue;
                }

                if (ResolveSingleFallback && catalog.Count == 1)
                {
                    result.Add(catalog[0] with { Identity = selected.Identity, DisplayName = selected.DisplayName });
                }
                else
                {
                    unresolved.Add(selected);
                }
            }

            return new SelectedTestResolution(result, unresolved);
        }
        public ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken)
        {
            LastSelected = request.Tests;
            return ValueTask.FromResult<IReadOnlyList<TestExecutionResult>>([.. request.Tests.Select(test => new TestExecutionResult(test.Identity, Outcome, Duration))]);
        }
        public ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            if (CancelObserve) throw new OperationCanceledException(cancellationToken);
            var execution = new TestExecutionResult(PlanIdentity, Outcome, Duration);
            return ValueTask.FromResult<IReadOnlyList<ObservationScope>>([new(PlanIdentity, Completeness, [new DynamicObservation(PlanIdentity, "unit:a", "fp", "1", "1", "run", "assembly", "")], execution, ScopeWarnings)]);
        }
        private TestCatalogEntry Catalog() => new(PlanIdentity, "A(1)", "xunit", "App.csproj", "A");
        private static BuildFingerprint Fingerprint() => new("fp", "head", "App.sln", "Debug", "AnyCPU", "10", ["net10.0"], "1", "1", []);
    }

    private sealed class PlanningOnlyAdapter : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => new("1.0", "dotnet", "test", "1", "1", "1", [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);
        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            var unit = new SourceUnit("unit:a", SourceUnitKind.File, "src/App.cs", request.Snapshot.Files[0].ContentHash, "");
            return ValueTask.FromResult(new AdapterIndex([unit], [new ImpactEdge("unit:a", "test:a", EvidenceKind.StaticDependency)], [new TestDescriptor("test:a", "A", "xunit")]));
        }
        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new MappingResult([new RequestedTest("test:a", "A", "xunit", [], true)], []));
    }

    private sealed class ExecutingAdapterWithoutResolver : ILanguageAdapter, IBuildPreparer, ITestDiscoverer, ISelectedTestExecutor
    {
        public int PrepareCalls { get; private set; }
        public AdapterDescriptor Describe() => new("1.0", "dotnet", "test", "1", "1", "1", [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map, AdapterCapability.Discover, AdapterCapability.Execute], ["minimal", "deep"]);
        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
        {
            var unit = new SourceUnit("unit:a", SourceUnitKind.File, "src/App.cs", request.Snapshot.Files[0].ContentHash, "signature");
            return ValueTask.FromResult(new AdapterIndex([unit], [new ImpactEdge("unit:a", "test:a", EvidenceKind.StaticDependency)], [new TestDescriptor("test:a", "A()", "xunit")]));
        }
        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MappingResult([new RequestedTest("test:a", "A()", "xunit", [], true)], []));
        public ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return ValueTask.FromResult(new BuildPreparationResult(new BuildFingerprint("fp", "head", "App.sln", "Debug", "AnyCPU", "10", ["net10.0"], "1", "1", []), []));
        }
        public ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DiscoveryCatalog(fingerprint, [new TestCatalogEntry("test:a", "A()", "xunit", "App.csproj", "A")], []));
        public ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<TestExecutionResult>>([]);
    }

    private sealed class State : IStateStore, IStatePublicationStore, IHistoryStore
    {
        public TerminalReport? Report { get; private set; }
        public List<HistoricalRun> History { get; } = [];
        public bool SeedHistory { get; init; }
        public string SeedTestIdentity { get; init; } = "test:a";
        public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken) => ValueTask.FromResult(new RunJournal(runId, ""));
        public ValueTask PublishAsync(RunJournal journal, TerminalReport report, CancellationToken cancellationToken) { Report = report; return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(RunJournal journal, StatePublication publication, CancellationToken cancellationToken) { Report = publication.TerminalReport; History.AddRange(publication.PersistedHistoryRuns); return ValueTask.CompletedTask; }
        public ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Report);
        public ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new StateStatus("memory", 1, 0, null, false));
        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<HistoricalRun>> ReadHistoryAsync(HistoryCompatibilityKey compatibility, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HistoricalRun>>(SeedHistory
            ? [new HistoricalRun(compatibility, HistoryProvenance.OfficialCi, HistoryRunStatus.Succeeded, true, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), ["unit:a"], [new HistoricalTestExecution(SeedTestIdentity, true, HistoricalTestOutcome.Passed, 4, ["unit:a"])])]
            : []);
    }

    private sealed class BasicState : IStateStore
    {
        public TerminalReport? Report { get; private set; }
        public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken) => ValueTask.FromResult(new RunJournal(runId, ""));
        public ValueTask PublishAsync(RunJournal journal, TerminalReport report, CancellationToken cancellationToken) { Report = report; return ValueTask.CompletedTask; }
        public ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Report);
        public ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new StateStatus("memory", 1, 0, null, false));
        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FixedTime : TimeProvider { public static readonly FixedTime Instance = new(); public override DateTimeOffset GetUtcNow() => new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero); }
}
