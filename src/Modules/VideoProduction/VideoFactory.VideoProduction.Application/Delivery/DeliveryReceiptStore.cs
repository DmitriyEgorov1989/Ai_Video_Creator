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
    Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken);
}
