using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Stages;

/// <summary>
/// Определяет persistence seam для сохранения попыток этапов.
/// </summary>
public interface IStageAttemptStore
{
    /// <summary>
    /// Сохраняет одну попытку этапа.
    /// </summary>
    /// <param name="attempt">Описание попытки.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Асинхронная операция сохранения.</returns>
    Task AppendAsync(StageAttemptRecord attempt, CancellationToken cancellationToken);

    /// <summary>
    /// Возвращает последнюю сохранённую попытку указанного этапа.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="stage">Этап выполнения.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Последняя попытка или <see langword="null"/>.</returns>
    Task<StageAttemptRecord?> GetLatestAsync(
        Domain.Identifiers.JobId jobId,
        ProductionStage stage,
        CancellationToken cancellationToken);
}
