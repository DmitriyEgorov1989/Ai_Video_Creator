using System.Reflection;
using System.Runtime.Versioning;

namespace VideoFactory.VideoProduction.UnitTests;

public sealed class ProjectConfigurationTests
{
    [Theory]
    [InlineData("VideoFactory.VideoProduction.Domain")]
    [InlineData("VideoFactory.VideoProduction.Application")]
    public void ModuleAssemblyTargetsNet10(string assemblyName)
    {
        Assembly assembly = Assembly.Load(assemblyName);
        TargetFrameworkAttribute? targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework?.FrameworkName);
    }

    [Fact]
    public void DomainHasNoDependenciesOnOtherVideoFactoryAssemblies()
    {
        Assembly domain = Assembly.Load("VideoFactory.VideoProduction.Domain");

        AssemblyName[] productDependencies = domain.GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith("VideoFactory.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(productDependencies);
    }
}
