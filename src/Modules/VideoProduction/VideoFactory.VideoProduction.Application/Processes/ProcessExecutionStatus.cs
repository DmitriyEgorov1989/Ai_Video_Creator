namespace VideoFactory.VideoProduction.Application.Processes;

/// <summary>
/// Результат завершения внешнего процесса.
/// </summary>
public enum ProcessExecutionStatus
{
    /// <summary>Процесс завершился с нулевым кодом.</summary>
    Succeeded,

    /// <summary>Процесс завершился с ненулевым кодом.</summary>
    NonZeroExitCode,

    /// <summary>Процесс превысил установленный timeout.</summary>
    TimedOut,

    /// <summary>Выполнение отменено вызывающим кодом.</summary>
    Cancelled,

    /// <summary>Процесс не удалось запустить.</summary>
    StartFailed
}
