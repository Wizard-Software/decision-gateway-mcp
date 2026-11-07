using System.Globalization;

namespace DecisionGateway.StartupHarness;

/// <summary>
/// Measures how long a stdio MCP host takes from process start to its answer to <c>initialize</c>.
/// Exit codes: 0 = measured and reported, 1 = measurement failed, 2 = invalid arguments or missing binary.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan HarnessDeadline = TimeSpan.FromMinutes(10);

    private static async Task<int> Main(string[] args)
    {
        if (args is [StartupProbe.WarmupNoopArgument])
        {
            // Throw-away process of the warm-up: answers like a host would, so the probe path runs unchanged.
            Console.Out.WriteLine(StartupProbe.SampleInitializeResponse);
            return 0;
        }

        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }

        if (!File.Exists(options.BinaryPath))
        {
            Console.Error.WriteLine($"error: binary not found: {options.BinaryPath}");
            return 2;
        }

        using var deadline = new CancellationTokenSource(HarnessDeadline);
        try
        {
            await StartupProbe.WarmUpAsync(deadline.Token);

            var samples = new List<double>(options.Runs);
            for (var run = 1; run <= options.Runs; run++)
            {
                var elapsed = await StartupProbe.MeasureOnceAsync(options, deadline.Token);
                samples.Add(elapsed.TotalMilliseconds);
                Console.Out.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $"run {run}/{options.Runs}: {elapsed.TotalMilliseconds:F3} ms"));
            }

            var summary = StartupStatistics.Summarize(samples);

            var fullOutputPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
            await using (var file = File.Create(fullOutputPath))
            {
                StartupReport.Write(file, options.Rid, summary, DateTimeOffset.UtcNow);
            }

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"summary: cold_ms={summary.ColdMs:F3} p50_ms={summary.P50Ms:F3} p95_ms={summary.P95Ms:F3}"));
            return 0;
        }
        catch (StartupProbeException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"error: the harness exceeded its overall deadline of {HarnessDeadline.TotalMinutes:0} minutes.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: could not write the report to '{options.OutputPath}': {exception.Message}");
            return 1;
        }
    }
}
