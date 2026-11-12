using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Jobs.Queue;

/// <summary>
/// Предоставляет атомарное получение Job и обновление его lease.
/// </summary>
public interface IJobWorkQueue
{
    /// <summary>
    /// Возвращает и захватывает следующий доступный Job.
    /// </summary>
    /// <param name="workerId">Идентификатор текущего обработчика.</param>
    /// <param name="leaseTimeout">Возраст heartbeat, после которого Job восстанавливается.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Захваченный Job или <see langword="null"/>, если работы нет.</returns>
    Task<JobCreationRecord?> TryClaimNextAsync(
        string workerId,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Продлевает lease активного Job.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="workerId">Идентификатор обработчика.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если heartbeat сохранён.</returns>
    Task<bool> TryHeartbeatAsync(
        JobId jobId,
        string workerId,
        CancellationToken cancellationToken);
}
