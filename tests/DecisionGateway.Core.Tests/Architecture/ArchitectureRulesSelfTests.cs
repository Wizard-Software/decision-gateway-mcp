using System.Text.Json;
using System.Xml.Linq;
using AwesomeAssertions;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>
/// Control tests for the architecture rule helpers: each gate must be able to turn red on synthetic input.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArchitectureRulesSelfTests
{
    private static XDocument Xml(string body) => XDocument.Parse($"<Project>{body}</Project>");

    private static JsonDocument Json(string text) => JsonDocument.Parse(text);

    private const string AssetsSample = """
    {
      "version": 4,
      "targets": {
        "net10.0": {
          "Polly.Core/8.0.0": { "type": "package" },
          "DecisionGateway.Core/1.0.0": { "type": "project" }
        },
        "net10.0/osx-arm64": { "Foo/1.0.0": { "type": "package" } }
      },
      "project": {
        "restore": { "projectName": "DecisionGateway.Providers", "projectPath": "/r/src/DecisionGateway.Providers/DecisionGateway.Providers.csproj" },
        "frameworks": { "net10.0": { "dependencies": {
          "Microsoft.NET.ILLink.Tasks": { "target": "Package", "autoReferenced": true },
          "Microsoft.Extensions.Http.Polly": { "target": "Package" }
        } } }
      },
      "logs": [ { "code": "NU1403", "level": "Error" }, { "code": "NU1603", "level": "Warning" } ]
    }
    """;

    [Fact]
    public void ProjectReferenceNames_ReturnsFileNamesWithoutExtension() =>
        ProjectFileRules.ProjectReferenceNames(Xml("<ItemGroup><ProjectReference Include=\"..\\DecisionGateway.Host\\DecisionGateway.Host.csproj\" /></ItemGroup>"))
            .Should().Equal("DecisionGateway.Host");

    [Fact]
    public void PackageReferenceIds_FindsIncludeUpdateAndGlobalReferences() =>
        ProjectFileRules.PackageReferenceIds(Xml("<ItemGroup><PackageReference Include=\"Polly\" /><PackageReference Update=\"Bar\" /><GlobalPackageReference Include=\"Foo\" /></ItemGroup>"))
            .Should().BeEquivalentTo(["Polly", "Bar", "Foo"]);

    [Fact]
    public void CentralPackageIds_FindsPackageVersionsAndGlobalReferences() =>
        ProjectFileRules.CentralPackageIds(Xml("<ItemGroup><PackageVersion Include=\"Microsoft.Extensions.Http.Resilience\" Version=\"1.0.0\" /><GlobalPackageReference Include=\"Polly\" Version=\"8.0.0\" /></ItemGroup>"))
            .Should().BeEquivalentTo(["Microsoft.Extensions.Http.Resilience", "Polly"]);

    [Fact]
    public void ElementNames_AreMatchedCaseInsensitively()
    {
        var doc = Xml("<itemgroup><packagereference Include=\"Polly\" /><projectreference Include=\"..\\A\\A.csproj\" /><packageversion Include=\"Polly.Core\" Version=\"8.0.0\" /></itemgroup><import Project=\"x.props\" />");
        ProjectFileRules.PackageReferenceIds(doc).Should().Equal("Polly");
        ProjectFileRules.ProjectReferenceNames(doc).Should().Equal("A");
        ProjectFileRules.CentralPackageIds(doc).Should().Equal("Polly.Core");
        ProjectFileRules.StructureViolations(doc).Should().ContainSingle();
    }

    [Fact]
    public void StructureViolations_FlagsImportReferenceCompileAndCondition() =>
        ProjectFileRules.StructureViolations(Xml("<Import Project=\"x.props\" /><ItemGroup><Reference Include=\"System.Foo\" /><Compile Remove=\"Net/**\" Condition=\"'$(Configuration)' == 'Debug'\" /></ItemGroup><PropertyGroup Condition=\"true\" />"))
            .Should().HaveCount(5);

    [Fact]
    public void StructureViolations_AcceptsPlainProject() =>
        ProjectFileRules.StructureViolations(Xml("<PropertyGroup><IsAotCompatible>true</IsAotCompatible></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\A\\A.csproj\" /></ItemGroup>"))
            .Should().BeEmpty();

    [Fact]
    public void PropertyValues_ReadsPropertyGroupChildrenCaseInsensitively() =>
        ProjectFileRules.PropertyValues(Xml("<PropertyGroup><isaotcompatible> true </isaotcompatible></PropertyGroup><ItemGroup><IsAotCompatible>false</IsAotCompatible></ItemGroup>"), "IsAotCompatible")
            .Should().Equal("true");

    [Theory]
    [InlineData("Polly", true)]
    [InlineData("Polly.Core", true)]
    [InlineData("polly.extensions", true)]
    [InlineData("Microsoft.Extensions.Http.Polly", true)]
    [InlineData("Pollyanna", true)]
    [InlineData("Microsoft.Extensions.Http.Resilience", true)]
    [InlineData("Microsoft.Extensions.Resilience", true)]
    [InlineData("Microsoft.Extensions.Http", false)]
    [InlineData("Resilience.Toolkit", false)]
    [InlineData("ModelContextProtocol", false)]
    public void IsResiliencePackage_MatchesPollySegmentsAndExtensionsResilience(string id, bool expected) =>
        ProjectFileRules.IsResiliencePackage(id).Should().Be(expected);

    [Theory]
    [InlineData("ModelContextProtocol", true)]
    [InlineData("ModelContextProtocol.Core", true)]
    [InlineData("Microsoft.Extensions.Hosting", true)]
    [InlineData("Microsoft.Extensions.Hosting.Abstractions", true)]
    [InlineData("Microsoft.Extensions.Logging", false)]
    [InlineData("ModelContextProtocolX", false)]
    public void IsHostOnlyDependency_MatchesSdkAndHosting(string name, bool expected) =>
        ProjectFileRules.IsHostOnlyDependency(name).Should().Be(expected);

    [Fact]
    public void AssetsFileRules_ReadHeaderDirectAutoTransitiveProjectsAndErrors()
    {
        using var assets = Json(AssetsSample);
        AssetsFileRules.Version(assets).Should().Be(4);
        AssetsFileRules.TargetEntryCount(assets).Should().Be(3);
        AssetsFileRules.ProjectName(assets).Should().Be("DecisionGateway.Providers");
        AssetsFileRules.ProjectPath(assets).Should().EndWith("/DecisionGateway.Providers.csproj");
        AssetsFileRules.DirectPackageIds(assets).Should().Equal("Microsoft.Extensions.Http.Polly");
        AssetsFileRules.AutoReferencedPackageIds(assets).Should().Equal("Microsoft.NET.ILLink.Tasks");
        AssetsFileRules.TransitivePackageIds(assets).Should().Equal("Foo", "Polly.Core");
        AssetsFileRules.ProjectIds(assets).Should().Equal("DecisionGateway.Core");
        AssetsFileRules.RestoreErrors(assets).Should().Equal("NU1403");
    }

    [Fact]
    public void AssetsFileRules_ReturnEmptyForMissingSections()
    {
        using var assets = Json("{}");
        AssetsFileRules.Version(assets).Should().BeNull();
        AssetsFileRules.TargetEntryCount(assets).Should().Be(0);
        AssetsFileRules.ProjectName(assets).Should().BeNull();
        AssetsFileRules.TransitivePackageIds(assets).Should().BeEmpty();
    }

    [Theory]
    [InlineData("System.Net", true)]
    [InlineData("System.Net.Http", true)]
    [InlineData("System.Network", false)]
    [InlineData(null, false)]
    public void IsNetworkingNamespace_MatchesSystemNetOnly(string? ns, bool expected) =>
        MetadataScanner.IsNetworkingNamespace(ns).Should().Be(expected);

    [Fact]
    public void NetworkingMentionLines_FindsConditionalUsingsButNotLookalikes() =>
        MetadataScanner.NetworkingMentionLines("namespace A;\n#if AOT\nusing System.Net.Http;\n#endif\n// System.Network\nvar x = typeof(System.Net.IPAddress);\n")
            .Should().Equal(3, 6);

    [Fact]
    public void NetworkingTypeUsages_FindsDecoyTypesSignaturesAndLateBoundNames() =>
        MetadataScanner.NetworkingTypeUsages(typeof(NetworkingDecoy).Assembly.Location)
            .Should().Contain(["System.Net.Http.HttpClient", "System.Net.IPAddress", "System.Net.Sockets.AddressFamily",
                               "userstring:System.Net.WebClient, System.Net.WebClient"]);

    [Fact]
    public void AssemblyReferenceNames_ListsReferencesOfTestAssembly() =>
        MetadataScanner.AssemblyReferenceNames(typeof(NetworkingDecoy).Assembly.Location)
            .Should().Contain("System.Net.Http").And.Contain("AwesomeAssertions");

    [Fact]
    public void SharedFrameworkAssemblyNames_ContainsBclButNotPackages() =>
        MetadataScanner.SharedFrameworkAssemblyNames().Should().Contain("System.Runtime").And.NotContain("AwesomeAssertions").And.NotContain("NSubstitute");

    [Fact]
    public void RepositoryLayout_FindsRootAndThreeProductionProjects()
    {
        File.Exists(Path.Combine(RepositoryLayout.Root, "DecisionGateway.slnx")).Should().BeTrue();
        RepositoryLayout.ProductionProjects.Should().HaveCount(3).And.OnlyContain(p => File.Exists(p));
        RepositoryLayout.AotProjects.Should().Equal(RepositoryLayout.CoreProject, RepositoryLayout.ProvidersProject);
    }
}

/// <summary>Decoy that uses networking types in a field type, signatures and a late-bound string.</summary>
internal sealed class NetworkingDecoy
{
    public System.Net.Http.HttpClient? Client { get; init; }

    public static System.Net.IPAddress Loopback(System.Net.Sockets.AddressFamily family) =>
        family == System.Net.Sockets.AddressFamily.InterNetworkV6 ? System.Net.IPAddress.IPv6Loopback : System.Net.IPAddress.Loopback;

    public static string LateBoundName() => "System.Net.WebClient, System.Net.WebClient";
}
