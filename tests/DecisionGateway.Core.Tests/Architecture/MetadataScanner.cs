using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>Inspects assembly metadata and source text without loading the inspected assemblies.</summary>
internal static partial class MetadataScanner
{
    private static readonly Lazy<FrozenSet<string>> Framework = new(() => LoadFramework());

    [GeneratedRegex(@"\bSystem\.Net(\.|\b)")]
    private static partial Regex UserStringNetRegex();

    [GeneratedRegex(@"\bSystem\.Net\b")]
    private static partial Regex SourceNetRegex();

    /// <summary>Names of the assemblies referenced by the given assembly.</summary>
    public static IReadOnlyList<string> AssemblyReferenceNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .ToList();
    }

    /// <summary>
    /// Full names of System.Net types referenced, defined or exported by the assembly, found in the type tables and
    /// in decoded field, method and property signatures, plus user strings that mention System.Net
    /// (reported as "userstring:value"). De-duplicated and sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> NetworkingTypeUsages(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var provider = new FullNameSignatureProvider();
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in md.TypeReferences)
        {
            provider.Seen.Add(FullName(md, handle));
        }

        foreach (var handle in md.TypeDefinitions)
        {
            provider.Seen.Add(FullName(md, handle));
            var definition = md.GetTypeDefinition(handle);
            foreach (var field in definition.GetFields())
            {
                md.GetFieldDefinition(field).DecodeSignature(provider, genericContext: null);
            }

            foreach (var method in definition.GetMethods())
            {
                md.GetMethodDefinition(method).DecodeSignature(provider, genericContext: null);
            }

            foreach (var property in definition.GetProperties())
            {
                md.GetPropertyDefinition(property).DecodeSignature(provider, genericContext: null);
            }
        }

        foreach (var handle in md.ExportedTypes)
        {
            var exported = md.GetExportedType(handle);
            var ns = md.GetString(exported.Namespace);
            var name = md.GetString(exported.Name);
            provider.Seen.Add(ns.Length == 0 ? name : ns + "." + name);
        }

        foreach (var name in provider.Seen)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0 && IsNetworkingNamespace(name[..dot]))
            {
                found.Add(name);
            }
        }

        if (md.GetHeapSize(HeapIndex.UserString) > 1)
        {
            for (var h = MetadataTokens.UserStringHandle(1); !h.IsNil; h = md.GetNextHandle(h))
            {
                var value = md.GetUserString(h);
                if (UserStringNetRegex().IsMatch(value))
                {
                    found.Add("userstring:" + value);
                }
            }
        }

        return found.Order(StringComparer.Ordinal).ToList();
    }

    public static bool IsNetworkingNamespace(string? ns) =>
        ns is not null && (ns == "System.Net" || ns.StartsWith("System.Net.", StringComparison.Ordinal));

    /// <summary>1-based numbers of source lines that mention System.Net, including lines inside #if blocks and comments.</summary>
    public static IReadOnlyList<int> NetworkingMentionLines(string sourceText)
    {
        var lines = sourceText.Split('\n');
        var result = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (SourceNetRegex().IsMatch(lines[i]))
            {
                result.Add(i + 1);
            }
        }

        return result;
    }

    /// <summary>File names (without .dll) of the assemblies in the shared framework directory of the running process.</summary>
    public static IReadOnlySet<string> SharedFrameworkAssemblyNames() => Framework.Value;

    private static FrozenSet<string> LoadFramework()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Directory.EnumerateFiles(directory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToFrozenSet(StringComparer.Ordinal);
    }

    internal static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var reference = md.GetTypeReference(handle);
        var name = md.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(md, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
        }

        var ns = md.GetString(reference.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    internal static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var name = md.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullName(md, declaring) + "+" + name;
        }

        var ns = md.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

/// <summary>Decodes signatures into readable type names and records every named type it meets.</summary>
internal sealed class FullNameSignatureProvider : ISignatureTypeProvider<string, object?>
{
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var name = MetadataScanner.FullName(reader, handle);
        Seen.Add(name);
        return name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var name = MetadataScanner.FullName(reader, handle);
        Seen.Add(name);
        return name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPinnedType(string elementType) => elementType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
}
