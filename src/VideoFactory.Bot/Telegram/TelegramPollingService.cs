using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using VideoFactory.Bot.Configuration;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Запускает Telegram long polling и передаёт сообщения в intake handler.
/// </summary>
public sealed class TelegramPollingService : BackgroundService
{
    private readonly TelegramBotClient bot;
    private readonly TelegramUpdateHandler handler;
    private readonly ILogger<TelegramPollingService> logger;
    private CancellationToken stoppingToken;

    /// <summary>Создаёт polling service.</summary>
    public TelegramPollingService(
        TelegramBotClient bot,
        TelegramUpdateHandler handler,
        ILogger<TelegramPollingService> logger,
        IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        this.bot = bot;
        this.handler = handler;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.stoppingToken = stoppingToken;
        bot.OnMessage += HandleMessageAsync;
        bot.OnError += HandleErrorAsync;
        logger.LogInformation("Telegram long polling started");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Telegram long polling stopped");
        }
        finally
        {
            bot.OnMessage -= HandleMessageAsync;
            bot.OnError -= HandleErrorAsync;
        }
    }

    private async Task HandleMessageAsync(Message message, UpdateType updateType)
    {
        if (message.From is null)
        {
            return;
        }

        TelegramMediaInput? media = null;
        if (message.Video is not null)
        {
            media = new TelegramMediaInput(
                message.Video.FileId,
                message.Video.FileUniqueId,
                message.Video.FileSize,
                "video/mp4",
                null);
        }
        else if (message.Document is not null && IsVideoDocument(message.Document.MimeType, message.Document.FileName))
        {
            media = new TelegramMediaInput(
                message.Document.FileId,
                message.Document.FileUniqueId,
                message.Document.FileSize,
                message.Document.MimeType,
                message.Document.FileName);
        }

        try
        {
            await handler.HandleAsync(
                new TelegramUpdate(message.From.Id, message.Chat.Id, message.Text, media),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Telegram update handling was cancelled during shutdown");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Telegram update handling failed for user {UserId}", message.From.Id);
        }
    }

    private Task HandleErrorAsync(Exception exception, HandleErrorSource source)
    {
        logger.LogError(exception, "Telegram polling error from {Source}", source);
        return Task.CompletedTask;
    }

    private static bool IsVideoDocument(string? mimeType, string? fileName)
    {
        if (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        string extension = Path.GetExtension(fileName ?? string.Empty);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }
}
