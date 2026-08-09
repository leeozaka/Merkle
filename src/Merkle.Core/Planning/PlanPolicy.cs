using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Core.Planning;

public interface IPlanPolicy
{
    PlanDecision Apply(
        IReadOnlyList<TestCandidate> candidates,
        IReadOnlyList<ChangedUnit> unmappedUnits,
        PolicyConfiguration configuration);
}

public sealed class PlanPolicy : IPlanPolicy
{
    public PlanDecision Apply(
        IReadOnlyList<TestCandidate> candidates,
        IReadOnlyList<ChangedUnit> unmappedUnits,
        PolicyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(unmappedUnits);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.MinSavingsPercent is < 0 or > 100)
        {
            throw new ConfigurationException(
                "InvalidSavingsThreshold",
                "Minimum savings percent must be between 0 and 100.");
        }

        ValidateCandidates(candidates);

        if (unmappedUnits.Count > 0 && configuration.Unmapped == UnmappedBehavior.Fail)
        {
            throw new PolicyException(
                "UnmappedSource",
                $"The plan contains {unmappedUnits.Count} unmapped changed source unit(s).");
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Mandatory)
            .ThenByDescending(candidate => candidate.ImpactProbability ?? -1)
            .ThenByDescending(candidate => candidate.EvidenceConfidence ?? -1)
            .ThenBy(candidate => candidate.ExpectedDurationMs ?? double.MaxValue)
            .ThenBy(candidate => candidate.Test.Identity, StringComparer.Ordinal)
            .ToArray();

        var hasAutomaticPolicy = configuration.ConfidenceThreshold.HasValue &&
                                 configuration.OnLowConfidence is not null;
        if (configuration.ConfidenceThreshold is < 0 or > 1)
        {
            throw new ConfigurationException(
                "InvalidConfidenceThreshold",
                "Confidence threshold must be between 0 and 1.");
        }

        if (configuration.OnLowConfidence is not null &&
            configuration.OnLowConfidence is not ("full-suite" or "plan-only" or "fail"))
        {
            throw new ConfigurationException(
                "InvalidLowConfidenceAction",
                "Low-confidence action must be 'full-suite', 'plan-only', or 'fail'.");
        }

        var lowConfidence = configuration.ConfidenceThreshold is { } threshold
            ? ordered.Where(candidate =>
                !candidate.Mandatory &&
                (candidate.EvidenceConfidence is null || candidate.EvidenceConfidence < threshold)).ToArray()
            : [];
        if (lowConfidence.Length > 0 && configuration.OnLowConfidence == "fail")
        {
            throw new PolicyException(
                "LowConfidence",
                $"The plan contains {lowConfidence.Length} discretionary test(s) below the configured confidence threshold.");
        }

        var forceFullSuite = lowConfidence.Length > 0 && configuration.OnLowConfidence == "full-suite";
        var planned = ordered
            .Select(candidate =>
            {
                var selected = candidate.Mandatory || forceFullSuite ||
                               configuration.ConfidenceThreshold is null ||
                               candidate.EvidenceConfidence >= configuration.ConfidenceThreshold.Value;
                return new PlannedTest(
                candidate.Test.Identity,
                candidate.Test.DisplayName,
                selected,
                candidate.ImpactProbability,
                candidate.EvidenceConfidence,
                candidate.ExpectedDurationMs,
                candidate.Reasons,
                selected ? null : "low-confidence",
                candidate.Estimates);
            })
            .ToArray();

        var selectedMean = SumComparable(planned.Where(test => test.Selected));
        var fullMean = SumComparable(planned);
        double? savings = selectedMean.HasValue && fullMean is > 0
            ? Math.Clamp((fullMean.Value - selectedMean.Value) * 100d / fullMean.Value, 0d, 100d)
            : null;

        if (hasAutomaticPolicy && savings.HasValue && savings.Value < configuration.MinSavingsPercent)
        {
            var fullSuite = planned.Select(test => test with { Selected = true, ExcludedBy = null }).ToArray();
            return new PlanDecision(
                fullSuite,
                PlanRecommendation.FullSuite,
                $"Expected savings of {savings.Value:F2}% are below the configured {configuration.MinSavingsPercent:F2}% floor.",
                fullMean,
                fullMean,
                0);
        }

        if (forceFullSuite)
        {
            return new PlanDecision(
                planned,
                PlanRecommendation.FullSuite,
                "At least one discretionary test is below the configured confidence threshold; policy requires the full suite.",
                fullMean,
                fullMean,
                0);
        }

        var recommendation = hasAutomaticPolicy
            ? configuration.OnLowConfidence == "plan-only" && lowConfidence.Length > 0
                ? PlanRecommendation.PlanOnly
                : PlanRecommendation.Selected
            : ordered.Any(candidate => !candidate.Mandatory)
                ? PlanRecommendation.DecisionNotConfigured
                : PlanRecommendation.PlanOnly;
        var reason = recommendation switch
        {
            PlanRecommendation.Selected => "The configured confidence action permits selected execution.",
            PlanRecommendation.DecisionNotConfigured =>
                "A confidence threshold and low-confidence action are required before automatic selected execution.",
            _ when lowConfidence.Length > 0 =>
                "Low-confidence discretionary tests are shown for review; policy does not authorize automatic execution.",
            _ => "The plan contains only mandatory mappings and remains advisory."
        };

        return new PlanDecision(
            planned,
            recommendation,
            reason,
            selectedMean,
            fullMean,
            savings);
    }

    private static double? SumComparable(IEnumerable<PlannedTest> tests)
    {
        var values = tests.Select(test => test.ExpectedDurationMs).ToArray();
        return values.All(value => value.HasValue)
            ? values.Sum(value => value!.Value)
            : null;
    }

    private static void ValidateCandidates(IEnumerable<TestCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.ImpactProbability is { } probability &&
                (!double.IsFinite(probability) || probability is < 0 or > 1) ||
                candidate.EvidenceConfidence is { } confidence &&
                (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            {
                throw new ConfigurationException(
                    "InvalidEvidenceEstimate",
                    "Impact probability and evidence confidence must be finite values between 0 and 1.");
            }

            if (candidate.ExpectedDurationMs is { } duration &&
                (!double.IsFinite(duration) || duration < 0))
            {
                throw new ConfigurationException(
                    "InvalidRuntimeEstimate",
                    "Expected test duration must be a finite non-negative value.");
            }
        }
    }
}
