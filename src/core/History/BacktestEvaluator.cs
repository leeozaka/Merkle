namespace Merkle.Core.History;

public sealed record BacktestPrediction(
    string TestIdentity,
    bool Selected,
    double? ImpactProbability,
    double? EvidenceConfidence,
    double? ExpectedDurationMs);

public sealed record BacktestOutcome(
    string TestIdentity,
    HistoricalTestOutcome Outcome,
    double DurationMs);

public sealed record BacktestCase(
    DateTimeOffset PlannedAt,
    DateTimeOffset EvidenceCutoff,
    IReadOnlyList<BacktestPrediction> Predictions,
    IReadOnlyList<BacktestOutcome> CompleteSuiteOutcomes,
    int CompatibleHistoryRuns,
    int UnmatchedHistoryRuns);

public sealed record CalibrationBucket(
    int LowerPercentInclusive,
    int UpperPercentInclusive,
    int SampleCount,
    double MeanPrediction,
    double ObservedFailureRate);

public sealed record BacktestReport(
    int CaseCount,
    int FailingTestCount,
    int SelectedFailingTestCount,
    double? FailingTestRecall,
    double? SelectedToFullRuntimeRatio,
    int ColdStartCaseCount,
    int CompatibleHistoryRuns,
    int UnmatchedHistoryRuns,
    IReadOnlyList<CalibrationBucket> ProbabilityCalibration);

public interface IBacktestEvaluator
{
    BacktestReport Evaluate(IReadOnlyList<BacktestCase> cases);
}

public sealed class BacktestEvaluator : IBacktestEvaluator
{
    public BacktestReport Evaluate(IReadOnlyList<BacktestCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var ordered = cases.OrderBy(item => item.PlannedAt).ToArray();
        var failing = 0;
        var selectedFailing = 0;
        var selectedDuration = 0d;
        var fullDuration = 0d;
        var coldStarts = 0;
        var compatible = 0;
        var unmatched = 0;
        var calibration = new List<(double Prediction, bool Failed)>();

        foreach (var item in ordered)
        {
            Validate(item);
            if (item.CompatibleHistoryRuns == 0)
            {
                coldStarts++;
            }

            compatible = checked(compatible + item.CompatibleHistoryRuns);
            unmatched = checked(unmatched + item.UnmatchedHistoryRuns);
            var predictions = item.Predictions.ToDictionary(
                prediction => prediction.TestIdentity,
                StringComparer.Ordinal);
            foreach (var outcome in item.CompleteSuiteOutcomes)
            {
                fullDuration += outcome.DurationMs;
                var failed = outcome.Outcome is HistoricalTestOutcome.Failed or
                    HistoricalTestOutcome.TimedOut or HistoricalTestOutcome.Crashed;
                if (failed)
                {
                    failing++;
                }

                if (!predictions.TryGetValue(outcome.TestIdentity, out var prediction))
                {
                    continue;
                }

                if (prediction.Selected)
                {
                    selectedDuration += outcome.DurationMs;
                    if (failed)
                    {
                        selectedFailing++;
                    }
                }

                if (prediction.ImpactProbability is { } probability)
                {
                    calibration.Add((probability, failed));
                }
            }
        }

        return new BacktestReport(
            ordered.Length,
            failing,
            selectedFailing,
            failing == 0 ? null : selectedFailing / (double)failing,
            fullDuration == 0 ? null : selectedDuration / fullDuration,
            coldStarts,
            compatible,
            unmatched,
            BuildCalibration(calibration));
    }

    private static IReadOnlyList<CalibrationBucket> BuildCalibration(
        IEnumerable<(double Prediction, bool Failed)> samples) =>
        [.. samples
            .GroupBy(sample => Math.Min(9, (int)(sample.Prediction * 10)))
            .OrderBy(group => group.Key)
            .Select(group => new CalibrationBucket(
                group.Key * 10,
                group.Key == 9 ? 100 : group.Key * 10 + 9,
                group.Count(),
                group.Average(sample => sample.Prediction),
                group.Count(sample => sample.Failed) / (double)group.Count()))];

    private static void Validate(BacktestCase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.EvidenceCutoff > item.PlannedAt)
        {
            throw new ArgumentException(
                "Backtests cannot use evidence recorded after the plan time.",
                nameof(item));
        }

        if (item.CompatibleHistoryRuns < 0 || item.UnmatchedHistoryRuns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "History counts cannot be negative.");
        }

        if (item.Predictions.Select(value => value.TestIdentity).Distinct(StringComparer.Ordinal).Count() !=
            item.Predictions.Count)
        {
            throw new ArgumentException("Backtest predictions must have unique test identities.", nameof(item));
        }

        foreach (var prediction in item.Predictions)
        {
            if (prediction.ImpactProbability is { } probability &&
                (!double.IsFinite(probability) || probability is < 0 or > 1))
            {
                throw new ArgumentOutOfRangeException(nameof(item), "Probability must be finite and between 0 and 1.");
            }
        }

        foreach (var outcome in item.CompleteSuiteOutcomes)
        {
            if (!double.IsFinite(outcome.DurationMs) || outcome.DurationMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(item), "Outcome duration must be finite and non-negative.");
            }
        }
    }
}
