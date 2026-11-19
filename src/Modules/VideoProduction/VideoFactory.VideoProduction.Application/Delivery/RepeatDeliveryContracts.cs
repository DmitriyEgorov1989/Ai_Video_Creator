using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Delivery;

/// <summary>Запрашивает повторную доставку завершённого Job.</summary>
/// <param name="JobId">Идентификатор завершённого задания.</param>
/// <param name="TelegramUserId">Идентификатор Telegram-пользователя.</param>
/// <param name="TelegramUpdateId">Уникальный идентификатор Telegram update.</param>
public sealed record RepeatDeliveryCommand(JobId JobId, long TelegramUserId, long TelegramUpdateId);

/// <summary>Проверенный итоговый файл, доступный для повторной доставки.</summary>
/// <param name="ArtifactId">Идентификатор итогового MP4.</param>
/// <param name="FilePath">Абсолютный путь к файлу, подготовленный Infrastructure.</param>
public sealed record RepeatDeliveryArtifact(ArtifactId ArtifactId, string FilePath);

/// <summary>Результат получения итогового файла.</summary>
public abstract record RepeatDeliveryArtifactResult
{
    /// <summary>Файл проверен и готов к отправке.</summary>
    public sealed record Succeeded(RepeatDeliveryArtifact Artifact) : RepeatDeliveryArtifactResult;

    /// <summary>Файл нельзя безопасно отправить.</summary>
    public sealed record Rejected(string Code, string Message) : RepeatDeliveryArtifactResult;
}

/// <summary>Находит и проверяет опубликованный MP4 и его manifest.</summary>
public interface IRepeatDeliveryArtifactSource
{
    /// <summary>Возвращает только файл, прошедший проверку целостности.</summary>
    Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(JobCreationRecord job, CancellationToken cancellationToken);
}

/// <summary>Результат регистрации команды повторной доставки.</summary>
public abstract record RepeatDeliveryRequestStart
{
    /// <summary>Команда ещё не выполнялась и может быть начата.</summary>
    public sealed record Started : RepeatDeliveryRequestStart;

    /// <summary>Команда уже завершалась ранее.</summary>
    public sealed record AlreadyProcessed(DeliveryReceipt Receipt) : RepeatDeliveryRequestStart;

    /// <summary>Предыдущая попытка прервалась до фиксации результата.</summary>
    public sealed record Pending : RepeatDeliveryRequestStart;

}

/// <summary>Сохраняет ключи Telegram-команд для идемпотентной обработки.</summary>
public interface IRepeatDeliveryRequestStore
{
    /// <summary>Атомарно резервирует команду или возвращает её предыдущий результат.</summary>
    Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken);

    /// <summary>Связывает команду с результатом доставки.</summary>
    Task CompleteAsync(long telegramUpdateId, DeliveryReceipt receipt, CancellationToken cancellationToken);
}

/// <summary>Результат application-сценария повторной доставки.</summary>
public abstract record RepeatDeliveryResult
{
    /// <summary>Файл повторно отправлен.</summary>
    public sealed record Succeeded(string? RemoteMessageId) : RepeatDeliveryResult;

    /// <summary>Повторная обработка уже завершённой команды.</summary>
    public sealed record AlreadyProcessed(string Status, string? RemoteMessageId) : RepeatDeliveryResult;

    /// <summary>Ожидаемый отказ сценария.</summary>
    public sealed record Rejected(string Code, string Message) : RepeatDeliveryResult;
}

/// <summary>Повторно доставляет проверенный MP4 без запуска конвейера.</summary>
public sealed class RepeatDeliveryUseCase
{
    private readonly IJobStateStore stateStore;
    private readonly IRepeatDeliveryArtifactSource artifactSource;
    private readonly IVideoDelivery delivery;
    private readonly IRepeatDeliveryRequestStore requestStore;
    private readonly IDeliveryReceiptStore receiptStore;

    /// <summary>Создаёт use case повторной доставки.</summary>
    public RepeatDeliveryUseCase(
        IJobStateStore stateStore,
        IRepeatDeliveryArtifactSource artifactSource,
        IVideoDelivery delivery,
        IRepeatDeliveryRequestStore requestStore,
        IDeliveryReceiptStore receiptStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.artifactSource = artifactSource ?? throw new ArgumentNullException(nameof(artifactSource));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        this.requestStore = requestStore ?? throw new ArgumentNullException(nameof(requestStore));
        this.receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
    }

    /// <summary>Повторно отправляет MP4, не изменяя состояние Job.</summary>
    public async Task<RepeatDeliveryResult> ExecuteAsync(
        RepeatDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        if (command.TelegramUserId <= 0 || command.TelegramUpdateId <= 0)
        {
            return new RepeatDeliveryResult.Rejected("delivery.invalid_command", "The repeat delivery command is invalid.");
        }

        JobCreationRecord? job = await stateStore.GetAsync(command.JobId, cancellationToken);
        if (job is null)
        {
            return new RepeatDeliveryResult.Rejected("delivery.job_missing", "The requested job does not exist.");
        }

        if (job.TelegramUserId != command.TelegramUserId)
        {
            return new RepeatDeliveryResult.Rejected("delivery.forbidden", "The requested job is not owned by this user.");
        }

        if (job.Progress.Status != JobStatus.Completed)
        {
            return new RepeatDeliveryResult.Rejected("delivery.job_not_completed", "The requested job is not completed yet.");
        }

        RepeatDeliveryRequestStart start = await requestStore.TryStartAsync(command, cancellationToken);
        if (start is RepeatDeliveryRequestStart.AlreadyProcessed previous)
        {
            return new RepeatDeliveryResult.AlreadyProcessed(previous.Receipt.Status, previous.Receipt.RemoteMessageId);
        }

        if (start is RepeatDeliveryRequestStart.Pending)
        {
            return new RepeatDeliveryResult.Rejected("delivery.unknown_result", "The previous delivery attempt has an unknown result.");
        }

        RepeatDeliveryArtifactResult artifactResult = await artifactSource.GetVerifiedAsync(job, cancellationToken);
        if (artifactResult is RepeatDeliveryArtifactResult.Rejected rejected)
        {
            return new RepeatDeliveryResult.Rejected(rejected.Code, rejected.Message);
        }

        RepeatDeliveryArtifact artifact = ((RepeatDeliveryArtifactResult.Succeeded)artifactResult).Artifact;
        VideoDeliveryResult deliveryResult = await delivery.SendAsync(
            new VideoDeliveryRequest(job.JobId, job.VariantId, artifact.FilePath, "Готовый ролик", command.TelegramUserId),
            cancellationToken);
        DeliveryReceipt receipt = new(
            Guid.NewGuid(),
            job.JobId,
            job.VariantId,
            artifact.ArtifactId,
            command.TelegramUserId,
            deliveryResult.RemoteMessageId,
            deliveryResult.IsSuccess ? "succeeded" : "failed",
            deliveryResult.ErrorCode,
            deliveryResult.ErrorMessage);
        await receiptStore.AppendAsync(receipt, cancellationToken);
        await requestStore.CompleteAsync(command.TelegramUpdateId, receipt, cancellationToken);

        return deliveryResult.IsSuccess
            ? new RepeatDeliveryResult.Succeeded(deliveryResult.RemoteMessageId)
            : new RepeatDeliveryResult.Rejected(
                deliveryResult.ErrorCode ?? "delivery.failed",
                deliveryResult.ErrorMessage ?? "Video delivery failed.");
    }
}
