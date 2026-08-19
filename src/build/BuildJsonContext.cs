using System.Text.Json.Serialization;

namespace Merkle.Build;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DotNetSmokeRequest))]
[JsonSerializable(typeof(AdapterManifestDocument))]
[JsonSerializable(typeof(BuildRunReportDocument))]
internal partial class BuildJsonContext : JsonSerializerContext;

internal sealed record DotNetSmokeRequest(
    string ProtocolVersion,
    string RequestId,
    string RepositoryRoot,
    string? ConfiguredSolution,
    object[] Files);

internal sealed record AdapterManifestDocument(
    int SchemaVersion,
    string MerkleVersion,
    string Configuration,
    string RuntimeIdentifier,
    AdapterManifestEntry[] Adapters);

internal sealed record AdapterManifestEntry(
    string Id,
    string Version,
    string ProtocolVersion,
    string Profile,
    AdapterManifestArtifact[] Artifacts);

internal sealed record AdapterManifestArtifact(string Path, string Sha256);

internal sealed record BuildRunReportDocument(
    int SchemaVersion,
    string MerkleVersion,
    string Command,
    string Configuration,
    string RuntimeIdentifier,
    string Outcome,
    int ExitCode,
    string? Diagnostic,
    BuildRunAdapterEntry[] Adapters,
    string? RunDirectory,
    string? ManifestPath);

internal sealed record BuildRunAdapterEntry(
    string AdapterId,
    string Status,
    string? Diagnostic,
    string[] Warnings,
    BuildRunArtifactEntry[] Artifacts,
    string? RequiredTool,
    string? DetectedVersion);

internal sealed record BuildRunArtifactEntry(string Path, string Sha256);
