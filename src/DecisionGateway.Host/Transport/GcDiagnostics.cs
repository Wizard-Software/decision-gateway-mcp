using System.Globalization;
using System.Runtime;

namespace DecisionGateway.Host.Transport;

/// <summary>
/// Spike diagnostics (DG_DIAG_GC=1): after each <c>tools/call</c> response the transport-memory spike wants to know how much
/// of the managed heap is still live. Writes one line per probe to stderr: heap size and LOH size before and after a forced
/// full, compacting collection. Off by default; forcing collections changes the very numbers the spike measures, so it is
/// never enabled in measurement runs.
/// </summary>
internal static class GcDiagnostics
{
    public const string Variable = "DG_DIAG_GC";

    public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>Schedules a probe shortly after the current call, off the request path.</summary>
    public static void ScheduleProbe()
    {
        if (!IsEnabled)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Probe();
        });
    }

    private static void Probe()
    {
        var before = GC.GetGCMemoryInfo();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = GC.GetGCMemoryInfo();

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"GC-DIAG heap_before_mb={Mb(before.HeapSizeBytes)} loh_before_mb={Mb(Loh(before))} frag_before_mb={Mb(before.FragmentedBytes)} "
            + $"live_after_full_gc_mb={Mb(GC.GetTotalMemory(false))} heap_after_mb={Mb(after.HeapSizeBytes)} loh_after_mb={Mb(Loh(after))} "
            + $"committed_after_mb={Mb(after.TotalCommittedBytes)}"));
    }

    private static long Loh(GCMemoryInfo info) => info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
}
