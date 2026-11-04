using VideoFactory.Bot;

namespace VideoFactory.Bot.Tests;

public sealed class StartupCommandTests
{
    [Fact]
    public async Task ValidConfigurationStartsAndStopsHost()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        int exitCode = await VideoFactoryHost.RunAsync(
            ["--Telegram:BotToken=test-token", "--Telegram:AllowedUserId=123456"],
            cancellation.Token);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UnknownCommandReturnsUsageError()
    {
        int exitCode = await VideoFactoryHost.RunAsync(["unknown"]);

        Assert.Equal(2, exitCode);
    }
}
