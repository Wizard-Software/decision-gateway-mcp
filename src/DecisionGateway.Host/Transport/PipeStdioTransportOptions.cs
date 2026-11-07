using System.Globalization;

namespace DecisionGateway.Host.Transport;

/// <summary>Settings of <see cref="PipeStdioServerTransport"/> (spike prototype).</summary>
internal sealed record PipeStdioTransportOptions
{
    /// <summary>Environment variable that selects the transport: <c>pipe</c> turns on the prototype, anything else keeps the SDK transport.</summary>
    public const string TransportVariable = "DG_STDIO_TRANSPORT";

    /// <summary>Environment variable with the frame size limit in bytes.</summary>
    public const string MaxFrameBytesVariable = "DG_MAX_FRAME_BYTES";

    /// <summary>Environment variable with the limit of <c>tools/call</c> requests in flight (0 = no limit).</summary>
    public const string MaxInFlightVariable = "DG_MAX_INFLIGHT_TOOL_CALLS";

    /// <summary>Default frame limit: 32 MiB, above the 20 MiB-payload frame (about 27 MB) measured by the spike.</summary>
    public const long DefaultMaxFrameBytes = 32L * 1024 * 1024;

    /// <summary>Largest accepted frame (one line without the trailing newline), checked before the line is parsed.</summary>
    public long MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;

    /// <summary>Maximum number of <c>tools/call</c> requests without a response; 0 turns the limit off.</summary>
    public int MaxInFlightToolCalls { get; init; }

    /// <summary>Returns true when the environment selects the prototype transport.</summary>
    public static bool IsSelected() =>
        string.Equals(Environment.GetEnvironmentVariable(TransportVariable), "pipe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the options from the environment; an unparsable value keeps the default.</summary>
    public static PipeStdioTransportOptions FromEnvironment()
    {
        var options = new PipeStdioTransportOptions();
        if (long.TryParse(Environment.GetEnvironmentVariable(MaxFrameBytesVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var maxFrame)
            && maxFrame > 0)
        {
            options = options with { MaxFrameBytes = maxFrame };
        }

        if (int.TryParse(Environment.GetEnvironmentVariable(MaxInFlightVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var maxInFlight))
        {
            options = options with { MaxInFlightToolCalls = maxInFlight };
        }

        return options;
    }
}
