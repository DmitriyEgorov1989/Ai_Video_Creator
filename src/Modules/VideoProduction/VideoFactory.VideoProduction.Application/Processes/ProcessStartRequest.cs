namespace VideoFactory.VideoProduction.Application.Processes;

/// <summary>
/// Описывает один безопасный запуск внешнего процесса.
/// </summary>
/// <param name="FileName">Путь или имя исполняемого файла.</param>
/// <param name="Arguments">
/// Аргументы процесса, каждый элемент которых передаётся отдельно.
/// </param>
/// <param name="Timeout">Максимальная продолжительность выполнения.</param>
/// <param name="MaxOutputBytes">
/// Максимальный сохраняемый размер каждого потока в байтах.
/// </param>
public sealed record ProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaxOutputBytes);
