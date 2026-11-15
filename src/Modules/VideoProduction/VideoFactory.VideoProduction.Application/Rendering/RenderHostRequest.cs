namespace VideoFactory.VideoProduction.Application.Rendering;

/// <summary>
/// Описывает подготовленный к запуску запрос render-host.
/// </summary>
/// <param name="RequestFilePath">Абсолютный путь к JSON-файлу запроса.</param>
/// <param name="ExpectedOutputPath">Ожидаемый путь временного результата.</param>
/// <param name="Timeout">Максимальная продолжительность рендера.</param>
/// <param name="MaxOutputBytes">Максимальный размер каждого потока процесса в байтах.</param>
public sealed record RenderHostRequest(
    string RequestFilePath,
    string ExpectedOutputPath,
    TimeSpan Timeout,
    int MaxOutputBytes);
