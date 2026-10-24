using System.Xml.Linq;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>Pure rules over MSBuild project XML. Element names are compared case-insensitively.</summary>
internal static class ProjectFileRules
{
    private static readonly string[] HostOnlyNames = ["ModelContextProtocol", "Microsoft.Extensions.Hosting"];

    /// <summary>The only place element names are compared; MSBuild itself ignores the case of item names.</summary>
    public static bool IsElement(XElement e, string name) =>
        string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>File names (without extension) of every ProjectReference Include.</summary>
    public static IReadOnlyList<string> ProjectReferenceNames(XDocument project) =>
        project.Descendants()
            .Where(e => IsElement(e, "ProjectReference"))
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

    /// <summary>Include and Update values of PackageReference and GlobalPackageReference elements.</summary>
    public static IReadOnlyList<string> PackageReferenceIds(XDocument document) =>
        IdsOf(document, "PackageReference", "GlobalPackageReference");

    /// <summary>Include and Update values of PackageVersion and GlobalPackageReference elements.</summary>
    public static IReadOnlyList<string> CentralPackageIds(XDocument packagesProps) =>
        IdsOf(packagesProps, "PackageVersion", "GlobalPackageReference");

    /// <summary>
    /// One entry per Import, Reference or Compile element and per attribute named exactly Condition.
    /// </summary>
    public static IReadOnlyList<string> StructureViolations(XDocument project)
    {
        var violations = new List<string>();
        foreach (var element in project.Descendants())
        {
            if (IsElement(element, "Import"))
            {
                violations.Add($"Import Project={(string?)element.Attribute("Project")}");
            }
            else if (IsElement(element, "Reference"))
            {
                violations.Add($"Reference Include={(string?)element.Attribute("Include")}");
            }
            else if (IsElement(element, "Compile"))
            {
                violations.Add("Compile item");
            }

            if (element.Attribute("Condition") is not null)
            {
                violations.Add($"Condition on {element.Name.LocalName}");
            }
        }

        return violations;
    }

    /// <summary>Trimmed values of elements named propertyName that are direct children of a PropertyGroup.</summary>
    public static IReadOnlyList<string> PropertyValues(XDocument project, string propertyName) =>
        project.Descendants()
            .Where(group => IsElement(group, "PropertyGroup"))
            .SelectMany(group => group.Elements())
            .Where(e => IsElement(e, propertyName))
            .Select(e => e.Value.Trim())
            .ToList();

    /// <summary>
    /// A resilience library: any dot-separated segment starting with "Polly", or an id under
    /// Microsoft.Extensions. that mentions "Resilience".
    /// </summary>
    public static bool IsResiliencePackage(string packageId) =>
        packageId.Split('.').Any(segment => segment.StartsWith("Polly", StringComparison.OrdinalIgnoreCase))
        || (packageId.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)
            && packageId.Contains("Resilience", StringComparison.OrdinalIgnoreCase));

    /// <summary>The MCP SDK or the generic host: the exact name or a dotted sub-name of it.</summary>
    public static bool IsHostOnlyDependency(string name) =>
        HostOnlyNames.Any(host =>
            name.Equals(host, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(host + ".", StringComparison.OrdinalIgnoreCase));

    private static List<string> IdsOf(XDocument document, string first, string second) =>
        document.Descendants()
            .Where(e => IsElement(e, first) || IsElement(e, second))
            .SelectMany(e => new[] { (string?)e.Attribute("Include"), (string?)e.Attribute("Update") })
            .OfType<string>()
            .ToList();
}
