using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VideoFactory.Bot.Configuration;

namespace VideoFactory.Bot.Tests;

public sealed class DotEnvLoaderTests
{
    [Fact]
    public void LoadsDotEnvBeforeProcessEnvironmentVariables()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, ".env");
        string variableName = $"VIDEO_FACTORY_TEST_{Guid.NewGuid():N}";
        string? previous = Environment.GetEnvironmentVariable(variableName);

        try
        {
            File.WriteAllText(path, $"AiApi__Provider=deepseek\n{variableName}=from-dotenv\n");
            Environment.SetEnvironmentVariable(variableName, "from-process");

            ConfigurationBuilder configuration = new();
            configuration.AddEnvironmentVariables();
            DotEnvLoader.Add(configuration, path);
            IConfiguration rootConfiguration = configuration.Build();

            Assert.Equal("deepseek", rootConfiguration["AiApi:Provider"]);
            Assert.Equal("from-process", rootConfiguration[variableName]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoresCommentsAndSupportsQuotedValues()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, ".env");

        try
        {
            File.WriteAllText(path, "# comment\nAiApi__Model=\"deepseek-flash\"\nAiApi__ApiKey='local-key'\n");
            ConfigurationBuilder configuration = new();

            DotEnvLoader.Add(configuration, path);
            IConfiguration rootConfiguration = configuration.Build();

            Assert.Equal("deepseek-flash", rootConfiguration["AiApi:Model"]);
            Assert.Equal("local-key", rootConfiguration["AiApi:ApiKey"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReloadsAlreadyBuiltConfigurationManager()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, ".env");

        try
        {
            File.WriteAllText(path, "AiApi__Provider=deepseek\n");
            ConfigurationManager configuration = new();
            configuration.AddEnvironmentVariables();

            Assert.True(DotEnvLoader.Add(configuration, path));

            Assert.Equal("deepseek", configuration["AiApi:Provider"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostConfigurationKeepsDotEnvBelowEnvironmentVariables()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, ".env");
        string variableName = $"VIDEO_FACTORY_HOST_TEST_{Guid.NewGuid():N}";
        string? previous = Environment.GetEnvironmentVariable(variableName);

        try
        {
            File.WriteAllText(path, $"AiApi__Provider=deepseek\n{variableName}=from-dotenv\n");
            Environment.SetEnvironmentVariable(variableName, "from-process");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
            Assert.True(DotEnvLoader.Add(builder.Configuration, path));

            Assert.Equal("deepseek", builder.Configuration["AiApi:Provider"]);
            Assert.Equal("from-process", builder.Configuration[variableName]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"video-factory-dotenv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
