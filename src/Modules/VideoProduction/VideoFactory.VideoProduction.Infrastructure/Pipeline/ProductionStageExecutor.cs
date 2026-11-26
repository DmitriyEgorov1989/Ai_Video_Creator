using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Pipeline;

/// <summary>Выбирает исполнитель по текущему этапу Job.</summary>
public sealed class ProductionStageExecutor : IJobStageExecutor
{
    private readonly IJobStageExecutor preparation;
    private readonly IJobStageExecutor analysis;
    private readonly IJobStageExecutor planning;
    private readonly IJobStageExecutor rendering;
    private readonly IJobStageExecutor critique;
    private readonly IJobStageExecutor verification;
    private readonly IJobStageExecutor packaging;
    private readonly IJobStageExecutor delivery;

    /// <summary>Создаёт маршрутизатор этапов MVP.</summary>
    public ProductionStageExecutor(IJobStageExecutor preparation, IJobStageExecutor analysis, IJobStageExecutor planning, IJobStageExecutor rendering, IJobStageExecutor critique, IJobStageExecutor verification, IJobStageExecutor packaging, IJobStageExecutor delivery)
    {
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
        this.critique = critique ?? throw new ArgumentNullException(nameof(critique));
        this.verification = verification ?? throw new ArgumentNullException(nameof(verification));
        this.packaging = packaging ?? throw new ArgumentNullException(nameof(packaging));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    /// <summary>Создаёт маршрутизатор без подключённого Critique.</summary>
    public ProductionStageExecutor(
        IJobStageExecutor preparation,
        IJobStageExecutor analysis,
        IJobStageExecutor planning,
        IJobStageExecutor rendering,
        IJobStageExecutor verification,
        IJobStageExecutor packaging,
        IJobStageExecutor delivery)
        : this(preparation, analysis, planning, rendering, new UnavailableCritiqueStageExecutor(), verification, packaging, delivery)
    {
    }

    /// <summary>Создаёт маршрутизатор без подключённого Planning.</summary>
    public ProductionStageExecutor(
        IJobStageExecutor preparation,
        IJobStageExecutor analysis,
        IJobStageExecutor rendering,
        IJobStageExecutor verification,
        IJobStageExecutor packaging,
        IJobStageExecutor delivery)
        : this(preparation, analysis, new UnavailablePlanningStageExecutor(), rendering, new UnavailableCritiqueStageExecutor(), verification, packaging, delivery)
    {
    }

    /// <summary>
    /// Создаёт маршрутизатор без подключённого анализа для совместимости с подготовительными тестовыми сценариями.
    /// </summary>
    public ProductionStageExecutor(
        IJobStageExecutor preparation,
        IJobStageExecutor rendering,
        IJobStageExecutor verification,
        IJobStageExecutor packaging,
        IJobStageExecutor delivery)
        : this(preparation, new UnavailableAnalysisStageExecutor(), new UnavailablePlanningStageExecutor(), rendering, new UnavailableCritiqueStageExecutor(), verification, packaging, delivery)
    {
    }

    /// <inheritdoc />
    public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
        job.Progress.Stage switch
        {
            ProductionStage.Preparation => preparation.ExecuteAsync(job, cancellationToken),
            ProductionStage.Analysis => analysis.ExecuteAsync(job, cancellationToken),
            ProductionStage.Planning => planning.ExecuteAsync(job, cancellationToken),
            ProductionStage.Rendering => rendering.ExecuteAsync(job, cancellationToken),
            ProductionStage.Critique => critique.ExecuteAsync(job, cancellationToken),
            ProductionStage.Verification => verification.ExecuteAsync(job, cancellationToken),
            ProductionStage.Packaging => packaging.ExecuteAsync(job, cancellationToken),
            ProductionStage.Delivery => delivery.ExecuteAsync(job, cancellationToken),
            _ => Task.FromResult(StageExecutionResult.Permanent(
                "stage.unsupported",
                $"Stage '{job.Progress.Stage}' is not implemented yet."))
        };

    private sealed class UnavailableAnalysisStageExecutor : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult(StageExecutionResult.Permanent(
                "stage.unsupported",
                "Analysis stage is not configured for this router."));
    }

    private sealed class UnavailablePlanningStageExecutor : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult(StageExecutionResult.Permanent(
                "stage.unsupported",
                "Planning stage is not configured for this router."));
    }

    private sealed class UnavailableCritiqueStageExecutor : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult(StageExecutionResult.Permanent(
                "stage.unsupported",
                "Critique stage is not configured for this router."));
    }
}
