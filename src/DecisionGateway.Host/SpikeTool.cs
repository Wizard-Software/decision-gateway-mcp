using System.Buffers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DecisionGateway.Host;

/// <summary>
/// Throwaway spike tool: proves that a single tool with a runtime-built schema can be served over stdio
/// through explicit list/call handlers, without reflection or assembly scanning (AOT-safe).
/// </summary>
internal static class SpikeTool
{
    public const string Name = "evaluate_decision";

    /// <summary>Fixed spike result, serialized through <see cref="HostJsonContext"/>.</summary>
    internal static SpikeDecisionResult FixedResult { get; } = new("spike", 1.0, 0, "jev", false);

    /// <summary>Fixed message of the validation error; never contains request data.</summary>
    internal const string InvalidQuestionMessage = "Validation Error: pole question musi być niepustym napisem.";

    /// <summary>Validation error returned for a missing, non-string or blank <c>question</c>.</summary>
    // Spike: literal error code; the target code takes it from the shared error-code constants.
    internal static SpikeErrorResult InvalidQuestionError { get; } =
        new(new SpikeError("VALIDATION_ERROR", InvalidQuestionMessage, "question", [], false));

    // Declared after InvalidQuestionError on purpose: static initializers run in textual order.
    // Serialized lazily on the first static access, which is the first tools/call, not the startup path.
    private static readonly string InvalidQuestionErrorJson =
        JsonSerializer.Serialize(InvalidQuestionError, HostJsonContext.Default.SpikeErrorResult);

    /// <summary>Builds the tool definition once; input and output schemas are written at runtime with <see cref="Utf8JsonWriter"/>.</summary>
    public static Tool Build()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject("question");
            writer.WriteString("type", "string");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("question");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new Tool
        {
            Name = Name,
            Description = "Spike tool returning a fixed response.",
            InputSchema = JsonElement.Parse(buffer.WrittenSpan),
            OutputSchema = BuildOutputSchema(),
        };
    }

    /// <summary>Writes the success-result schema (five required fields) at runtime.</summary>
    private static JsonElement BuildOutputSchema()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            writer.WriteStartObject("selected_option");
            writer.WriteString("type", "string");
            writer.WriteEndObject();

            writer.WriteStartObject("confidence");
            writer.WriteString("type", "number");
            writer.WriteNumber("minimum", 0);
            writer.WriteNumber("maximum", 1);
            writer.WriteEndObject();

            writer.WriteStartObject("latency_ms");
            writer.WriteString("type", "integer");
            writer.WriteNumber("minimum", 0);
            writer.WriteEndObject();

            writer.WriteStartObject("provider");
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            writer.WriteStringValue("jev");
            writer.WriteStringValue("openai");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("fallback_used");
            writer.WriteString("type", "boolean");
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("selected_option");
            writer.WriteStringValue("confidence");
            writer.WriteStringValue("latency_ms");
            writer.WriteStringValue("provider");
            writer.WriteStringValue("fallback_used");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }

    /// <summary>Handler for tools/list: exactly one tool.</summary>
    public static ValueTask<ListToolsResult> ListAsync(
        RequestContext<ListToolsRequestParams> context,
        Tool tool,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListToolsResult { Tools = [tool] });

    /// <summary>
    /// Handler for tools/call: returns the fixed result as snake_case JSON in both the text block and structured content;
    /// an invalid <c>question</c> yields an error result (text block only, no structured content); unknown tool names are a protocol error.
    /// </summary>
    public static ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Params?.Name, Name, StringComparison.Ordinal))
        {
            throw new McpProtocolException("Unknown tool", McpErrorCode.InvalidParams);
        }

        if (!HasValidQuestion(context.Params?.Arguments))
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = InvalidQuestionErrorJson }],
            });
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(FixedResult, HostJsonContext.Default.SpikeDecisionResult);

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(json) }],
            StructuredContent = JsonElement.Parse(json),
        });
    }

    // ValueKind is checked before GetString() so that no kind of value can throw.
    private static bool HasValidQuestion(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null
        && arguments.TryGetValue("question", out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());
}
