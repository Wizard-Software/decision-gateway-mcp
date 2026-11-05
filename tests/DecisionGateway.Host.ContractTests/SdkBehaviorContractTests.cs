using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.ContractTests;

public sealed class SdkBehaviorContractTests
{
    private const string EchoMarker = "DG-ECHO-MARKER-13c9";
    private const string ExpectedMessage = "Validation Error: pole question musi być niepustym napisem.";

    private static readonly string[] SuccessFields =
        ["selected_option", "confidence", "latency_ms", "provider", "fallback_used"];

    private static readonly string[] ErrorFields =
        ["code", "message", "field", "providers_tried", "fallback_used"];

    public static TheoryData<string> InvalidQuestionCases => new()
    {
        "42", "true", "null", "{}", "[]", "\"\"", "\"   \"",
        "<absent-key>",
        "<no-arguments>",
    };

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Req_3_5_ToolsList_DeclaresOutputSchemaWithFiveRequiredFields()
    {
        using var cts = NewTimeout();
        var schema = await ReadOutputSchemaAsync(cts.Token);

        schema.GetProperty("type").GetString().Should().Be("object");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(SuccessFields);
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(SuccessFields);

        properties.GetProperty("selected_option").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("fallback_used").GetProperty("type").GetString().Should().Be("boolean");
        var confidence = properties.GetProperty("confidence");
        confidence.GetProperty("type").GetString().Should().Be("number");
        confidence.GetProperty("minimum").GetDouble().Should().Be(0);
        confidence.GetProperty("maximum").GetDouble().Should().Be(1);
        var latency = properties.GetProperty("latency_ms");
        latency.GetProperty("type").GetString().Should().Be("integer");
        latency.GetProperty("minimum").GetDouble().Should().Be(0);
        var provider = properties.GetProperty("provider");
        provider.GetProperty("type").GetString().Should().Be("string");
        provider.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["jev", "openai"]);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Req_3_5_ToolsCall_SuccessStructuredContent_SatisfiesOutputSchema()
    {
        using var cts = NewTimeout();
        var ct = cts.Token;
        await using var client = await HostProcess.ConnectAsync(ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var outputSchema = tools.Should().ContainSingle().Which.ProtocolTool.OutputSchema;
        outputSchema.Should().NotBeNull();
        var schema = outputSchema!.Value;

        var result = await client.CallToolAsync(
            "evaluate_decision",
            new Dictionary<string, object?> { ["question"] = "q" },
            cancellationToken: ct);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.StructuredContent));
        var structured = document.RootElement;
        var properties = schema.GetProperty("properties");

        structured.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        structured.GetProperty("selected_option").ValueKind.Should().Be(JsonValueKind.String);

        var confidence = structured.GetProperty("confidence");
        confidence.ValueKind.Should().Be(JsonValueKind.Number);
        confidence.GetDouble().Should().BeInRange(
            properties.GetProperty("confidence").GetProperty("minimum").GetDouble(),
            properties.GetProperty("confidence").GetProperty("maximum").GetDouble());

        var latency = structured.GetProperty("latency_ms");
        latency.ValueKind.Should().Be(JsonValueKind.Number);
        latency.TryGetInt64(out var latencyValue).Should().BeTrue();
        latencyValue.Should().BeGreaterThanOrEqualTo(
            (long)properties.GetProperty("latency_ms").GetProperty("minimum").GetDouble());

        var allowed = properties.GetProperty("provider").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        allowed.Should().Contain(structured.GetProperty("provider").GetString());
        structured.GetProperty("fallback_used").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Theory]
    [Trait("Category", "Contract")]
    [MemberData(nameof(InvalidQuestionCases))]
    public async Task ToolsCall_InvalidQuestion_ReturnsIsErrorWithoutStructuredContent(string rawQuestion)
    {
        using var cts = NewTimeout();
        var ct = cts.Token;
        await using var client = await HostProcess.ConnectAsync(ct);

        IReadOnlyDictionary<string, object?>? arguments = rawQuestion switch
        {
            "<no-arguments>" => null,
            "<absent-key>" => new Dictionary<string, object?>(),
            _ => new Dictionary<string, object?> { ["question"] = JsonElement.Parse(rawQuestion) },
        };

        var result = await client.CallToolAsync("evaluate_decision", arguments, cancellationToken: ct);

        result.IsError.Should().BeTrue();
        result.StructuredContent.Should().BeNull();
        var text = result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["error"]);
        var error = root.GetProperty("error");
        error.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ErrorFields);
        error.GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
        error.GetProperty("message").GetString().Should().Be(ExpectedMessage);
        error.GetProperty("field").GetString().Should().Be("question");
        error.GetProperty("providers_tried").ValueKind.Should().Be(JsonValueKind.Array);
        error.GetProperty("providers_tried").GetArrayLength().Should().Be(0);
        error.GetProperty("fallback_used").ValueKind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task ToolsCall_InvalidQuestion_DoesNotEchoValueToResultOrStderr()
    {
        using var cts = NewTimeout();
        var ct = cts.Token;

        var (command, arguments, environment) = HostProcess.ResolveStartInfo();
        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        async Task SendAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }

        var stdoutLines = new List<string>();
        var frames = new Dictionary<int, JsonElement>();

        async Task<bool> ReadFrameAsync()
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                return false;
            }

            stdoutLines.Add(line);
            if (TryParseFrame(line, out var frame) && frame.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number)
            {
                frames[id.GetInt32()] = frame;
            }

            return true;
        }

        await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"echo-test","version":"1.0"}}}""");
        while (!frames.ContainsKey(1) && await ReadFrameAsync())
        {
        }

        await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await SendAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":{"m":"DG-ECHO-MARKER-13c9"}}}}""");
        await SendAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":["DG-ECHO-MARKER-13c9"]}}}""");
        await SendAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"evaluate_decision","arguments":["DG-ECHO-MARKER-13c9"]}}""");

        while (!(frames.ContainsKey(2) && frames.ContainsKey(3) && frames.ContainsKey(4)) && await ReadFrameAsync())
        {
        }

        process.StandardInput.Close();

        using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exitCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Host did not exit within 5 seconds after stdin was closed.");
        }

        while (await ReadFrameAsync())
        {
        }

        var stderr = await stderrTask;

        foreach (var id in new[] { 2, 3 })
        {
            frames.Should().ContainKey(id);
            var result = frames[id].GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            result.TryGetProperty("structuredContent", out _).Should().BeFalse();
            result.GetProperty("content")[0].GetProperty("text").GetString().Should().NotContain(EchoMarker);
        }

        frames[4].TryGetProperty("result", out _).Should().BeFalse("non-object arguments are a JSON-RPC error");
        var rpcError = frames[4].GetProperty("error");
        rpcError.GetProperty("message").GetString().Should().NotContain(EchoMarker);

        stderr.Should().NotContain(EchoMarker);
        stdoutLines.Should().NotBeEmpty();
        foreach (var line in stdoutLines)
        {
            TryParseFrame(line, out _).Should().BeTrue("stdout line must be a JSON-RPC 2.0 object: {0}", line);
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Req_1_1_ToolsCall_UnknownTool_IsJsonRpcErrorNotToolResult()
    {
        using var cts = NewTimeout();
        var ct = cts.Token;
        await using var client = await HostProcess.ConnectAsync(ct);

        var act = async () => await client.CallToolAsync(
            "no_such_tool",
            new Dictionary<string, object?> { ["question"] = "q" },
            cancellationToken: ct);

        var exception = await act.Should().ThrowAsync<McpProtocolException>();
        exception.Which.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
    }

    private static CancellationTokenSource NewTimeout()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return cts;
    }

    private static async Task<JsonElement> ReadOutputSchemaAsync(CancellationToken ct)
    {
        await using var client = await HostProcess.ConnectAsync(ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var tool = tools.Should().ContainSingle().Which;
        tool.Name.Should().Be("evaluate_decision");
        var schema = tool.ProtocolTool.OutputSchema;
        schema.Should().NotBeNull();
        return schema!.Value.Clone();
    }

    private static bool TryParseFrame(string line, out JsonElement frame)
    {
        frame = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("jsonrpc", out var version)
                || version.GetString() != "2.0")
            {
                return false;
            }

            frame = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
