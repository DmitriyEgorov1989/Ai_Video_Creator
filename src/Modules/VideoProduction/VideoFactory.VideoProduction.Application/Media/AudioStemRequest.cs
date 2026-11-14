using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает создание отдельного audio stem.</summary>
public sealed record AudioStemRequest(
    string SourcePath,
    string OutputPath,
    MediaMetadata SourceMetadata,
    TimeSpan Timeout,
    int MaxOutputBytes);

/// <summary>Результат создания audio stem.</summary>
public sealed record AudioStemResult(
    bool IsCreated,
    string? PublishedPath,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт результат без полезного аудиопотока.</summary>
    public static AudioStemResult Skipped() => new(false, null, null, null);

    /// <summary>Создаёт успешный результат.</summary>
    public static AudioStemResult Created(string path) => new(true, path, null, null);

    /// <summary>Создаёт ошибочный результат.</summary>
    public static AudioStemResult Failed(string code, string message) =>
        new(false, null, code, message);
}

/// <summary>Создаёт audio stem только при наличии полезного audio stream.</summary>
public interface IAudioStemBuilder
{
    /// <summary>Создаёт stem атомарно или возвращает пропуск.</summary>
    Task<AudioStemResult> BuildAsync(
        AudioStemRequest request,
        CancellationToken cancellationToken);
}
