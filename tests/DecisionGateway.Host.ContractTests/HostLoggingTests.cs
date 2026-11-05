using AwesomeAssertions;
using DecisionGateway.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace DecisionGateway.Host.ContractTests;

public sealed class HostLoggingTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Req", "A6.1")]
    public void Req_A6_4_ConsoleLogger_DropWriteQueue1024AndMcpCategoryFloor()
    {
        using var services = new ServiceCollection()
            .AddLogging(logging => HostLogging.Configure(logging))
            .BuildServiceProvider();

        var console = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        console.LogToStandardErrorThreshold.Should().Be(LogLevel.Trace);
        console.QueueFullMode.Should().Be(ConsoleLoggerQueueFullMode.DropWrite);
        console.MaxQueueLength.Should().Be(1024);
        console.FormatterName.Should().Be("json");

        services.GetServices<ILoggerProvider>().Should().ContainSingle()
            .Which.Should().BeOfType<ConsoleLoggerProvider>();

        services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules
            .Should().Contain(r => r.CategoryName == "ModelContextProtocol" && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Req", "A6.1")]
    public void Req_A6_4_McpCategoryFilter_BlocksBelowInformationWhenGlobalLevelIsTrace()
    {
        using var services = new ServiceCollection()
            .AddLogging(logging => HostLogging.Configure(logging.SetMinimumLevel(LogLevel.Trace)))
            .BuildServiceProvider();
        var factory = services.GetRequiredService<ILoggerFactory>();

        var mcp = factory.CreateLogger("ModelContextProtocol.Server.McpServer");
        mcp.IsEnabled(LogLevel.Debug).Should().BeFalse();
        mcp.IsEnabled(LogLevel.Information).Should().BeTrue();

        factory.CreateLogger("DecisionGateway.Host").IsEnabled(LogLevel.Debug).Should().BeTrue();
    }
}
