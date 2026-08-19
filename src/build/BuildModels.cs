namespace Merkle.Build;

public enum BuildCommand
{
    Build,
    Publish
}

public enum AdapterBuildPolicy
{
    Strict,
    BestEffort
}

public enum BuildScheduling
{
    Sequential,
    Parallel
}

public enum BuildOutputFormat
{
    Text,
    Json
}

public enum AdapterReadinessStatus
{
    Ready,
    Unavailable
}

public enum AdapterBuildStatus
{
    Built,
    Skipped,
    Failed,
    Cancelled,
    NotRun
}

public enum BuildOutcome
{
    Success,
    PartialSuccess,
    Failed,
    Cancelled
}

public sealed record BuildRequest(
    BuildCommand Command,
    IReadOnlyList<string> Adapters,
    AdapterBuildPolicy Policy = AdapterBuildPolicy.Strict,
    BuildScheduling Scheduling = BuildScheduling.Sequential,
    int? MaxParallel = null,
    bool RunTests = false,
    bool NoWarnings = false,
    string Configuration = "Debug",
    string? RuntimeIdentifier = null,
    string? OutputPath = null,
    string? ReportPath = null,
    BuildOutputFormat Format = BuildOutputFormat.Text,
    bool Clean = false);

public sealed record BuildContext(
    string RepositoryRoot,
    string Configuration,
    string? RuntimeIdentifier,
    string RunDirectory,
    string StagingDirectory,
    string? OutputPath = null,
    string? HostStagingDirectory = null);

public sealed record AdapterBuildRequest(
    BuildContext Context,
    bool RunTests,
    AdapterReadiness Readiness);

public sealed record AdapterBuildDefinition(
    string Id,
    IReadOnlyList<string> Aliases,
    string Version,
    IReadOnlyList<string> SupportedPlatforms);

public sealed record AdapterReadiness(
    string AdapterId,
    AdapterReadinessStatus Status,
    string? Reason = null,
    string? RequiredTool = null,
    string? DetectedVersion = null);

public sealed record AdapterBuildArtifact(
    string AdapterId,
    string RelativePath,
    string Sha256,
    string Version,
    string ProtocolVersion,
    string Profile);

public sealed record AdapterBuildResult(
    string AdapterId,
    AdapterBuildStatus Status,
    IReadOnlyList<AdapterBuildArtifact> Artifacts,
    string? Diagnostic = null,
    IReadOnlyList<string>? Warnings = null,
    string? RequiredTool = null,
    string? DetectedVersion = null);

public sealed record HostPublishRequest(
    BuildRequest Request,
    BuildContext Context,
    IReadOnlyList<AdapterBuildResult> SuccessfulAdapters);

public sealed record HostPublishResult(
    bool Succeeded,
    string? Diagnostic = null);

public sealed record BuildOutputRequest(
    string OutputPath,
    string HostStagingDirectory,
    string AdapterStagingDirectory,
    string Configuration,
    string RuntimeIdentifier,
    IReadOnlyList<AdapterBuildResult> Adapters,
    string MerkleVersion = "0.1.0");

public sealed record BuildOutputResult(
    string OutputPath,
    string ManifestPath);

public sealed record BuildReportRequest(
    string ReportPath,
    BuildCommand Command,
    string Configuration,
    string RuntimeIdentifier,
    BuildReport Report,
    string MerkleVersion = "0.1.0");

public sealed record BuildReport(
    BuildOutcome Outcome,
    int ExitCode,
    IReadOnlyList<AdapterBuildResult> Adapters,
    string? RunDirectory = null,
    string? ManifestPath = null,
    string? ReportPath = null,
    string? Diagnostic = null);
