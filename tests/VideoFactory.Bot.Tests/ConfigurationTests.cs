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
            ["Limits:MaxVariantsPerJob"] = "7"
        });

        ServiceProvider services = BuildServices(configuration);

        TelegramOptions telegram = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
        PathsOptions paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        LimitsOptions limits = services.GetRequiredService<IOptions<LimitsOptions>>().Value;

        Assert.Equal("test-token", telegram.BotToken);
        Assert.Equal(123456, telegram.AllowedUserId);
        Assert.Equal("workspace", paths.WorkspaceRoot);
        Assert.Equal(7, limits.MaxVariantsPerJob);
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

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void VariantLimitOutsideSupportedRangeIsRejected(int maxVariants)
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Limits:MaxVariantsPerJob"] = maxVariants.ToString()
        });

        ServiceProvider services = BuildServices(configuration);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<LimitsOptions>>().Value);

        Assert.Contains("Limits:MaxVariantsPerJob must be between 1 and 10.", exception.Failures);
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
