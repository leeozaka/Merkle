using System.Text.Json.Serialization;

namespace Merkle.Core.Reporting;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TerminalReport))]
public sealed partial class MerkleJsonContext : JsonSerializerContext;
