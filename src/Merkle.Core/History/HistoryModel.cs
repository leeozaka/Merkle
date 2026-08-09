namespace Merkle.Core.History;

/// <summary>Pure v1 estimator for one change event. Event A means historical relevance to that event.</summary>
public sealed class HistoryModel : IHistoryModel
{
    private const double PriorAlpha = 1d;
    private const double PriorBeta = 1d;

    public HistoryEstimate Estimate(HistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.CancellationToken.ThrowIfCancellationRequested();

        var candidates = query.CandidateTestIdentities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var changed = query.ChangedUnitIdentities.ToHashSet(StringComparer.Ordinal);
        var compatible = new List<HistoricalRun>();
        var unmatched = 0;

        foreach (var run in query.Runs)
        {
            query.CancellationToken.ThrowIfCancellationRequested();
            if (run is null || !IsTerminal(run.Status) || run.CompletedAt > query.AsOf ||
                !query.Compatibility.Matches(run.Compatibility))
            {
                unmatched++;
                continue;
            }

            compatible.Add(run);
        }

        var tests = candidates.Select(candidate => EstimateTest(candidate, compatible, unmatched, changed, query)).ToArray();
        var available = tests.Any(test => test.Availability == HistoryAvailability.Available);
        var reasons = available
            ? Array.Empty<string>()
            : ["No eligible historical evidence exists for the requested candidates."];

        return new HistoryEstimate(
            available ? HistoryAvailability.Available : HistoryAvailability.Unavailable,
            compatible.Count,
            unmatched,
            Array.AsReadOnly(tests),
            Array.AsReadOnly(reasons));
    }

    private static HistoryTestEstimate EstimateTest(
        string candidate,
        IReadOnlyList<HistoricalRun> compatible,
        int unmatchedRuns,
        HashSet<string> changed,
        HistoryQuery query)
    {
        var labels = new List<RunLabel>();
        var runtime = new Welford();
        var censored = 0;

        foreach (var run in compatible.OrderBy(run => run.CompletedAt).ThenBy(run => run.Provenance))
        {
            query.CancellationToken.ThrowIfCancellationRequested();
            if (!run.ChangedUnitIdentities.Any(changed.Contains))
            {
                continue;
            }

            var executions = run.Tests.Where(test => string.Equals(test.TestIdentity, candidate, StringComparison.Ordinal)).ToArray();
            if (executions.Length == 0 || executions.All(test => !test.Executed))
            {
                if (!run.IsCompleteSuite)
                {
                    censored++;
                }

                continue;
            }

            // A run is a correlation cap: duplicate instrumentation records add at most one label and one timing sample.
            var executed = executions.Where(test => test.Executed).ToArray();
            var positive = executed.Any(test =>
                test.Outcome is HistoricalTestOutcome.Failed or
                    HistoricalTestOutcome.TimedOut or
                    HistoricalTestOutcome.Crashed ||
                test.ObservedUnitIdentities.Any(changed.Contains));
            labels.Add(new RunLabel(positive, run.Provenance, run.CompletedAt));

            var duration = executed
                .Select(test => test.DurationMs)
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .OrderBy(value => value)
                .FirstOrDefault();
            if (duration > 0 || executed.Any(test => test.DurationMs == 0))
            {
                runtime.Add(duration);
            }
        }

        var local = labels.Count(label => label.Provenance == HistoryProvenance.Local);
        var official = labels.Count(label => label.Provenance == HistoryProvenance.OfficialCi);
        var imported = labels.Count(label => label.Provenance == HistoryProvenance.Imported);
        var positives = labels.Count(label => label.Positive);
        if (labels.Count == 0)
        {
            return new HistoryTestEstimate(
                candidate, HistoryAvailability.Unavailable, null, null, 0, 0, censored,
                local, official, imported, null,
                ["No eligible executed historical sample exists; selected-only omissions are censored."]);
        }

        var probability = Clamp((positives + PriorAlpha) / (labels.Count + PriorAlpha + PriorBeta));
        var confidence = Confidence(labels, compatible.Count, unmatchedRuns, query.AsOf);
        var stats = runtime.Count == 0 ? null : new RuntimeStatistics(runtime.Count, runtime.Mean, runtime.Variance);
        return new HistoryTestEstimate(
            candidate, HistoryAvailability.Available, probability, confidence, labels.Count, positives, censored,
            local, official, imported, stats, []);
    }

    private static double Confidence(
        IReadOnlyList<RunLabel> labels,
        int compatibleRuns,
        int unmatchedRuns,
        DateTimeOffset asOf)
    {
        var maturity = labels.Count / (labels.Count + 3d);
        var averageAgeDays = labels.Average(label => Math.Max(0d, (asOf - label.CompletedAt).TotalDays));
        var recency = 1d / (1d + averageAgeDays / 30d);
        var official = labels.Count(label => label.Provenance == HistoryProvenance.OfficialCi);
        var nonLocal = labels.Count - labels.Count(label => label.Provenance == HistoryProvenance.Local);
        var provenance = official > 0 ? 1d : nonLocal > 0 ? .8d : .6d;
        var candidateCoverage = labels.Count / (double)Math.Max(1, compatibleRuns);
        var compatibility = compatibleRuns / (double)Math.Max(1, compatibleRuns + unmatchedRuns);
        return Clamp(maturity * recency * provenance * candidateCoverage * compatibility);
    }

    private static bool IsTerminal(HistoryRunStatus status) =>
        status is HistoryRunStatus.Succeeded or HistoryRunStatus.Failed;

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    private sealed record RunLabel(bool Positive, HistoryProvenance Provenance, DateTimeOffset CompletedAt);

    private sealed class Welford
    {
        private double _mean;
        private double _sumSquares;
        public int Count { get; private set; }
        public double Mean => _mean;
        public double Variance => Count > 1 ? _sumSquares / (Count - 1) : 0d;

        public void Add(double value)
        {
            Count++;
            var delta = value - _mean;
            _mean += delta / Count;
            _sumSquares += delta * (value - _mean);
        }
    }
}
