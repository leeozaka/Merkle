namespace Merkle.Core.History;

public enum HistoryProvenance
{
    Local,
    OfficialCi,
    Imported
}

public enum HistoryRunStatus
{
    Succeeded,
    Failed,
    Interrupted,
    InProgress
}

public enum HistoricalTestOutcome
{
    Passed,
    Failed,
    Skipped,
    TimedOut,
    Crashed,
    Cancelled
}

public enum HistoryAvailability
{
    Available,
    Unavailable
}

public sealed record HistoryCompatibilityKey(
    string RepositoryIdentity,
    string SchemaVersion,
    string AdapterIdentity,
    string BuildFingerprintFamily)
{
    public bool Matches(HistoryCompatibilityKey other) =>
        other is not null &&
        string.Equals(RepositoryIdentity, other.RepositoryIdentity, StringComparison.Ordinal) &&
        string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(AdapterIdentity, other.AdapterIdentity, StringComparison.Ordinal) &&
        string.Equals(BuildFingerprintFamily, other.BuildFingerprintFamily, StringComparison.Ordinal);
}

public sealed record HistoricalTestExecution
{
    public string TestIdentity { get; }
    public bool Executed { get; }
    public HistoricalTestOutcome Outcome { get; }
    public double? DurationMs { get; }
    public IReadOnlyList<string> ObservedUnitIdentities { get; }

    public HistoricalTestExecution(
        string testIdentity,
        bool executed,
        HistoricalTestOutcome outcome,
        double? durationMs,
        IReadOnlyList<string>? observedUnitIdentities)
    {
        if (string.IsNullOrWhiteSpace(testIdentity))
        {
            throw new ArgumentException("Test identity is required.", nameof(testIdentity));
        }

        if (durationMs is < 0 or double.NaN or double.PositiveInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs), "Duration must be finite and non-negative.");
        }

        TestIdentity = testIdentity;
        Executed = executed;
        Outcome = outcome;
        DurationMs = durationMs;
        ObservedUnitIdentities = Array.AsReadOnly((observedUnitIdentities ?? []).ToArray());
    }
}

public sealed record HistoricalRun
{
    public HistoryCompatibilityKey Compatibility { get; }
    public HistoryProvenance Provenance { get; }
    public HistoryRunStatus Status { get; }
    public bool IsCompleteSuite { get; }
    public DateTimeOffset CompletedAt { get; }
    public IReadOnlyList<string> ChangedUnitIdentities { get; }
    public IReadOnlyList<HistoricalTestExecution> Tests { get; }

    public HistoricalRun(
        HistoryCompatibilityKey compatibility,
        HistoryProvenance provenance,
        HistoryRunStatus status,
        bool isCompleteSuite,
        DateTimeOffset completedAt,
        IReadOnlyList<string>? changedUnitIdentities,
        IReadOnlyList<HistoricalTestExecution>? tests)
    {
        Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        Provenance = provenance;
        Status = status;
        IsCompleteSuite = isCompleteSuite;
        CompletedAt = completedAt;
        ChangedUnitIdentities = Array.AsReadOnly((changedUnitIdentities ?? []).ToArray());
        Tests = Array.AsReadOnly((tests ?? []).ToArray());
    }
}

public sealed record HistoryQuery
{
    public HistoryCompatibilityKey Compatibility { get; }
    public IReadOnlyList<string> ChangedUnitIdentities { get; }
    public IReadOnlyList<string> CandidateTestIdentities { get; }
    public IReadOnlyList<HistoricalRun> Runs { get; }
    public DateTimeOffset AsOf { get; }
    public CancellationToken CancellationToken { get; }

    public HistoryQuery(
        HistoryCompatibilityKey compatibility,
        IEnumerable<string>? changedUnitIdentities,
        IEnumerable<string>? candidateTestIdentities,
        IEnumerable<HistoricalRun>? runs,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        ChangedUnitIdentities = Array.AsReadOnly((changedUnitIdentities ?? []).ToArray());
        CandidateTestIdentities = Array.AsReadOnly((candidateTestIdentities ?? []).ToArray());
        Runs = Array.AsReadOnly((runs ?? []).ToArray());
        AsOf = asOf;
        CancellationToken = cancellationToken;
    }
}

public sealed record RuntimeStatistics(int SampleCount, double MeanMs, double VarianceMsSquared);

public sealed record HistoryTestEstimate(
    string TestIdentity,
    HistoryAvailability Availability,
    double? ImpactProbability,
    double? EvidenceConfidence,
    int EligibleRunCount,
    int PositiveRunCount,
    int CensoredRunCount,
    int LocalRunCount,
    int OfficialCiRunCount,
    int ImportedRunCount,
    RuntimeStatistics? Runtime,
    IReadOnlyList<string> Reasons);

public sealed record HistoryEstimate(
    HistoryAvailability Availability,
    int CompatibleRunCount,
    int UnmatchedRunCount,
    IReadOnlyList<HistoryTestEstimate> Tests,
    IReadOnlyList<string> Reasons);

public interface IHistoryModel
{
    HistoryEstimate Estimate(HistoryQuery query);
}
