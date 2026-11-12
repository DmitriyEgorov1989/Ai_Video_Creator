using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VideoFactory.Bot.Configuration;

namespace VideoFactory.Bot.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ConfigurationBindsTypedOptions()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "test-token",
            ["Telegram:AllowedUserId"] = "123456",
            ["Paths:WorkspaceRoot"] = "workspace",
            ["Storage:DataRoot"] = "data",
            ["Storage:DatabaseFileName"] = "jobs.sqlite",
            ["Limits:MaxInputDurationSeconds"] = "900"
        });

        ServiceProvider services = BuildServices(configuration);

        TelegramOptions telegram = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
        PathsOptions paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        StorageOptions storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        LimitsOptions limits = services.GetRequiredService<IOptions<LimitsOptions>>().Value;

        Assert.Equal("test-token", telegram.BotToken);
        Assert.Equal(123456, telegram.AllowedUserId);
        Assert.Equal("workspace", paths.WorkspaceRoot);
        Assert.Equal("data", storage.DataRoot);
        Assert.Equal("jobs.sqlite", storage.DatabaseFileName);
        Assert.Equal(900, limits.MaxInputDurationSeconds);
    }

    [Fact]
    public void MissingTelegramTokenIsRejected()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Telegram:AllowedUserId"] = "123456"
        });

        ServiceProvider services = BuildServices(configuration);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<TelegramOptions>>().Value);

        Assert.Contains("Telegram:BotToken is required.", exception.Failures);
    }

    [Fact]
    public void DatabaseFileNameCannotContainDirectory()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:DatabaseFileName"] = "nested/jobs.sqlite"
        });

        ServiceProvider services = BuildServices(configuration);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("Storage:DatabaseFileName must be a file name without a directory.", exception.Failures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveInputDurationLimitIsRejected(int maxInputDurationSeconds)
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Limits:MaxInputDurationSeconds"] = maxInputDurationSeconds.ToString()
        });

        ServiceProvider services = BuildServices(configuration);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<LimitsOptions>>().Value);

        Assert.Contains("Limits:MaxInputDurationSeconds must be greater than zero.", exception.Failures);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static ServiceProvider BuildServices(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddVideoFactoryConfiguration(configuration, validateOnStart: false);
        return services.BuildServiceProvider();
    }
}
