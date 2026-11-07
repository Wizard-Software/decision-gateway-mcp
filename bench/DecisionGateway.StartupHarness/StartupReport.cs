using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DecisionGateway.StartupHarness;

/// <summary>Writes the <c>startup-{rid}.json</c> report; the field names are a fixed contract for its consumers.</summary>
internal static class StartupReport
{
    internal const int SchemaVersion = 1;
    internal const int Stage = 0;

    /// <summary>Writes the report as indented UTF-8 JSON without reflection. Milliseconds carry exactly three decimals.</summary>
    public static void Write(Stream destination, string rid, StartupSummary summary, DateTimeOffset measuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rid);
        ArgumentNullException.ThrowIfNull(summary);

        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteString("rid", rid);
        writer.WriteNumber("stage", Stage);
        writer.WriteNull("threshold_ms"); // stage 0: report only, no threshold
        writer.WriteNumber("runs", summary.Runs);
        WriteMs(writer, "cold_ms", summary.ColdMs);

        writer.WriteStartObject("warm");
        writer.WriteNumber("count", summary.WarmCount);
        WriteMs(writer, "p50_ms", summary.P50Ms);
        WriteMs(writer, "p95_ms", summary.P95Ms);
        WriteMs(writer, "min_ms", summary.MinMs);
        WriteMs(writer, "max_ms", summary.MaxMs);
        writer.WriteEndObject();

        // One line: the samples are data for tooling, not for reading, and indenting each of 49 numbers is noise.
        writer.WritePropertyName("warm_samples_ms");
        writer.WriteRawValue("[" + string.Join(", ", summary.WarmSamplesMs.Select(FormatMs)) + "]");

        writer.WriteStartObject("runner");
        writer.WriteString("os", OperatingSystemName());
        writer.WriteString("arch", RuntimeInformation.OSArchitecture.ToString());
        writer.WriteNumber("processor_count", Environment.ProcessorCount);
        writer.WriteEndObject();

        writer.WriteString(
            "measured_at_utc",
            measuredAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteMs(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(FormatMs(value));
    }

    private static string FormatMs(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string OperatingSystemName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "Other";
}
