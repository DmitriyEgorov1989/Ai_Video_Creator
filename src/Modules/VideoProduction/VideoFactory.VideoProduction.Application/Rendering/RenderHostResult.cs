namespace VideoFactory.VideoProduction.Application.Rendering;

/// <summary>
/// Результат запуска render-host.
/// </summary>
public sealed record RenderHostResult(
    RenderHostResultKind Kind,
    string? ErrorCode,
    string? ErrorMessage,
    string? OutputPath,
    int? DurationFrames,
    IReadOnlyList<RenderProgress> Progress,
    string StandardOutput,
    string StandardError)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static RenderHostResult Succeeded(
        string outputPath,
        int durationFrames,
        IReadOnlyList<RenderProgress> progress,
        string standardOutput,
        string standardError) =>
        new(RenderHostResultKind.Succeeded, null, null, outputPath, durationFrames, progress, standardOutput, standardError);

    /// <summary>Создаёт результат ошибки render-host.</summary>
    public static RenderHostResult Failed(
        string code,
        string message,
        IReadOnlyList<RenderProgress> progress,
        string standardOutput,
        string standardError) =>
        new(RenderHostResultKind.Failed, code, message, null, null, progress, standardOutput, standardError);
}

/// <summary>Классификация результата render-host.</summary>
public enum RenderHostResultKind
{
    /// <summary>Рендер завершён и результат найден.</summary>
    Succeeded,

    /// <summary>Рендер не завершён успешно.</summary>
    Failed
}

/// <summary>Одно событие прогресса render-host.</summary>
/// <param name="Stage">Внутренний этап Node-процесса.</param>
/// <param name="Percent">Процент выполнения от 0 до 100.</param>
public sealed record RenderProgress(string Stage, int Percent);
