using System.Reflection;
using AwesomeAssertions;

namespace DecisionGateway.Host.ContractTests;

public sealed class SdkBehaviorPackageTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ModelContextProtocol")]
    [InlineData("ModelContextProtocol.Core")]
    public void SdkAssembly_DeclaresAotCompatibleAndTrimmable(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

        metadata.Should().ContainKey("IsAotCompatible").WhoseValue.Should().Be("True");
        metadata.Should().ContainKey("IsTrimmable").WhoseValue.Should().Be("True");
    }
}
