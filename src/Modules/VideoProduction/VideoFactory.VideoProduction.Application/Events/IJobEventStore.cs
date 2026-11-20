using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Events;

/// <summary>
/// Определяет persistence seam для событий Job.
/// </summary>
public interface IJobEventStore
{
    /// <summary>
    /// Добавляет неизменяемое событие.
    /// </summary>
    /// <param name="eventRecord">Событие Job.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Асинхронная операция сохранения.</returns>
    Task AppendAsync(JobEventRecord eventRecord, CancellationToken cancellationToken);

    /// <summary>
    /// Возвращает события Job в порядке создания.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>События Job; коллекция не равна <see langword="null"/>.</returns>
    Task<IReadOnlyList<JobEventRecord>> GetAsync(
        JobId jobId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Передаёт сохранённое событие фоновому обработчику пользовательских уведомлений.
/// </summary>
public interface IJobEventSignal
{
    /// <summary>Добавляет событие в сигнал после его сохранения в SQLite.</summary>
    void Publish(JobEventRecord eventRecord);

    /// <summary>Читает новые события до отмены host.</summary>
    IAsyncEnumerable<JobEventRecord> ReadAllAsync(CancellationToken cancellationToken);
}
