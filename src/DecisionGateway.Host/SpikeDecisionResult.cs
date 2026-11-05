namespace DecisionGateway.Host;

/// <summary>Spike result DTO shaped like the success result of the public contract.</summary>
internal sealed record SpikeDecisionResult(
    string SelectedOption,
    double Confidence,
    long LatencyMs,
    string Provider,
    bool FallbackUsed);
