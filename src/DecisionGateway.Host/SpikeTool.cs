using System.Buffers;
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

    /// <summary>Builds the tool definition once; the input schema is written at runtime with <see cref="Utf8JsonWriter"/>.</summary>
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
        };
    }

    /// <summary>Handler for tools/list: exactly one tool.</summary>
    public static ValueTask<ListToolsResult> ListAsync(
        RequestContext<ListToolsRequestParams> context,
        Tool tool,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListToolsResult { Tools = [tool] });

    /// <summary>Handler for tools/call: returns a fixed text block; unknown tool names are a protocol error.</summary>
    public static ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Params?.Name, Name, StringComparison.Ordinal))
        {
            throw new McpProtocolException("Unknown tool", McpErrorCode.InvalidParams);
        }

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "spike" }],
        });
    }
}
