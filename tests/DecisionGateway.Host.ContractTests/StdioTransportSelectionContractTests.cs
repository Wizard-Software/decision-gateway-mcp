using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

/// <summary>
/// Which stdio transport serves the host: the pipe transport by default, the MCP SDK transport with
/// <c>DG_STDIO_TRANSPORT=sdk</c> (fallback). The rest of the contract suite runs on whatever the environment selects; this
/// class pins one round trip on each transport so the fallback stays covered without running the whole suite twice.
/// The SDK transport is recognised by its own log category on stderr.
/// </summary>
public sealed class StdioTransportSelectionContractTests
{
    private const string SdkTransportCategory = "\"Category\":\"ModelContextProtocol.Server.StdioServerTransport\"";

    [Fact(Timeout = 60_000)]
    [Trait("Category", "Contract")]
    public async Task DefaultTransport_IsPipe_AndServesToolsCall()
    {
        var stderr = await RoundTripAsync(new Dictionary<string, string?> { ["DG_STDIO_TRANSPORT"] = null }, TestContext.Current.CancellationToken);
        stderr.Should().NotContain(SdkTransportCategory);
    }

    [Fact(Timeout = 60_000)]
    [Trait("Category", "Contract")]
    public async Task SdkFallback_ServesToolsCall()
    {
        var stderr = await RoundTripAsync(new Dictionary<string, string?> { ["DG_STDIO_TRANSPORT"] = "sdk" }, TestContext.Current.CancellationToken);
        stderr.Should().Contain(SdkTransportCategory);
    }

    /// <summary>initialize, tools/list and a valid tools/call; returns stderr after a clean shutdown.</summary>
    private static async Task<string> RoundTripAsync(IReadOnlyDictionary<string, string?> overrides, CancellationToken ct)
    {
        using var child = HostProcess.StartRaw(overrides);
        using var kill = ct.Register(() => KillQuietly(child));
        var stderrTask = child.StandardError.ReadToEndAsync(ct);
        var stdin = child.StandardInput.BaseStream;

        await WriteLineAsync(stdin, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"transport-selection-test","version":"1.0"}}}""", ct);
        (await ReadUntilIdAsync(child, 1, ct)).TryGetProperty("result", out _).Should().BeTrue();
        await WriteLineAsync(stdin, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", ct);

        await WriteLineAsync(stdin, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", ct);
        (await ReadUntilIdAsync(child, 2, ct)).GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(1);

        await WriteLineAsync(stdin, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"evaluate_decision","arguments":{"question":"q"}}}""", ct);
        var call = await ReadUntilIdAsync(child, 3, ct);
        call.GetProperty("result").GetProperty("structuredContent").GetProperty("selected_option").GetString().Should().Be("spike");

        child.StandardInput.Close();
        if (!child.WaitForExit(10_000))
        {
            KillQuietly(child);
        }

        return await stderrTask;
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
}
