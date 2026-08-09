using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Planning;

namespace Merkle.Tests.Planning;

public sealed class PlanPolicyTests
{
    [Fact]
    public void Apply_KeepsMandatoryRequestedTestSelected()
    {
        var test = Candidate("test:slow", mandatory: true, probability: 1, duration: 9_000);
        var result = new PlanPolicy().Apply([test], [], new PolicyConfiguration(
            MinSavingsPercent: 90, ConfidenceThreshold: null,
            OnLowConfidence: null, UnmappedBehavior.Warn));

        Assert.True(Assert.Single(result.Tests).Selected);
        Assert.Equal(PlanRecommendation.PlanOnly, result.Recommendation);
    }

    [Fact]
    public void Apply_PedanticUnmappedCreatesPolicyFailure()
    {
        var changed = new ChangedUnit("file:new.cs", SourceUnitKind.File, ChangeKind.Added, false);

        var error = Assert.Throws<PolicyException>(() => new PlanPolicy().Apply([], [changed],
            new PolicyConfiguration(30, null, null, UnmappedBehavior.Fail)));

        Assert.Equal("UnmappedSource", error.Code);
    }

    [Fact]
    public void Apply_RejectsSavingsPercentageOutsideValidRange()
    {
        var error = Assert.Throws<ConfigurationException>(() => new PlanPolicy().Apply([], [],
            new PolicyConfiguration(101, null, null, UnmappedBehavior.Warn)));

        Assert.Equal("InvalidSavingsThreshold", error.Code);
    }

    [Fact]
    public void Apply_RequiresExplicitConfidencePolicyForDiscretionaryExecution()
    {
        var result = new PlanPolicy().Apply(
            [Candidate("test:mandatory", true, 1, 10), Candidate("test:possible", false, .7, 10, .6)],
            [],
            new PolicyConfiguration(30, null, null, UnmappedBehavior.Warn));

        Assert.Equal(PlanRecommendation.DecisionNotConfigured, result.Recommendation);
        Assert.All(result.Tests, test => Assert.True(test.Selected));
    }

    [Fact]
    public void Apply_UsesFullSuiteWhenSavingsAreBelowConfiguredFloor()
    {
        var result = new PlanPolicy().Apply(
            [Candidate("test:mandatory", true, 1, 80), Candidate("test:optional", false, .5, 20, .2)],
            [],
            new PolicyConfiguration(30, .8, "plan-only", UnmappedBehavior.Warn));

        Assert.Equal(PlanRecommendation.FullSuite, result.Recommendation);
        Assert.All(result.Tests, test => Assert.True(test.Selected));
        Assert.Equal(0, result.SavingsPercent);
    }

    [Fact]
    public void Apply_SelectsHighConfidenceCandidatesAndKeepsMandatorySlowTests()
    {
        var result = new PlanPolicy().Apply(
            [
                Candidate("test:mandatory", true, 1, 60, .1),
                Candidate("test:high", false, .9, 10, .9),
                Candidate("test:low", false, .2, 100, .2)
            ],
            [],
            new PolicyConfiguration(30, .8, "plan-only", UnmappedBehavior.Warn));

        Assert.Equal(PlanRecommendation.PlanOnly, result.Recommendation);
        Assert.True(result.Tests.Single(test => test.Identity == "test:mandatory").Selected);
        Assert.True(result.Tests.Single(test => test.Identity == "test:high").Selected);
        Assert.False(result.Tests.Single(test => test.Identity == "test:low").Selected);
        Assert.Equal("low-confidence", result.Tests.Single(test => test.Identity == "test:low").ExcludedBy);
    }

    [Fact]
    public void Apply_FullSuiteAndFailActionsHandleLowConfidenceExplicitly()
    {
        var candidates = new[]
        {
            Candidate("test:mandatory", true, 1, 10),
            Candidate("test:low", false, .3, 90, .2)
        };
        var full = new PlanPolicy().Apply(candidates, [],
            new PolicyConfiguration(30, .8, "full-suite", UnmappedBehavior.Warn));
        Assert.Equal(PlanRecommendation.FullSuite, full.Recommendation);

        var error = Assert.Throws<PolicyException>(() => new PlanPolicy().Apply(candidates, [],
            new PolicyConfiguration(30, .8, "fail", UnmappedBehavior.Warn)));
        Assert.Equal("LowConfidence", error.Code);
    }

    [Theory]
    [InlineData(-0.1, 0.5, "plan-only", "InvalidEvidenceEstimate")]
    [InlineData(0.5, 1.1, "plan-only", "InvalidEvidenceEstimate")]
    [InlineData(0.5, 0.5, "unknown", "InvalidLowConfidenceAction")]
    public void Apply_RejectsInvalidEvidenceAndPolicy(
        double probability,
        double confidence,
        string action,
        string code)
    {
        var error = Assert.Throws<ConfigurationException>(() => new PlanPolicy().Apply(
            [Candidate("test:a", false, probability, 1, confidence)],
            [],
            new PolicyConfiguration(30, .5, action, UnmappedBehavior.Warn)));

        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData(double.NaN, .5, "InvalidEvidenceEstimate")]
    [InlineData(.5, double.PositiveInfinity, "InvalidEvidenceEstimate")]
    public void Apply_RejectsNonFiniteEvidence(double probability, double confidence, string code)
    {
        var error = Assert.Throws<ConfigurationException>(() => new PlanPolicy().Apply(
            [Candidate("test:a", false, probability, 1, confidence)], [],
            new PolicyConfiguration(30, .5, "plan-only", UnmappedBehavior.Warn)));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Apply_ReturnsUnknownSavingsWhenAnyDurationIsUnavailable()
    {
        var candidate = new TestCandidate(new TestDescriptor("test:a", "A", "xunit"), false, .5, .9,
            null, [new ImpactReason(EvidenceKind.StaticDependency, "file:a", ["file:a", "test:a"])]);

        var decision = new PlanPolicy().Apply([candidate], [], new PolicyConfiguration(30, .8, "plan-only", UnmappedBehavior.Warn));

        Assert.Equal(PlanRecommendation.Selected, decision.Recommendation);
        Assert.Null(decision.SavingsPercent);
        Assert.Null(decision.SelectedMeanMs);
    }

    [Fact]
    public void Apply_RejectsInvalidConfidenceThresholdAndRuntime()
    {
        var invalidThreshold = Assert.Throws<ConfigurationException>(() => new PlanPolicy().Apply([], [],
            new PolicyConfiguration(30, 1.1, "plan-only", UnmappedBehavior.Warn)));
        Assert.Equal("InvalidConfidenceThreshold", invalidThreshold.Code);

        var invalidRuntime = Assert.Throws<ConfigurationException>(() => new PlanPolicy().Apply(
            [Candidate("test:a", false, .5, -1, .9)], [],
            new PolicyConfiguration(30, .5, "plan-only", UnmappedBehavior.Warn)));
        Assert.Equal("InvalidRuntimeEstimate", invalidRuntime.Code);
    }

    private static TestCandidate Candidate(
        string identity, bool mandatory, double probability, double duration, double confidence = 1) =>
        new(new TestDescriptor(identity, identity, "xunit"), mandatory, probability, confidence,
            duration, [new ImpactReason(EvidenceKind.StaticDependency, "file:a", ["file:a", identity])]);
}
