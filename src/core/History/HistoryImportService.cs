using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Reporting;
using Merkle.Core.State;

namespace Merkle.Core.History;

public interface IHistoryImportService
{
    ValueTask<TerminalReport> ImportAsync(
        TerminalReport source,
        CancellationToken cancellationToken);
}

public sealed class HistoryImportService(
    IStateStore stateStore,
    string repositoryIdentity,
    TimeProvider timeProvider) : IHistoryImportService
{
    private readonly IStateStore _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly string _repositoryIdentity = string.IsNullOrWhiteSpace(repositoryIdentity)
            ? throw new ArgumentException("A repository identity is required.", nameof(repositoryIdentity))
            : repositoryIdentity;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async ValueTask<TerminalReport> ImportAsync(
        TerminalReport source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != 1)
        {
            throw new ConfigurationException(
                "IncompatibleImportSchema",
                "Only terminal report schema 1 can be imported.");
        }

        if (!StringComparer.Ordinal.Equals(source.RepositoryIdentity, _repositoryIdentity))
        {
            throw new ConfigurationException(
                "ImportRepositoryMismatch",
                "The terminal report belongs to a different repository identity.");
        }

        if (source.Executions is not { Count: > 0 })
        {
            throw new ConfigurationException(
                "ImportHasNoExecutions",
                "The terminal report contains no executed tests to import.");
        }

        if (source.Adapters.Count != 1)
        {
            throw new ConfigurationException(
                "ImportAdapterAmbiguous",
                "An imported execution report must identify exactly one adapter.");
        }

        var compatibility = HistoryCompatibility.ForReportAdapter(
            source.RepositoryIdentity,
            source.Adapters[0],
            source.BuildContext);
        var history = new HistoricalRun(
            compatibility,
            HistoryProvenance.Imported,
            ToHistoryStatus(source.TerminalStatus),
            source.BuildContext?.ExecutionMode == "observe" ||
                source.Policy.Recommendation == PlanRecommendation.FullSuite,
            source.EvidenceCutoff,
            [.. source.ChangedUnits.Select(unit => unit.Identity)],
            [.. source.Executions.Select(ToHistoricalExecution)]);
        if (_stateStore is IHistoryStore historyStore)
        {
            var existing = await historyStore.ReadHistoryAsync(compatibility, cancellationToken)
                .ConfigureAwait(false);
            if (existing.Any(candidate => Equivalent(candidate, history)))
            {
                return source;
            }
        }

        var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N");
        var report = source with
        {
            RunId = runId,
            Warnings = [.. source.Warnings
                .Append($"Imported terminal evidence from run {source.RunId}.")
                .Distinct(StringComparer.Ordinal)],
            EvidenceCutoff = _timeProvider.GetUtcNow()
        };
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
            throw new CapabilityException(
                "HistoryImportUnavailable",
                "The configured state provider cannot publish imported history.");
        }

        return report;
    }

    private static HistoricalTestExecution ToHistoricalExecution(ReportTestExecution execution)
    {
        if (!Enum.TryParse<HistoricalTestOutcome>(execution.Outcome, ignoreCase: true, out var outcome))
        {
            throw new ConfigurationException(
                "InvalidImportedOutcome",
                $"The imported outcome '{execution.Outcome}' is not supported.");
        }

        return new HistoricalTestExecution(
            execution.TestIdentity,
            execution.Executed,
            outcome,
            execution.DurationMs,
            execution.ObservationComplete ? execution.ObservedUnitIdentities : []);
    }

    private static HistoryRunStatus ToHistoryStatus(TerminalStatus status) => status switch
    {
        TerminalStatus.Succeeded => HistoryRunStatus.Succeeded,
        TerminalStatus.Interrupted => HistoryRunStatus.Interrupted,
        _ => HistoryRunStatus.Failed
    };

    private static bool Equivalent(HistoricalRun left, HistoricalRun right) =>
        left.Provenance == HistoryProvenance.Imported &&
        left.CompletedAt == right.CompletedAt &&
        left.IsCompleteSuite == right.IsCompleteSuite &&
        left.ChangedUnitIdentities.SequenceEqual(right.ChangedUnitIdentities, StringComparer.Ordinal) &&
        left.Tests.Count == right.Tests.Count &&
        left.Tests.Zip(right.Tests).All(pair =>
            pair.First.TestIdentity == pair.Second.TestIdentity &&
            pair.First.Executed == pair.Second.Executed &&
            pair.First.Outcome == pair.Second.Outcome &&
            pair.First.DurationMs == pair.Second.DurationMs &&
            pair.First.ObservedUnitIdentities.SequenceEqual(
                pair.Second.ObservedUnitIdentities,
                StringComparer.Ordinal));
}
