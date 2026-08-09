namespace Merkle.Core.Domain;

public enum SourceUnitKind
{
    Repository,
    Language,
    Project,
    Path,
    File,
    Namespace,
    Type,
    Member
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted
}

public enum EvidenceKind
{
    Containment,
    StaticDependency,
    DynamicObservation,
    HistoricalAssociation,
    AncestorFallback,
    ConfiguredRule
}

public enum TerminalStatus
{
    Succeeded,
    Failed,
    PolicyFailed,
    Interrupted
}

public enum ErrorClass
{
    ConfigurationError,
    CapabilityError,
    AnalysisError,
    TestFailure,
    PolicyFailure,
    Interrupted
}

public enum ReportFormat
{
    Text,
    Json
}

public enum SnapshotEntryKind
{
    RegularFile,
    ExecutableFile,
    SymbolicLink,
    GitLink
}

public enum UnmappedBehavior
{
    Warn,
    Fail
}

public enum PlanRecommendation
{
    Selected,
    FullSuite,
    PlanOnly,
    DecisionNotConfigured,
    PolicyFailure
}

public sealed record SnapshotIdentity(string Value, string Reference, string Provider);

public sealed record SnapshotFile
{
    public SnapshotFile(
        string path,
        string contentHash,
        byte[] content,
        SnapshotEntryKind kind = SnapshotEntryKind.RegularFile,
        string mode = "100644")
    {
        Path = path;
        ContentHash = contentHash;
        Content = content.ToArray();
        Kind = kind;
        Mode = mode;
    }

    public string Path { get; }

    public string ContentHash { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public SnapshotEntryKind Kind { get; }

    public string Mode { get; }
}

public sealed record RepositorySnapshot
{
    public RepositorySnapshot(
        SnapshotIdentity identity,
        string repositoryRoot,
        string repositoryIdentity,
        IReadOnlyList<SnapshotFile> files)
    {
        Identity = identity;
        RepositoryRoot = repositoryRoot;
        RepositoryIdentity = repositoryIdentity;
        Files = Array.AsReadOnly(files.ToArray());
    }

    public SnapshotIdentity Identity { get; }

    public string RepositoryRoot { get; }

    public string RepositoryIdentity { get; }

    public IReadOnlyList<SnapshotFile> Files { get; }
}

public sealed record SnapshotPair(RepositorySnapshot Baseline, RepositorySnapshot Candidate);

public sealed record SourceUnit(
    string Identity,
    SourceUnitKind Kind,
    string Path,
    string ContentHash,
    string SemanticSignature);

public sealed record ChangedUnit(
    string Identity,
    SourceUnitKind Kind,
    ChangeKind ChangeKind,
    bool Mapped);

public sealed record TestDescriptor(string Identity, string DisplayName, string Framework);

public sealed record ImpactReason(
    EvidenceKind Kind,
    string ChangedUnit,
    IReadOnlyList<string> Path);

public sealed record RequestedTest(
    string Identity,
    string DisplayName,
    string Framework,
    IReadOnlyList<ImpactReason> Reasons,
    bool Mandatory = true);

public sealed record TestCandidate(
    TestDescriptor Test,
    bool Mandatory,
    double? ImpactProbability,
    double? EvidenceConfidence,
    double? ExpectedDurationMs,
    IReadOnlyList<ImpactReason> Reasons,
    PlanEstimateMetadata? Estimates = null);

public enum EstimateStatus
{
    Unavailable,
    Estimated
}

public sealed record EstimateDescriptor(
    EstimateStatus Status,
    string? Reason,
    string? ModelVersion,
    int? SampleCount)
{
    public static EstimateDescriptor Unavailable(string reason) =>
        new(EstimateStatus.Unavailable, reason, null, null);

    public static EstimateDescriptor Estimated(string modelVersion, int? sampleCount = null) =>
        new(EstimateStatus.Estimated, null, modelVersion, sampleCount);
}

public sealed record PlanEstimateMetadata(
    EstimateDescriptor ImpactProbability,
    EstimateDescriptor EvidenceConfidence,
    EstimateDescriptor ExpectedDuration)
{
    public static PlanEstimateMetadata Unavailable(string reason) => new(
        EstimateDescriptor.Unavailable(reason),
        EstimateDescriptor.Unavailable(reason),
        EstimateDescriptor.Unavailable(reason));
}

public sealed record PlannedTest
{
    public PlannedTest(
        string Identity,
        string DisplayName,
        bool Selected,
        double? ImpactProbability,
        double? EvidenceConfidence,
        double? ExpectedDurationMs,
        IReadOnlyList<ImpactReason> Reasons,
        string? ExcludedBy,
        PlanEstimateMetadata? Estimates = null)
    {
        this.Identity = Identity;
        this.DisplayName = DisplayName;
        this.Selected = Selected;
        this.ImpactProbability = ImpactProbability;
        this.EvidenceConfidence = EvidenceConfidence;
        this.ExpectedDurationMs = ExpectedDurationMs;
        this.Reasons = Reasons;
        this.ExcludedBy = ExcludedBy;
        this.Estimates = Estimates ?? new PlanEstimateMetadata(
            ImpactProbability.HasValue
                ? EstimateDescriptor.Estimated("unspecified")
                : EstimateDescriptor.Unavailable("no-compatible-history"),
            EvidenceConfidence.HasValue
                ? EstimateDescriptor.Estimated("unspecified")
                : EstimateDescriptor.Unavailable("no-compatible-history"),
            ExpectedDurationMs.HasValue
                ? EstimateDescriptor.Estimated("runtime-mean-v1")
                : EstimateDescriptor.Unavailable("no-comparable-runtime"));
    }

    public string Identity { get; init; }

    public string DisplayName { get; init; }

    public bool Selected { get; init; }

    public double? ImpactProbability { get; init; }

    public double? EvidenceConfidence { get; init; }

    public double? ExpectedDurationMs { get; init; }

    public IReadOnlyList<ImpactReason> Reasons { get; init; }

    public string? ExcludedBy { get; init; }

    public PlanEstimateMetadata Estimates { get; init; }
}

public sealed record LanguageSelection(string Language, string Profile);

public sealed record DetectionEvidence(string Kind, string Path, int? Count = null);

public sealed record DetectedLanguage(
    string Language,
    string Confidence,
    IReadOnlyList<DetectionEvidence> Evidence);

public sealed record PolicyConfiguration(
    double MinSavingsPercent,
    double? ConfidenceThreshold,
    string? OnLowConfidence,
    UnmappedBehavior Unmapped);

public sealed record PlanDecision(
    IReadOnlyList<PlannedTest> Tests,
    PlanRecommendation Recommendation,
    string DecisiveReason,
    double? SelectedMeanMs,
    double? FullMeanMs,
    double? SavingsPercent);
