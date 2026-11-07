using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.Transport;

/// <summary>
/// Spike prototype of a stdio server transport built on the public <see cref="TransportBase"/> of the MCP SDK.
/// Reads newline-delimited JSON-RPC frames from a <see cref="PipeReader"/> (pooled buffers), finds line ends with a
/// <see cref="SequenceReader{T}"/>, rejects a frame above <see cref="PipeStdioTransportOptions.MaxFrameBytes"/> before it is
/// materialized, and deserializes <see cref="JsonRpcMessage"/> straight from the UTF-8 <see cref="ReadOnlySequence{T}"/>
/// with the SDK converter, without an intermediate UTF-16 string. Optionally caps the number of <c>tools/call</c>
/// requests in flight.
/// </summary>
internal sealed partial class PipeStdioServerTransport : TransportBase
{
    /// <summary>JSON-RPC server-defined error code for a request rejected by the in-flight limit (provisional).</summary>
    internal const int BusyErrorCode = -32000;

    internal const string FrameTooLargeMessage = "Frame exceeds the maximum allowed size.";
    internal const string BusyMessage = "Server is busy: too many tool calls in flight.";
    internal const string ParseErrorMessage = "Failed to parse the JSON-RPC request.";

    private const int ReadBufferSize = 64 * 1024;
    private const int MinimumReadSize = 16 * 1024;

    /// <summary>How many leading bytes of an oversized frame are kept to recover its request id.</summary>
    private const int IdRecoveryPrefixBytes = 4 * 1024;

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly PipeReader _reader;
    private readonly Stream _output;
    private readonly PipeStdioTransportOptions _options;
    private readonly ILogger _logger;
    private readonly JsonTypeInfo<JsonRpcMessage> _messageTypeInfo;
    private readonly JsonReaderOptions _readerOptions;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<RequestId, byte> _toolCallsInFlight = new();
    private readonly ConcurrentDictionary<RequestId, string> _extractedHandles = new();
    private readonly Task _readLoop;
    private int _disposed;

    // Read-loop state (touched only by the read loop).
    private long _scannedBytes;
    private bool _discarding;
    private byte[]? _oversizedPrefix;
    private long _oversizedBytes;

    public PipeStdioServerTransport(Stream input, Stream output, PipeStdioTransportOptions options, ILoggerFactory? loggerFactory)
        : base("Server (pipe stdio)", loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        _reader = PipeReader.Create(input, new StreamPipeReaderOptions(bufferSize: ReadBufferSize, minimumReadSize: MinimumReadSize));
        _output = output;
        _options = options;
        _logger = loggerFactory?.CreateLogger<PipeStdioServerTransport>() ?? NullLogger<PipeStdioServerTransport>.Instance;

        var serializerOptions = McpJsonUtilities.DefaultOptions;
        _messageTypeInfo = (JsonTypeInfo<JsonRpcMessage>)serializerOptions.GetTypeInfo(typeof(JsonRpcMessage));

        // Same reader settings the serializer would use for Deserialize(string, options).
        _readerOptions = new JsonReaderOptions
        {
            AllowTrailingCommas = serializerOptions.AllowTrailingCommas,
            CommentHandling = serializerOptions.ReadCommentHandling,
            MaxDepth = serializerOptions.MaxDepth == 0 ? 64 : serializerOptions.MaxDepth,
        };

        SetConnected();
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>Number of <c>tools/call</c> requests read and not yet answered (diagnostics only).</summary>
    internal int ToolCallsInFlight => _toolCallsInFlight.Count;

    /// <summary>Creates the transport over the process stdin and stdout.</summary>
    public static PipeStdioServerTransport CreateForConsole(PipeStdioTransportOptions options, ILoggerFactory? loggerFactory) =>
        new(
            new CancellableStream(Console.OpenStandardInput()),
            new BufferedStream(Console.OpenStandardOutput()),
            options,
            loggerFactory);

    /// <inheritdoc />
    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        if (message is JsonRpcResponse or JsonRpcError)
        {
            var answeredId = ((JsonRpcMessageWithId)message).Id;
            _toolCallsInFlight.TryRemove(answeredId, out _);

            // A payload the handler never took (error before the handler, unknown tool) must not stay in the store.
            if (_extractedHandles.TryRemove(answeredId, out var handle))
            {
                ImagePayloadStore.Discard(handle);
            }
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, _messageTypeInfo);
            await _output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var exceptionType = ex.GetType().Name;
            LogSendFailed(exceptionType);
            throw new IOException("Failed to send message.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _reader.CancelPendingRead();

            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            await _reader.CompleteAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            SetDisconnected();
            _shutdownCts.Dispose();
            _sendLock.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        var token = _shutdownCts.Token;
        Exception? error = null;
        var inbound = new List<JsonRpcMessage>();
        var outbound = new List<JsonRpcMessage>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(token).ConfigureAwait(false);
                if (result.IsCanceled)
                {
                    break;
                }

                var buffer = result.Buffer;
                var consumed = ScanBuffer(buffer, result.IsCompleted, inbound, outbound);
                _reader.AdvanceTo(consumed, buffer.End);

                // Messages are forwarded after AdvanceTo: none of them references pipe memory any more.
                foreach (var message in outbound)
                {
                    await TrySendAsync(message, token).ConfigureAwait(false);
                }

                foreach (var message in inbound)
                {
                    await WriteMessageAsync(message, token).ConfigureAwait(false);
                }

                inbound.Clear();
                outbound.Clear();

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown requested.
        }
        catch (Exception ex)
        {
            var exceptionType = ex.GetType().Name;
            LogReadFailed(exceptionType);
            error = ex;
        }
        finally
        {
            SetDisconnected(error);
        }
    }

    /// <summary>
    /// Splits the buffered bytes into complete lines, parses each one and returns the position up to which the buffer
    /// was consumed. Bytes already scanned for a newline are not scanned again on the next read.
    /// </summary>
    private SequencePosition ScanBuffer(
        ReadOnlySequence<byte> buffer,
        bool isCompleted,
        List<JsonRpcMessage> inbound,
        List<JsonRpcMessage> outbound)
    {
        var reader = new SequenceReader<byte>(buffer);
        var lineStart = buffer.Start;
        reader.Advance(_scannedBytes);

        while (reader.TryAdvanceTo((byte)'\n', advancePastDelimiter: false))
        {
            var line = buffer.Slice(lineStart, reader.Position);
            reader.Advance(1);
            lineStart = reader.Position;
            _scannedBytes = 0;

            if (_discarding)
            {
                // The end of an oversized frame whose head has already been dropped.
                _oversizedBytes += line.Length;
                FinishOversized(outbound);
                continue;
            }

            if (line.Length > _options.MaxFrameBytes)
            {
                // The whole oversized frame arrived in one read: reject it without parsing.
                StartOversized(line);
                FinishOversized(outbound);
                continue;
            }

            ProcessLine(line, inbound, outbound);
        }

        var tail = buffer.Slice(lineStart);

        if (_discarding)
        {
            // Drop everything up to the next newline; nothing of the oversized frame is kept in the pipe.
            _oversizedBytes += tail.Length;
            _scannedBytes = 0;
            if (isCompleted)
            {
                FinishOversized(outbound);
            }

            return buffer.End;
        }

        if (tail.Length > _options.MaxFrameBytes)
        {
            StartOversized(tail);
            _scannedBytes = 0;
            if (isCompleted)
            {
                FinishOversized(outbound);
            }

            return buffer.End;
        }

        if (isCompleted && !tail.IsEmpty)
        {
            // A last line without a trailing newline is still a message (same as TextReader.ReadLine).
            ProcessLine(tail, inbound, outbound);
            _scannedBytes = 0;
            return buffer.End;
        }

        _scannedBytes = tail.Length;
        return lineStart;
    }

    private void StartOversized(ReadOnlySequence<byte> head)
    {
        var prefixLength = (int)Math.Min(head.Length, IdRecoveryPrefixBytes);
        _oversizedPrefix = head.Slice(0, prefixLength).ToArray();
        _oversizedBytes = head.Length;
        _discarding = true;
    }

    private void FinishOversized(List<JsonRpcMessage> outbound)
    {
        LogFrameTooLarge(_oversizedBytes, _options.MaxFrameBytes);
        if (_oversizedPrefix is not null && TryRecoverRequestId(_oversizedPrefix, isFinalBlock: false, out var id))
        {
            outbound.Add(Error(id, (int)McpErrorCode.InvalidRequest, FrameTooLargeMessage));
        }

        _oversizedPrefix = null;
        _oversizedBytes = 0;
        _discarding = false;
    }

    private void ProcessLine(ReadOnlySequence<byte> line, List<JsonRpcMessage> inbound, List<JsonRpcMessage> outbound)
    {
        if (IsWhiteSpace(line))
        {
            return;
        }

        string? extractedHandle = null;
        if (_options.ExtractImagePayload
            && ImagePayloadExtractor.TryExtract(line, _readerOptions, out var rewritten, out var handle, out var payload))
        {
            // From here on the SDK sees a frame of a few hundred bytes; the payload waits in the store.
            line = new ReadOnlySequence<byte>(rewritten);
            ImagePayloadStore.Put(handle, payload);
            extractedHandle = handle;
        }

        JsonRpcMessage? message;
        try
        {
            var jsonReader = new Utf8JsonReader(line, _readerOptions);
            message = JsonSerializer.Deserialize(ref jsonReader, _messageTypeInfo);

            // Deserialize(string) rejects trailing data after the value; keep that behaviour.
            if (jsonReader.Read())
            {
                throw new JsonException("Unexpected data after the JSON-RPC message.");
            }
        }
        catch (JsonException ex)
        {
            // The exception text may quote the frame: log only its type.
            var exceptionType = ex.GetType().Name;
            LogParseFailed(exceptionType);
            if (extractedHandle is not null)
            {
                ImagePayloadStore.Discard(extractedHandle);
            }

            var bytes = line.IsSingleSegment ? line.First : line.ToArray();
            if (TryRecoverRequestId(bytes.Span, isFinalBlock: true, out var id))
            {
                outbound.Add(Error(id, (int)McpErrorCode.ParseError, ParseErrorMessage));
            }

            return;
        }

        if (message is null)
        {
            LogUnexpectedMessage();
            if (extractedHandle is not null)
            {
                ImagePayloadStore.Discard(extractedHandle);
            }

            return;
        }

        if (extractedHandle is not null)
        {
            // TryExtract only accepts tools/call, which deserializes to a request.
            _extractedHandles[((JsonRpcRequest)message).Id] = extractedHandle;
        }

        if (message is JsonRpcRequest { Method: RequestMethods.ToolsCall } request && !TryAdmitToolCall(request.Id))
        {
            outbound.Add(Error(request.Id, BusyErrorCode, BusyMessage));
            return;
        }

        if (message is JsonRpcNotification { Method: NotificationMethods.CancelledNotification } cancelled
            && TryGetCancelledRequestId(cancelled.Params, out var cancelledId))
        {
            // A cancelled request gets no response, so it must leave the in-flight set here.
            _toolCallsInFlight.TryRemove(cancelledId, out _);
            if (_extractedHandles.TryRemove(cancelledId, out var cancelledHandle))
            {
                ImagePayloadStore.Discard(cancelledHandle);
            }
        }

        inbound.Add(message);
    }

    private bool TryAdmitToolCall(RequestId id)
    {
        if (_options.MaxInFlightToolCalls <= 0)
        {
            return true;
        }

        // Single reader: the count can only drop concurrently (responses), so check-then-add is safe.
        if (_toolCallsInFlight.Count >= _options.MaxInFlightToolCalls)
        {
            LogToolCallRejectedBusy(_options.MaxInFlightToolCalls);
            return false;
        }

        _toolCallsInFlight.TryAdd(id, 0);
        return true;
    }

    private async Task TrySendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Already logged by SendMessageAsync; a failed error reply must not end the read loop.
        }
    }

    private static JsonRpcError Error(RequestId id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcErrorDetail { Code = code, Message = message } };

    private static bool IsWhiteSpace(ReadOnlySequence<byte> line)
    {
        foreach (var segment in line)
        {
            foreach (var b in segment.Span)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryGetCancelledRequestId(JsonNode? parameters, out RequestId id)
    {
        id = default;
        if (parameters is not JsonObject obj || !obj.TryGetPropertyValue("requestId", out var node) || node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<string>(out var text))
        {
            id = new RequestId(text);
            return true;
        }

        if (value.TryGetValue<long>(out var number))
        {
            id = new RequestId(number);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Best-effort recovery of the top-level <c>id</c> of a frame that was rejected (parse error or size limit).
    /// With <paramref name="isFinalBlock"/> false the input is only the head of the frame: values that do not fit are
    /// treated as "id not found".
    /// </summary>
    internal static bool TryRecoverRequestId(ReadOnlySpan<byte> utf8, bool isFinalBlock, out RequestId id)
    {
        id = default;
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock, new JsonReaderState(new JsonReaderOptions { MaxDepth = int.MaxValue }));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var isId = reader.ValueTextEquals("id"u8);
                if (!reader.Read())
                {
                    return false;
                }

                if (isId)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            id = new RequestId(reader.GetString()!);
                            return true;
                        case JsonTokenType.Number when reader.TryGetInt64(out var number):
                            id = new RequestId(number);
                            return true;
                        default:
                            return false;
                    }
                }

                if (!reader.TrySkip())
                {
                    return false;
                }
            }
        }
        catch (JsonException)
        {
            // Too malformed to locate a top-level id.
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe stdio transport rejected a frame of {FrameBytes} bytes (limit {MaxFrameBytes} bytes).")]
    private partial void LogFrameTooLarge(long frameBytes, long maxFrameBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipe stdio transport failed to parse a message ({ExceptionType}).")]
    private partial void LogParseFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipe stdio transport received a message that is not a JSON-RPC object.")]
    private partial void LogUnexpectedMessage();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe stdio transport rejected a tool call: {MaxInFlight} tool calls already in flight.")]
    private partial void LogToolCallRejectedBusy(int maxInFlight);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipe stdio transport failed to send a message ({ExceptionType}).")]
    private partial void LogSendFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe stdio transport stopped reading ({ExceptionType}).")]
    private partial void LogReadFailed(string exceptionType);

    /// <summary>
    /// Console streams ignore cancellation and are not unblocked by Dispose; waiting with the token lets shutdown finish
    /// while a read is still pending in the syscall (same approach as the SDK stdio transport).
    /// </summary>
    private sealed class CancellableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = inner.ReadAsync(buffer, cancellationToken);
            return pending.IsCompletedSuccessfully ? pending : new ValueTask<int>(pending.AsTask().WaitAsync(cancellationToken));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
