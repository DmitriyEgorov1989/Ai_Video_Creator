using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using System.Security.Cryptography;

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
    /// <summary>Возвращает сохранённое состояние команды без создания pending-записи.</summary>
    /// <param name="telegramUpdateId">Идентификатор Telegram update.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сохранённое состояние или <see langword="null"/>, если update ещё не встречался.</returns>
    Task<RepeatDeliveryRequestStart?> FindAsync(long telegramUpdateId, CancellationToken cancellationToken);

    /// <summary>Атомарно резервирует команду или возвращает её предыдущий результат.</summary>
    Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken);

    /// <summary>Атомарно сохраняет receipt и завершает связанную команду.</summary>
    /// <param name="telegramUpdateId">Идентификатор Telegram update.</param>
    /// <param name="receipt">Итоговая запись доставки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
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
        ArgumentNullException.ThrowIfNull(receiptStore);
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

        RepeatDeliveryRequestStart? existing = await requestStore.FindAsync(
            command.TelegramUpdateId, cancellationToken);
        if (existing is RepeatDeliveryRequestStart.AlreadyProcessed processed)
        {
            return new RepeatDeliveryResult.AlreadyProcessed(
                processed.Receipt.Status, processed.Receipt.RemoteMessageId);
        }

        if (existing is RepeatDeliveryRequestStart.Pending)
        {
            return new RepeatDeliveryResult.Rejected(
                "delivery.unknown_result", "The previous delivery attempt has an unknown result.");
        }

        RepeatDeliveryArtifact artifact;
        string expectedSha256;
        long expectedSizeBytes;
        try
        {
            RepeatDeliveryArtifactResult artifactResult = await artifactSource.GetVerifiedAsync(job, cancellationToken);
            if (artifactResult is RepeatDeliveryArtifactResult.Rejected rejected)
            {
                return new RepeatDeliveryResult.Rejected(rejected.Code, rejected.Message);
            }

            artifact = ((RepeatDeliveryArtifactResult.Succeeded)artifactResult).Artifact;
            await using FileStream uploadSource = new(
                artifact.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            expectedSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(uploadSource, cancellationToken));
            expectedSizeBytes = uploadSource.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RepeatDeliveryResult.Rejected(
                "delivery.artifact_unavailable", "The verified video is unavailable for delivery.");
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

        VideoDeliveryResult deliveryResult = await delivery.SendAsync(
            new VideoDeliveryRequest(job.JobId, job.VariantId, artifact.FilePath, "Готовый ролик", command.TelegramUserId),
            cancellationToken);
        bool uploadMatches = deliveryResult.IsSuccess
            && string.Equals(deliveryResult.UploadedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            && deliveryResult.UploadedSizeBytes == expectedSizeBytes;
        DeliveryReceipt receipt = new(
            Guid.NewGuid(),
            job.JobId,
            job.VariantId,
            artifact.ArtifactId,
            command.TelegramUserId,
            deliveryResult.RemoteMessageId,
            uploadMatches ? "succeeded" : "failed",
            deliveryResult.IsSuccess && !uploadMatches ? "delivery.evidence_mismatch" : deliveryResult.ErrorCode,
            deliveryResult.IsSuccess && !uploadMatches
                ? "Uploaded bytes do not match the verified artifact."
                : deliveryResult.ErrorMessage,
            deliveryResult.UploadedSha256,
            deliveryResult.UploadedSizeBytes);
        using CancellationTokenSource persistenceCts = new(TimeSpan.FromSeconds(5));
        await requestStore.CompleteAsync(command.TelegramUpdateId, receipt, persistenceCts.Token);

        return uploadMatches
            ? new RepeatDeliveryResult.Succeeded(deliveryResult.RemoteMessageId)
            : new RepeatDeliveryResult.Rejected(
                receipt.ErrorCode ?? "delivery.failed",
                receipt.ErrorMessage ?? "Video delivery failed.");
    }
}
