using System.Text.Json.Serialization;
using Merkle.Core.Adapters;
using Merkle.Core.History;

namespace Merkle.Infrastructure.State;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AdapterIndex))]
[JsonSerializable(typeof(HistoricalRun))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
