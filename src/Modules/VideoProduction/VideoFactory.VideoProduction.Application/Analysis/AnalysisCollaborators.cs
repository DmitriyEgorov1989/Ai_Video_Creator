using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Запрос определения речевой активности.</summary>
public sealed record AudioActivityRequest(string FilePath, long DurationMicroseconds);

/// <summary>Результат определения речевой активности.</summary>
public sealed record AudioActivityResult(
    bool HasUsefulSpeech,
    IReadOnlyList<MasterRange> SpeechWindows,
    IReadOnlyList<MasterRange> Pauses,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Определяет интервалы речи и тишины.</summary>
public interface IAudioActivityDetector
{
    /// <summary>Выполняет определение речевой активности.</summary>
    Task<AudioActivityResult> DetectAsync(
        AudioActivityRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Запрос распознавания речи.</summary>
public sealed record TranscriptionRequest(string FilePath, long DurationMicroseconds);

/// <summary>Результат распознавания речи.</summary>
public sealed record TranscriptionResult(
    bool IsSuccess,
    IReadOnlyList<SpeechSegment> Segments,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static TranscriptionResult Succeeded(IReadOnlyList<SpeechSegment> segments) =>
        new(true, segments, null, null);

    /// <summary>Создаёт ошибочный результат.</summary>
    public static TranscriptionResult Failed(string code, string message) =>
        new(false, [], code, message);
}

/// <summary>Запускает изолированный worker распознавания.</summary>
public interface IWhisperTranscriber
{
    /// <summary>Распознаёт речь и word timestamps.</summary>
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Запрос определения границ сцен.</summary>
public sealed record SceneDetectionRequest(string FilePath, long DurationMicroseconds);

/// <summary>Результат определения границ сцен.</summary>
public sealed record SceneDetectionResult(
    bool IsSuccess,
    IReadOnlyList<MasterRange> Scenes,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static SceneDetectionResult Succeeded(IReadOnlyList<MasterRange> scenes) =>
        new(true, scenes, null, null);

    /// <summary>Создаёт ошибочный результат.</summary>
    public static SceneDetectionResult Failed(string code, string message) =>
        new(false, [], code, message);
}

/// <summary>Определяет базовые границы сцен.</summary>
public interface ISceneBoundaryDetector
{
    /// <summary>Находит интервалы сцен в master timeline.</summary>
    Task<SceneDetectionResult> DetectAsync(
        SceneDetectionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Запрашивает объективные признаки качества video intervals.</summary>
public sealed record VideoFeatureAnalysisRequest(
    string FilePath,
    IReadOnlyList<AllowedSegment> Segments);

/// <summary>Результат вычисления objective video features.</summary>
public sealed record VideoFeatureAnalysisResult(
    bool IsSuccess,
    IReadOnlyList<SegmentAnalysisFeatures> Features,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static VideoFeatureAnalysisResult Succeeded(IReadOnlyList<SegmentAnalysisFeatures> features) =>
        new(true, features, null, null);

    /// <summary>Создаёт результат с ошибкой адаптера.</summary>
    public static VideoFeatureAnalysisResult Failed(string code, string message) =>
        new(false, [], code, message);
}

/// <summary>Вычисляет объективные признаки качества сегментов через внешний adapter.</summary>
public interface IVideoFeatureAnalyzer
{
    /// <summary>Выполняет анализ video intervals без изменения исходного файла.</summary>
    Task<VideoFeatureAnalysisResult> AnalyzeAsync(
        VideoFeatureAnalysisRequest request,
        CancellationToken cancellationToken);
}
