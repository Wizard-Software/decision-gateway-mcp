using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

public sealed class StdoutPurityContractTests
{
    private const string Marker = "DG-PURITY-MARKER-7f3a";

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Req", "A6.1")]
    public async Task Req_7_2_Stdout_ContainsOnlyJsonRpcFrames()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

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

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        async Task SendAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }

        var stdoutLines = new List<string>();
        var responseIds = new HashSet<int>();
        JsonElement? errorResponse = null;

        async Task<bool> ReadFrameAsync()
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                return false;
            }

            stdoutLines.Add(line);
            if (line.Length > 0 && TryParseFrame(line, out var frame) && frame.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number)
            {
                responseIds.Add(id.GetInt32());
                if (id.GetInt32() == 4 && frame.TryGetProperty("error", out var error))
                {
                    errorResponse = error.Clone();
                }
            }

            return true;
        }

        await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"purity-test","version":"1.0"}}}""");
        while (!responseIds.Contains(1) && await ReadFrameAsync())
        {
        }

        await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await SendAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"DG-PURITY-MARKER-7f3a"}}}""");
        await SendAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""");

        while (!(responseIds.Contains(2) && responseIds.Contains(3) && responseIds.Contains(4)) && await ReadFrameAsync())
        {
        }

        process.StandardInput.Close();

        using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exitCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Host did not exit within 5 seconds after stdin was closed.");
        }

        while (await ReadFrameAsync())
        {
        }

        var stderr = await stderrTask;

        stdoutLines.Should().NotBeEmpty();
        foreach (var line in stdoutLines)
        {
            line.Should().NotBeEmpty("stdout must not contain blank lines");
            TryParseFrame(line, out var frame).Should().BeTrue("stdout line must be a JSON-RPC 2.0 object: {0}", line);
            frame.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        }

        responseIds.Should().BeEquivalentTo([1, 2, 3, 4]);
        errorResponse.Should().NotBeNull("the unknown tool must produce a JSON-RPC error");

        stderr.Should().Contain("Microsoft.Hosting.Lifetime");
        var stderrLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in stderrLines)
        {
            JsonDocument.Parse(line).RootElement.ValueKind.Should().Be(
                JsonValueKind.Object, "stderr line must be a JSON log record: {0}", line);
        }

        stderr.Should().NotContain(Marker);
    }

    private static bool TryParseFrame(string line, out JsonElement frame)
    {
        frame = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("jsonrpc", out var version)
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
}
