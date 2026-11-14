namespace VideoFactory.Bot.Configuration;

/// <summary>
/// Timeout внешних процессов для этапов видеообработки.
/// Значения времени задаются в секундах, размер вывода — в байтах.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class ProcessTimeoutOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "ProcessTimeouts";

    /// <summary>Timeout FFprobe в секундах.</summary>
    public int ProbeSeconds { get; init; } = 30;

    /// <summary>Timeout полного decode через FFmpeg в секундах.</summary>
    public int DecodeSeconds { get; init; } = 300;

    /// <summary>Timeout нормализации видео в секундах.</summary>
    public int NormalizeSeconds { get; init; } = 600;

    /// <summary>Timeout Remotion render в секундах.</summary>
    public int RenderSeconds { get; init; } = 1_800;

    /// <summary>Максимальный сохраняемый размер одного потока в байтах.</summary>
    public int MaxOutputBytes { get; init; } = 65_536;
}
