using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Delivery;

/// <summary>Неизменяемая запись попытки доставки.</summary>
public sealed record DeliveryReceipt(
    Guid ReceiptId,
    JobId JobId,
    VariantId VariantId,
    ArtifactId ArtifactId,
    long RecipientId,
    string? RemoteMessageId,
    string Status,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Сохраняет результат каждой попытки доставки.</summary>
public interface IDeliveryReceiptStore
{
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
