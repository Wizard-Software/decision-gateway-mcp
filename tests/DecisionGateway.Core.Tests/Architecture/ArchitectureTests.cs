using System.Text.Json;
using AwesomeAssertions;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>
/// Dependency rules for the production projects, checked against project files, restore output and built assemblies.
/// Run after restore and build: the restore output (obj/project.assets.json) is part of the evidence.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArchitectureTests
{
    private static readonly string[] HostOnlyChecked = ["DecisionGateway.Core.dll", "DecisionGateway.Providers.dll"];

    private static T Assets<T>(string project, Func<JsonDocument, T> select)
    {
        using var assets = AssetsFileRules.Load(RepositoryLayout.AssetsFile(project));
        return select(assets);
    }

    private static IReadOnlyList<string> Packages(string project, Func<JsonDocument, IReadOnlyList<string>> select) => Assets(project, select);

    [Fact]
    public void Req_A1_1_DependencyDirection_HostToProvidersToCore()
    {
        // Forbidden directions: Core must not reach Providers or Host, Providers must not reach Host.
        // Checked in project files, in restore output (projects) and in assembly references.
        ProjectFileRules.ProjectReferenceNames(RepositoryLayout.LoadXml(RepositoryLayout.CoreProject))
            .Should().NotContain(n => n == "DecisionGateway.Providers" || n == "DecisionGateway.Host");
        ProjectFileRules.ProjectReferenceNames(RepositoryLayout.LoadXml(RepositoryLayout.ProvidersProject))
            .Should().NotContain(n => n == "DecisionGateway.Host");
        Packages(RepositoryLayout.CoreProject, AssetsFileRules.ProjectIds)
            .Should().NotContain(n => n == "DecisionGateway.Providers" || n == "DecisionGateway.Host");
        Packages(RepositoryLayout.ProvidersProject, AssetsFileRules.ProjectIds)
            .Should().NotContain(n => n == "DecisionGateway.Host");
        MetadataScanner.AssemblyReferenceNames(RepositoryLayout.OutputAssembly("DecisionGateway.Core.dll"))
            .Should().NotContain(n => n == "DecisionGateway.Providers" || n == "decision-gateway-mcp");
        MetadataScanner.AssemblyReferenceNames(RepositoryLayout.OutputAssembly("DecisionGateway.Providers.dll"))
            .Should().NotContain(n => n == "decision-gateway-mcp");
        // Required direction Host -> Providers -> Core: containment, not equality.
        ProjectFileRules.ProjectReferenceNames(RepositoryLayout.LoadXml(RepositoryLayout.ProvidersProject)).Should().Contain("DecisionGateway.Core");
        ProjectFileRules.ProjectReferenceNames(RepositoryLayout.LoadXml(RepositoryLayout.HostProject))
            .Should().Contain(["DecisionGateway.Core", "DecisionGateway.Providers"]);
    }

    [Fact]
    public void Req_A1_2_Core_ReferencesOnlyBcl()
    {
        var core = RepositoryLayout.LoadXml(RepositoryLayout.CoreProject);
        ProjectFileRules.PackageReferenceIds(core).Should().BeEmpty();
        ProjectFileRules.ProjectReferenceNames(core).Should().BeEmpty();
        ProjectFileRules.PackageReferenceIds(RepositoryLayout.LoadXml(RepositoryLayout.DirectoryBuildProps)).Should().BeEmpty();
        // A global package reference in the central props would flow into Core.
        ProjectFileRules.PackageReferenceIds(RepositoryLayout.LoadXml(RepositoryLayout.DirectoryPackagesProps)).Should().BeEmpty();
        Packages(RepositoryLayout.CoreProject, AssetsFileRules.ProjectIds).Should().BeEmpty();
        Packages(RepositoryLayout.CoreProject, AssetsFileRules.DirectPackageIds).Should().BeEmpty();
        var auto = Packages(RepositoryLayout.CoreProject, AssetsFileRules.AutoReferencedPackageIds);
        Packages(RepositoryLayout.CoreProject, AssetsFileRules.TransitivePackageIds).Should().OnlyContain(id => auto.Contains(id));
        var bcl = MetadataScanner.SharedFrameworkAssemblyNames();
        MetadataScanner.AssemblyReferenceNames(RepositoryLayout.OutputAssembly("DecisionGateway.Core.dll"))
            .Should().OnlyContain(n => bcl.Contains(n));
    }

    [Fact]
    public void Req_A1_2_BuildFiles_OnlyRootDirectoryBuildAndPackagesProps() =>
        RepositoryLayout.MsBuildFiles().Should().Equal("Directory.Build.props", "Directory.Packages.props");

    [Fact]
    public void Req_A1_2_CoreAndProviders_HaveNoImportReferenceCompileOrCondition()
    {
        foreach (var project in RepositoryLayout.AotProjects)
        {
            ProjectFileRules.StructureViolations(RepositoryLayout.LoadXml(project)).Should().BeEmpty(project);
        }
    }

    [Fact]
    public void Architecture_CoreAndProviders_DeclareIsAotCompatible()
    {
        // The AOT-compatibility rule has no requirement id of its own, hence no Req_ prefix.
        foreach (var project in RepositoryLayout.AotProjects)
        {
            ProjectFileRules.PropertyValues(RepositoryLayout.LoadXml(project), "IsAotCompatible")
                .Should().ContainSingle(project).Which.Should().BeEquivalentTo("true");
        }
    }

    [Fact]
    public void Req_A1_4_SrcProjects_AreExactlyCoreProvidersHost() =>
        RepositoryLayout.ProjectFilesUnderSrc().Should().Equal(
            "src/DecisionGateway.Core/DecisionGateway.Core.csproj",
            "src/DecisionGateway.Host/DecisionGateway.Host.csproj",
            "src/DecisionGateway.Providers/DecisionGateway.Providers.csproj");

    [Fact]
    public void Req_A1_4_OnlyHostReferencesMcpSdkAndHosting()
    {
        foreach (var project in RepositoryLayout.AotProjects)
        {
            ProjectFileRules.PackageReferenceIds(RepositoryLayout.LoadXml(project)).Should().NotContain(id => ProjectFileRules.IsHostOnlyDependency(id));
            Packages(project, AssetsFileRules.DirectPackageIds).Should().NotContain(id => ProjectFileRules.IsHostOnlyDependency(id));
            Packages(project, AssetsFileRules.TransitivePackageIds).Should().NotContain(id => ProjectFileRules.IsHostOnlyDependency(id));
        }

        foreach (var dll in HostOnlyChecked)
        {
            MetadataScanner.AssemblyReferenceNames(RepositoryLayout.OutputAssembly(dll)).Should().NotContain(n => ProjectFileRules.IsHostOnlyDependency(n));
        }

        ProjectFileRules.PackageReferenceIds(RepositoryLayout.LoadXml(RepositoryLayout.HostProject))
            .Should().Contain(["ModelContextProtocol", "Microsoft.Extensions.Hosting"]);
    }

    [Fact]
    public void Req_A1_4_AssetsFiles_ComeFromSuccessfulRestore()
    {
        // Guard against a false green: every project must have restore output from a successful restore of
        // the right project with a non-empty target graph.
        foreach (var project in RepositoryLayout.ProductionProjects)
        {
            // Restore records the PackageId as the project name; the Host declares PackageId DecisionGateway.Mcp.
            var name = project == RepositoryLayout.HostProject ? "DecisionGateway.Mcp" : Path.GetFileNameWithoutExtension(project);
            Packages(project, AssetsFileRules.RestoreErrors).Should().BeEmpty(project);
            Assets(project, AssetsFileRules.Version).Should().Be(4, project);
            Assets(project, AssetsFileRules.TargetEntryCount).Should().BePositive(project);
            Assets(project, AssetsFileRules.ProjectName).Should().Be(name, project);
            Path.GetFileName(Assets(project, AssetsFileRules.ProjectPath)).Should().Be(Path.GetFileName(project), project);
        }

        foreach (var project in RepositoryLayout.AotProjects)
        {
            Packages(project, AssetsFileRules.AutoReferencedPackageIds).Should().Contain("Microsoft.NET.ILLink.Tasks", project);
        }

        Packages(RepositoryLayout.ProvidersProject, AssetsFileRules.ProjectIds).Should().Contain("DecisionGateway.Core");
        Packages(RepositoryLayout.HostProject, AssetsFileRules.TransitivePackageIds)
            .Should().Contain(["ModelContextProtocol", "Microsoft.Extensions.Hosting"]);
    }

    [Fact]
    public void Req_A1_5_Core_UsesNoNetworkingTypes() =>
        MetadataScanner.NetworkingTypeUsages(RepositoryLayout.OutputAssembly("DecisionGateway.Core.dll")).Should().BeEmpty();

    [Fact]
    public void Req_A1_5_CoreSources_MentionNoSystemNet() =>
        // Text scan: also covers code inside #if blocks that the compiled assembly would not contain.
        RepositoryLayout.SourceFiles(RepositoryLayout.CoreProject)
            .SelectMany(f => MetadataScanner.NetworkingMentionLines(File.ReadAllText(f)).Select(l => $"{RepositoryLayout.Relative(f)}:{l}"))
            .Should().BeEmpty();

    [Fact]
    public void Req_A3_1_NoResilienceLibraries_InCpmProjectsOrAssets()
    {
        ProjectFileRules.CentralPackageIds(RepositoryLayout.LoadXml(RepositoryLayout.DirectoryPackagesProps))
            .Should().NotContain(id => ProjectFileRules.IsResiliencePackage(id));
        foreach (var project in RepositoryLayout.ProductionProjects)
        {
            ProjectFileRules.PackageReferenceIds(RepositoryLayout.LoadXml(project)).Should().NotContain(id => ProjectFileRules.IsResiliencePackage(id));
            Packages(project, AssetsFileRules.DirectPackageIds).Should().NotContain(id => ProjectFileRules.IsResiliencePackage(id));
            Packages(project, AssetsFileRules.TransitivePackageIds).Should().NotContain(id => ProjectFileRules.IsResiliencePackage(id));
        }

        foreach (var dll in HostOnlyChecked)
        {
            MetadataScanner.AssemblyReferenceNames(RepositoryLayout.OutputAssembly(dll)).Should().NotContain(n => ProjectFileRules.IsResiliencePackage(n));
        }
    }
}
