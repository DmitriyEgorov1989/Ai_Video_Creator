using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Представляет атомарно создаваемую запись задания и его единственного варианта.
/// </summary>
/// <param name="JobId">Идентификатор задания.</param>
/// <param name="VariantId">Идентификатор единственного варианта задания.</param>
/// <param name="Request">Проверенный запрос пользователя.</param>
/// <param name="Progress">Начальное состояние задания.</param>
/// <param name="CurrentApprovedEditPlanRevisionId">Текущая утверждённая ревизия плана или <see langword="null"/> до планирования.</param>
public sealed record JobCreationRecord(
    JobId JobId,
    VariantId VariantId,
    JobRequest Request,
    JobProgress Progress,
    RevisionId? CurrentApprovedEditPlanRevisionId);
