using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.Application.Planning;

/// <summary>
/// Запрос построения одного монтажного плана.
/// </summary>
public sealed record PlanningRequest(
    JobId JobId,
    VariantId VariantId,
    AnalysisResult Analysis,
    string? Comment,
    ProductionSettings Settings,
    IReadOnlyDictionary<ArtifactId, MediaAsset> Assets,
    IReadOnlyDictionary<TemplateId, TemplateDefinition> Templates);

/// <summary>
/// Данные, передаваемые provider-адаптеру. Provider не получает пути и произвольные assets.
/// </summary>
public sealed record ProviderPlanningRequest(
    string? Comment,
    EditTempo Tempo,
    int? TargetDurationSeconds,
    bool CaptionsEnabled,
    ImmutableArray<ProviderSegment> AllowedSegments,
    int RepairAttempt = 0);

/// <summary>Известный provider-у сегмент анализа.</summary>
public sealed record ProviderSegment(SegmentId Id, long DurationMicroseconds);

/// <summary>Нейтральный ответ provider до доменной проверки.</summary>
public sealed record ProviderPlanResponse(
    int SchemaVersion,
    int Seed,
    ImmutableArray<ProviderVideoClip> VideoClips,
    ImmutableArray<ProviderCaption> Captions,
    decimal ActualCostUsd);

/// <summary>Видео-фрагмент ответа provider.</summary>
public sealed record ProviderVideoClip(
    SegmentId SegmentId,
    OutputRange Output,
    CropPreset Crop);

/// <summary>Субтитр ответа provider.</summary>
public sealed record ProviderCaption(string Text, OutputRange Output);

/// <summary>Оценка стоимости платного provider-вызова.</summary>
public sealed record PlanningCostEstimate(
    decimal EstimatedCostUsd,
    string Provider,
    string Model);

/// <summary>Результат проверки лимита стоимости.</summary>
public sealed record PlanningCostDecision(
    bool IsAllowed,
    bool RequiresApproval,
    string? Message);

/// <summary>Абстракция внешнего планировщика.</summary>
public interface IEditPlanProvider
{
    /// <summary>Оценивает стоимость без выполнения платного вызова.</summary>
    Task<PlanningCostEstimate> EstimateCostAsync(
        ProviderPlanningRequest request,
        CancellationToken cancellationToken);

    /// <summary>Возвращает структурированный ответ provider.</summary>
    Task<ProviderPlanResponse> CreatePlanAsync(
        ProviderPlanningRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Политика лимита стоимости одного задания.</summary>
public interface IPlanningCostPolicy
{
    /// <summary>Принимает решение до платного вызова.</summary>
    PlanningCostDecision Decide(PlanningCostEstimate estimate);
}

/// <summary>Неизменяемая сохранённая ревизия монтажного плана.</summary>
public sealed record EditPlanRevision(
    RevisionId RevisionId,
    JobId JobId,
    VariantId VariantId,
    string PlanHash,
    EditPlan Plan,
    decimal ActualCostUsd);

/// <summary>Результат поиска или сохранения ревизии плана.</summary>
public interface IEditPlanRevisionStore
{
    /// <summary>Находит ранее сохранённый план с тем же содержимым.</summary>
    Task<EditPlanRevision?> FindAsync(
        JobId jobId,
        VariantId variantId,
        string planHash,
        CancellationToken cancellationToken);

    /// <summary>Сохраняет новую ревизию, если hash ещё не занят.</summary>
    Task<EditPlanRevision> SaveAsync(
        EditPlan plan,
        string planHash,
        decimal actualCostUsd,
        CancellationToken cancellationToken);
}

/// <summary>Результат построения монтажного плана.</summary>
public abstract record PlanningResult
{
    private PlanningResult() { }

    /// <summary>План создан впервые.</summary>
    public sealed record Created(EditPlanRevision Revision) : PlanningResult;

    /// <summary>Найден ранее сохранённый идентичный план.</summary>
    public sealed record Existing(EditPlanRevision Revision) : PlanningResult;

    /// <summary>Планирование ожидает подтверждения превышенного лимита.</summary>
    public sealed record WaitingForApproval(decimal EstimatedCostUsd, string Message) : PlanningResult;

    /// <summary>Планирование завершилось ожидаемой ошибкой.</summary>
    public sealed record Rejected(string ErrorCode, string ErrorMessage) : PlanningResult;
}

/// <summary>Исключение преобразования ответа provider в доменную модель.</summary>
public sealed class PlanConstructionException : Exception
{
    /// <summary>Создаёт исключение с машинным кодом ошибки.</summary>
    public PlanConstructionException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <summary>Машинный код ошибки.</summary>
    public string ErrorCode { get; }
}
