namespace VideoFactory.VideoProduction.Application.Pipeline;

/// <summary>
/// Определяет классификацию результата одного этапа.
/// </summary>
public enum StageExecutionKind
{
    /// <summary>Этап завершён успешно.</summary>
    Completed,

    /// <summary>Ошибка временная, этап можно повторить.</summary>
    Retryable,

    /// <summary>Ошибка постоянная, повтор не поможет.</summary>
    Permanent,

    /// <summary>Этап отменён.</summary>
    Cancelled
}

/// <summary>
/// Результат выполнения этапа с исходной диагностикой.
/// </summary>
/// <param name="Kind">Классификация результата.</param>
/// <param name="ErrorCode">Исходный код ошибки или <see langword="null"/>.</param>
/// <param name="ErrorMessage">Исходное сообщение ошибки или <see langword="null"/>.</param>
public sealed record StageExecutionResult(
    StageExecutionKind Kind,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static StageExecutionResult Success() =>
        new(StageExecutionKind.Completed, null, null);

    /// <summary>Создаёт результат временной ошибки.</summary>
    public static StageExecutionResult Retryable(string code, string message) =>
        new(StageExecutionKind.Retryable, code, message);

    /// <summary>Создаёт результат постоянной ошибки.</summary>
    public static StageExecutionResult Permanent(string code, string message) =>
        new(StageExecutionKind.Permanent, code, message);

    /// <summary>Создаёт результат отмены.</summary>
    public static StageExecutionResult Cancelled(string? code, string? message) =>
        new(StageExecutionKind.Cancelled, code, message);
}
