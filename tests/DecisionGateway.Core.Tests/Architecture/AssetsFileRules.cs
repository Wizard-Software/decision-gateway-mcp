using System.Text.Json;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>Pure rules over the restore output (project.assets.json).</summary>
internal static class AssetsFileRules
{
    /// <summary>Parses the file from bytes so no stream stays open.</summary>
    public static JsonDocument Load(string assetsPath) =>
        JsonDocument.Parse(File.ReadAllBytes(assetsPath), new JsonDocumentOptions { AllowTrailingCommas = false });

    public static int? Version(JsonDocument assets) =>
        assets.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>Number of entries across all targets.</summary>
    public static int TargetEntryCount(JsonDocument assets) =>
        TargetEntries(assets).Count();

    public static string? ProjectName(JsonDocument assets) => RestoreString(assets, "projectName");

    public static string? ProjectPath(JsonDocument assets) => RestoreString(assets, "projectPath");

    /// <summary>Direct package dependencies that are not auto-referenced.</summary>
    public static IReadOnlyList<string> DirectPackageIds(JsonDocument assets) =>
        Dependencies(assets)
            .Where(d => d.Value.ValueKind == JsonValueKind.Object
                        && d.Value.TryGetProperty("target", out var t) && t.GetString() == "Package"
                        && !IsAutoReferenced(d.Value))
            .Select(d => d.Name)
            .ToList();

    /// <summary>Dependencies flagged autoReferenced (SDK-injected packages).</summary>
    public static IReadOnlyList<string> AutoReferencedPackageIds(JsonDocument assets) =>
        Dependencies(assets)
            .Where(d => d.Value.ValueKind == JsonValueKind.Object && IsAutoReferenced(d.Value))
            .Select(d => d.Name)
            .ToList();

    /// <summary>Names of all package entries in all targets, de-duplicated and sorted ordinally.</summary>
    public static IReadOnlyList<string> TransitivePackageIds(JsonDocument assets) => EntryNames(assets, "package");

    /// <summary>Names of all project entries in all targets, de-duplicated and sorted ordinally.</summary>
    public static IReadOnlyList<string> ProjectIds(JsonDocument assets) => EntryNames(assets, "project");

    /// <summary>Codes of log entries with level Error.</summary>
    public static IReadOnlyList<string> RestoreErrors(JsonDocument assets)
    {
        if (!assets.RootElement.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return logs.EnumerateArray()
            .Where(l => l.ValueKind == JsonValueKind.Object
                        && l.TryGetProperty("level", out var level) && level.GetString() == "Error")
            .Select(l => l.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty)
            .ToList();
    }

    private static bool IsAutoReferenced(JsonElement dependency) =>
        dependency.TryGetProperty("autoReferenced", out var a) && a.ValueKind == JsonValueKind.True;

    private static string? RestoreString(JsonDocument assets, string name) =>
        assets.RootElement.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
        && project.TryGetProperty("restore", out var restore) && restore.ValueKind == JsonValueKind.Object
        && restore.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<JsonProperty> Dependencies(JsonDocument assets)
    {
        if (!assets.RootElement.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object
            || !project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            if (framework.Value.ValueKind == JsonValueKind.Object
                && framework.Value.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    yield return dependency;
                }
            }
        }
    }

    private static IEnumerable<JsonProperty> TargetEntries(JsonDocument assets)
    {
        if (!assets.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var target in targets.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in target.Value.EnumerateObject())
            {
                yield return entry;
            }
        }
    }

    private static List<string> EntryNames(JsonDocument assets, string type) =>
        TargetEntries(assets)
            .Where(e => e.Value.ValueKind == JsonValueKind.Object
                        && e.Value.TryGetProperty("type", out var t) && t.GetString() == type)
            .Select(e => e.Name.Contains('/', StringComparison.Ordinal) ? e.Name[..e.Name.IndexOf('/', StringComparison.Ordinal)] : e.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
