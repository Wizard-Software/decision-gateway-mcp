using DecisionGateway.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The stdio transport writes MCP frames to the raw stdout stream, not to Console.Out, so redirecting Console.Out
// only catches accidental Console.Write* calls and cannot affect the protocol.
Console.SetOut(Console.Error);

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

// stdout carries JSON-RPC frames only; every log record goes to stderr.
HostLogging.Configure(builder.Logging);

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
