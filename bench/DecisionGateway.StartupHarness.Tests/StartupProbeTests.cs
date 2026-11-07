using System.Text;
using AwesomeAssertions;
using DecisionGateway.StartupHarness;

namespace DecisionGateway.StartupHarness.Tests;

/// <summary>End-to-end probe tests against the real JIT host. Excluded from the build-test job by the Startup category.</summary>
[Trait("Category", "Startup")]
public sealed class StartupProbeTests
{
    // The probe never touches the output path; a fixed name that is never created keeps the tests free of temp files.
    private static readonly string UnusedOutputPath = Path.Combine(AppContext.BaseDirectory, "unused-startup-probe.json");

    private static HarnessOptions JitHost(int runs = 2) => new(
        BinaryPath: Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath
            ? hostPath
            : FindDotnet(),
        Arguments: [Path.Combine(AppContext.BaseDirectory, "decision-gateway-mcp.dll")],
        Runs: runs,
        Rid: "local",
        OutputPath: UnusedOutputPath,
        Timeout: TimeSpan.FromSeconds(60));

    [Fact]
    public async Task MeasureOnceAsync_returns_positive_time_for_real_host()
    {
        var elapsed = await StartupProbe.MeasureOnceAsync(JitHost(), TestContext.Current.CancellationToken);

        elapsed.Should().BePositive().And.BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task MeasureOnceAsync_fails_when_process_exits_without_response()
    {
        // `dotnet --version` prints the SDK version and exits: no initialize response is ever produced.
        var options = JitHost() with { Arguments = ["--version"] };

        await FluentActions.Awaiting(() => StartupProbe.MeasureOnceAsync(options, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<StartupProbeException>().WithMessage("*exited*");
    }

    [Fact]
    public async Task MeasureOnceAsync_fails_when_the_binary_cannot_be_started()
    {
        var options = JitHost() with { BinaryPath = Path.Combine(AppContext.BaseDirectory, "no-such-binary") };

        await FluentActions.Awaiting(() => StartupProbe.MeasureOnceAsync(options, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<StartupProbeException>();
    }

    [Fact]
    public async Task MeasureOnceAsync_times_out_when_the_host_never_answers()
    {
        // A 1 ms budget cannot cover a JIT host start-up, so the probe must report a timeout.
        var options = JitHost() with { Timeout = TimeSpan.FromMilliseconds(1) };

        await FluentActions.Awaiting(() => StartupProbe.MeasureOnceAsync(options, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<StartupProbeException>().WithMessage("*timed out*");
    }

    [Fact]
    public async Task Warmup_noop_prints_one_response_line_and_exits_zero()
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "DecisionGateway.StartupHarness.dll");
        var options = JitHost() with { Arguments = [harnessDll, "--warmup-noop"] };

        var elapsed = await StartupProbe.MeasureOnceAsync(options, TestContext.Current.CancellationToken);

        elapsed.Should().BePositive();
    }

    [Fact]
    public async Task Program_runs_the_full_loop_and_writes_the_report()
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "DecisionGateway.StartupHarness.dll");
        var host = JitHost();
        var outputDir = Path.Combine(AppContext.BaseDirectory, "program-run", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(outputDir, "startup-local.json");

        var result = await RunHarnessAsync(
            host.BinaryPath,
            [
                harnessDll, "--binary", host.BinaryPath, "--arg", host.Arguments[0],
                "--runs", "3", "--rid", "local", "--output", outputPath, "--timeout-seconds", "60",
            ]);

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("run 1/3:").And.Contain("run 3/3:");
        result.Stdout.Should().MatchRegex(@"summary: cold_ms=\d+\.\d{3} p50_ms=\d+\.\d{3} p95_ms=\d+\.\d{3}");
        File.Exists(outputPath).Should().BeTrue();
        Directory.Delete(outputDir, recursive: true);
    }

    [Fact]
    public async Task Program_exits_2_for_invalid_arguments()
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "DecisionGateway.StartupHarness.dll");

        var result = await RunHarnessAsync(JitHost().BinaryPath, [harnessDll, "--runs", "50", "--rid", "local", "--output", "o.json"]);

        result.ExitCode.Should().Be(2);
        result.Stderr.Should().Contain("--binary");
    }

    [Fact]
    public async Task Program_exits_1_when_the_binary_never_answers()
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "DecisionGateway.StartupHarness.dll");
        var host = JitHost();
        var outputPath = Path.Combine(AppContext.BaseDirectory, "program-run", "never-written.json");

        var result = await RunHarnessAsync(
            host.BinaryPath,
            [harnessDll, "--binary", host.BinaryPath, "--arg", "--version", "--runs", "2", "--rid", "local", "--output", outputPath]);

        result.ExitCode.Should().Be(1);
        File.Exists(outputPath).Should().BeFalse();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHarnessAsync(string dotnet, string[] arguments)
    {
        var ct = TestContext.Current.CancellationToken;
        var startInfo = new System.Diagnostics.ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string FindDotnet()
    {
        var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("dotnet not found on PATH");
    }
}

[Trait("Category", "Unit")]
public sealed class StartupProbeParsingTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TryParseInitializeResponse_accepts_a_result_with_id_1() =>
        StartupProbe.TryParseInitializeResponse(Utf8("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25"}}"""))
            .Should().BeTrue();

    [Fact]
    public void TryParseInitializeResponse_accepts_the_built_in_sample() =>
        StartupProbe.TryParseInitializeResponse(Utf8(StartupProbe.SampleInitializeResponse)).Should().BeTrue();

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":2,"result":{}}""")] // a response to another request
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/message","params":{}}""")] // a notification
    [InlineData("10.0.403")] // a non-JSON-object line (valid JSON, not an object)
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"jsonrpc":"2.0","id":"1","result":{}}""")] // string id is not the request id we sent
    public void TryParseInitializeResponse_ignores_lines_that_are_not_the_response(string line) =>
        StartupProbe.TryParseInitializeResponse(Utf8(line)).Should().BeFalse();

    [Fact]
    public void TryParseInitializeResponse_throws_for_an_error_response() =>
        FluentActions.Invoking(() => StartupProbe.TryParseInitializeResponse(
                Utf8("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"nope"}}""")))
            .Should().Throw<StartupProbeException>().WithMessage("*nope*");

    [Fact]
    public async Task BoundedLineReader_reads_lines_and_returns_null_at_eof()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Utf8("first\r\nsecond\n\nlast"));
        var reader = new BoundedLineReader(stream, maxLineBytes: 1024);

        Encoding.UTF8.GetString((await reader.ReadLineAsync(ct))!).Should().Be("first\r");
        Encoding.UTF8.GetString((await reader.ReadLineAsync(ct))!).Should().Be("second");
        (await reader.ReadLineAsync(ct))!.Should().BeEmpty();
        Encoding.UTF8.GetString((await reader.ReadLineAsync(ct))!).Should().Be("last");
        (await reader.ReadLineAsync(ct)).Should().BeNull();
    }

    [Fact]
    public async Task BoundedLineReader_rejects_a_line_longer_than_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(new byte[(1024 * 1024) + 1]); // no newline at all
        var reader = new BoundedLineReader(stream, maxLineBytes: 1024 * 1024);

        await FluentActions.Awaiting(async () => await reader.ReadLineAsync(ct))
            .Should().ThrowAsync<StartupProbeException>().WithMessage("*1048576*");
    }

    [Fact]
    public async Task BoundedLineReader_accepts_a_line_of_exactly_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new byte[1025];
        Array.Fill(data, (byte)'a', 0, 1024);
        data[1024] = (byte)'\n';
        using var stream = new MemoryStream(data);
        var reader = new BoundedLineReader(stream, maxLineBytes: 1024);

        (await reader.ReadLineAsync(ct))!.Should().HaveCount(1024);
    }
}
