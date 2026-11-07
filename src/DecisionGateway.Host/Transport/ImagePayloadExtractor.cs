using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DecisionGateway.Host.Transport;

/// <summary>
/// Spike variant 3(b) (DG_PIPE_EXTRACT_IMAGE=1): before the SDK sees a <c>tools/call</c> frame, the transport decodes the
/// large base64 <c>params.arguments.image_payload</c> string straight from the frame bytes into <see cref="ImagePayloadStore"/>
/// and rewrites the frame with a short handle in its place. The SDK then parses and binds a frame of a few hundred bytes.
/// </summary>
internal static class ImagePayloadExtractor
{
    public const string HandlePrefix = "dg-blob:";

    /// <summary>Strings shorter than this stay in the frame (no gain from extracting them).</summary>
    public const int MinExtractBytes = 64 * 1024;

    private static ReadOnlySpan<byte> ArgumentName => "image_payload"u8;

    /// <summary>
    /// Returns a rewritten frame and the decoded payload when the frame is a <c>tools/call</c> request whose
    /// <c>image_payload</c> argument is an unescaped base64 string of at least <see cref="MinExtractBytes"/>; otherwise false
    /// (the frame is then processed unchanged, so malformed input keeps the normal error path).
    /// </summary>
    public static bool TryExtract(ReadOnlySequence<byte> frame, JsonReaderOptions options, out byte[] rewritten, out string handle, out byte[] payload)
    {
        rewritten = [];
        handle = string.Empty;
        payload = [];
        if (frame.Length < MinExtractBytes)
        {
            return false;
        }

        long start = -1, end = -1;
        ReadOnlySequence<byte> value = default;
        var isToolsCall = false;
        try
        {
            var reader = new Utf8JsonReader(frame, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("method"u8))
                {
                    reader.Read();
                    isToolsCall = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("tools/call"u8);
                }
                else if (reader.ValueTextEquals("params"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (!reader.ValueTextEquals("arguments"u8))
                        {
                            reader.Read();
                            reader.Skip();
                            continue;
                        }

                        reader.Read();
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            reader.Skip();
                            continue;
                        }

                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var isImage = reader.ValueTextEquals(ArgumentName);
                            reader.Read();
                            if (isImage && reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && start < 0)
                            {
                                start = reader.TokenStartIndex;
                                end = reader.BytesConsumed;
                                value = reader.HasValueSequence ? reader.ValueSequence : new ReadOnlySequence<byte>(reader.ValueSpan.ToArray());
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (!isToolsCall || start < 0 || value.Length < MinExtractBytes || !TryDecode(value, out payload))
        {
            return false;
        }

        handle = HandlePrefix + Guid.NewGuid().ToString("N");
        var handleJson = Encoding.UTF8.GetBytes("\"" + handle + "\"");
        var prefix = frame.Slice(0, start);
        var suffix = frame.Slice(end);
        rewritten = new byte[prefix.Length + handleJson.Length + suffix.Length];
        prefix.CopyTo(rewritten);
        handleJson.CopyTo(rewritten.AsSpan((int)prefix.Length));
        suffix.CopyTo(rewritten.AsSpan((int)prefix.Length + handleJson.Length));
        return true;
    }

    /// <summary>Streaming base64 decode across sequence segments, without joining them first.</summary>
    private static bool TryDecode(ReadOnlySequence<byte> base64, out byte[] payload)
    {
        payload = [];
        var length = base64.Length;
        if (length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[length / 4 * 3];
        var written = 0;
        Span<byte> carry = stackalloc byte[4];
        var carryLength = 0;
        var position = 0L;
        foreach (var memory in base64)
        {
            var span = memory.Span;
            position += span.Length;
            var isLast = position == length;

            if (carryLength > 0)
            {
                var take = Math.Min(4 - carryLength, span.Length);
                span[..take].CopyTo(carry[carryLength..]);
                carryLength += take;
                span = span[take..];
                if (carryLength < 4)
                {
                    continue;
                }

                if (Base64.DecodeFromUtf8(carry, buffer.AsSpan(written), out _, out var carryWritten, isFinalBlock: isLast && span.IsEmpty) != OperationStatus.Done)
                {
                    return false;
                }

                written += carryWritten;
                carryLength = 0;
            }

            var whole = span.Length - (span.Length % 4);
            if (whole > 0)
            {
                if (Base64.DecodeFromUtf8(span[..whole], buffer.AsSpan(written), out _, out var chunkWritten, isFinalBlock: isLast && whole == span.Length) != OperationStatus.Done)
                {
                    return false;
                }

                written += chunkWritten;
            }

            span[whole..].CopyTo(carry);
            carryLength = span.Length - whole;
        }

        if (carryLength != 0)
        {
            return false;
        }

        payload = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        return true;
    }
}

/// <summary>Side store for payloads extracted by the transport; the handler takes (and thereby removes) them by handle.</summary>
internal static class ImagePayloadStore
{
    private static readonly ConcurrentDictionary<string, byte[]> Payloads = new(StringComparer.Ordinal);

    public static int Count => Payloads.Count;

    public static void Put(string handle, byte[] payload) => Payloads[handle] = payload;

    public static bool TryTake(string handle, out byte[]? payload) => Payloads.TryRemove(handle, out payload);

    public static void Discard(string handle) => Payloads.TryRemove(handle, out _);
}
