using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DecisionGateway.Host.Transport;

/// <summary>
/// Before the SDK sees a <c>tools/call</c> frame, the transport copies the original base64 text of the large
/// <c>params.arguments.image_payload</c> string (UTF-8 bytes, not decoded) from the frame into <see cref="ImagePayloadStore"/>
/// and rewrites the frame with a short handle in its place. The SDK then parses and binds a frame of a few hundred bytes,
/// so none of its copies of the argument (UTF-16 line, <c>JsonNode</c>, the worst-case escaping buffer it rents when it
/// re-serializes <c>params</c>) scale with the image. The image itself is never decoded here.
/// </summary>
internal static class ImagePayloadExtractor
{
    public const string HandlePrefix = "dg-blob:";

    /// <summary>Strings shorter than this stay in the frame (no gain from extracting them).</summary>
    public const int MinExtractBytes = 64 * 1024;

    private static ReadOnlySpan<byte> ArgumentName => "image_payload"u8;

    /// <summary>
    /// Returns a rewritten frame and the copied base64 text when the frame is a <c>tools/call</c> request whose
    /// <c>image_payload</c> argument is an unescaped string of at least <see cref="MinExtractBytes"/> that is valid base64
    /// without whitespace; otherwise false. The frame is then processed unchanged, so invalid input keeps the normal path
    /// (and the handler's validation).
    /// </summary>
    public static bool TryExtract(ReadOnlySequence<byte> frame, JsonReaderOptions options, out byte[] rewritten, out string handle, out ExtractedImagePayload payload)
    {
        rewritten = [];
        handle = string.Empty;
        payload = default!;
        if (frame.Length < MinExtractBytes)
        {
            return false;
        }

        long start = -1, end = -1;
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

        if (!isToolsCall || start < 0)
        {
            return false;
        }

        // An unescaped JSON string token is the quote, the raw bytes and the quote.
        var value = frame.Slice(start + 1, end - start - 2);
        if (value.Length < MinExtractBytes || !TryCopyBase64(value, out payload))
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

    /// <summary>
    /// Copies the base64 text across sequence segments into one array of the exact size and validates the copy without
    /// allocating: no whitespace (which <see cref="Base64.IsValid(ReadOnlySpan{byte}, out int)"/> would skip) and valid
    /// base64 with correct padding. A <c>data:</c> URL prefix fails the base64 check.
    /// </summary>
    private static bool TryCopyBase64(ReadOnlySequence<byte> base64, out ExtractedImagePayload payload)
    {
        payload = default!;
        var copy = base64.ToArray();
        if (copy.AsSpan().IndexOfAny(" \t\r\n"u8) >= 0 || !Base64.IsValid(copy, out var decodedLength))
        {
            return false;
        }

        payload = new ExtractedImagePayload(copy, decodedLength);
        return true;
    }
}

/// <summary>Original base64 text (UTF-8) of an extracted image and the length of the image it encodes.</summary>
internal sealed record ExtractedImagePayload(ReadOnlyMemory<byte> Base64Utf8, int DecodedLength);

/// <summary>Side store for payloads extracted by the transport; the handler takes (and thereby removes) them by handle.</summary>
internal static class ImagePayloadStore
{
    private static readonly ConcurrentDictionary<string, ExtractedImagePayload> Payloads = new(StringComparer.Ordinal);

    public static int Count => Payloads.Count;

    public static void Put(string handle, ExtractedImagePayload payload) => Payloads[handle] = payload;

    public static bool TryTake(string handle, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExtractedImagePayload? payload) => Payloads.TryRemove(handle, out payload);

    public static void Discard(string handle) => Payloads.TryRemove(handle, out _);
}
