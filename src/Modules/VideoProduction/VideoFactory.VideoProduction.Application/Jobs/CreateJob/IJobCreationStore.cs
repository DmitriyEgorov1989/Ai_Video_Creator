namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Определяет persistence seam для атомарного создания одного задания.
/// </summary>
public interface IJobCreationStore
{
    /// <summary>
    /// Создаёт запись по ключу исходного ArtifactId или возвращает существующую запись.
    /// </summary>
    /// <param name="record">Предлагаемая запись задания.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат создания, повтора или ожидаемого отказа.</returns>
    Task<JobCreationStoreResult> CreateAsync(
        JobCreationRecord record,
        CancellationToken cancellationToken);
}
