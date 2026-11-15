using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Delivery;

/// <summary>Запрос на доставку готового Artifact пользователю.</summary>
public sealed record VideoDeliveryRequest(
    JobId JobId,
    VariantId VariantId,
    string FilePath,
    string Caption,
    long RecipientId);

/// <summary>Результат доставки MP4.</summary>
public sealed record VideoDeliveryResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    string? RemoteMessageId)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static VideoDeliveryResult Succeeded(string? remoteMessageId) => new(true, null, null, remoteMessageId);

    /// <summary>Создаёт ожидаемую ошибку.</summary>
    public static VideoDeliveryResult Failed(string code, string message) => new(false, code, message, null);
}

/// <summary>Отправляет только проверенный и опубликованный MP4.</summary>
public interface IVideoDelivery
{
    Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken);
}
