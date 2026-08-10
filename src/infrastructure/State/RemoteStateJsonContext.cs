using System.Text.Json.Serialization;
using Merkle.Core.History;

namespace Merkle.Infrastructure.State;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemotePublishDto))]
[JsonSerializable(typeof(RemoteReadDto))]
internal sealed partial class RemoteStateJsonContext : JsonSerializerContext;

internal class RemotePublishDto
{
    public int Schema { get; set; } = 1;
    public RemoteCompatibilityDto? Compatibility { get; set; }
    public RemoteRunDto[]? Runs { get; set; }
}

internal sealed class RemoteReadDto : RemotePublishDto
{
    public string? NextCursor { get; set; }
}

internal sealed class RemoteCompatibilityDto
{
    public string? RepositoryIdentity { get; set; }
    public string? SchemaVersion { get; set; }
    public string? AdapterIdentity { get; set; }
    public string? BuildFingerprintFamily { get; set; }
}

internal sealed class RemoteRunDto
{
    public string? Id { get; set; }
    public HistoryProvenance Provenance { get; set; }
    public HistoryRunStatus Status { get; set; }
    public bool IsCompleteSuite { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string[]? ChangedUnitIdentities { get; set; }
    public RemoteTestDto[]? Tests { get; set; }
}

internal sealed class RemoteTestDto
{
    public string? TestIdentity { get; set; }
    public bool Executed { get; set; }
    public HistoricalTestOutcome Outcome { get; set; }
    public double? DurationMs { get; set; }
    public string[]? ObservedUnitIdentities { get; set; }
}
