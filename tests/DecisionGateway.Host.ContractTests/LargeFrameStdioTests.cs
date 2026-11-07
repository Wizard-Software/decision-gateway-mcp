using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

/// <summary>
/// Serial collection shared by every <c>LargeFrame*</c> test class: large-frame measurements must not run in parallel
/// with other tests or with each other, otherwise working-set samples would be polluted by neighbours.
/// </summary>
[CollectionDefinition(LargeFrameSerial.Name, DisableParallelization = true)]
public sealed class LargeFrameSerial
{
    public const string Name = "LargeFrame";
}

/// <summary>
/// Spike measurement: a single <c>tools/call</c> frame carrying a 20 MiB (about 27 MB base64) <c>image_payload</c>
/// travels through the real stdio transport of a freshly started host process.
/// Numbers are printed to the test output as findings; assertions cover correctness only.
/// </summary>
[Collection(LargeFrameSerial.Name)]
public sealed class LargeFrameStdioTests
{
    internal const int PayloadBytes = 20 * 1024 * 1024;
    internal const int Runs = 5;
    internal static readonly int SeqCalls = ReadSeqCalls();

    /// <summary>Optional override of the serial-call count (spike measurements of growth over more calls).</summary>
    private const string SeqCallsVariable = "DG_LARGE_FRAME_SEQ_CALLS";

    private const string Marker = "DG-LARGE-FRAME-MARKER-91d4";
    private const string BinaryPathVariable = "DG_BINARY_PATH";
    private const int FragmentStart = 1_000_000;
    private const int FragmentLength = 64;
    private const int BaselineWindowMs = 200;
    private const int SettleDelayMs = 500;
    private const int SamplingIntervalMs = 10;

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Contract")]
    [Trait("Req", "A5.3")]
    public async Task Req_A5_3_LargeFrame_20MiBImagePayload_RoundTripsOverStdio()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        // The kernel maximum is a process-wide, monotonic value: remember the level before this test starts.
        var kernel = new KernelRssTracker();

        var payload = BuildPayload(PayloadBytes);
        payload.Length.Should().Be(27_962_028);

        // Built once, before any Stopwatch: serialization cost must not leak into the measured interval.
        var frame = BuildCallFrame(2, Marker, payload);
        (frame.Length - payload.Length).Should().BeLessThan(512, "base64 must not be escaped by the writer");

        var fragment = AlnumFragment(payload, FragmentStart, FragmentLength);
        fragment.Length.Should().Be(FragmentLength);

        var runs = new List<RunResult>();
        for (var i = 1; i <= Runs; i++)
        {
            var run = await RunOnceAsync(i, frame, fragment, ct);
            var kernelLabel = kernel.Observe();
            runs.Add(run);

            output.WriteLine(
                $"LARGE-FRAME-RUN n={run.Index} elapsed_ms={run.ElapsedMs} baseline_ws_mb={Mb(run.BaselineWs)} "
                + $"peak_ws_mb={Mb(run.PeakWs)} delta_ws_mb={Mb(run.PeakWs - run.BaselineWs)} settled_ws_mb={Mb(run.SettledWs)} "
                + $"ratio={Ratio(run)} kernel_maxrss_mb={kernelLabel} ok={(run.Ok ? "true" : "false")}");
            if (!run.Ok)
            {
                output.WriteLine($"LARGE-FRAME-RUN-FAILURE n={run.Index} exit={run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} "
                    + $"reason={run.Failure} stderr_head={run.StderrHead}");
            }
        }

        var runtime = Environment.GetEnvironmentVariable(BinaryPathVariable) is null ? "jit" : "native";
        var transport = Environment.GetEnvironmentVariable("DG_STDIO_TRANSPORT") ?? "pipe";
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        output.WriteLine(
            $"LARGE-FRAME runtime={runtime} transport={transport} config={config} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.OSArchitecture} "
            + $"runs={Runs} payload_chars={payload.Length} frame_bytes={frame.Length} "
            + $"elapsed_ms_min/median/max={MinMedMax(runs.Select(r => (double)r.ElapsedMs), "F0")} "
            + $"delta_ws_mb_min/median/max={MinMedMax(runs.Select(r => (r.PeakWs - r.BaselineWs) / 1048576.0), "F1")} "
            + $"ratio_min/median/max={MinMedMax(runs.Select(r => (double)(r.PeakWs - r.BaselineWs) / PayloadBytes), "F2")} "
            + $"peak_ws_mb_max={Mb(runs.Max(r => r.PeakWs))} kernel_maxrss_mb={kernel.SummaryLabel()}");

        var reason = string.Join(
            "; ",
            runs.Where(r => !r.Ok).Select(r => $"run {r.Index}: exit={r.ExitCode} failure={r.Failure} stderr={r.StderrHead}"));
        runs.Should().OnlyContain(r => r.Ok, "{0}", reason);
        runs.Should().OnlyContain(r => r.PeakWs > r.BaselineWs);
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Contract")]
    [Trait("Req", "A5.3")]
    public async Task Req_A5_3_LargeFrame_SerialCalls_PeakPerCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;
        var kernel = new KernelRssTracker();

        // One distinct frame per request id (2..6): same payload, built before the first Stopwatch.
        var payload = BuildPayload(PayloadBytes);
        var frames = new byte[SeqCalls][];
        for (var k = 0; k < SeqCalls; k++)
        {
            frames[k] = BuildCallFrame(k + 2, Marker, payload);
        }

        var fragment = AlnumFragment(payload, FragmentStart, FragmentLength);
        var failures = new List<string>();
        var elapsed = new long[SeqCalls];
        var peaks = new long[SeqCalls];
        var settled = new long[SeqCalls];
        long baseline = 0;
        int completed = 0;
        string stderr;

        await using (var session = HostSession.Start(ct))
        {
            try
            {
                await session.InitializeAsync(ct);
                baseline = await SampleBaselineAsync(session.Child, ct);

                for (var k = 0; k < SeqCalls; k++)
                {
                    // A dedicated sampler per call: its peak belongs to this call only.
                    var sampler = WsSampler.Start(session.Child);
                    var stopwatch = Stopwatch.StartNew();
                    JsonElement response;
                    try
                    {
                        await session.SendFrameAsync(frames[k], ct);
                        response = await session.ReadUntilIdAsync(k + 2, ct);
                        stopwatch.Stop();
                    }
                    finally
                    {
                        peaks[k] = await sampler.StopAsync();
                    }

                    elapsed[k] = stopwatch.ElapsedMilliseconds;
                    await Task.Delay(SettleDelayMs, ct);
                    settled[k] = SampleOnce(session.Child);

                    var problem = CheckResponse(response);
                    if (problem is not null)
                    {
                        failures.Add($"call {k + 1}: {problem}");
                    }

                    completed++;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                failures.Add($"{e.GetType().Name}: {Truncate(e.Message)}");
            }

            var shutdown = await session.ShutdownAsync(ct);
            stderr = shutdown.Stderr;
            if (shutdown.Failure is not null)
            {
                failures.Add(shutdown.Failure);
            }

            if (!session.StdoutLines.All(IsJsonRpcFrame))
            {
                failures.Add("stdout contains a line that is not a JSON-RPC frame");
            }
        }

        if (stderr.Contains(Marker, StringComparison.Ordinal))
        {
            failures.Add("stderr contains the marker");
        }

        if (stderr.Contains(fragment, StringComparison.Ordinal))
        {
            failures.Add("stderr contains a payload fragment");
        }

        var kernelLabel = kernel.Observe();
        for (var k = 0; k < completed; k++)
        {
            output.WriteLine($"LARGE-FRAME-SEQ call={k + 1} elapsed_ms={elapsed[k]} peak_ws_mb={Mb(peaks[k])} settled_ws_mb={Mb(settled[k])}");
        }

        if (completed > 0)
        {
            output.WriteLine(
                $"LARGE-FRAME-SEQ-SUMMARY calls={completed} baseline_ws_mb={Mb(baseline)} peak_ws_mb_first={Mb(peaks[0])} "
                + $"peak_ws_mb_last={Mb(peaks[completed - 1])} settled_ws_mb_last={Mb(settled[completed - 1])} kernel_maxrss_mb={kernelLabel}");
        }

        // Growth (or its absence) across the serial calls is a finding, not an assertion.
        var reason = string.Join("; ", failures);
        failures.Should().BeEmpty("{0}", reason);
        completed.Should().Be(SeqCalls);
    }

    private static int ReadSeqCalls() =>
        int.TryParse(Environment.GetEnvironmentVariable(SeqCallsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var calls) && calls > 0
            ? calls
            : 5;

    internal static string BuildPayload(int bytes)
    {
        var data = new byte[bytes];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = unchecked((byte)((i * 31) + 7));
        }

        return Convert.ToBase64String(data);
    }

    /// <summary>Builds one single-line <c>tools/call</c> frame (without the trailing newline) carrying the extra <c>image_payload</c> argument.</summary>
    internal static byte[] BuildCallFrame(int id, string question, string imagePayload)
    {
        var buffer = new ArrayBufferWriter<byte>(imagePayload.Length + 1024);
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", "tools/call");
            writer.WriteStartObject("params");
            writer.WriteString("name", "evaluate_decision");
            writer.WriteStartObject("arguments");
            writer.WriteString("question", question);
            writer.WriteString("image_payload", imagePayload);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>First <paramref name="length"/>-character window at or after <paramref name="start"/> made only of [A-Za-z0-9].</summary>
    internal static string AlnumFragment(string payload, int start, int length)
    {
        var run = 0;
        for (var i = start; i < payload.Length; i++)
        {
            run = char.IsAsciiLetterOrDigit(payload[i]) ? run + 1 : 0;
            if (run == length)
            {
                return payload.Substring(i - length + 1, length);
            }
        }

        throw new InvalidOperationException("No alphanumeric fragment of the requested length was found in the payload.");
    }

    private static async Task<RunResult> RunOnceAsync(int index, byte[] frame, string fragment, CancellationToken ct)
    {
        var failures = new List<string>();
        long baseline = 0, peak = 0, settled = 0, elapsedMs = 0;
        int? exitCode = null;
        var stderr = string.Empty;

        await using var session = HostSession.Start(ct);
        try
        {
            await session.InitializeAsync(ct);
            baseline = await SampleBaselineAsync(session.Child, ct);

            var sampler = WsSampler.Start(session.Child);
            var stopwatch = Stopwatch.StartNew();
            JsonElement response;
            try
            {
                await session.SendFrameAsync(frame, ct);
                response = await session.ReadUntilIdAsync(2, ct);
                stopwatch.Stop();
            }
            finally
            {
                peak = await sampler.StopAsync();
            }

            elapsedMs = stopwatch.ElapsedMilliseconds;
            await Task.Delay(SettleDelayMs, ct);
            settled = SampleOnce(session.Child);

            var problem = CheckResponse(response);
            if (problem is not null)
            {
                failures.Add(problem);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // A broken pipe on a 28 MB frame is a spike result, not a harness error: record it and keep the numbers.
            failures.Add($"{e.GetType().Name}: {Truncate(e.Message)}");
        }

        var shutdown = await session.ShutdownAsync(ct);
        exitCode = shutdown.ExitCode;
        stderr = shutdown.Stderr;
        if (shutdown.Failure is not null)
        {
            failures.Add(shutdown.Failure);
        }

        if (!session.StdoutLines.All(IsJsonRpcFrame))
        {
            failures.Add("stdout contains a line that is not a JSON-RPC frame: "
                + Truncate(session.StdoutLines.FirstOrDefault(l => !IsJsonRpcFrame(l)) ?? string.Empty));
        }

        if (stderr.Contains(Marker, StringComparison.Ordinal))
        {
            failures.Add("stderr contains the marker");
        }

        if (stderr.Contains(fragment, StringComparison.Ordinal))
        {
            failures.Add("stderr contains a payload fragment");
        }

        return new RunResult(
            index,
            failures.Count == 0,
            elapsedMs,
            baseline,
            peak,
            settled,
            exitCode,
            Truncate(stderr),
            string.Join(" | ", failures));
    }

    /// <summary>Returns a short problem description, or null when the response is a successful spike result.</summary>
    private static string? CheckResponse(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            return "response carries an error: " + Truncate(error.GetRawText());
        }

        if (!response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("structuredContent", out var structured)
            || !structured.TryGetProperty("selected_option", out var selected)
            || selected.ValueKind != JsonValueKind.String
            || selected.GetString() != "spike")
        {
            return "response has no structuredContent.selected_option == \"spike\"";
        }

        return null;
    }

    /// <summary>Maximum working set seen during a short idle window after the handshake.</summary>
    private static async Task<long> SampleBaselineAsync(Process process, CancellationToken ct)
    {
        var sampler = WsSampler.Start(process);
        await Task.Delay(BaselineWindowMs, ct);
        return await sampler.StopAsync();
    }

    private static long SampleOnce(Process process)
    {
        process.Refresh();
        return process.WorkingSet64;
    }

    private static bool TryParseFrame(string line, out JsonElement frame)
    {
        frame = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
            {
                return false;
            }

            frame = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsJsonRpcFrame(string line) => TryParseFrame(line, out _);

    private static string Truncate(string value, int max = 200) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    private static string Ratio(RunResult run) =>
        ((double)(run.PeakWs - run.BaselineWs) / PayloadBytes).ToString("F2", CultureInfo.InvariantCulture);

    private static string MinMedMax(IEnumerable<double> values, string format)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return string.Join('/', new[] { sorted[0], median, sorted[^1] }.Select(v => v.ToString(format, CultureInfo.InvariantCulture)));
    }

    private const int RUsageChildren = -1;

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern int GetRUsage(int who, out RUsage usage);

    /// <summary>Current maximum RSS (bytes) over all reaped children of this process, or null where unavailable.</summary>
    private static long? KernelMaxRssBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (GetRUsage(RUsageChildren, out var usage) != 0)
            {
                return null;
            }

            // macOS reports bytes, Linux reports KiB.
            return OperatingSystem.IsMacOS() ? usage.MaxRss : usage.MaxRss * 1024;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timeval
    {
        public long TvSec;
        public long TvUsec;
    }

    /// <summary>Full 64-bit <c>struct rusage</c> (144 bytes): two timevals followed by fourteen longs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsage
    {
        public Timeval Utime;
        public Timeval Stime;
        public long MaxRss;
        public long IxRss;
        public long IdRss;
        public long IsRss;
        public long MinFlt;
        public long MajFlt;
        public long NSwap;
        public long InBlock;
        public long OuBlock;
        public long MsgSnd;
        public long MsgRcv;
        public long NSignals;
        public long NvCsw;
        public long NivCsw;
    }

    internal sealed record RunResult(
        int Index,
        bool Ok,
        long ElapsedMs,
        long BaselineWs,
        long PeakWs,
        long SettledWs,
        int? ExitCode,
        string StderrHead,
        string Failure);

    /// <summary>
    /// Tracks the kernel-reported maximum RSS of reaped children. The value only grows and is shared by every child the
    /// test process ever reaped, so a number is reported only when this observation raised the maximum.
    /// </summary>
    private sealed class KernelRssTracker
    {
        private readonly long _before;
        private long _max;

        public KernelRssTracker()
        {
            // A struct that is too small would let getrusage write past it: verify the layout before the first call.
            Marshal.SizeOf<RUsage>().Should().Be(144);
            _before = KernelMaxRssBytes() ?? 0;
            _max = _before;
        }

        public string Observe()
        {
            var now = KernelMaxRssBytes();
            if (now is null)
            {
                return "n/a";
            }

            if (now.Value > _max)
            {
                _max = now.Value;
                return Mb(now.Value);
            }

            return "masked";
        }

        public string SummaryLabel()
        {
            if (KernelMaxRssBytes() is null)
            {
                return "n/a";
            }

            return _max > _before ? Mb(_max) : "masked";
        }
    }

    /// <summary>Background sampler of a process working set (maximum of samples taken every 10 ms).</summary>
    private sealed class WsSampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;
        private long _peak;

        public static WsSampler Start(Process process)
        {
            var sampler = new WsSampler();
            sampler._loop = Task.Run(() => sampler.LoopAsync(process, sampler._cts.Token));
            return sampler;
        }

        public void Dispose() => _cts.Dispose();

        public async Task<long> StopAsync()
        {
            await _cts.CancelAsync();
            await _loop;
            _cts.Dispose();
            return Volatile.Read(ref _peak);
        }

        private async Task LoopAsync(Process process, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    process.Refresh();
                    _peak = Math.Max(_peak, process.WorkingSet64);
                }
                catch (InvalidOperationException)
                {
                    // The process has exited: sampling ends.
                    return;
                }

                try
                {
                    await Task.Delay(SamplingIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>A host child process with raw stdin/stdout/stderr access, killed on cancellation and on dispose.</summary>
    private sealed class HostSession : IAsyncDisposable
    {
        private readonly CancellationTokenRegistration _killOnCancel;
        private readonly Task<string> _stderrTask;

        private HostSession(Process child, CancellationToken ct)
        {
            Child = child;
            _killOnCancel = ct.Register(Kill);
            _stderrTask = child.StandardError.ReadToEndAsync(ct);
        }

        public Process Child { get; }

        public List<string> StdoutLines { get; } = [];

        public static HostSession Start(CancellationToken ct)
        {
            var (command, arguments, environment) = HostProcess.ResolveStartInfo();
            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Clear();
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }

            var process = new Process { StartInfo = startInfo };
            process.Start();
            return new HostSession(process, ct);
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            await SendLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"large-frame-test","version":"1.0"}}}""",
                ct);
            await ReadUntilIdAsync(1, ct);
            await SendLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", ct);
        }

        public async Task SendLineAsync(string line, CancellationToken ct) =>
            await SendFrameAsync(Encoding.UTF8.GetBytes(line), ct);

        public async Task SendFrameAsync(byte[] frame, CancellationToken ct)
        {
            var stdin = Child.StandardInput.BaseStream;
            await stdin.WriteAsync(frame, ct);
            await stdin.WriteAsync("\n"u8.ToArray(), ct);
            await stdin.FlushAsync(ct);
        }

        public async Task<JsonElement> ReadUntilIdAsync(int id, CancellationToken ct)
        {
            while (true)
            {
                var line = await Child.StandardOutput.ReadLineAsync(ct)
                    ?? throw new IOException($"Host closed stdout before the response with id {id}.");
                StdoutLines.Add(line);
                if (TryParseFrame(line, out var frame)
                    && frame.TryGetProperty("id", out var frameId)
                    && frameId.ValueKind == JsonValueKind.Number
                    && frameId.GetInt32() == id)
                {
                    return frame;
                }
            }
        }

        /// <summary>Closes stdin, waits up to 10 s for exit (kills otherwise), drains stdout and returns stderr.</summary>
        public async Task<(int? ExitCode, string Stderr, string? Failure)> ShutdownAsync(CancellationToken ct)
        {
            string? failure = null;
            try
            {
                Child.StandardInput.Close();
            }
            catch (IOException)
            {
                // Pipe already broken.
            }

            using (var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                exitCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await Child.WaitForExitAsync(exitCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Kill();
                    await Child.WaitForExitAsync(ct);
                    failure = "host did not exit within 10 seconds after stdin was closed";
                }
            }

            while (await Child.StandardOutput.ReadLineAsync(ct) is { } rest)
            {
                StdoutLines.Add(rest);
            }

            var stderr = await _stderrTask;
            return (Child.ExitCode, stderr, failure);
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            _killOnCancel.Dispose();
            Child.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Kill()
        {
            try
            {
                if (!Child.HasExited)
                {
                    Child.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited or disposed.
            }
        }
    }
}
