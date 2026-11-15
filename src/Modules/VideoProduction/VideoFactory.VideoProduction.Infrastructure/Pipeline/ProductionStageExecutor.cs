using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Pipeline;

/// <summary>Выбирает исполнитель по текущему этапу Job.</summary>
public sealed class ProductionStageExecutor : IJobStageExecutor
{
    private readonly IJobStageExecutor preparation;
    private readonly IJobStageExecutor rendering;
    private readonly IJobStageExecutor verification;
    private readonly IJobStageExecutor packaging;
    private readonly IJobStageExecutor delivery;

    /// <summary>Создаёт маршрутизатор этапов MVP.</summary>
    public ProductionStageExecutor(IJobStageExecutor preparation, IJobStageExecutor rendering, IJobStageExecutor verification, IJobStageExecutor packaging, IJobStageExecutor delivery)
    {
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
        this.verification = verification ?? throw new ArgumentNullException(nameof(verification));
        this.packaging = packaging ?? throw new ArgumentNullException(nameof(packaging));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    /// <inheritdoc />
    public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
        job.Progress.Stage switch
        {
            ProductionStage.Preparation => preparation.ExecuteAsync(job, cancellationToken),
            ProductionStage.Rendering => rendering.ExecuteAsync(job, cancellationToken),
            ProductionStage.Verification => verification.ExecuteAsync(job, cancellationToken),
            ProductionStage.Packaging => packaging.ExecuteAsync(job, cancellationToken),
            ProductionStage.Delivery => delivery.ExecuteAsync(job, cancellationToken),
            _ => Task.FromResult(StageExecutionResult.Permanent(
                "stage.unsupported",
                $"Stage '{job.Progress.Stage}' is not implemented yet."))
        };
}
