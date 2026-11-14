namespace VideoFactory.VideoProduction.Application.Processes;

/// <summary>
/// Результат выполнения внешнего процесса.
/// </summary>
/// <param name="Status">Итоговый статус выполнения.</param>
/// <param name="ExitCode">
/// Код завершения или <see langword="null"/>, если процесс не завершился
/// обычным образом или не был запущен.
/// </param>
/// <param name="StandardOutput">Сохранённый stdout, не равен <see langword="null"/>.</param>
/// <param name="StandardError">Сохранённый stderr, не равен <see langword="null"/>.</param>
/// <param name="ErrorCode">Код ошибки интеграции или <see langword="null"/>.</param>
/// <param name="ErrorMessage">Сообщение ошибки или <see langword="null"/>.</param>
/// <param name="StandardOutputTruncated">stdout был ограничен по размеру.</param>
/// <param name="StandardErrorTruncated">stderr был ограничен по размеру.</param>
public sealed record ProcessExecutionResult(
    ProcessExecutionStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? ErrorCode,
    string? ErrorMessage,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated)
{
    /// <summary>Возвращает признак завершения с кодом 0.</summary>
    public bool IsSuccess =>
        Status == ProcessExecutionStatus.Succeeded && ExitCode == 0;
}
