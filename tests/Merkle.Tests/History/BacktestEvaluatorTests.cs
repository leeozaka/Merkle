using Merkle.Core.History;

namespace Merkle.Tests.History;

public sealed class BacktestEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ReportsRecallRuntimeCalibrationAndHistoryConditions()
    {
        var report = new BacktestEvaluator().Evaluate([
            Case(
                [Prediction("test:a", true, .9), Prediction("test:b", false, .2)],
                [Outcome("test:a", HistoricalTestOutcome.Failed, 10), Outcome("test:b", HistoricalTestOutcome.Passed, 30)],
                compatible: 0,
                unmatched: 2),
            Case(
                [Prediction("test:a", false, .8), Prediction("test:b", true, .1)],
                [Outcome("test:a", HistoricalTestOutcome.Failed, 20), Outcome("test:b", HistoricalTestOutcome.Passed, 20)],
                compatible: 3,
                unmatched: 1)
        ]);

        Assert.Equal(2, report.CaseCount);
        Assert.Equal(2, report.FailingTestCount);
        Assert.Equal(1, report.SelectedFailingTestCount);
        Assert.Equal(.5, report.FailingTestRecall);
        Assert.Equal(.375, report.SelectedToFullRuntimeRatio);
        Assert.Equal(1, report.ColdStartCaseCount);
        Assert.Equal(3, report.CompatibleHistoryRuns);
        Assert.Equal(3, report.UnmatchedHistoryRuns);
        Assert.Equal(4, report.ProbabilityCalibration.Sum(bucket => bucket.SampleCount));
    }

    [Fact]
    public void Evaluate_ReturnsUnavailableRatiosWhenNoFailuresOrRuntimeExist()
    {
        var report = new BacktestEvaluator().Evaluate([
            Case([Prediction("test:a", true, null)], [Outcome("test:a", HistoricalTestOutcome.Passed, 0)])
        ]);

        Assert.Null(report.FailingTestRecall);
        Assert.Null(report.SelectedToFullRuntimeRatio);
        Assert.Empty(report.ProbabilityCalibration);
    }

    [Fact]
    public void Evaluate_RejectsFutureEvidenceDuplicatePredictionsAndInvalidValues()
    {
        var evaluator = new BacktestEvaluator();
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate([
            Case([], [], cutoff: Now.AddMinutes(1))
        ]));
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate([
            Case([Prediction("test:a", true, .5), Prediction("test:a", false, .5)], [])
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => evaluator.Evaluate([
            Case([Prediction("test:a", true, 2)], [])
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => evaluator.Evaluate([
            Case([], [Outcome("test:a", HistoricalTestOutcome.Passed, -1)])
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => evaluator.Evaluate([
            Case([], [], compatible: -1)
        ]));
    }

    private static BacktestCase Case(
        IReadOnlyList<BacktestPrediction> predictions,
        IReadOnlyList<BacktestOutcome> outcomes,
        int compatible = 1,
        int unmatched = 0,
        DateTimeOffset? cutoff = null) =>
        new(Now, cutoff ?? Now.AddMinutes(-1), predictions, outcomes, compatible, unmatched);

    private static BacktestPrediction Prediction(string id, bool selected, double? probability) =>
        new(id, selected, probability, .5, 10);

    private static BacktestOutcome Outcome(string id, HistoricalTestOutcome outcome, double duration) =>
        new(id, outcome, duration);
}
