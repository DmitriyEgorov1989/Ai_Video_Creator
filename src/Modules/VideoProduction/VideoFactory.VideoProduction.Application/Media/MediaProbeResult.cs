using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>
/// Результат получения технических metadata медиафайла.
/// </summary>
public sealed record MediaProbeResult
{
    private MediaProbeResult(
        MediaMetadata? metadata,
        string? errorCode,
        string? errorMessage)
    {
        Metadata = metadata;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Получает metadata или <see langword="null"/>, если probe завершился ошибкой.
    /// </summary>
    public MediaMetadata? Metadata { get; }

    /// <summary>
    /// Получает машинный код ошибки или <see langword="null"/> при успехе.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Получает диагностическое сообщение или <see langword="null"/> при успехе.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Получает признак успешного получения metadata.
    /// </summary>
    public bool IsSuccess => Metadata is not null;

    /// <summary>
    /// Создаёт успешный результат.
    /// </summary>
    public static MediaProbeResult Succeeded(MediaMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new MediaProbeResult(metadata, null, null);
    }

    /// <summary>
    /// Создаёт ожидаемый результат ошибки.
    /// </summary>
    public static MediaProbeResult Failed(string errorCode, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new MediaProbeResult(null, errorCode, errorMessage);
    }
}
