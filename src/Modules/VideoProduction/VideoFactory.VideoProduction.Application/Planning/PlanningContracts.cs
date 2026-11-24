using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;

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
    IReadOnlyDictionary<TemplateId, TemplateDefinition> Templates,
    long? TelegramUserId = null,
    decimal JobLimitUsd = 0m);

/// <summary>
/// Данные, передаваемые provider-адаптеру. Provider не получает пути и произвольные assets.
/// </summary>
public sealed record ProviderPlanningRequest(
    string? Comment,
    EditTempo Tempo,
    int? TargetDurationSeconds,
    bool CaptionsEnabled,
    ImmutableArray<ProviderSegment> AllowedSegments,
    string? ExtractedText = null,
    int RepairAttempt = 0,
    EditingEvaluation? EditingEvaluation = null);

/// <summary>Известный provider-у сегмент анализа.</summary>
public sealed record ProviderSegment(SegmentId Id, long DurationMicroseconds);

/// <summary>Нейтральный ответ provider до доменной проверки.</summary>
public sealed record ProviderPlanResponse(
    int SchemaVersion,
    int Seed,
    ImmutableArray<ProviderVideoClip> VideoClips,
    ImmutableArray<ProviderCaption> Captions,
    decimal ActualCostUsd);

/// <summary>Ошибка provider после выполнения платного вызова.</summary>
public sealed class PlanningProviderException : HttpRequestException
{
    /// <summary>Создаёт ошибку provider.</summary>
    public PlanningProviderException(string message, decimal? actualCostUsd = null, Exception? innerException = null)
        : base(message, innerException) => ActualCostUsd = actualCostUsd;

    /// <summary>Фактическая стоимость вызова или <see langword="null" />, если provider её не сообщил.</summary>
    public decimal? ActualCostUsd { get; }
}

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
    /// <summary>Принимает решение до платного вызова с учётом уже понесённых расходов.</summary>
    PlanningCostDecision Decide(PlanningCostEstimate estimate, decimal accumulatedActualCostUsd);
}

/// <summary>Параметры политики стоимости планирования.</summary>
public sealed record PlanningCostPolicyOptions(decimal JobLimitUsd);

/// <summary>Политика подтверждения стоимости одного задания.</summary>
public sealed class PlanningCostPolicy : IPlanningCostPolicy
{
    private readonly decimal jobLimitUsd;

    /// <summary>Создаёт политику с лимитом в долларах США.</summary>
    public PlanningCostPolicy(PlanningCostPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.JobLimitUsd < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Planning cost limit cannot be negative.");
        }

        jobLimitUsd = options.JobLimitUsd;
    }

    /// <inheritdoc />
    public PlanningCostDecision Decide(PlanningCostEstimate estimate, decimal accumulatedActualCostUsd)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (accumulatedActualCostUsd < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(accumulatedActualCostUsd));
        }

        decimal projectedCost = accumulatedActualCostUsd + estimate.EstimatedCostUsd;
        return projectedCost > jobLimitUsd
            ? new PlanningCostDecision(false, true,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Planning is estimated at {0:0.####} USD and exceeds the {1:0.####} USD job limit.",
                    estimate.EstimatedCostUsd,
                    jobLimitUsd))
            : new PlanningCostDecision(true, false, null);
    }
}

/// <summary>Состояние сохранённого подтверждения дополнительной стоимости.</summary>
public enum PlanningApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>Сохранённый запрос подтверждения стоимости.</summary>
public sealed record PlanningApproval(
    Guid ApprovalId,
    JobId JobId,
    VariantId VariantId,
    long TelegramUserId,
    int Version,
    decimal EstimatedCostUsd,
    decimal AccumulatedActualCostUsd,
    decimal LimitUsd,
    PlanningApprovalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

/// <summary>Решение пользователя по запросу подтверждения.</summary>
public enum PlanningApprovalDecision
{
    Approve,
    Reject
}

/// <summary>Результат атомарного применения решения по стоимости.</summary>
public abstract record PlanningApprovalResult
{
    /// <summary>Решение применено впервые.</summary>
    public sealed record Applied(PlanningApproval Approval) : PlanningApprovalResult;

    /// <summary>Решение уже было применено ранее.</summary>
    public sealed record AlreadyResolved(PlanningApproval Approval) : PlanningApprovalResult;

    /// <summary>Запрос не найден или устарел.</summary>
    public sealed record Stale : PlanningApprovalResult;

    /// <summary>Пользователь не является владельцем задания.</summary>
    public sealed record Forbidden : PlanningApprovalResult;
}

/// <summary>Persistence seam подтверждений стоимости.</summary>
public interface IPlanningApprovalStore
{
    /// <summary>Возвращает актуальное подтверждение или <see langword="null"/>.</summary>
    Task<PlanningApproval?> FindAsync(JobId jobId, VariantId variantId, CancellationToken cancellationToken);

    /// <summary>Сохраняет новое ожидание и возвращает его версию.</summary>
    Task<PlanningApproval> SavePendingAsync(
        JobId jobId,
        VariantId variantId,
        long telegramUserId,
        decimal estimatedCostUsd,
        decimal accumulatedActualCostUsd,
        decimal limitUsd,
        CancellationToken cancellationToken);

    /// <summary>Применяет решение только к актуальному ожиданию.</summary>
    Task<PlanningApprovalResult> ResolveAsync(
        JobId jobId,
        VariantId variantId,
        long telegramUserId,
        int version,
        PlanningApprovalDecision decision,
        CancellationToken cancellationToken);
}

/// <summary>Входные данные Telegram-действия по подтверждению стоимости.</summary>
public sealed record PlanningApprovalCommand(
    JobId JobId,
    VariantId VariantId,
    long TelegramUserId,
    int Version,
    PlanningApprovalDecision Decision);

/// <summary>Application use case применения решения пользователя.</summary>
public sealed class PlanningApprovalUseCase
{
    private readonly IPlanningApprovalStore approvalStore;
    private readonly IJobStateStore stateStore;
    private readonly Jobs.Queue.IJobSignal? signal;

    /// <summary>Создаёт use case подтверждения стоимости.</summary>
    public PlanningApprovalUseCase(
        IPlanningApprovalStore approvalStore,
        IJobStateStore stateStore,
        Jobs.Queue.IJobSignal? signal = null)
    {
        this.approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.signal = signal;
    }

    /// <summary>Применяет решение только к ожидающему подтверждения Job.</summary>
    public async Task<PlanningApprovalResult> ExecuteAsync(
        PlanningApprovalCommand command,
        CancellationToken cancellationToken)
    {
        JobCreationRecord? job = await stateStore.GetAsync(command.JobId, cancellationToken);
        if (job is null || job.VariantId != command.VariantId)
        {
            return new PlanningApprovalResult.Stale();
        }

        if (job.TelegramUserId is not long owner || owner != command.TelegramUserId)
        {
            return new PlanningApprovalResult.Forbidden();
        }

        if (job.Progress.Status != Domain.Jobs.JobStatus.AwaitingConfirmation
            || job.Progress.Stage != Domain.Jobs.ProductionStage.Planning
            || job.Progress.PendingConfirmation != Domain.Jobs.ConfirmationKind.CostApproval)
        {
            return new PlanningApprovalResult.Stale();
        }

        PlanningApprovalResult result = await approvalStore.ResolveAsync(
            command.JobId,
            command.VariantId,
            command.TelegramUserId,
            command.Version,
            command.Decision,
            cancellationToken);
        PlanningApproval? approval = result switch
        {
            PlanningApprovalResult.Applied applied => applied.Approval,
            PlanningApprovalResult.AlreadyResolved resolved => resolved.Approval,
            _ => null
        };
        if (approval is null)
        {
            return result;
        }

        JobProgress next = command.Decision == PlanningApprovalDecision.Approve
            ? job.Progress.ApproveCost()
            : job.Progress.Fail();
        JobStateTransitionResult transition = await stateStore.TryTransitionAsync(
            job.JobId,
            job.Progress.Status,
            job.Progress.Stage,
            next,
            cancellationToken);
        if (transition is JobStateTransitionResult.Applied && command.Decision == PlanningApprovalDecision.Approve)
        {
            if (signal is not null)
            {
                await signal.PublishAsync(command.JobId, cancellationToken);
            }
        }

        return transition is JobStateTransitionResult.Applied
            ? result
            : new PlanningApprovalResult.Stale();
    }
}

/// <summary>Результат одной платной попытки планирования.</summary>
public sealed record PlanningAttemptRecord(
    Guid AttemptId,
    JobId JobId,
    VariantId VariantId,
    int AttemptNumber,
    int RepairAttempt,
    decimal EstimatedCostUsd,
    decimal? ActualCostUsd,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

/// <summary>Хранилище аудита платных попыток планирования.</summary>
public interface IPlanningAttemptStore
{
    /// <summary>Сохраняет запись одной платной попытки.</summary>
    Task SaveAsync(PlanningAttemptRecord attempt, CancellationToken cancellationToken);
}

/// <summary>Сохраняемые решения оформления поверх корректного semantic timeline.</summary>
/// <param name="Style">Запрошенный и разрешённый Reels style.</param>
/// <param name="Captions">Решение Caption Director или <see langword="null"/>.</param>
/// <param name="Visuals">Решение Visual Director или <see langword="null"/>.</param>
/// <param name="Reframe">Решение Smart Reframe или <see langword="null"/>.</param>
/// <param name="Audio">Решение Audio Director или <see langword="null"/>.</param>
public sealed record ReelsPresentationPlan(
    ReelsStyleConfiguration Style,
    CaptionDirectorDecision? Captions,
    VisualDirectorDecision? Visuals,
    SmartReframeDecision? Reframe,
    AudioDirectorDecision? Audio)
{
    /// <summary>Создаёт presentation из результатов единого skill engine.</summary>
    /// <param name="style">Разрешённые параметры style.</param>
    /// <param name="evaluation">Результаты editing skills.</param>
    /// <returns>Типизированные решения directors; отсутствующие решения представлены <see langword="null"/>.</returns>
    public static ReelsPresentationPlan FromEvaluation(
        ReelsStyleConfiguration style,
        EditingEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(evaluation);
        return new(
            style,
            evaluation.Decisions.OfType<CaptionDirectorDecision>().SingleOrDefault(),
            evaluation.Decisions.OfType<VisualDirectorDecision>().SingleOrDefault(),
            evaluation.Decisions.OfType<SmartReframeDecision>().SingleOrDefault(),
            evaluation.Decisions.OfType<AudioDirectorDecision>().SingleOrDefault());
    }
}

/// <summary>Неизменяемая сохранённая ревизия монтажного плана.</summary>
public sealed record EditPlanRevision(
    RevisionId RevisionId,
    JobId JobId,
    VariantId VariantId,
    string PlanHash,
    EditPlan Plan,
    decimal ActualCostUsd,
    ReelsPresentationPlan? Presentation = null);

/// <summary>Результат поиска или сохранения ревизии плана.</summary>
public interface IEditPlanRevisionStore
{
    /// <summary>Находит текущую утверждённую ревизию задания.</summary>
    /// <returns>Утверждённая ревизия или <see langword="null" />, если план ещё не утверждён.</returns>
    Task<EditPlanRevision?> FindApprovedAsync(
        JobId jobId,
        VariantId variantId,
        CancellationToken cancellationToken);

    /// <summary>Находит ревизию по её идентификатору.</summary>
    /// <param name="revisionId">Идентификатор ревизии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ревизия или <see langword="null"/>, если она не найдена.</returns>
    Task<EditPlanRevision?> GetAsync(
        RevisionId revisionId,
        CancellationToken cancellationToken);

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

    /// <summary>Сохраняет timeline и опциональные presentation decisions одной неизменяемой ревизией.</summary>
    Task<EditPlanRevision> SaveAsync(
        EditPlan plan,
        string planHash,
        decimal actualCostUsd,
        ReelsPresentationPlan? presentation,
        CancellationToken cancellationToken) =>
        SaveAsync(plan, planHash, actualCostUsd, cancellationToken);
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
    public sealed record WaitingForApproval(decimal EstimatedCostUsd, string Message, int? Version = null) : PlanningResult;

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
