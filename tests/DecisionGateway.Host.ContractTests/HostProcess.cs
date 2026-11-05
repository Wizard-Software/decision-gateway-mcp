using ModelContextProtocol.Client;

namespace DecisionGateway.Host.ContractTests;

/// <summary>Starts the gateway host as a child process and connects an MCP client to it over stdio.</summary>
internal static class HostProcess
{
    private const string BinaryPathVariable = "DG_BINARY_PATH";
    private const string HostPathVariable = "DOTNET_HOST_PATH";
    private const string HostDllName = "decision-gateway-mcp.dll";

    // The child process starts from an EMPTY environment on purpose: secrets and DecisionGateway__*
    // variables of the test runner must not leak into the host under test. Later tests that need a
    // specific key inject it explicitly. Only the variables the runtime needs to start are copied.
    private static readonly string[] AllowedEnvironmentVariables =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "DOTNET_ROOT",
        "TMP",
        "TEMP",
        "TMPDIR",
        "SYSTEMROOT",
        "PATHEXT",
    ];

    /// <summary>Creates the stdio transport: a native binary when DG_BINARY_PATH is set, otherwise "dotnet" plus the host dll.</summary>
    public static StdioClientTransport CreateTransport()
    {
        var environment = new Dictionary<string, string?>();
        foreach (var name in AllowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        string command;
        string[]? arguments = null;

        var binaryPath = Environment.GetEnvironmentVariable(BinaryPathVariable);
        if (binaryPath is not null)
        {
            // Set but unusable must fail loudly: a silent fallback would let a native-binary job pass on the JIT host.
            if (!Path.IsPathFullyQualified(binaryPath) || !File.Exists(binaryPath))
            {
                throw new InvalidOperationException(
                    $"{BinaryPathVariable} is set but is not a fully qualified path to an existing file.");
            }

            command = binaryPath;
        }
        else
        {
            var hostDll = Path.Combine(AppContext.BaseDirectory, HostDllName);
            if (!File.Exists(hostDll))
            {
                throw new FileNotFoundException(
                    $"Host assembly '{HostDllName}' was not found in the test output directory.", hostDll);
            }

            var dotnet = Environment.GetEnvironmentVariable(HostPathVariable);
            command = string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet;
            arguments = [hostDll];
        }

        var options = new StdioClientTransportOptions
        {
            Name = "decision-gateway-mcp",
            Command = command,
            Arguments = arguments,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };

        return new StdioClientTransport(options);
    }

    /// <summary>Connects to the host and performs the MCP initialize handshake.</summary>
    public static Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        // Pinning the protocol revision forces a real initialize handshake rather than server discovery.
        var clientOptions = new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
            InitializationTimeout = TimeSpan.FromSeconds(15),
        };

        return McpClient.CreateAsync(CreateTransport(), clientOptions, loggerFactory: null, cancellationToken);
    }
}
