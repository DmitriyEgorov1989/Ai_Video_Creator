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
