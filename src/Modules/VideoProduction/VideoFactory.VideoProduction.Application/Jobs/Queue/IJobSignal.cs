using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Jobs.Queue;

/// <summary>
/// Передаёт обработчику сигнал о новой работе. Сигнал не является источником истины.
/// </summary>
public interface IJobSignal
{
    /// <summary>
    /// Публикует идентификатор Job.
    /// </summary>
    ValueTask PublishAsync(JobId jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Ожидает сигнал ограниченное время. По таймауту вызывающий код обязан проверить SQLite.
    /// </summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Читает опубликованные сигналы.
    /// </summary>
    IAsyncEnumerable<JobId> ReadAllAsync(CancellationToken cancellationToken);
}
