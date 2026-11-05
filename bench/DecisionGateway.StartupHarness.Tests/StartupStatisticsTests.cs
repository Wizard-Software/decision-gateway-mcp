using AwesomeAssertions;
using DecisionGateway.StartupHarness;

namespace DecisionGateway.StartupHarness.Tests;

[Trait("Category", "Unit")]
public sealed class StartupStatisticsTests
{
    [Fact]
    public void Summarize_excludes_the_cold_run_from_percentiles()
    {
        // cold run 500 ms, then 49 warm runs 1..49 ms
        double[] samples = [500.0, .. Enumerable.Range(1, 49).Select(i => (double)i)];

        var summary = StartupStatistics.Summarize(samples);

        summary.Runs.Should().Be(50);
        summary.ColdMs.Should().Be(500.0);
        summary.WarmCount.Should().Be(49);
        summary.P50Ms.Should().Be(25.0); // nearest-rank ceil(0.50 * 49) = 25
        summary.P95Ms.Should().Be(47.0); // nearest-rank ceil(0.95 * 49) = 47
        summary.MinMs.Should().Be(1.0);
        summary.MaxMs.Should().Be(49.0); // 500 must not leak into the warm maximum
        summary.WarmSamplesMs.Should().HaveCount(49).And.NotContain(500.0);
    }

    [Fact]
    public void Summarize_keeps_warm_samples_in_run_order()
    {
        var summary = StartupStatistics.Summarize([100.0, 30.0, 10.0, 20.0]);

        summary.WarmSamplesMs.Should().Equal(30.0, 10.0, 20.0);
        summary.P50Ms.Should().Be(20.0);
    }

    [Theory]
    [InlineData(100, 5.0)]
    [InlineData(1, 1.0)]
    [InlineData(50, 3.0)]
    public void NearestRank_on_five_values(double percentile, double expected) =>
        StartupStatistics.NearestRank([1.0, 2.0, 3.0, 4.0, 5.0], percentile).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void NearestRank_rejects_percentile_outside_range(double percentile) =>
        FluentActions.Invoking(() => StartupStatistics.NearestRank([1.0, 2.0], percentile))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Summarize_rejects_fewer_than_two_samples() =>
        FluentActions.Invoking(() => StartupStatistics.Summarize([10.0])).Should().Throw<ArgumentException>();
}
