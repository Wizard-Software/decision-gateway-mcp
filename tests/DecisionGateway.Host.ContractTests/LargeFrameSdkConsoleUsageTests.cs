using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DecisionGateway.Host.ContractTests;

/// <summary>
/// Spike measurement: which <c>System.Console</c> members the MCP SDK assemblies reference, and from which methods.
/// The scan walks the IL of every method body with a real opcode decoder (operand sizes taken from
/// <see cref="OpCodes"/>, including <c>switch</c>), so a stray byte that merely looks like a call opcode is not
/// mistaken for one. Anything outside a small closed allowlist fails the test (default-deny).
/// </summary>
[Collection(LargeFrameSerial.Name)]
public sealed class LargeFrameSdkConsoleUsageTests
{
    // get_Error / OpenStandardError write to stderr, which is safe for a stdio protocol host.
    private static readonly HashSet<string> AlwaysAllowedMembers = ["get_Error", "OpenStandardError"];

    // Members that can write to the protocol stdout; used only to label the report.
    private static readonly HashSet<string> StdoutMembers = ["get_Out", "Write", "WriteLine", "OpenStandardOutput", "SetOut"];

    // Closed allowlist of every other Console call the SDK is known to make. Any new hit (for example after an SDK
    // update, or a stdin read outside the transport that would steal protocol frames) turns the test red.
    private static readonly HashSet<(string Caller, string Member)> AllowedConsoleHits =
    [
        // Server stdio transport: the single place that opens the protocol stdout stream.
        ("ModelContextProtocol.Server.StdioServerTransport..ctor", "OpenStandardOutput"),
        // Server stdio transport: the single place that opens the protocol stdin stream.
        ("ModelContextProtocol.Server.StdioServerTransport..ctor", "OpenStandardInput"),
        // Client OAuth default handler: prints the authorization URL. Unreachable from the host (second test).
        ("ModelContextProtocol.Authentication.ClientOAuthProvider.DefaultAuthorizationUrlHandler", "WriteLine"),
        // Client OAuth default handler: prompts for the redirect URL. Unreachable from the host (second test).
        ("ModelContextProtocol.Authentication.ClientOAuthProvider.DefaultAuthorizationUrlHandler", "Write"),
        // Client OAuth default handler: reads the answer from stdin; would steal frames if it were ever reachable.
        ("ModelContextProtocol.Authentication.ClientOAuthProvider.DefaultAuthorizationUrlHandler", "ReadLine"),
    ];

    private static readonly Dictionary<ushort, OperandType> OperandTypes = BuildOperandTypes();

    [Fact, Trait("Category", "Unit"), Trait("Req", "A6.1")]
    public void Req_A6_1_SdkAssemblies_ConsoleUsage_LimitedToAllowlist()
    {
        var output = TestContext.Current.TestOutputHelper!;
        var paths = new[]
        {
            typeof(McpServer).Assembly.Location,
            typeof(McpServerBuilderExtensions).Assembly.Location,
        }.Distinct().ToList();

        var scans = paths.Select(p => (Path: p, Scan: FindConsoleCalls(p))).ToList();

        foreach (var (path, scan) in scans)
        {
            output.WriteLine($"SDK-ASSEMBLY file={Path.GetFileName(path)} sha256={Sha256Hex(path)} version={AssemblyName.GetAssemblyName(path).Version}");
            foreach (var call in scan.Calls.Distinct())
            {
                output.WriteLine($"SDK-CONSOLE caller={call.Caller} member={call.Member}");
            }
        }

        var allCalls = scans.SelectMany(x => x.Scan.Calls).Distinct().ToList();
        output.WriteLine($"SDK-SUMMARY stdoutCapableHits={allCalls.Count(c => StdoutMembers.Contains(c.Member))} totalHits={allCalls.Count}");

        // The detector must not lose references it cannot attribute to a calling method.
        scans.SelectMany(x => x.Scan.UnattributedMembers).Should().BeEmpty();
        // Sanity: the detector does find the known transport stdout stream.
        allCalls.Should().Contain(c => c.Member == "OpenStandardOutput");
        // Default-deny against the closed allowlist.
        allCalls.Where(c => !AlwaysAllowedMembers.Contains(c.Member))
            .Should().OnlyContain(c => AllowedConsoleHits.Contains(c));
    }

    [Fact, Trait("Category", "Unit"), Trait("Req", "A6.1")]
    public void Req_A6_1_HostAssembly_DoesNotReference_SdkClientConsoleWriters()
    {
        // The client-side SDK types that own the Console writers must be unreachable from the host.
        ReferencedTypeNames(typeof(HostLogging).Assembly.Location)
            .Should().NotContain(n => n == "HttpClientTransport" || n == "ClientOAuthProvider");
    }

    internal static ConsoleScan FindConsoleCalls(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var consoleRefs = new Dictionary<int, string>();
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (md.GetString(type.Namespace) == "System" && md.GetString(type.Name) == "Console")
            {
                consoleRefs[MetadataTokens.GetRowNumber(handle)] = md.GetString(member.Name);
            }
        }

        var calls = new List<(string Caller, string Member)>();
        var seenRefs = new HashSet<int>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var typeDef = md.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is null)
                {
                    continue;
                }

                var caller = $"{FullTypeName(md, typeHandle)}.{md.GetString(method.Name)}";
                foreach (var row in ReferencedMemberRefRows(md, il))
                {
                    if (consoleRefs.TryGetValue(row, out var memberName))
                    {
                        seenRefs.Add(row);
                        calls.Add((caller, memberName));
                    }
                }
            }
        }

        var unattributed = consoleRefs.Where(kv => !seenRefs.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        return new ConsoleScan(calls.Distinct().ToList(), unattributed);
    }

    internal static IReadOnlyList<string> ReferencedTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.TypeReferences.Select(h => md.GetString(md.GetTypeReference(h).Name)).Distinct().ToList();
    }

    internal static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FullTypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var name = md.GetString(def.Name);
        var declaring = def.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullTypeName(md, declaring)}+{name}";
        }

        var ns = md.GetString(def.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    // Walks the IL stream instruction by instruction and yields MemberRef row numbers used as the operand of
    // call / callvirt / ldftn / ldvirtftn, resolving MethodSpec (generic instantiation) tokens to their method.
    private static List<int> ReferencedMemberRefRows(MetadataReader md, byte[] il)
    {
        var results = new List<int>();
        var pos = 0;
        while (pos < il.Length)
        {
            ushort code = il[pos++];
            if (code == 0xFE)
            {
                code = (ushort)(0xFE00 | il[pos++]);
            }

            if (!OperandTypes.TryGetValue(code, out var operandType))
            {
                throw new InvalidOperationException($"Unknown IL opcode 0x{code:X}.");
            }

            int size;
            switch (operandType)
            {
                case OperandType.InlineNone:
                    size = 0;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    size = 1;
                    break;
                case OperandType.InlineVar:
                    size = 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    size = 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, pos);
                    size = 4 + (4 * count);
                    break;
                default:
                    size = 4;
                    break;
            }

            var isCallLike = code is 0x28 or 0x6F or 0xFE06 or 0xFE07;
            if (isCallLike)
            {
                var token = BitConverter.ToInt32(il, pos);
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind == HandleKind.MethodSpecification)
                {
                    handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                }

                if (handle.Kind == HandleKind.MemberReference)
                {
                    results.Add(MetadataTokens.GetRowNumber(handle));
                }
            }

            pos += size;
        }

        return results;
    }

    private static Dictionary<ushort, OperandType> BuildOperandTypes()
    {
        var map = new Dictionary<ushort, OperandType>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                map[(ushort)op.Value] = op.OperandType;
            }
        }

        return map;
    }

    internal sealed record ConsoleScan(IReadOnlyList<(string Caller, string Member)> Calls, IReadOnlyList<string> UnattributedMembers);
}
