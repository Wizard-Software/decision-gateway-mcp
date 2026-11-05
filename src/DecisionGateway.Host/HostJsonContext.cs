using System.Text.Json.Serialization;

namespace DecisionGateway.Host;

/// <summary>Source-generated serialization context for every DTO the host writes (AOT-safe, snake_case).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SpikeDecisionResult))]
internal sealed partial class HostJsonContext : JsonSerializerContext;
