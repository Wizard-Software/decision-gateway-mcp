using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DecisionGateway.Host.Transport;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.ContractTests;

/// <summary>Spike prototype: framing, size limit, parse errors and in-flight limit of the pipe stdio transport, over in-memory pipes.</summary>
public sealed class PipeStdioServerTransportTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":ID,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"q"}}}""";

    [Fact, Trait("Category", "Unit")]
    public async Task FrameSplitAcrossWrites_IsDeliveredAsOneMessage()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions());
        var frame = Encoding.UTF8.GetBytes(ToolCall.Replace("ID", "7", StringComparison.Ordinal) + "\n");
        foreach (var chunk in frame.Chunk(5))
        {
            await harness.WriteAsync(chunk);
        }

        var message = await harness.ReadInboundAsync();
        message.Should().BeOfType<JsonRpcRequest>().Which.Id.Should().Be(new RequestId(7));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task OversizedFrame_StreamedInChunks_IsRejectedWithIdAndNextFrameStillWorks()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions { MaxFrameBytes = 1024 });
        var big = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"q","image_payload":"""
            + "\"" + new string('A', 200_000) + "\"}}}\n";
        foreach (var chunk in Encoding.UTF8.GetBytes(big).Chunk(4096))
        {
            await harness.WriteAsync(chunk);
        }

        await harness.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");

        var error = await harness.ReadOutboundAsync();
        error.GetProperty("id").GetInt32().Should().Be(2);
        error.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        error.GetProperty("error").GetProperty("message").GetString().Should().Be(PipeStdioServerTransport.FrameTooLargeMessage);

        var next = await harness.ReadInboundAsync();
        next.Should().BeOfType<JsonRpcRequest>().Which.Id.Should().Be(new RequestId(3));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task OversizedFrame_ArrivingAsOneCompleteLine_IsRejectedWithoutParsing()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions { MaxFrameBytes = 64 });
        await harness.WriteLineAsync("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"0123456789"}}}""");
        await harness.WriteLineAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");

        var error = await harness.ReadOutboundAsync();
        error.GetProperty("id").GetString().Should().Be("abc");
        error.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);

        var next = await harness.ReadInboundAsync();
        next.Should().BeOfType<JsonRpcRequest>().Which.Method.Should().Be("ping");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task MalformedFrameWithId_GetsParseError()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions());
        await harness.WriteLineAsync("""{"jsonrpc":"1.0","id":9,"method":"tools/list"}""");

        var error = await harness.ReadOutboundAsync();
        error.GetProperty("id").GetInt32().Should().Be(9);
        error.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task BlankLinesAreSkipped_AndLastLineWithoutNewlineIsDelivered()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions());
        await harness.WriteAsync("\n  \r\n"u8.ToArray());
        await harness.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        await harness.CompleteInputAsync();

        var message = await harness.ReadInboundAsync();
        message.Should().BeOfType<JsonRpcNotification>().Which.Method.Should().Be("notifications/initialized");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task InFlightLimit_RejectsExtraToolCall_UntilAResponseIsSent()
    {
        await using var harness = new Harness(new PipeStdioTransportOptions { MaxInFlightToolCalls = 1 });
        await harness.WriteLineAsync(ToolCall.Replace("ID", "10", StringComparison.Ordinal));
        await harness.WriteLineAsync(ToolCall.Replace("ID", "11", StringComparison.Ordinal));

        (await harness.ReadInboundAsync()).Should().BeOfType<JsonRpcRequest>().Which.Id.Should().Be(new RequestId(10));
        var busy = await harness.ReadOutboundAsync();
        busy.GetProperty("id").GetInt32().Should().Be(11);
        busy.GetProperty("error").GetProperty("code").GetInt32().Should().Be(PipeStdioServerTransport.BusyErrorCode);

        await harness.Transport.SendMessageAsync(new JsonRpcResponse { Id = new RequestId(10), Result = null }, TestContext.Current.CancellationToken);
        (await harness.ReadOutboundAsync()).GetProperty("id").GetInt32().Should().Be(10);

        await harness.WriteLineAsync(ToolCall.Replace("ID", "12", StringComparison.Ordinal));
        (await harness.ReadInboundAsync()).Should().BeOfType<JsonRpcRequest>().Which.Id.Should().Be(new RequestId(12));
    }

    /// <summary>The transport over two in-memory pipes: the test writes "stdin" and reads "stdout" line by line.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Pipe _stdin = new();
        private readonly Pipe _stdout = new();
        private readonly StreamReader _stdoutReader;

        public Harness(PipeStdioTransportOptions options)
        {
            Transport = new PipeStdioServerTransport(_stdin.Reader.AsStream(), _stdout.Writer.AsStream(), options, loggerFactory: null);
            _stdoutReader = new StreamReader(_stdout.Reader.AsStream(), Encoding.UTF8);
        }

        public PipeStdioServerTransport Transport { get; }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        public async Task WriteAsync(byte[] bytes)
        {
            await _stdin.Writer.WriteAsync(bytes, Token);
            await _stdin.Writer.FlushAsync(Token);
        }

        public Task WriteLineAsync(string line) => WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

        public Task CompleteInputAsync() => _stdin.Writer.CompleteAsync().AsTask();

        public async Task<JsonRpcMessage> ReadInboundAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await Transport.MessageReader.ReadAsync(timeout.Token);
        }

        public async Task<JsonElement> ReadOutboundAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await _stdoutReader.ReadLineAsync(timeout.Token) ?? throw new IOException("stdout closed");
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await _stdin.Writer.CompleteAsync();
            await Transport.DisposeAsync();
            _stdoutReader.Dispose();
        }
    }
}
