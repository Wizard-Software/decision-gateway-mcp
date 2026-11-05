using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DecisionGateway.Host.ContractTests;

/// <summary>
/// Spike measurement: which log records the MCP SDK emits for client-supplied content, at which category and level,
/// and whether the host floor (<see cref="HostLogging.Configure"/>) cuts them. The server runs in-process on pipes and is
/// fed raw JSON-RPC lines, so malformed input can be sent. Findings go to the test output; assertions cover only the
/// floor contract and that the capture works.
/// </summary>
[Collection(LargeFrameSerial.Name)]
public sealed class LargeFrameSdkFrameLoggingTests
{
    private const string Marker = "DG-FRAME-LOG-MARKER-5c1e";
    private const string SlowToolName = "dg-slow";
    private const string CancelEventName = "LogRequestCanceled";
    private const int MegabyteChars = 1024 * 1024;

    // Failure safety net only: every scenario has an explicit end condition and never relies on this timeout.
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);

    private static readonly string[] Scenarios = ["happy", "malformed_json", "unknown_tool", "cancelled_reason", "unknown_method"];

    internal enum Variant
    {
        Unfloored,
        Floored,
        FlooredOverride,
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "Integration")]
    [Trait("Req", "A6.5")]
    public async Task Req_A6_5_SdkFrameLogging_HostFloor_CutsBelowInformation()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        var belowFloor = new List<string>();
        var flooredHappyMarkerRecords = new List<string>();
        var unflooredHappyMarkerRecords = 0;

        foreach (var variant in Enum.GetValues<Variant>())
        {
            foreach (var scenario in Scenarios)
            {
                var records = await RunScenarioAsync(scenario, variant, markerOverride: null, ct);
                var marked = records.Where(r => r.Contains(Marker)).ToList();

                foreach (var group in marked.GroupBy(r => (r.Category, r.Level, r.EventName)))
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"SDK-FRAME-LOG variant={Label(variant)} scenario={scenario} category={group.Key.Category} level={group.Key.Level} event={group.Key.EventName} count={group.Count()} max_chars={group.Max(r => r.MaxFieldLength)}"));
                }

                if (variant == Variant.Unfloored && scenario == "happy")
                {
                    unflooredHappyMarkerRecords = marked.Count;
                }

                if (variant == Variant.Floored)
                {
                    belowFloor.AddRange(records
                        .Where(r => r.Category.StartsWith(HostLogging.McpCategory, StringComparison.Ordinal) && r.Level < LogLevel.Information)
                        .Select(r => $"{scenario}: {r.Category} {r.Level} {r.EventName} {Truncate(r.Message)}"));

                    if (scenario == "happy")
                    {
                        flooredHappyMarkerRecords.AddRange(marked.Select(r => $"{r.Category} {r.Level} {r.EventName} {Truncate(r.Message)}"));
                    }
                }
            }
        }

        unflooredHappyMarkerRecords.Should().BeGreaterThan(0, "the capture must see marker-carrying records without the floor");
        belowFloor.Should().BeEmpty("the host floor must cut every ModelContextProtocol record below Information");
        flooredHappyMarkerRecords.Should().BeEmpty("with the floor the happy-path frame must not reach any record");
    }

    [Fact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    [Trait("Req", "A6.5")]
    public async Task Req_A6_5_SdkFrameLogging_ClientContentLength_Unbounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        var method = Marker + new string('x', MegabyteChars);

        // RunScenarioAsync returns only after the response to the request arrived (explicit end condition).
        var records = await RunScenarioAsync("unknown_method", Variant.Floored, method, ct);
        var marked = records.Where(r => r.Contains(Marker)).ToList();

        var loggedMax = marked.Count == 0 ? 0 : marked.Max(r => r.MaxFieldLength);
        var loggedTotal = marked.Sum(r => (long)r.TotalFieldLength);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SDK-FRAME-LOG-SIZE method_chars={method.Length} logged_max_chars={loggedMax} logged_total_chars={loggedTotal} records={marked.Count}"));

        foreach (var group in marked.GroupBy(r => (r.Category, r.Level, r.EventName)))
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"SDK-FRAME-LOG-SIZE-RECORDS category={group.Key.Category} level={group.Key.Level} event={group.Key.EventName} count={group.Count()} max_chars={group.Max(r => r.MaxFieldLength)}"));
        }
    }

    internal static async Task<IReadOnlyList<CapturedLog>> RunScenarioAsync(
        string scenario,
        Variant variant,
        string? markerOverride,
        CancellationToken ct)
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = CreateFactory(variant, capture);

        var c2s = new Pipe();
        var s2c = new Pipe();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tool = SpikeTool.Build();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "dg-spike", Version = "0.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (context, token) => SpikeTool.ListAsync(context, tool, token),
                CallToolHandler = async (context, token) =>
                {
                    if (!string.Equals(context.Params?.Name, SlowToolName, StringComparison.Ordinal))
                    {
                        return await SpikeTool.CallAsync(context, token);
                    }

                    slowStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    finally
                    {
                        slowFinished.TrySetResult();
                    }

                    return new CallToolResult();
                },
            },
        };

        var transport = new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream(), "dg-spike", loggerFactory);
        var server = McpServer.Create(transport, options, loggerFactory);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runTask = server.RunAsync(runCts.Token);

        using var safety = CancellationTokenSource.CreateLinkedTokenSource(ct);
        safety.CancelAfter(SafetyNet);

        var writer = c2s.Writer.AsStream();
        using var reader = new StreamReader(s2c.Reader.AsStream(), Encoding.UTF8);

        try
        {
            await DriveAsync(scenario, variant, markerOverride, writer, reader, capture, slowStarted.Task, slowFinished.Task, safety.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && safety.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Safety net of {SafetyNet.TotalSeconds:0} s hit: scenario={scenario} variant={Label(variant)}; the explicit end condition was never met.");
        }
        finally
        {
            await runCts.CancelAsync();
            await c2s.Writer.CompleteAsync();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // Expected: the run was cancelled by the test.
            }

            await server.DisposeAsync();
            await s2c.Writer.CompleteAsync();
            await s2c.Reader.CompleteAsync();
            await c2s.Reader.CompleteAsync();
        }

        return capture.Snapshot();
    }

    internal static ILoggerFactory CreateFactory(Variant variant, CapturingLoggerProvider capture) =>
        variant switch
        {
            Variant.Unfloored => LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddProvider(capture);
            }),
            Variant.Floored => LoggerFactory.Create(b => ConfigureFloored(b, capture)),
            Variant.FlooredOverride => LoggerFactory.Create(b =>
            {
                ConfigureFloored(b, capture);

                // A more specific category rule that a future logging configuration could introduce.
                b.AddFilter("ModelContextProtocol.Server", LogLevel.Trace);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

    // Exactly the host setup; ClearProviders drops the JSON console (no marker records on the test process stderr),
    // the MCP floor rule stays because filter rules are not providers.
    private static void ConfigureFloored(ILoggingBuilder b, CapturingLoggerProvider capture)
    {
        HostLogging.Configure(b);
        b.ClearProviders();
        b.AddProvider(capture);
        b.SetMinimumLevel(LogLevel.Trace);
    }

    private static async Task DriveAsync(
        string scenario,
        Variant variant,
        string? markerOverride,
        Stream writer,
        StreamReader reader,
        CapturingLoggerProvider capture,
        Task slowStarted,
        Task slowFinished,
        CancellationToken token)
    {
        await SendAsync(writer, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"dg-test","version":"0.0.0"}}}""", token);
        await ReadUntilIdAsync(reader, 1, token);
        await SendAsync(writer, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", token);

        switch (scenario)
        {
            case "happy":
                await SendAsync(writer, $$$$"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"{{{{Marker}}}}"}}}""", token);
                await ReadUntilIdAsync(reader, 2, token);
                break;

            case "malformed_json":
                // Unterminated object; the ping probe proves the server consumed and rejected the previous line.
                await SendAsync(writer, $$$"""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"x":"{{{Marker}}}"}""", token);
                await SendAsync(writer, """{"jsonrpc":"2.0","id":8,"method":"ping"}""", token);
                await ReadUntilIdAsync(reader, 8, token);
                break;

            case "unknown_tool":
                await SendAsync(writer, $$$$"""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"{{{{Marker}}}}","arguments":{}}}""", token);
                await ReadUntilIdAsync(reader, 3, token);
                break;

            case "cancelled_reason":
                await SendAsync(writer, $$$$"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"{{{{SlowToolName}}}}","arguments":{}}}""", token);
                await slowStarted.WaitAsync(token);
                await SendAsync(writer, $$$"""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9,"reason":"{{{Marker}}}"}}""", token);

                if (variant == Variant.Floored)
                {
                    // The floor may cut the cancel record, so the end condition is the handler observing the cancellation.
                    await slowFinished.WaitAsync(token);
                    await SendAsync(writer, """{"jsonrpc":"2.0","id":10,"method":"ping"}""", token);
                    await ReadUntilIdAsync(reader, 10, token);
                }
                else
                {
                    while (!capture.Snapshot().Any(r => string.Equals(r.EventName, CancelEventName, StringComparison.Ordinal)))
                    {
                        await Task.Delay(20, token);
                    }
                }

                break;

            case "unknown_method":
                await SendAsync(writer, $$$"""{"jsonrpc":"2.0","id":5,"method":"{{{markerOverride ?? Marker}}}"}""", token);
                await ReadUntilIdAsync(reader, 5, token);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static async Task SendAsync(Stream writer, string line, CancellationToken token)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token);
        await writer.FlushAsync(token);
    }

    private static async Task ReadUntilIdAsync(StreamReader reader, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException($"Server output ended before the response with id {id}.");
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.GetInt32() == id)
            {
                return;
            }
        }
    }

    private static string Label(Variant v) =>
        v switch
        {
            Variant.Unfloored => "unfloored",
            Variant.Floored => "floored",
            _ => "floored+override",
        };

    private static string Truncate(string value, int max = 200) =>
        value.Length <= max ? value : value[..max] + "...";

    internal sealed record CapturedLog(string Category, LogLevel Level, string EventName, string Message, string? Exception, string State, string Scopes)
    {
        public bool Contains(string marker) =>
            Message.Contains(marker, StringComparison.Ordinal)
            || EventName.Contains(marker, StringComparison.Ordinal)
            || (Exception?.Contains(marker, StringComparison.Ordinal) ?? false)
            || State.Contains(marker, StringComparison.Ordinal)
            || Scopes.Contains(marker, StringComparison.Ordinal);

        public int MaxFieldLength => Math.Max(Math.Max(Message.Length, Exception?.Length ?? 0), Math.Max(State.Length, Scopes.Length));

        public int TotalFieldLength => Message.Length + (Exception?.Length ?? 0) + State.Length + Scopes.Length;
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<CapturedLog> _records = new();
        private IExternalScopeProvider? _scopes;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public IReadOnlyList<CapturedLog> Snapshot() => [.. _records];

        public void Dispose()
        {
        }

        private void Add(CapturedLog record) => _records.Enqueue(record);

        private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var stateText = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                    ? string.Join(';', pairs.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}={p.Value}")))
                    : string.Empty;

                var scopes = new StringBuilder();
                owner._scopes?.ForEachScope((scope, sb) => sb.Append(scope).Append(';'), scopes);

                owner.Add(new CapturedLog(
                    category,
                    logLevel,
                    eventId.Name ?? string.Create(CultureInfo.InvariantCulture, $"id:{eventId.Id}"),
                    formatter(state, exception),
                    exception?.ToString(),
                    stateText,
                    scopes.ToString()));
            }
        }
    }
}
