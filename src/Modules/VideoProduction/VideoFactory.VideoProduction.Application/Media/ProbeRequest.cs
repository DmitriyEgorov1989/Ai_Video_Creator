namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>
/// Описывает запрос на получение технических metadata медиафайла.
/// </summary>
/// <param name="FilePath">Абсолютный путь к локальному файлу.</param>
/// <param name="Timeout">Максимальная продолжительность probe в секундах.</param>
/// <param name="MaxOutputBytes">Максимальный размер каждого вывода процесса в байтах.</param>
public sealed record ProbeRequest(
    string FilePath,
    TimeSpan Timeout,
    int MaxOutputBytes);
