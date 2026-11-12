using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Events;

/// <summary>
/// Описывает неизменяемое событие жизненного цикла Job.
/// </summary>
/// <param name="EventId">Идентификатор события.</param>
/// <param name="JobId">Идентификатор Job.</param>
/// <param name="VariantId">Идентификатор Variant или <see langword="null"/>.</param>
/// <param name="EventType">Стабильное имя события.</param>
/// <param name="Stage">Связанный этап или <see langword="null"/>.</param>
/// <param name="PayloadJson">Небольшие технические данные события или <see langword="null"/>.</param>
/// <param name="CreatedAt">Время создания события.</param>
public sealed record JobEventRecord(
    Guid EventId,
    JobId JobId,
    VariantId? VariantId,
    string EventType,
    ProductionStage? Stage,
    string? PayloadJson,
    DateTimeOffset CreatedAt);
