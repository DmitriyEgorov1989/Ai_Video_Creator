using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает создание отдельного audio stem.</summary>
/// <param name="SourcePath">Безопасный локальный путь исходного media.</param>
/// <param name="OutputPath">Безопасный локальный путь нового stem.</param>
/// <param name="SourceMetadata">Проверенные metadata источника.</param>
/// <param name="Timeout">Максимальное время FFmpeg.</param>
/// <param name="MaxOutputBytes">Максимальный объём диагностики процесса в байтах.</param>
/// <param name="TargetSampleRate">Целевая частота дискретизации в герцах.</param>
/// <param name="ChannelLayout">Целевая раскладка каналов.</param>
/// <param name="TargetLoudnessLufs">Целевая интегральная громкость в LUFS.</param>
/// <param name="TruePeakDbtp">Максимальный true peak в dBTP.</param>
/// <param name="Denoise">Нужно ли применить ограниченный FFmpeg denoise до нормализации.</param>
public sealed record AudioStemRequest(
    string SourcePath,
    string OutputPath,
    MediaMetadata SourceMetadata,
    TimeSpan Timeout,
    int MaxOutputBytes,
    int TargetSampleRate = 48_000,
    string ChannelLayout = "stereo",
    double TargetLoudnessLufs = -16,
    double TruePeakDbtp = -1,
    bool Denoise = false);

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
