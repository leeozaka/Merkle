using Merkle.Core.Domain;

namespace Merkle.Core.Reporting;

public sealed record ReportAdapter(
    string Language,
    string Producer,
    string Version,
    string ProtocolVersion,
    string UnitIdentityVersion,
    string TestIdentityVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> SupportedTargets,
    IReadOnlyList<string> SupportedPlatforms);

public sealed record ReportHistory(
    int CompatibleRuns,
    int UnmatchedRuns,
    IReadOnlyList<string> ProvenanceTiers);

public sealed record ReportEconomics(
    double? SelectedMeanMs,
    double? FullMeanMs,
    double? SavingsPercent);

public sealed record ReportPolicy(
    PolicyConfiguration EffectiveConfiguration,
    PlanRecommendation Recommendation,
    string DecisiveReason);

public sealed record ReportTestExecution(
    string TestIdentity,
    string Outcome,
    bool Executed,
    double? DurationMs,
    IReadOnlyList<string> ObservedUnitIdentities,
    bool ObservationComplete,
    string? Diagnostics);

public sealed record ReportBuildContext(
    string? Solution,
    string Configuration,
    string Platform,
    string? ExecutionMode);

public sealed record ReportLimitStatus(
    int SnapshotFileLimit,
    long SnapshotByteLimit,
    long SnapshotSingleFileByteLimit,
    int ReportByteLimit,
    int DiagnosticByteLimit,
    int ExplanationReasonLimit,
    bool ExplanationTruncated);

public sealed record TerminalReport(
    int SchemaVersion,
    string RunId,
    TerminalStatus TerminalStatus,
    ErrorClass? ErrorClass,
    string? ErrorCode,
    SnapshotIdentity Baseline,
    SnapshotIdentity Candidate,
    string RepositoryIdentity,
    IReadOnlyList<DetectedLanguage> Languages,
    IReadOnlyList<ReportAdapter> Adapters,
    IReadOnlyList<string> Capabilities,
    int IndexSchema,
    IReadOnlyList<string> IdentitySchemas,
    string? BuildFingerprint,
    IReadOnlyList<ChangedUnit> ChangedUnits,
    IReadOnlyList<PlannedTest> Tests,
    IReadOnlyList<ChangedUnit> UnmappedUnits,
    IReadOnlyList<string> Warnings,
    ReportHistory History,
    ReportEconomics Economics,
    ReportPolicy Policy,
    DateTimeOffset EvidenceCutoff,
    IReadOnlyList<ReportTestExecution>? Executions = null,
    ReportBuildContext? BuildContext = null,
    ReportLimitStatus? Limits = null);

public static class TerminalReportFactory
{
    private static readonly PolicyConfiguration DefaultPolicy =
        new(30, null, null, UnmappedBehavior.Warn);

    public static TerminalReport Success(
        string runId,
        SnapshotIdentity baseline,
        SnapshotIdentity candidate,
        string repositoryIdentity,
        IReadOnlyList<PlannedTest>? tests = null) =>
        new(
            SchemaVersion: 1,
            runId,
            TerminalStatus.Succeeded,
            ErrorClass: null,
            ErrorCode: null,
            baseline,
            candidate,
            repositoryIdentity,
            Languages: [],
            Adapters: [],
            Capabilities: [],
            IndexSchema: Indexing.MerkleIndex.SchemaVersion,
            IdentitySchemas: ["unit:1", "test:1"],
            BuildFingerprint: null,
            ChangedUnits: [],
            Tests: tests ?? [],
            UnmappedUnits: [],
            Warnings: ["Merkle is advisory; unselected tests may still fail."],
            History: new ReportHistory(0, 0, []),
            Economics: new ReportEconomics(null, null, null),
            Policy: new ReportPolicy(DefaultPolicy, PlanRecommendation.PlanOnly, "No execution decision was requested."),
            EvidenceCutoff: DateTimeOffset.UnixEpoch);
}
