using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;

namespace VideoFactory.Bot.Workers;

/// <summary>
/// Явно сообщает, что прикладные stage adapters появятся в следующих задачах.
/// </summary>
public sealed class UnavailableJobStageExecutor : IJobStageExecutor
{
    /// <inheritdoc />
    public Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StageExecutionResult.Permanent(
            "STAGE_EXECUTOR_NOT_IMPLEMENTED",
            $"Stage '{job.Progress.Stage}' has no adapter yet."));
    }
}
