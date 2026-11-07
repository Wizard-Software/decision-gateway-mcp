using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

/// <summary>
/// Spike measurement: the pipe stdio transport (the default; forced for this test whatever the outer environment) with a 1 MiB frame
/// limit receives the 20 MiB-payload <c>tools/call</c> frame. The frame must be rejected with a JSON-RPC error carrying
/// its id, the session must keep working, and the working-set growth is printed as a finding.
/// </summary>
[Collection(LargeFrameSerial.Name)]
public sealed class LargeFramePipeLimitTests
{
    private const int LimitBytes = 1024 * 1024;
    private const int SamplingIntervalMs = 10;

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Contract")]
    [Trait("Req", "A5.3")]
    public async Task PipeTransport_FrameAboveLimit_IsRejectedBeforeParsing_AndSessionContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        var payload = LargeFrameStdioTests.BuildPayload(LargeFrameStdioTests.PayloadBytes);
        var frame = LargeFrameStdioTests.BuildCallFrame(2, "DG-PIPE-LIMIT-MARKER-5e2b", payload);

        using var child = HostProcess.StartRaw(new Dictionary<string, string?>
        {
            ["DG_STDIO_TRANSPORT"] = null,
            ["DG_MAX_FRAME_BYTES"] = LimitBytes.ToString(CultureInfo.InvariantCulture),
        });
        using var kill = ct.Register(() => KillQuietly(child));
        var stderrTask = child.StandardError.ReadToEndAsync(ct);
        var stdin = child.StandardInput.BaseStream;

        try
        {
            await WriteLineAsync(stdin, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"pipe-limit-test","version":"1.0"}}}""", ct);
            (await ReadUntilIdAsync(child, 1, ct)).TryGetProperty("result", out _).Should().BeTrue();
            await WriteLineAsync(stdin, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", ct);

            await Task.Delay(200, ct);
            child.Refresh();
            var baseline = child.WorkingSet64;

            using var samplerCts = new CancellationTokenSource();
            var peak = baseline;
            var sampler = Task.Run(
                async () =>
                {
                    while (!samplerCts.IsCancellationRequested)
                    {
                        child.Refresh();
                        peak = Math.Max(peak, child.WorkingSet64);
                        try
                        {
                            await Task.Delay(SamplingIntervalMs, samplerCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                },
                CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            await stdin.WriteAsync(frame, ct);
            await WriteLineAsync(stdin, string.Empty, ct);
            var rejection = await ReadUntilIdAsync(child, 2, ct);
            stopwatch.Stop();
            await samplerCts.CancelAsync();
            await sampler;

            rejection.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);

            await WriteLineAsync(stdin, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", ct);
            (await ReadUntilIdAsync(child, 3, ct)).TryGetProperty("result", out _).Should().BeTrue();

            output.WriteLine(
                $"LARGE-FRAME-PIPE-LIMIT runtime={(Environment.GetEnvironmentVariable("DG_BINARY_PATH") is null ? "jit" : "native")} "
                + $"limit_bytes={LimitBytes} frame_bytes={frame.Length} elapsed_ms={stopwatch.ElapsedMilliseconds} "
                + $"baseline_ws_mb={Mb(baseline)} peak_ws_mb={Mb(peak)} delta_ws_mb={Mb(peak - baseline)}");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(10_000))
            {
                KillQuietly(child);
            }
        }

        var stderr = await stderrTask;
        stderr.Should().NotContain("DG-PIPE-LIMIT-MARKER-5e2b");
        stderr.Should().Contain("rejected a frame");
    }

    private static async Task WriteLineAsync(Stream stdin, string line, CancellationToken ct)
    {
        await stdin.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stdin.FlushAsync(ct);
    }

    private static async Task<JsonElement> ReadUntilIdAsync(Process child, int id, CancellationToken ct)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(ct) ?? throw new IOException($"stdout closed before id {id}");
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var frameId)
                && frameId.ValueKind == JsonValueKind.Number
                && frameId.GetInt32() == id)
            {
                return document.RootElement.Clone();
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
}
