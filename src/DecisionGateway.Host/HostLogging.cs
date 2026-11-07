using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DecisionGateway.Host;

/// <summary>Single place of logging setup: every provider writes to stderr, stdout stays with the MCP transport.</summary>
internal static class HostLogging
{
    public const int MaxQueueLength = 1024;
    public const string McpCategory = "ModelContextProtocol";

    /// <summary>
    /// Registers the JSON console logger as the only provider, routes every level to stderr, drops records on a full
    /// queue instead of blocking, and floors the MCP SDK categories at <see cref="LogLevel.Information"/>.
    /// </summary>
    public static ILoggingBuilder Configure(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole();
        logging.Services.Configure<ConsoleLoggerOptions>(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
            options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite;
            options.MaxQueueLength = MaxQueueLength;
        });
        logging.AddFilter(McpCategory, LogLevel.Information);
        return logging;
    }
}
