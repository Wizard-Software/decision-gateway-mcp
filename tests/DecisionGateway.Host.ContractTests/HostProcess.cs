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

    /// <summary>
    /// Resolves how to start the host: a native binary when DG_BINARY_PATH is set, otherwise "dotnet" plus the host dll,
    /// together with the allowlisted environment. Shared by the SDK transport and the raw-process tests.
    /// </summary>
    internal static (string Command, string[] Arguments, IReadOnlyDictionary<string, string> Environment) ResolveStartInfo()
    {
        var environment = new Dictionary<string, string>();
        foreach (var name in AllowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        var binaryPath = Environment.GetEnvironmentVariable(BinaryPathVariable);
        if (binaryPath is not null)
        {
            // Set but unusable must fail loudly: a silent fallback would let a native-binary job pass on the JIT host.
            if (!Path.IsPathFullyQualified(binaryPath) || !File.Exists(binaryPath))
            {
                throw new InvalidOperationException(
                    $"{BinaryPathVariable} is set but is not a fully qualified path to an existing file.");
            }

            return (binaryPath, [], environment);
        }

        var hostDll = Path.Combine(AppContext.BaseDirectory, HostDllName);
        if (!File.Exists(hostDll))
        {
            throw new FileNotFoundException(
                $"Host assembly '{HostDllName}' was not found in the test output directory.", hostDll);
        }

        var dotnet = Environment.GetEnvironmentVariable(HostPathVariable);
        return (string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet, [hostDll], environment);
    }

    /// <summary>Creates the stdio transport for the host resolved by <see cref="ResolveStartInfo"/>.</summary>
    public static StdioClientTransport CreateTransport()
    {
        var (command, arguments, environment) = ResolveStartInfo();

        var options = new StdioClientTransportOptions
        {
            Name = "decision-gateway-mcp",
            Command = command,
            Arguments = arguments.Length == 0 ? null : arguments,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
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
