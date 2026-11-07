using DecisionGateway.Host;
using DecisionGateway.Host.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

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
    .WithCallToolHandler((context, ct) =>
    {
        // Spike diagnostics (DG_DIAG_GC=1 only): probe the live heap shortly after this call.
        GcDiagnostics.ScheduleProbe();
        return SpikeTool.CallAsync(context, ct);
    });

// Spike switch (DG_STDIO_TRANSPORT=pipe): replace the SDK stdio transport with the PipeReader-based prototype.
// The SDK registers its transport with a factory, so replacing the descriptor means the SDK transport is never built.
if (PipeStdioTransportOptions.IsSelected())
{
    var transportOptions = PipeStdioTransportOptions.FromEnvironment();
    builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(sp =>
        PipeStdioServerTransport.CreateForConsole(transportOptions, sp.GetService<ILoggerFactory>())));
}

await builder.Build().RunAsync();
return 0;
