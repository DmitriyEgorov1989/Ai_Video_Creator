using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Представляет результат сценария создания задания.
/// </summary>
public abstract record CreateJobResult
{
    /// <summary>
    /// Представляет успешно созданное или найденное задание.
    /// </summary>
    /// <param name="JobId">Идентификатор задания.</param>
    /// <param name="VariantId">Идентификатор единственного варианта.</param>
    /// <param name="WasCreated">Признак создания записи в текущем вызове.</param>
    public sealed record Succeeded(
        JobId JobId,
        VariantId VariantId,
        bool WasCreated) : CreateJobResult;

    /// <summary>
    /// Представляет ожидаемый отказ без исключения.
    /// </summary>
    /// <param name="Error">Типизированная причина отказа.</param>
    public sealed record Rejected(CreateJobError Error) : CreateJobResult;
}
