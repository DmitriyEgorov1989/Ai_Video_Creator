using System.Reflection;
using System.Runtime.Versioning;

namespace VideoFactory.VideoProduction.IntegrationTests;

public sealed class ProjectConfigurationTests
{
    [Theory]
    [InlineData("VideoFactory.VideoProduction.Application")]
    [InlineData("VideoFactory.VideoProduction.Infrastructure")]
    public void ModuleAssemblyTargetsNet10(string assemblyName)
    {
        Assembly assembly = Assembly.Load(assemblyName);
        TargetFrameworkAttribute? targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework?.FrameworkName);
    }

    [Fact]
    public void InfrastructureDoesNotDependOnHost()
    {
        Assembly infrastructure = Assembly.Load("VideoFactory.VideoProduction.Infrastructure");

        Assert.DoesNotContain(
            infrastructure.GetReferencedAssemblies(),
            reference => reference.Name == "VideoFactory.Bot");
    }
}
