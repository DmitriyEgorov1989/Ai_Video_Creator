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
/// <param name="IsSuccess">Признак подтверждённой доставки.</param>
/// <param name="ErrorCode">Стабильный код ошибки или <see langword="null"/> при успехе.</param>
/// <param name="ErrorMessage">Исходное сообщение интеграции или <see langword="null"/> при успехе.</param>
/// <param name="RemoteMessageId">Идентификатор сообщения провайдера или <see langword="null"/>, если он отсутствует.</param>
/// <param name="UploadedSha256">SHA-256 прочитанного upload-потока или <see langword="null"/>, если доставка не подтверждена.</param>
/// <param name="UploadedSizeBytes">Размер прочитанного upload-потока в байтах или <see langword="null"/>, если доставка не подтверждена.</param>
public sealed record VideoDeliveryResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    string? RemoteMessageId,
    string? UploadedSha256,
    long? UploadedSizeBytes)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static VideoDeliveryResult Succeeded(
        string? remoteMessageId,
        string uploadedSha256,
        long uploadedSizeBytes) =>
        new(true, null, null, remoteMessageId, uploadedSha256, uploadedSizeBytes);

    /// <summary>Создаёт ожидаемую ошибку.</summary>
    public static VideoDeliveryResult Failed(string code, string message) => new(false, code, message, null, null, null);
}

/// <summary>Отправляет только проверенный и опубликованный MP4.</summary>
public interface IVideoDelivery
{
    Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken);
}
