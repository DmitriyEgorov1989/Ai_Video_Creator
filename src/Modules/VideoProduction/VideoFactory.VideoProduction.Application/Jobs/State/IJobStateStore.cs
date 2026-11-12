using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Jobs.State;

/// <summary>
/// Определяет persistence seam для восстановления и конкурентного перехода состояния Job.
/// </summary>
public interface IJobStateStore
{
    /// <summary>
    /// Возвращает сохранённое состояние Job или <see langword="null"/>, если Job не найден.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Сохранённая запись или <see langword="null"/>.</returns>
    Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Применяет переход только при совпадении ожидаемых статуса и этапа.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="expectedStatus">Ожидаемый текущий статус.</param>
    /// <param name="expectedStage">Ожидаемый текущий этап.</param>
    /// <param name="nextProgress">Новое состояние Job.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Применённый переход, конфликт или отсутствие Job.</returns>
    Task<JobStateTransitionResult> TryTransitionAsync(
        JobId jobId,
        JobStatus expectedStatus,
        ProductionStage expectedStage,
        JobProgress nextProgress,
        CancellationToken cancellationToken);
}
