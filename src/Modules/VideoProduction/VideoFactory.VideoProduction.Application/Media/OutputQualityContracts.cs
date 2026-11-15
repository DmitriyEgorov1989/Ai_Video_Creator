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
    int? MaxOutputBytes = null);

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
