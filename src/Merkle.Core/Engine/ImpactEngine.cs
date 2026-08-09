using System.Security.Cryptography;
using System.Text;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Indexing;
using Merkle.Core.History;
using Merkle.Core.Planning;
using Merkle.Core.Reporting;
using Merkle.Core.Snapshots;
using Merkle.Core.State;

namespace Merkle.Core.Engine;

public sealed record PlanRequest(
    string? BaselineReference,
    string? CandidateReference,
    IReadOnlyList<LanguageSelection> Languages,
    bool Pedantic,
    string? ConfiguredSolution,
    PolicyConfiguration? Policy = null,
    string Configuration = "Debug",
    string Platform = "AnyCPU");

public sealed class ImpactEngine(
    ISnapshotSource snapshotSource,
    LanguageDetector languageDetector,
    AdapterRegistry adapterRegistry,
    IStateStore stateStore,
    TimeProvider timeProvider,
    string repositoryIdentity = "unresolved",
    IHistoryModel? historyModel = null,
    SecretRedactor? redactor = null,
    IPlanPolicy? planPolicy = null)
{
    private static readonly AdapterCapability[] PlanCapabilities =
        [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map];

    private readonly ISnapshotSource _snapshotSource = snapshotSource;
    private readonly LanguageDetector _languageDetector = languageDetector;
    private readonly AdapterRegistry _adapterRegistry = adapterRegistry;
    private readonly IStateStore _stateStore = stateStore;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly string _repositoryIdentity = repositoryIdentity;
    private readonly IHistoryModel _historyModel = historyModel ?? new HistoryModel();
    private readonly SecretRedactor _redactor = redactor ?? SecretRedactor.Default;
    private readonly IPlanPolicy _planPolicy = planPolicy ?? new PlanPolicy();

    public async ValueTask<TerminalReport> PlanAsync(
        PlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N");
        RunJournal? journal = null;
        SnapshotPair? snapshots = null;
        IReadOnlyList<DetectedLanguage> detected = [];

        try
        {
            snapshots = await _snapshotSource.BindAsync(
                request.BaselineReference,
                request.CandidateReference,
                cancellationToken).ConfigureAwait(false);
            journal = await _stateStore.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);

            detected = _languageDetector.Detect(
                snapshots.Baseline.Files.Select(file => file.Path)
                    .Concat(snapshots.Candidate.Files.Select(file => file.Path)));
            var selections = ResolveSelections(detected, request.Languages);
            LanguageDetector.ValidateSelection(detected, selections);

            var adapters = selections
                .Select(selection => _adapterRegistry.Resolve(selection, PlanCapabilities))
                .ToArray();

            var changed = new List<ChangedUnit>();
            var requestedTests = new Dictionary<string, RequestedTest>(StringComparer.Ordinal);
            var testCatalog = new Dictionary<string, TestDescriptor>(StringComparer.Ordinal);
            var historyEstimates = new Dictionary<string, HistoryTestEstimate>(StringComparer.Ordinal);
            var unmapped = new Dictionary<string, ChangedUnit>(StringComparer.Ordinal);
            var adapterWarnings = new List<string>();
            var persistedIndexes = new List<PersistedAdapterIndex>();
            var compatibleHistoryRuns = 0;
            var unmatchedHistoryRuns = 0;

            foreach (var adapter in adapters)
            {
                var descriptor = adapter.Describe();
                var baselineIndexResult = await ReadOrBuildIndexAsync(
                    adapter,
                    descriptor,
                    snapshots.Baseline,
                    request.ConfiguredSolution,
                    cancellationToken).ConfigureAwait(false);
                var candidateIndexResult = await ReadOrBuildIndexAsync(
                    adapter,
                    descriptor,
                    snapshots.Candidate,
                    request.ConfiguredSolution,
                    cancellationToken).ConfigureAwait(false);
                var baselineIndex = baselineIndexResult.Index;
                var candidateIndex = candidateIndexResult.Index;
                if (baselineIndexResult.Persisted is not null)
                {
                    persistedIndexes.Add(baselineIndexResult.Persisted);
                }

                if (candidateIndexResult.Persisted is not null)
                {
                    persistedIndexes.Add(candidateIndexResult.Persisted);
                }

                foreach (var test in candidateIndex.Tests)
                {
                    testCatalog[test.Identity] = test;
                }
                if (baselineIndex.Warnings is not null)
                {
                    adapterWarnings.AddRange(baselineIndex.Warnings);
                }

                if (candidateIndex.Warnings is not null)
                {
                    adapterWarnings.AddRange(candidateIndex.Warnings);
                }
                var adapterChanges = MerkleIndex.Compare(
                    MerkleIndex.Build(baselineIndex.Units, baselineIndex.Edges),
                    MerkleIndex.Build(candidateIndex.Units, candidateIndex.Edges));
                changed.AddRange(adapterChanges);

                var mappingIndex = new AdapterIndex(
                    candidateIndex.Units,
                    [.. baselineIndex.Edges.Concat(candidateIndex.Edges).Distinct()],
                    candidateIndex.Tests,
                    candidateIndex.Warnings);
                var mapping = await adapter.MapAsync(
                    new AdapterMapRequest(snapshots.Candidate, mappingIndex, adapterChanges),
                    cancellationToken).ConfigureAwait(false);
                foreach (var requestedTest in mapping.RequestedTests)
                {
                    if (requestedTests.TryGetValue(requestedTest.Identity, out var current))
                    {
                        requestedTests[requestedTest.Identity] = current with
                        {
                            Reasons = [.. current.Reasons
                                .Concat(requestedTest.Reasons)
                                .Distinct()]
                        };
                    }
                    else
                    {
                        requestedTests.Add(requestedTest.Identity, requestedTest);
                    }
                }

                foreach (var unit in mapping.UnmappedUnits)
                {
                    unmapped[unit.Identity] = unit;
                }

                if (mapping.Warnings is not null)
                {
                    adapterWarnings.AddRange(mapping.Warnings);
                }

                if (_stateStore is IHistoryStore historyStore && candidateIndex.Tests.Count > 0)
                {
                    var historyKey = HistoryCompatibility.ForAdapter(
                        snapshots.Candidate.RepositoryIdentity,
                        descriptor,
                        request.ConfiguredSolution,
                        request.Configuration,
                        request.Platform);
                    var history = await historyStore.ReadHistoryAsync(historyKey, cancellationToken)
                        .ConfigureAwait(false);
                    var estimate = _historyModel.Estimate(new HistoryQuery(
                        historyKey,
                        adapterChanges.Select(unit => unit.Identity),
                        candidateIndex.Tests.Select(test => test.Identity),
                        history,
                        _timeProvider.GetUtcNow(),
                        cancellationToken));
                    compatibleHistoryRuns += estimate.CompatibleRunCount;
                    unmatchedHistoryRuns += estimate.UnmatchedRunCount;
                    foreach (var testEstimate in estimate.Tests.Where(item =>
                                 item.Availability == HistoryAvailability.Available))
                    {
                        historyEstimates[testEstimate.TestIdentity] = testEstimate;
                    }
                }
            }

            var candidateIdentities = requestedTests.Keys
                .Concat(historyEstimates.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray();
            var candidates = candidateIdentities
                .Select(identity => BuildCandidate(
                    identity,
                    requestedTests,
                    testCatalog,
                    historyEstimates,
                    changed))
                .ToArray();
            var policyConfiguration = EffectivePolicy(request);
            PlanDecision decision;
            PolicyException? policyFailure = null;
            try
            {
                decision = _planPolicy.Apply(
                    candidates,
                    [.. unmapped.Values],
                    policyConfiguration);
            }
            catch (PolicyException error)
            {
                policyFailure = error;
                decision = new PlanDecision(
                    [.. candidates.Select(candidate => new PlannedTest(
                            candidate.Test.Identity,
                            candidate.Test.DisplayName,
                            Selected: true,
                            candidate.ImpactProbability,
                            candidate.EvidenceConfidence,
                            candidate.ExpectedDurationMs,
                            candidate.Reasons,
                            ExcludedBy: null))
                        .OrderBy(test => test.Identity, StringComparer.Ordinal)],
                    PlanRecommendation.PolicyFailure,
                    error.Message,
                    null,
                    null,
                    null);
            }
            var warnings = BuildWarnings(unmapped.Count, request.Pedantic)
                .Concat(adapterWarnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var reportedChanges = changed
                .Select(unit => unit with { Mapped = !unmapped.ContainsKey(unit.Identity) })
                .OrderBy(unit => unit.Identity, StringComparer.Ordinal)
                .ToArray();

            var report = new TerminalReport(
                SchemaVersion: 1,
                runId,
                policyFailure is null ? TerminalStatus.Succeeded : TerminalStatus.PolicyFailed,
                policyFailure?.ErrorClass,
                policyFailure?.Code,
                snapshots.Baseline.Identity,
                snapshots.Candidate.Identity,
                snapshots.Candidate.RepositoryIdentity,
                detected,
                [.. adapters.Select(adapter => ToReportAdapter(adapter.Describe()))],
                [.. adapters.SelectMany(adapter => adapter.Describe().Capabilities)
                    .Select(capability => capability.ToString().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(capability => capability, StringComparer.Ordinal)],
                MerkleIndex.SchemaVersion,
                [.. adapters.SelectMany(adapter => new[]
                    {
                        $"unit:{adapter.Describe().UnitIdentityVersion}",
                        $"test:{adapter.Describe().TestIdentityVersion}"
                    })
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)],
                BuildFingerprint: null,
                reportedChanges,
                decision.Tests,
                [.. unmapped.Values.OrderBy(unit => unit.Identity, StringComparer.Ordinal)],
                warnings,
                new ReportHistory(
                    compatibleHistoryRuns,
                    unmatchedHistoryRuns,
                    compatibleHistoryRuns == 0 ? [] : ["local", "official-ci", "imported"]),
                new ReportEconomics(decision.SelectedMeanMs, decision.FullMeanMs, decision.SavingsPercent),
                new ReportPolicy(policyConfiguration, decision.Recommendation, decision.DecisiveReason),
                _timeProvider.GetUtcNow(),
                BuildContext: new ReportBuildContext(
                    request.ConfiguredSolution,
                    request.Configuration,
                    request.Platform,
                    null),
                Limits: Limits(adapterWarnings));

            await PublishAsync(
                journal!,
                report,
                persistedIndexes,
                cancellationToken).ConfigureAwait(false);
            return report;
        }
        catch (MerkleException error)
        {
            var report = FailureReport(runId, request, snapshots, detected, error);
            journal ??= await _stateStore.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);
            await _stateStore.PublishAsync(journal!, report, cancellationToken).ConfigureAwait(false);
            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            var classified = new AnalysisException(
                "UnexpectedAnalysisFailure",
                $"Analysis failed unexpectedly: {error.Message}",
                error);
            var report = FailureReport(runId, request, snapshots, detected, classified);
            journal ??= await _stateStore.BeginRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
            await _stateStore.PublishAsync(journal, report, CancellationToken.None).ConfigureAwait(false);
            return report;
        }
    }

    private static IReadOnlyList<LanguageSelection> ResolveSelections(
        IReadOnlyList<DetectedLanguage> detected,
        IReadOnlyList<LanguageSelection> requested)
    {
        if (requested.Count > 0 || detected.Count != 1)
        {
            return requested;
        }

        return [new LanguageSelection(detected[0].Language, "minimal")];
    }

    private async ValueTask<IndexReadResult> ReadOrBuildIndexAsync(
        ILanguageAdapter adapter,
        AdapterDescriptor descriptor,
        RepositorySnapshot snapshot,
        string? configuredSolution,
        CancellationToken cancellationToken)
    {
        var key = IndexKey(snapshot, descriptor, configuredSolution);
        if (_stateStore is IIndexStore indexStore)
        {
            var stored = await indexStore.ReadIndexAsync(key, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return new IndexReadResult(stored, null);
            }
        }

        var index = await adapter.IndexAsync(
            new AdapterIndexRequest(snapshot, configuredSolution),
            cancellationToken).ConfigureAwait(false);
        return new IndexReadResult(index, new PersistedAdapterIndex(key, index));
    }

    private static IndexCompatibilityKey IndexKey(
        RepositorySnapshot snapshot,
        AdapterDescriptor descriptor,
        string? configuredSolution) => new(
        snapshot.RepositoryIdentity,
        snapshot.Identity.Value,
        MerkleIndex.SchemaVersion,
        MerkleIndex.HashAlgorithm,
        "semantic-v1",
        $"{descriptor.Producer}/{descriptor.AdapterVersion}",
        descriptor.ProtocolVersion,
        descriptor.UnitIdentityVersion,
        descriptor.TestIdentityVersion,
        descriptor.Language,
        Digest(configuredSolution ?? "auto"));

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TestCandidate BuildCandidate(
        string identity,
        IReadOnlyDictionary<string, RequestedTest> requested,
        IReadOnlyDictionary<string, TestDescriptor> catalog,
        IReadOnlyDictionary<string, HistoryTestEstimate> estimates,
        IReadOnlyList<ChangedUnit> changed)
    {
        var mandatory = requested.TryGetValue(identity, out var mapped);
        var descriptor = catalog.TryGetValue(identity, out var discovered)
            ? discovered
            : new TestDescriptor(identity, mapped?.DisplayName ?? identity, mapped?.Framework ?? "unknown");
        estimates.TryGetValue(identity, out var estimate);
        var reasons = mandatory
            ? mapped!.Reasons
            : [.. changed.Take(1)
                .Select(unit => new ImpactReason(
                    EvidenceKind.HistoricalAssociation,
                    unit.Identity,
                    [unit.Identity, identity]))];
        var metadata = estimate is null
            ? PlanEstimateMetadata.Unavailable("no-compatible-history")
            : new PlanEstimateMetadata(
                EstimateDescriptor.Estimated("beta-binomial-v1", estimate.EligibleRunCount),
                EstimateDescriptor.Estimated("evidence-confidence-v1", estimate.EligibleRunCount),
                estimate.Runtime is null
                    ? EstimateDescriptor.Unavailable("no-comparable-runtime")
                    : EstimateDescriptor.Estimated("runtime-mean-v1", estimate.Runtime.SampleCount));
        return new TestCandidate(
            descriptor,
            mandatory,
            estimate?.ImpactProbability,
            estimate?.EvidenceConfidence,
            estimate?.Runtime?.MeanMs,
            reasons,
            metadata);
    }

    private ValueTask PublishAsync(
        RunJournal journal,
        TerminalReport report,
        IReadOnlyList<PersistedAdapterIndex> indexes,
        CancellationToken cancellationToken) =>
        _stateStore is IStatePublicationStore publisher
            ? publisher.PublishAsync(journal, new StatePublication(report, indexes), cancellationToken)
            : _stateStore.PublishAsync(journal, report, cancellationToken);

    private sealed record IndexReadResult(AdapterIndex Index, PersistedAdapterIndex? Persisted);

    private static ReportAdapter ToReportAdapter(AdapterDescriptor descriptor) => new(
        descriptor.Language,
        descriptor.Producer,
        descriptor.AdapterVersion,
        descriptor.ProtocolVersion,
        descriptor.UnitIdentityVersion,
        descriptor.TestIdentityVersion,
        [.. descriptor.Capabilities
            .Select(capability => capability.ToString().ToLowerInvariant())
            .OrderBy(capability => capability, StringComparer.Ordinal)],
        descriptor.SupportedTargets?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [],
        descriptor.SupportedPlatforms?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? []);

    private static IReadOnlyList<string> BuildWarnings(int unmappedCount, bool pedantic)
    {
        var warnings = new List<string>
        {
            "Merkle is advisory; unselected tests may still fail."
        };
        if (unmappedCount > 0)
        {
            warnings.Add($"{unmappedCount} changed source unit(s) have no known test relationship.");
        }

        warnings.Add("Phase 1 uses project-level .NET test targets; individual test discovery is not yet available.");
        if (pedantic && unmappedCount > 0)
        {
            warnings.Add("Pedantic policy rejected this plan because it contains unmapped source units.");
        }

        return warnings;
    }

    private TerminalReport FailureReport(
        string runId,
        PlanRequest request,
        SnapshotPair? snapshots,
        IReadOnlyList<DetectedLanguage> detected,
        MerkleException error)
    {
        var baseline = snapshots?.Baseline.Identity ??
                       new SnapshotIdentity("unresolved", request.BaselineReference ?? "unspecified", "git");
        var candidate = snapshots?.Candidate.Identity ??
                        new SnapshotIdentity("unresolved", request.CandidateReference ?? "unspecified", "git");
        var status = error.ErrorClass == ErrorClass.PolicyFailure
            ? TerminalStatus.PolicyFailed
            : TerminalStatus.Failed;
        var redactedMessage = _redactor.Redact(error.Message);
        var policy = EffectivePolicy(request);

        return new TerminalReport(
            1,
            runId,
            status,
            error.ErrorClass,
            error.Code,
            baseline,
            candidate,
            snapshots?.Candidate.RepositoryIdentity ?? _repositoryIdentity,
            detected,
            [],
            [],
            MerkleIndex.SchemaVersion,
            [],
            null,
            [],
            [],
            [],
            [redactedMessage],
            new ReportHistory(0, 0, []),
            new ReportEconomics(null, null, null),
            new ReportPolicy(policy, PlanRecommendation.PolicyFailure, redactedMessage),
            _timeProvider.GetUtcNow(),
            BuildContext: new ReportBuildContext(
                request.ConfiguredSolution,
                request.Configuration,
                request.Platform,
                null),
            Limits: Limits([]));
    }

    private static PolicyConfiguration EffectivePolicy(PlanRequest request)
    {
        var configured = request.Policy ?? new PolicyConfiguration(
            30,
            null,
            null,
            UnmappedBehavior.Warn);
        return request.Pedantic
            ? configured with { Unmapped = UnmappedBehavior.Fail }
            : configured;
    }

    private static ReportLimitStatus Limits(IReadOnlyList<string> warnings) => new(
        100_000,
        1L * 1024 * 1024 * 1024,
        256L * 1024 * 1024,
        16 * 1024 * 1024,
        1 * 1024 * 1024,
        100,
        warnings.Any(warning => warning.Contains("truncated", StringComparison.OrdinalIgnoreCase)));
}
