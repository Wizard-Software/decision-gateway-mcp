using System.Runtime.InteropServices;
using System.Text.Json;
using DecisionGateway.Host.Transport;

namespace DecisionGateway.Host;

/// <summary>Reads the <c>image_payload</c> tool argument that the transport may have replaced with a handle.</summary>
internal static class ImagePayloadArgument
{
    public const string Name = "image_payload";

    /// <summary>A handle is short; anything longer is an inline base64 value and is not read here.</summary>
    private const int MaxHandleBytes = 128;

    /// <summary>
    /// Takes (and removes from <see cref="ImagePayloadStore"/>) the original base64 text the transport extracted for this call.
    /// Returns null when the argument is missing, is not a handle, or the store no longer holds it. An inline base64 value
    /// (SDK transport, or extraction turned off) is recognised by its raw length and never materialized as a string.
    /// </summary>
    public static ExtractedImagePayload? TakeExtracted(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null
            || !arguments.TryGetValue(Name, out var value)
            || value.ValueKind != JsonValueKind.String
            || JsonMarshal.GetRawUtf8Value(value).Length > MaxHandleBytes
            || value.GetString() is not { } handle
            || !handle.StartsWith(ImagePayloadExtractor.HandlePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return ImagePayloadStore.TryTake(handle, out var payload) ? payload : null;
    }
}
