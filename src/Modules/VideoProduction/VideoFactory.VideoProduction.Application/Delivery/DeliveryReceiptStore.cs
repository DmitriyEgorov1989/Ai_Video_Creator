using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Delivery;

/// <summary>Неизменяемая запись попытки доставки.</summary>
/// <param name="ReceiptId">Идентификатор попытки.</param>
/// <param name="JobId">Идентификатор задания.</param>
/// <param name="VariantId">Идентификатор варианта.</param>
/// <param name="ArtifactId">Идентификатор отправленного final artifact.</param>
/// <param name="RecipientId">Идентификатор получателя.</param>
/// <param name="RemoteMessageId">Идентификатор сообщения провайдера или <see langword="null"/>.</param>
/// <param name="Status">Статус попытки.</param>
/// <param name="ErrorCode">Код ошибки или <see langword="null"/>.</param>
/// <param name="ErrorMessage">Сообщение ошибки или <see langword="null"/>.</param>
/// <param name="UploadedSha256">SHA-256 прочитанного upload-потока или <see langword="null"/> для старой или неуспешной попытки.</param>
/// <param name="UploadedSizeBytes">Размер прочитанного upload-потока или <see langword="null"/> для старой или неуспешной попытки.</param>
public sealed record DeliveryReceipt(
    Guid ReceiptId,
    JobId JobId,
    VariantId VariantId,
    ArtifactId ArtifactId,
    long RecipientId,
    string? RemoteMessageId,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    string? UploadedSha256 = null,
    long? UploadedSizeBytes = null);

/// <summary>Намерение отправить конкретный итоговый артефакт получателю.</summary>
/// <param name="ReceiptId">Стабильный идентификатор попытки, используемый при её завершении.</param>
/// <param name="JobId">Идентификатор задания.</param>
/// <param name="VariantId">Идентификатор варианта.</param>
/// <param name="ArtifactId">Идентификатор итогового артефакта.</param>
/// <param name="RecipientId">Идентификатор получателя.</param>
/// <param name="ExpectedSha256">Ожидаемый SHA-256 итогового артефакта.</param>
/// <param name="ExpectedSizeBytes">Ожидаемый размер итогового артефакта в байтах.</param>
public sealed record DeliveryIntent(
    Guid ReceiptId,
    JobId JobId,
    VariantId VariantId,
    ArtifactId ArtifactId,
    long RecipientId,
    string ExpectedSha256,
    long ExpectedSizeBytes);

/// <summary>Результат восстановления или регистрации намерения доставки.</summary>
public abstract record DeliveryIntentStart
{
    /// <summary>Новое намерение сохранено, отправку можно начинать.</summary>
    public sealed record Started : DeliveryIntentStart;

    /// <summary>Доставка уже имеет надёжный успешный barrier.</summary>
    public sealed record AlreadySucceeded : DeliveryIntentStart;

    /// <summary>Сохранено намерение без достоверного результата; автоматическая повторная отправка запрещена.</summary>
    public sealed record Indeterminate : DeliveryIntentStart;
}

/// <summary>Сохраняет результат каждой попытки доставки.</summary>
public interface IDeliveryReceiptStore
{
    /// <summary>Атомарно проверяет recovery-state и сохраняет новое намерение до внешней отправки.</summary>
    /// <param name="intent">Намерение и актуальная идентичность итогового артефакта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Разрешение начать отправку либо восстановленное состояние.</returns>
    Task<DeliveryIntentStart> TryStartAsync(DeliveryIntent intent, CancellationToken cancellationToken);

    /// <summary>Идемпотентно завершает ранее сохранённое намерение результатом попытки.</summary>
    /// <param name="receipt">Итоговая запись с тем же идентификатором, что у намерения.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task CompleteAsync(DeliveryReceipt receipt, CancellationToken cancellationToken);

    /// <summary>Сохраняет результат одной попытки доставки.</summary>
    /// <param name="receipt">Неизменяемая запись попытки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken);

    /// <summary>Проверяет, была ли уже подтверждена доставка этого артефакта получателю.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="variantId">Идентификатор варианта.</param>
    /// <param name="artifactId">Идентификатор доставляемого final artifact.</param>
    /// <param name="recipientId">Идентификатор получателя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если успешная доставка уже сохранена.</returns>
    Task<bool> HasSucceededAsync(
        JobId jobId,
        VariantId variantId,
        ArtifactId artifactId,
        long recipientId,
        CancellationToken cancellationToken);
}
