using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DecisionGateway.StartupHarness;

namespace DecisionGateway.StartupHarness.Tests;

[Trait("Category", "Unit")]
public sealed class StartupReportTests
{
    private static readonly DateTimeOffset MeasuredAt = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static string Render(string rid, double[] samples)
    {
        using var stream = new MemoryStream();
        StartupReport.Write(stream, rid, StartupStatistics.Summarize(samples), MeasuredAt);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Write_emits_the_contract_fields()
    {
        var json = Render("osx-arm64", [40.0, 10.0, 20.0, 30.0]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("rid").GetString().Should().Be("osx-arm64");
        root.GetProperty("stage").GetInt32().Should().Be(0);
        root.GetProperty("threshold_ms").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("runs").GetInt32().Should().Be(4);
        root.GetProperty("cold_ms").GetDouble().Should().Be(40.0);

        var warm = root.GetProperty("warm");
        warm.GetProperty("count").GetInt32().Should().Be(3);
        warm.GetProperty("p50_ms").GetDouble().Should().Be(20.0);
        warm.GetProperty("p95_ms").GetDouble().Should().Be(30.0);
        warm.GetProperty("min_ms").GetDouble().Should().Be(10.0);
        warm.GetProperty("max_ms").GetDouble().Should().Be(30.0);

        root.GetProperty("measured_at_utc").GetString().Should().Be("2026-11-05T12:00:00Z");

        var runner = root.GetProperty("runner");
        runner.GetProperty("os").GetString().Should().BeOneOf("Windows", "Linux", "macOS");
        runner.GetProperty("arch").GetString().Should().NotBeNullOrEmpty();
        runner.GetProperty("processor_count").GetInt32().Should().BePositive();
    }

    [Fact]
    public void Write_emits_warm_samples_in_run_order_with_three_decimals()
    {
        // The cold run is first; warm samples must NOT be sorted.
        var json = Render("linux-x64", [99.0, 30.00049, 10.12345, 20.5]);

        using var doc = JsonDocument.Parse(json);
        var samples = doc.RootElement.GetProperty("warm_samples_ms").EnumerateArray()
            .Select(e => e.GetDouble()).ToArray();
        samples.Should().Equal(30.0, 10.123, 20.5);

        // Three decimals are written literally, trailing zeros included.
        json.Should().Contain("30.000").And.Contain("10.123").And.Contain("20.500");
        doc.RootElement.GetProperty("cold_ms").GetDouble().Should().Be(99.0);
        json.Should().Contain("\"cold_ms\": 99.000");
    }
}
