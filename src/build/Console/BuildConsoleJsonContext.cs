using System.Text.Json.Serialization;

namespace Merkle.Build;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConsoleBuildReport))]
internal partial class BuildConsoleJsonContext : JsonSerializerContext;
