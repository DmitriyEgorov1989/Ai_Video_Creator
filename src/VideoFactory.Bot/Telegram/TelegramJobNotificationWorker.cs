using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoFactory.VideoProduction.Application.Events;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Доставляет сохранённые события Job в Telegram и повторяет временные ошибки отправки.
/// </summary>
public sealed class TelegramJobNotificationWorker : BackgroundService
{
    private readonly IJobEventSignal signal;
    private readonly TelegramJobNotificationService notifications;
    private readonly ILogger<TelegramJobNotificationWorker> logger;

    /// <summary>Создаёт worker уведомлений.</summary>
    public TelegramJobNotificationWorker(
        IJobEventSignal signal,
        TelegramJobNotificationService notifications,
        ILogger<TelegramJobNotificationWorker> logger)
    {
        this.signal = signal ?? throw new ArgumentNullException(nameof(signal));
        this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (JobEventRecord eventRecord in signal.ReadAllAsync(stoppingToken))
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await notifications.NotifyAsync(eventRecord, stoppingToken);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException
                    or TimeoutException
                    or global::Telegram.Bot.Exceptions.RequestException)
                {
                    logger.LogWarning(
                        "Telegram Job notification failed for {JobId}: {ErrorType}",
                        eventRecord.JobId,
                        exception.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
    }
}
