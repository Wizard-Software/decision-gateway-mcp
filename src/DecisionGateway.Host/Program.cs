using DecisionGateway.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

// stdout carries JSON-RPC frames only; every log record goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var tool = SpikeTool.Build();

builder.Services
    .AddMcpServer(options =>
        options.ServerInfo = new()
        {
            Name = "decision-gateway-mcp",
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        })
    .WithStdioServerTransport()
    .WithListToolsHandler((context, ct) => SpikeTool.ListAsync(context, tool, ct))
    .WithCallToolHandler(SpikeTool.CallAsync);

await builder.Build().RunAsync();
return 0;
