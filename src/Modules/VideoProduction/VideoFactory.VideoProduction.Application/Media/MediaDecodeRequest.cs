namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает запрос на полный decode медиафайла.</summary>
public sealed record MediaDecodeRequest(
    string FilePath,
    TimeSpan Timeout,
    int MaxOutputBytes);

/// <summary>Результат полного decode.</summary>
public sealed record MediaDecodeResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static MediaDecodeResult Succeeded() => new(true, null, null);

    /// <summary>Создаёт ожидаемую ошибку.</summary>
    public static MediaDecodeResult Failed(string code, string message) =>
        new(false, code, message);
}

/// <summary>Проверяет возможность полного декодирования медиафайла.</summary>
public interface IMediaDecodeVerifier
{
    /// <summary>Выполняет полный decode без публикации нового файла.</summary>
    Task<MediaDecodeResult> VerifyAsync(
        MediaDecodeRequest request,
        CancellationToken cancellationToken);
}
