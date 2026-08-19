namespace Merkle.Build;

public sealed record ConsoleBuildReport(
    string Outcome,
    int ExitCode,
    IReadOnlyList<ConsoleAdapterBuildResult> Adapters,
    string? RunDirectory,
    string? ManifestPath,
    string? ReportPath,
    string? Diagnostic);

public sealed record ConsoleAdapterBuildResult(
    string AdapterId,
    string Status,
    IReadOnlyList<AdapterBuildArtifact> Artifacts,
    string? Diagnostic,
    IReadOnlyList<string>? Warnings,
    string? RequiredTool,
    string? DetectedVersion);
