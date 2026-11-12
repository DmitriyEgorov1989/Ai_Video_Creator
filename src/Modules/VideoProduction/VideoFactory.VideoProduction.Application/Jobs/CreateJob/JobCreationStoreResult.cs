namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Представляет результат попытки атомарного сохранения задания.
/// </summary>
public abstract record JobCreationStoreResult
{
    /// <summary>
    /// Представляет результат создания новой записи.
    /// </summary>
    /// <param name="Record">Созданная запись задания.</param>
    public sealed record Created(JobCreationRecord Record) : JobCreationStoreResult;

    /// <summary>
    /// Представляет идемпотентный повтор уже существующей записи.
    /// </summary>
    /// <param name="Record">Ранее созданная запись задания.</param>
    public sealed record Existing(JobCreationRecord Record) : JobCreationStoreResult;

    /// <summary>
    /// Представляет ожидаемый отказ persistence seam.
    /// </summary>
    /// <param name="Error">Причина отказа.</param>
    public sealed record Rejected(CreateJobError Error) : JobCreationStoreResult;
}
