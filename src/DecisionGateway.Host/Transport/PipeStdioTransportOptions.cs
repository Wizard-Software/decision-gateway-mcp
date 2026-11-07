using System.Globalization;

namespace DecisionGateway.Host.Transport;

/// <summary>Settings of <see cref="PipeStdioServerTransport"/>, read from the environment.</summary>
internal sealed record PipeStdioTransportOptions
{
    /// <summary>Environment variable that selects the stdio transport: <c>sdk</c> falls back to the MCP SDK transport; anything else keeps the pipe transport.</summary>
    public const string TransportVariable = "DG_STDIO_TRANSPORT";

    /// <summary>Environment variable with the frame size limit in bytes.</summary>
    public const string MaxFrameBytesVariable = "DG_MAX_FRAME_BYTES";

    /// <summary>Environment variable with the limit of <c>tools/call</c> requests in flight (0 or unset = no limit).</summary>
    public const string MaxInFlightVariable = "DG_MAX_INFLIGHT_TOOL_CALLS";

    /// <summary>Environment variable that turns off extracting <c>image_payload</c> in the transport when set to <c>0</c>.</summary>
    public const string ExtractImageVariable = "DG_PIPE_EXTRACT_IMAGE";

    /// <summary>
    /// Default frame limit: 32 MiB. A 20 MiB image becomes about 27.96 MB of base64, so its <c>tools/call</c> frame fits
    /// with about 4.6 MB to spare.
    /// </summary>
    public const long DefaultMaxFrameBytes = 32L * 1024 * 1024;

    /// <summary>Largest accepted frame (one line without the trailing newline), checked before the line is parsed.</summary>
    public long MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;

    /// <summary>Maximum number of <c>tools/call</c> requests without a response; 0 turns the limit off (default).</summary>
    public int MaxInFlightToolCalls { get; init; }

    /// <summary>Decode a large base64 <c>image_payload</c> in the transport and hand the SDK a short handle instead (default on).</summary>
    public bool ExtractImagePayload { get; init; } = true;

    /// <summary>Returns true when the environment asks for the MCP SDK stdio transport instead of the pipe transport.</summary>
    public static bool UsesSdkTransport() =>
        string.Equals(Environment.GetEnvironmentVariable(TransportVariable), "sdk", StringComparison.OrdinalIgnoreCase);

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

        if (Environment.GetEnvironmentVariable(ExtractImageVariable) == "0")
        {
            options = options with { ExtractImagePayload = false };
        }

        return options;
    }
}
