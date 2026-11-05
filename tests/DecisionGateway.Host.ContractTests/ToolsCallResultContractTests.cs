using System.Text.Json;
using AwesomeAssertions;
using ModelContextProtocol.Protocol;

namespace DecisionGateway.Host.ContractTests;

public sealed class ToolsCallResultContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Req", "A5.1")]
    public async Task Req_A5_1_ToolsCall_ResultIsSnakeCaseJsonInTextAndStructuredContent()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var client = await HostProcess.ConnectAsync(ct);
        var result = await client.CallToolAsync(
            "evaluate_decision",
            new Dictionary<string, object?> { ["question"] = "q" },
            cancellationToken: ct);

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Which.Text;
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["selected_option", "confidence", "latency_ms", "provider", "fallback_used"]);
        root.GetProperty("selected_option").GetString().Should().Be("spike");
        root.GetProperty("provider").GetString().Should().Be("jev");
        root.GetProperty("fallback_used").GetBoolean().Should().BeFalse();
        root.GetProperty("latency_ms").GetInt64().Should().Be(0);

        result.StructuredContent.Should().NotBeNull();
        JsonSerializer.Serialize(result.StructuredContent).Should().Be(JsonSerializer.Serialize(root));
    }
}
