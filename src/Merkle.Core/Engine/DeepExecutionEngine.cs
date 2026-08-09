using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.Snapshots;
using Merkle.Core.State;

namespace Merkle.Core.Engine;

public enum DeepExecutionMode
{
    Observe,
    RunSelected
}

public sealed record DeepExecutionRequest(
    PlanRequest Plan,
    DeepExecutionMode Mode,
    bool NoBuild,
    TimeSpan? Timeout,
    string StateDirectory);

public interface IDeepExecutionEngine
{
    ValueTask<TerminalReport> ExecuteAsync(
        DeepExecutionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DeepExecutionEngine(
    ImpactEngine planner,
    ISnapshotSource snapshotSource,
    ILanguageAdapter adapter,
    IStateStore stateStore,
    TimeProvider timeProvider,
    SecretRedactor? redactor = null) : IDeepExecutionEngine
{
    private readonly ImpactEngine _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly ISnapshotSource _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
    private readonly ILanguageAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IStateStore _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SecretRedactor _redactor = redactor ?? SecretRedactor.Default;

    public async ValueTask<TerminalReport> ExecuteAsync(
        DeepExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await _planner.PlanAsync(request.Plan, cancellationToken).ConfigureAwait(false);
        if (plan.TerminalStatus != TerminalStatus.Succeeded)
        {
            return plan;
        }

        if (request.Mode == DeepExecutionMode.RunSelected &&
            plan.Policy.Recommendation is PlanRecommendation.PlanOnly or PlanRecommendation.DecisionNotConfigured)
        {
            return plan;
        }

        try
        {
            var resolver = request.Mode == DeepExecutionMode.RunSelected &&
                           plan.Policy.Recommendation != PlanRecommendation.FullSuite
                ? Require<ISelectedTestResolver>("resolve-selected-tests")
                : null;
            var buildPreparer = Require<IBuildPreparer>("build");
            var discoverer = Require<ITestDiscoverer>("discover");
            var snapshots = await _snapshotSource.BindAsync(
                request.Plan.BaselineReference,
                request.Plan.CandidateReference,
                cancellationToken).ConfigureAwait(false);
            var context = new DeepAdapterContext(
                snapshots.Candidate,
                request.Plan.ConfiguredSolution,
                request.Plan.Configuration,
                request.Plan.Platform,
                request.StateDirectory);
            var prepared = await buildPreparer.PrepareBuildAsync(
                new BuildPreparationRequest(context, request.NoBuild),
                cancellationToken).ConfigureAwait(false);
            var catalog = await discoverer.DiscoverAsync(
                context,
                prepared.Fingerprint,
                cancellationToken).ConfigureAwait(false);
            var selected = SelectTests(plan, catalog.Tests, request.Mode, resolver);
            if (selected.Count == 0 && plan.Tests.Any(test => test.Selected))
            {
                throw new CapabilityException(
                    "SelectedTestsUnavailable",
                    "The runtime test catalog could not resolve any selected test identities.");
            }

            IReadOnlyList<ReportTestExecution> reportExecutions;
            IReadOnlyList<HistoricalTestExecution> historyExecutions;
            var executionWarnings = new List<string>(prepared.Warnings.Concat(catalog.Warnings));
            if (request.Mode == DeepExecutionMode.Observe)
            {
                var observer = Require<ITestObserver>("observe");
                var scopes = await observer.ObserveAsync(
                    new ObservationRequest(
                        context,
                        prepared.Fingerprint,
                        selected,
                        request.Timeout),
                    cancellationToken).ConfigureAwait(false);
                reportExecutions = [.. scopes.Select(ToReportExecution)];
                historyExecutions = [.. scopes.Select(ToHistoricalExecution)];
                executionWarnings.AddRange(scopes.SelectMany(scope => scope.Warnings));
            }
            else
            {
                var executor = Require<ISelectedTestExecutor>("execute");
                var executions = await executor.ExecuteAsync(
                    new SelectedExecutionRequest(
                        context,
                        prepared.Fingerprint,
                        selected,
                        request.Timeout),
                    cancellationToken).ConfigureAwait(false);
                reportExecutions = [.. executions.Select(ToReportExecution)];
                historyExecutions = [.. executions.Select(ToHistoricalExecution)];
            }

            return await PublishAsync(
                plan,
                prepared.Fingerprint,
                reportExecutions,
                historyExecutions,
                executionWarnings,
                request.Mode == DeepExecutionMode.Observe ||
                    plan.Policy.Recommendation == PlanRecommendation.FullSuite,
                request.Mode == DeepExecutionMode.Observe ? "observe" : "run-selected",
                request.Plan,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MerkleException error)
        {
            return await PublishFailureAsync(plan, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return await PublishFailureAsync(
                plan,
                new AnalysisException(
                    "UnexpectedExecutionFailure",
                    "Deep execution failed unexpectedly.",
                    error),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<TerminalReport> PublishAsync(
        TerminalReport plan,
        BuildFingerprint fingerprint,
        IReadOnlyList<ReportTestExecution> reportExecutions,
        IReadOnlyList<HistoricalTestExecution> historyExecutions,
        IReadOnlyList<string> warnings,
        bool completeSuite,
        string executionMode,
        PlanRequest planRequest,
        CancellationToken cancellationToken)
    {
        var failed = historyExecutions.Any(execution => execution.Outcome is
            HistoricalTestOutcome.Failed or
            HistoricalTestOutcome.TimedOut or
            HistoricalTestOutcome.Crashed or
            HistoricalTestOutcome.Cancelled);
        var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N");
        var report = plan with
        {
            RunId = runId,
            TerminalStatus = failed ? TerminalStatus.Failed : TerminalStatus.Succeeded,
            ErrorClass = failed ? ErrorClass.TestFailure : null,
            ErrorCode = failed ? "SelectedTestsFailed" : null,
            BuildFingerprint = fingerprint.Value,
            Executions = reportExecutions,
            BuildContext = new ReportBuildContext(
                planRequest.ConfiguredSolution,
                planRequest.Configuration,
                planRequest.Platform,
                executionMode),
            Warnings = [.. plan.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal)],
            EvidenceCutoff = _timeProvider.GetUtcNow()
        };
        var descriptor = _adapter.Describe();
        var compatibility = HistoryCompatibility.ForAdapter(
            plan.RepositoryIdentity,
            descriptor,
            planRequest.ConfiguredSolution,
            fingerprint.Configuration,
            fingerprint.Platform);
        var history = new HistoricalRun(
            compatibility,
            HistoryProvenance.Local,
            failed ? HistoryRunStatus.Failed : HistoryRunStatus.Succeeded,
            completeSuite,
            report.EvidenceCutoff,
            [.. plan.ChangedUnits.Select(unit => unit.Identity)],
            historyExecutions);
        var journal = await _stateStore.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (_stateStore is IStatePublicationStore publisher)
        {
            await publisher.PublishAsync(
                journal,
                new StatePublication(report, HistoryRuns: [history]),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _stateStore.PublishAsync(journal, report, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    private async ValueTask<TerminalReport> PublishFailureAsync(
        TerminalReport plan,
        MerkleException error,
        CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N");
        var report = plan with
        {
            RunId = runId,
            TerminalStatus = TerminalStatus.Failed,
            ErrorClass = error.ErrorClass,
            ErrorCode = error.Code,
            Warnings = [.. plan.Warnings
                .Append(_redactor.Redact(error.Message))
                .Distinct(StringComparer.Ordinal)],
            EvidenceCutoff = _timeProvider.GetUtcNow()
        };
        var journal = await _stateStore.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);
        await _stateStore.PublishAsync(journal, report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private IReadOnlyList<TestCatalogEntry> SelectTests(
        TerminalReport plan,
        IReadOnlyList<TestCatalogEntry> catalog,
        DeepExecutionMode mode,
        ISelectedTestResolver? resolver)
    {
        if (mode == DeepExecutionMode.Observe || plan.Policy.Recommendation == PlanRecommendation.FullSuite)
        {
            return [.. catalog.OrderBy(test => test.Identity, StringComparer.Ordinal)];
        }

        var selected = plan.Tests
            .Where(test => test.Selected)
            .Select(test => new SelectedTestReference(test.Identity, test.DisplayName))
            .ToArray();
        var resolution = resolver!.ResolveSelectedTests(selected, catalog);
        if (resolution.UnresolvedTests.Count > 0)
        {
            throw new CapabilityException(
                "SelectedTestsUnavailable",
                $"The runtime test catalog could not resolve {resolution.UnresolvedTests.Count} selected test identities.");
        }
        return resolution.Tests;
    }

    private static ReportTestExecution ToReportExecution(TestExecutionResult execution) => new(
        execution.TestIdentity,
        execution.Outcome.ToString(),
        true,
        execution.Duration?.TotalMilliseconds,
        [],
        false,
        execution.Diagnostics);

    private static ReportTestExecution ToReportExecution(ObservationScope scope) => new(
        scope.TestIdentity,
        scope.Execution.Outcome.ToString(),
        true,
        scope.Execution.Duration?.TotalMilliseconds,
        scope.Completeness == ObservationCompleteness.Complete
            ? scope.Observations.Select(observation => observation.UnitIdentity).Distinct(StringComparer.Ordinal).ToArray()
            : [],
        scope.Completeness == ObservationCompleteness.Complete,
        scope.Execution.Diagnostics);

    private static HistoricalTestExecution ToHistoricalExecution(TestExecutionResult execution) => new(
        execution.TestIdentity,
        true,
        ToHistoricalOutcome(execution.Outcome),
        execution.Duration?.TotalMilliseconds,
        []);

    private static HistoricalTestExecution ToHistoricalExecution(ObservationScope scope) => new(
        scope.TestIdentity,
        true,
        ToHistoricalOutcome(scope.Execution.Outcome),
        scope.Execution.Duration?.TotalMilliseconds,
            scope.Completeness == ObservationCompleteness.Complete
            ? scope.Observations.Select(observation => observation.UnitIdentity).ToArray()
            : []);

    private static HistoricalTestOutcome ToHistoricalOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => HistoricalTestOutcome.Passed,
        TestOutcome.Failed => HistoricalTestOutcome.Failed,
        TestOutcome.Skipped => HistoricalTestOutcome.Skipped,
        TestOutcome.TimedOut => HistoricalTestOutcome.TimedOut,
        TestOutcome.Crashed => HistoricalTestOutcome.Crashed,
        TestOutcome.Cancelled => HistoricalTestOutcome.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static CapabilityException MissingCapability(string capability) => new(
        "DeepToolchainUnavailable",
        $"The configured adapter does not expose the '{capability}' capability.");

    private T Require<T>(string capability) where T : class =>
        _adapter as T ?? throw MissingCapability(capability);
}
