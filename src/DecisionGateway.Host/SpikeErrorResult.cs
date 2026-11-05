namespace DecisionGateway.Host;

/// <summary>Spike error result shaped like the error object of the public contract.</summary>
internal sealed record SpikeErrorResult(SpikeError Error);

/// <summary>Error object: code, message, field (always emitted), providers tried, fallback flag.</summary>
internal sealed record SpikeError(
    string Code,
    string Message,
    string? Field,
    IReadOnlyList<string> ProvidersTried,
    bool FallbackUsed);
