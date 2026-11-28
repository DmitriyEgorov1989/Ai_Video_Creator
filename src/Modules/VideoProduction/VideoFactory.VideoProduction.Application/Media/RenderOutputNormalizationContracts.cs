namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Параметры нормализации аудиодорожки готового MP4.</summary>
/// <param name="SourcePath">Путь к неизменяемому результату рендера.</param>
/// <param name="OutputPath">Путь временного нормализованного MP4.</param>
/// <param name="TargetLoudnessLufs">Целевая интегральная громкость в LUFS.</param>
/// <param name="MaximumTruePeakDbtp">Максимальный true peak в dBTP.</param>
/// <param name="AudioBitrateBitsPerSecond">Целевой audio bitrate в битах в секунду.</param>
/// <param name="SampleRate">Частота дискретизации в герцах.</param>
/// <param name="ChannelLayout">Целевая раскладка каналов: <c>mono</c> или <c>stereo</c>.</param>
public sealed record RenderOutputNormalizationRequest(
    string SourcePath,
    string OutputPath,
    double TargetLoudnessLufs,
    double MaximumTruePeakDbtp,
    long AudioBitrateBitsPerSecond,
    int SampleRate,
    string ChannelLayout);

/// <summary>Результат нормализации аудиодорожки готового MP4.</summary>
/// <param name="IsSuccess">Признак создания непустого временного MP4.</param>
/// <param name="ErrorCode">Машинный код ошибки или <see langword="null"/> при успехе.</param>
/// <param name="ErrorMessage">Исходная диагностика процесса или <see langword="null"/> при успехе.</param>
public sealed record RenderOutputNormalizationResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Нормализует аудиодорожку результата рендера без перекодирования видео.</summary>
public interface IRenderOutputNormalizer
{
    /// <summary>Создаёт временный нормализованный MP4, поддерживая отмену операции.</summary>
    Task<RenderOutputNormalizationResult> NormalizeAsync(
        RenderOutputNormalizationRequest request,
        CancellationToken cancellationToken);
}
