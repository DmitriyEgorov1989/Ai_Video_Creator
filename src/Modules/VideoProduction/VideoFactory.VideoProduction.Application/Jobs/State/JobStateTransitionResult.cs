using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.VideoProduction.Application.Jobs.State;

/// <summary>
/// Представляет результат оптимистичного перехода состояния Job.
/// </summary>
public abstract record JobStateTransitionResult
{
    /// <summary>
    /// Переход применён.
    /// </summary>
    /// <param name="Record">Обновлённая запись Job.</param>
    public sealed record Applied(JobCreationRecord Record) : JobStateTransitionResult;

    /// <summary>
    /// Переход отклонён из-за устаревшего ожидаемого состояния.
    /// </summary>
    /// <param name="Current">Актуальная запись Job.</param>
    public sealed record Conflict(JobCreationRecord Current) : JobStateTransitionResult;

    /// <summary>
    /// Job с указанным идентификатором не найден.
    /// </summary>
    public sealed record NotFound : JobStateTransitionResult;
}
