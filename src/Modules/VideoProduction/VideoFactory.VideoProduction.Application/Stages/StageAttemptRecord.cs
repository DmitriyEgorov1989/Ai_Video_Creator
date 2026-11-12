using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Stages;

/// <summary>
/// Описывает одну попытку выполнения этапа Job.
/// </summary>
/// <param name="AttemptId">Идентификатор попытки.</param>
/// <param name="JobId">Идентификатор Job.</param>
/// <param name="VariantId">Идентификатор Variant или <see langword="null"/>.</param>
/// <param name="Stage">Этап выполнения.</param>
/// <param name="AttemptNumber">Порядковый номер попытки, начиная с единицы.</param>
/// <param name="Status">Результат попытки.</param>
/// <param name="ErrorCode">Исходный код ошибки или <see langword="null"/>.</param>
/// <param name="ErrorMessage">Исходное сообщение ошибки или <see langword="null"/>.</param>
/// <param name="StartedAt">Время начала попытки.</param>
/// <param name="CompletedAt">Время завершения или <see langword="null"/>.</param>
public sealed record StageAttemptRecord(
    Guid AttemptId,
    JobId JobId,
    VariantId? VariantId,
    ProductionStage Stage,
    int AttemptNumber,
    StageAttemptStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Определяет результат выполнения попытки этапа.
/// </summary>
public enum StageAttemptStatus
{
    /// <summary>Этап выполняется.</summary>
    Running,

    /// <summary>Этап завершён успешно.</summary>
    Completed,

    /// <summary>Этап можно повторить.</summary>
    Retryable,

    /// <summary>Этап завершён необратимой ошибкой.</summary>
    Permanent,

    /// <summary>Этап отменён.</summary>
    Cancelled
}
