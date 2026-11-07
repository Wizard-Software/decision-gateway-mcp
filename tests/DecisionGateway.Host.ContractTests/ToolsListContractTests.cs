using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

public sealed class ToolsListContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public async Task Req_1_1_InitializeAndToolsList_ReturnsExactlyOneEvaluateDecisionTool()
    {
        // One budget for the whole test; the token goes into every call so a silent host fails instead of hanging.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var client = await HostProcess.ConnectAsync(ct);

        client.ServerInfo.Should().NotBeNull();
        client.ServerInfo!.Name.Should().Be("decision-gateway-mcp");

        client.ServerCapabilities.Tools.Should().NotBeNull();
        client.ServerCapabilities.Tools!.ListChanged.Should().NotBe(true);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        var tool = tools.Should().ContainSingle().Which;
        tool.Name.Should().Be("evaluate_decision");

        var schema = tool.ProtocolTool.InputSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required")[0].GetString().Should().Be("question");
    }
}
