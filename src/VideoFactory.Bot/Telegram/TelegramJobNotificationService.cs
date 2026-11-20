using VideoFactory.Bot.Configuration;
using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Преобразует сохранённые события Job в ограниченные по частоте сообщения Telegram.
/// </summary>
public sealed class TelegramJobNotificationService
{
    private readonly TelegramOptions options;
    private readonly IJobStateStore stateStore;
    private readonly ITelegramResponder responder;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<Guid, DateTimeOffset> lastSent = [];
    private readonly HashSet<Guid> processedEvents = [];

    /// <summary>Создаёт обработчик уведомлений Job.</summary>
    public TelegramJobNotificationService(
        TelegramOptions options,
        IJobStateStore stateStore,
        ITelegramResponder responder,
        TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.responder = responder ?? throw new ArgumentNullException(nameof(responder));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Обрабатывает одно событие. Ошибка Telegram отправляется вызывающему коду,
    /// чтобы hosted service мог повторить операцию, не меняя состояние Job.
    /// </summary>
    public async Task NotifyAsync(JobEventRecord eventRecord, CancellationToken cancellationToken)
    {
        if (eventRecord.EventId == Guid.Empty || processedEvents.Contains(eventRecord.EventId))
        {
            return;
        }

        JobCreationRecord? job = await stateStore.GetAsync(eventRecord.JobId, cancellationToken);
        if (job is null || job.TelegramUserId is not long recipientId)
        {
            processedEvents.Add(eventRecord.EventId);
            return;
        }

        string? message = CreateMessage(eventRecord, job.Progress);
        if (message is null)
        {
            processedEvents.Add(eventRecord.EventId);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        bool final = job.Progress.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
        Guid key = eventRecord.JobId.Value;
        if (!final
            && lastSent.TryGetValue(key, out DateTimeOffset sentAt)
            && now - sentAt < TimeSpan.FromSeconds(options.ProgressMessageIntervalSeconds))
        {
            processedEvents.Add(eventRecord.EventId);
            return;
        }

        await responder.SendTextAsync(recipientId, message, cancellationToken);
        lastSent[key] = now;
        processedEvents.Add(eventRecord.EventId);
    }

    private static string? CreateMessage(JobEventRecord eventRecord, JobProgress progress) =>
        eventRecord.EventType switch
        {
            "job.stage_changed" => progress.Stage switch
            {
                ProductionStage.Preparation => "Проверяю исходное видео.",
                ProductionStage.Analysis => "Анализирую видео.",
                ProductionStage.Planning => "Готовлю план монтажа.",
                ProductionStage.Rendering => "Рендерю видео.",
                ProductionStage.Verification => "Проверяю готовый MP4.",
                ProductionStage.Packaging => "Финализирую результат.",
                ProductionStage.Delivery => "Отправляю готовый ролик.",
                _ => null
            },
            "job.awaiting_confirmation" => "Для продолжения требуется подтверждение дополнительной стоимости.",
            "job.completed" => "Готово. Ролик отправлен.",
            "job.failed" => CreateFailureMessage(eventRecord),
            "job.cancelled" => "Обработка задания отменена. Готовый файл не опубликован.",
            _ => null
        };

    private static string CreateFailureMessage(JobEventRecord eventRecord) =>
        eventRecord.PayloadJson?.Contains("media.unsupported", StringComparison.Ordinal) == true
            || eventRecord.PayloadJson?.Contains("media.corrupt", StringComparison.Ordinal) == true
            ? "Не удалось обработать видео: файл повреждён или его формат не поддерживается."
            : "Не удалось обработать видео. Проверьте исходный файл и попробуйте ещё раз.";
}
