using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает ожидаемый технический профиль итогового MP4.</summary>
public sealed record OutputQualityRequest(
    string FilePath,
    int ExpectedWidth,
    int ExpectedHeight,
    FrameRate ExpectedFrameRate,
    int ExpectedDurationFrames,
    int DurationToleranceFrames,
    bool RequireAudio,
    string? ExpectedPixelFormat = null,
    TimeSpan? ProbeTimeout = null,
    int? MaxOutputBytes = null,
    string? ExpectedVideoCodec = null,
    string? ExpectedAudioCodec = null,
    decimal? ExpectedLoudnessLufs = null,
    decimal LoudnessToleranceLufs = 1m,
    decimal? MaximumTruePeakDbtp = null);

/// <summary>Описывает запрос измерения громкости итогового файла.</summary>
/// <param name="FilePath">Абсолютный путь к итоговому MP4.</param>
/// <param name="Timeout">Максимальное время измерения.</param>
/// <param name="MaxOutputBytes">Максимальный размер диагностического вывода процесса в байтах.</param>
public sealed record OutputAudioQualityRequest(string FilePath, TimeSpan Timeout, int MaxOutputBytes);

/// <summary>Содержит измеренную интегральную громкость и true peak итогового файла.</summary>
/// <param name="IsSuccess">Признак успешного измерения.</param>
/// <param name="IntegratedLoudnessLufs">Интегральная громкость в LUFS или <see langword="null"/> при ошибке.</param>
/// <param name="TruePeakDbtp">True peak в dBTP или <see langword="null"/> при ошибке.</param>
/// <param name="ErrorCode">Код ожидаемой ошибки или <see langword="null"/> при успехе.</param>
/// <param name="ErrorMessage">Описание ожидаемой ошибки или <see langword="null"/> при успехе.</param>
public sealed record OutputAudioQualityResult(
    bool IsSuccess,
    decimal? IntegratedLoudnessLufs,
    decimal? TruePeakDbtp,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешное измерение.</summary>
    public static OutputAudioQualityResult Succeeded(decimal loudnessLufs, decimal truePeakDbtp) =>
        new(true, loudnessLufs, truePeakDbtp, null, null);

    /// <summary>Создаёт ожидаемую ошибку измерения.</summary>
    public static OutputAudioQualityResult Failed(string code, string message) =>
        new(false, null, null, code, message);
}

/// <summary>Измеряет громкость уже собранного итогового файла.</summary>
public interface IOutputAudioQualityAnalyzer
{
    /// <summary>Возвращает интегральную громкость и true peak итогового audio stream.</summary>
    Task<OutputAudioQualityResult> AnalyzeAsync(
        OutputAudioQualityRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Результат технической проверки итогового MP4.</summary>
public sealed record OutputQualityResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    int? Width,
    int? Height,
    int? DurationFrames)
{
    /// <summary>Создаёт успешный результат проверки.</summary>
    public static OutputQualityResult Succeeded(
        int width,
        int height,
        int durationFrames) => new(true, null, null, width, height, durationFrames);

    /// <summary>Создаёт ожидаемый результат ошибки.</summary>
    public static OutputQualityResult Failed(string code, string message) =>
        new(false, code, message, null, null, null);
}

/// <summary>Проверяет техническое качество итогового MP4.</summary>
public interface IOutputQualityAssurance
{
    /// <summary>Проверяет файл через FFprobe и полный decode FFmpeg.</summary>
    Task<OutputQualityResult> VerifyAsync(
        OutputQualityRequest request,
        CancellationToken cancellationToken);
}
