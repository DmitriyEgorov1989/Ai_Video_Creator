namespace VideoFactory.VideoProduction.Application.Processes;

/// <summary>
/// Безопасно запускает внешние процессы без shell-интерполяции.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Запускает процесс и ожидает его завершения, timeout или отмену.
    /// Ожидаемые ошибки интеграции возвращаются в результате.
    /// </summary>
    /// <param name="request">Параметры запуска.</param>
    /// <param name="cancellationToken">Токен отмены вызывающей операции.</param>
    /// <returns>Результат выполнения процесса.</returns>
    Task<ProcessExecutionResult> RunAsync(
        ProcessStartRequest request,
        CancellationToken cancellationToken);
}
