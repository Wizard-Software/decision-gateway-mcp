namespace DecisionGateway.StartupHarness;

/// <summary>Aggregated startup measurements of one harness run.</summary>
/// <param name="Runs">Total number of runs, including the cold one.</param>
/// <param name="ColdMs">The first (cold) run, excluded from every warm statistic.</param>
/// <param name="WarmCount">Number of warm runs (<paramref name="Runs"/> minus one).</param>
/// <param name="P50Ms">Nearest-rank median of the warm runs.</param>
/// <param name="P95Ms">Nearest-rank 95th percentile of the warm runs.</param>
/// <param name="MinMs">Fastest warm run.</param>
/// <param name="MaxMs">Slowest warm run.</param>
/// <param name="WarmSamplesMs">Warm runs in the order they were executed.</param>
internal sealed record StartupSummary(
    int Runs,
    double ColdMs,
    int WarmCount,
    double P50Ms,
    double P95Ms,
    double MinMs,
    double MaxMs,
    IReadOnlyList<double> WarmSamplesMs);

internal static class StartupStatistics
{
    /// <summary>Splits the samples into the cold run and the warm runs and summarizes the warm runs.</summary>
    /// <param name="samplesMs">Samples in run order; the first element is the cold run. At least two are required.</param>
    public static StartupSummary Summarize(IReadOnlyList<double> samplesMs)
    {
        ArgumentNullException.ThrowIfNull(samplesMs);
        if (samplesMs.Count < 2)
        {
            throw new ArgumentException("At least two samples are required: one cold run and one warm run.", nameof(samplesMs));
        }

        var warmInRunOrder = new double[samplesMs.Count - 1];
        for (var i = 1; i < samplesMs.Count; i++)
        {
            warmInRunOrder[i - 1] = samplesMs[i];
        }

        var sorted = (double[])warmInRunOrder.Clone();
        Array.Sort(sorted);

        return new StartupSummary(
            Runs: samplesMs.Count,
            ColdMs: samplesMs[0],
            WarmCount: sorted.Length,
            P50Ms: NearestRank(sorted, 50),
            P95Ms: NearestRank(sorted, 95),
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            WarmSamplesMs: warmInRunOrder);
    }

    /// <summary>Nearest-rank percentile: <c>sorted[ceil(p / 100 * n) - 1]</c>.</summary>
    /// <param name="sortedAscending">Non-empty list sorted ascending.</param>
    /// <param name="percentile">A value in (0, 100].</param>
    public static double NearestRank(IReadOnlyList<double> sortedAscending, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("The sample list must not be empty.", nameof(sortedAscending));
        }

        if (!(percentile > 0 && percentile <= 100))
        {
            throw new ArgumentException("The percentile must be in the range (0, 100].", nameof(percentile));
        }

        var n = sortedAscending.Count;
        var rank = (int)Math.Ceiling(percentile / 100.0 * n);
        return sortedAscending[Math.Clamp(rank, 1, n) - 1];
    }
}
