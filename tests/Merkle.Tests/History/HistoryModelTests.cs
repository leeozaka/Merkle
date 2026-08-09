using Merkle.Core.History;

namespace Merkle.Tests.History;

public sealed class HistoryModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Estimate_ExcludesNonterminalAndIncompatibleRunsAsUnmatched()
    {
        var result = Estimate([
            Run(status: HistoryRunStatus.Interrupted),
            Run(key: new HistoryCompatibilityKey("other", "1", "dotnet", "fp")),
            Run()]);

        Assert.Equal(1, result.CompatibleRunCount);
        Assert.Equal(2, result.UnmatchedRunCount);
        Assert.Equal(HistoryAvailability.Available, result.Availability);
    }

    [Fact]
    public void Estimate_SeparatesLocalOfficialAndImportedEvidence()
    {
        var result = Estimate([
            Run(provenance: HistoryProvenance.Local),
            Run(provenance: HistoryProvenance.OfficialCi),
            Run(provenance: HistoryProvenance.Imported)]);

        var test = Assert.Single(result.Tests);
        Assert.Equal(1, test.LocalRunCount);
        Assert.Equal(1, test.OfficialCiRunCount);
        Assert.Equal(1, test.ImportedRunCount);
    }

    [Fact]
    public void Estimate_TreatsUnexecutedSelectedOnlyTestAsCensored()
    {
        var result = Estimate([Run(complete: false, execution: Execution(executed: false))]);

        var test = Assert.Single(result.Tests);
        Assert.Equal(HistoryAvailability.Unavailable, test.Availability);
        Assert.Equal(1, test.CensoredRunCount);
        Assert.Equal(0, test.EligibleRunCount);
        Assert.Null(test.ImpactProbability);
        Assert.Null(test.Runtime);
    }

    [Fact]
    public void Estimate_UsesBetaOneOneSmoothingWithMonotonicBoundedProbability()
    {
        var low = Assert.Single(Estimate([Run(execution: Execution(observed: []))]).Tests);
        var high = Assert.Single(Estimate([
            Run(execution: Execution(observed: [])),
            Run(execution: Execution(observed: ["unit:changed"]))]).Tests);

        Assert.Equal(1d / 3d, low.ImpactProbability!.Value, 12);
        Assert.Equal(1d / 2d, high.ImpactProbability!.Value, 12);
        Assert.True(high.ImpactProbability > low.ImpactProbability);
        Assert.InRange(low.ImpactProbability.Value, 0, 1);
        Assert.InRange(high.ImpactProbability.Value, 0, 1);
    }

    [Fact]
    public void Estimate_CapsDuplicateEvidenceAndRuntimeByRun()
    {
        var duplicate = Run(executions: [
            Execution(observed: ["unit:changed"], duration: 10),
            Execution(observed: ["unit:changed"], duration: 100)]);

        var test = Assert.Single(Estimate([duplicate]).Tests);

        Assert.Equal(1, test.EligibleRunCount);
        Assert.Equal(1, test.PositiveRunCount);
        Assert.Equal(1, test.Runtime!.SampleCount);
        Assert.Equal(10, test.Runtime.MeanMs);
    }

    [Fact]
    public void Estimate_ReturnsUnavailableInsteadOfFakeZerosForColdStart()
    {
        var result = Estimate([]);
        var test = Assert.Single(result.Tests);

        Assert.Equal(HistoryAvailability.Unavailable, result.Availability);
        Assert.Null(test.ImpactProbability);
        Assert.Null(test.EvidenceConfidence);
        Assert.Null(test.Runtime);
        Assert.Contains(result.Reasons, reason => reason.Contains("No eligible", StringComparison.Ordinal));
    }

    [Fact]
    public void Estimate_WeakensConfidenceForOldUnmatchedAndLocalOnlyEvidence()
    {
        var freshOfficial = Assert.Single(Estimate([Run(provenance: HistoryProvenance.OfficialCi)]).Tests);
        var weak = Assert.Single(Estimate([
            Run(provenance: HistoryProvenance.Local, completedAt: Now.AddDays(-180)),
            Run(status: HistoryRunStatus.Interrupted)]).Tests);

        Assert.True(freshOfficial.EvidenceConfidence > weak.EvidenceConfidence);
        Assert.InRange(weak.EvidenceConfidence!.Value, 0, 1);
    }

    [Fact]
    public void Estimate_CalculatesStableWelfordRuntimeStatistics()
    {
        var result = Estimate([
            Run(execution: Execution(duration: 10)),
            Run(execution: Execution(duration: 20)),
            Run(execution: Execution(duration: 30))]);

        var runtime = Assert.Single(result.Tests).Runtime!;
        Assert.Equal(3, runtime.SampleCount);
        Assert.Equal(20, runtime.MeanMs);
        Assert.Equal(100, runtime.VarianceMsSquared);
    }

    [Fact]
    public void Estimate_DefinesPositiveEventFromObservedChangedUnitOrFailure()
    {
        var result = Estimate([
            Run(execution: Execution(observed: [])),
            Run(execution: Execution(outcome: HistoricalTestOutcome.Failed, observed: []))]);

        var test = Assert.Single(result.Tests);
        Assert.Equal(1, test.PositiveRunCount);
        Assert.Equal(.5, test.ImpactProbability!.Value, 12);
    }

    [Fact]
    public void Estimate_OrdersCandidatesDeterministicallyAndIgnoresDuplicateCandidates()
    {
        var query = new HistoryQuery(Key, ["unit:changed"], ["test:z", "test:a", "test:z"],
            [Run(test: "test:z"), Run(test: "test:a")], Now);

        var result = new HistoryModel().Estimate(query);

        Assert.Equal(["test:a", "test:z"], result.Tests.Select(test => test.TestIdentity));
    }

    [Fact]
    public void Estimate_ValidatesNullAndHonorsCancellation()
    {
        var model = new HistoryModel();
        Assert.Throws<ArgumentNullException>(() => model.Estimate(null!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = new HistoryQuery(Key, ["unit:changed"], ["test:a"], [], Now, cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => model.Estimate(query));
    }

    [Fact]
    public void Estimate_HandlesFailedTerminalRunAndCompleteSuiteAbsenceWithoutCensoring()
    {
        var result = Estimate([
            Run(status: HistoryRunStatus.Failed, execution: Execution(outcome: HistoricalTestOutcome.Failed)),
            Run(complete: true, execution: Execution(executed: false))]);

        var test = Assert.Single(result.Tests);
        Assert.Equal(1, test.EligibleRunCount);
        Assert.Equal(1, test.PositiveRunCount);
        Assert.Equal(0, test.CensoredRunCount);
    }

    [Fact]
    public void Estimate_HandlesZeroDurationAndSamplesWithoutRelevantChangedUnit()
    {
        var notRelevant = new HistoricalRun(Key, HistoryProvenance.OfficialCi, HistoryRunStatus.Succeeded,
            true, Now, ["unit:other"], [Execution(duration: 50)]);
        var result = Estimate([Run(execution: Execution(duration: 0)), notRelevant]);

        var test = Assert.Single(result.Tests);
        Assert.Equal(1, test.EligibleRunCount);
        Assert.Equal(1, test.Runtime!.SampleCount);
        Assert.Equal(0, test.Runtime.MeanMs);
    }

    [Fact]
    public void Estimate_RejectsInvalidExecutionValuesAndCopiesInputCollections()
    {
        Assert.Throws<ArgumentException>(() => new HistoricalTestExecution(" ", true, HistoricalTestOutcome.Passed, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalTestExecution("test:a", true, HistoricalTestOutcome.Passed, -1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalTestExecution("test:a", true, HistoricalTestOutcome.Passed, double.NaN, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalTestExecution("test:a", true, HistoricalTestOutcome.Passed, double.PositiveInfinity, []));

        var units = new List<string> { "unit:changed" };
        var execution = new HistoricalTestExecution("test:a", true, HistoricalTestOutcome.Passed, 1, units);
        units.Clear();
        Assert.Single(execution.ObservedUnitIdentities);
    }

    [Fact]
    public void Estimate_TreatsNullRunAndWhitespaceCandidateAsUnmatchedOrIgnored()
    {
        var query = new HistoryQuery(Key, ["unit:changed"], [" ", "test:a"],
            (IReadOnlyList<HistoricalRun>)[null!, Run()], Now);

        var result = new HistoryModel().Estimate(query);

        Assert.Equal(1, result.UnmatchedRunCount);
        Assert.Equal(["test:a"], result.Tests.Select(test => test.TestIdentity));
    }

    [Theory]
    [InlineData(HistoricalTestOutcome.TimedOut)]
    [InlineData(HistoricalTestOutcome.Crashed)]
    public void Estimate_TreatsAbnormalTestTerminationAsPositive(HistoricalTestOutcome outcome)
    {
        var test = Assert.Single(Estimate([Run(execution: Execution(outcome: outcome, observed: []))]).Tests);

        Assert.Equal(1, test.PositiveRunCount);
        Assert.Equal(2d / 3d, test.ImpactProbability!.Value, 12);
    }

    [Fact]
    public void Estimate_LeavesACompatibleButIrrelevantRunOutOfTheEvidence()
    {
        var irrelevant = new HistoricalRun(Key, HistoryProvenance.OfficialCi, HistoryRunStatus.Succeeded,
            true, Now, ["unit:other"], [Execution()]);

        var result = Estimate([irrelevant]);

        Assert.Equal(1, result.CompatibleRunCount);
        Assert.Equal(HistoryAvailability.Unavailable, Assert.Single(result.Tests).Availability);
    }

    private static readonly HistoryCompatibilityKey Key = new("repo", "1", "dotnet", "fp");

    private static HistoryEstimate Estimate(IReadOnlyList<HistoricalRun> runs) =>
        new HistoryModel().Estimate(new HistoryQuery(Key, ["unit:changed"], ["test:a"], runs, Now));

    private static HistoricalRun Run(
        HistoryCompatibilityKey? key = null,
        HistoryProvenance provenance = HistoryProvenance.OfficialCi,
        HistoryRunStatus status = HistoryRunStatus.Succeeded,
        bool complete = true,
        DateTimeOffset? completedAt = null,
        string test = "test:a",
        HistoricalTestExecution? execution = null,
        IReadOnlyList<HistoricalTestExecution>? executions = null) =>
        new(key ?? Key, provenance, status, complete, completedAt ?? Now, ["unit:changed"],
            executions ?? [execution ?? Execution(test: test)]);

    private static HistoricalTestExecution Execution(
        string test = "test:a",
        bool executed = true,
        HistoricalTestOutcome outcome = HistoricalTestOutcome.Passed,
        double? duration = 10,
        IReadOnlyList<string>? observed = null) =>
        new(test, executed, outcome, duration, observed ?? ["unit:changed"]);
}
