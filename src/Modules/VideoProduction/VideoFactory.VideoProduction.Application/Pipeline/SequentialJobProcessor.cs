using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Pipeline;

/// <summary>
/// Выполняет не более одного Job за вызов и сохраняет результат текущего этапа.
/// </summary>
public sealed class SequentialJobProcessor
{
    private readonly IJobWorkQueue workQueue;
    private readonly IJobStateStore stateStore;
    private readonly IStageAttemptStore attemptStore;
    private readonly IJobStageExecutor stageExecutor;

    /// <summary>
    /// Создаёт последовательный процессор Job.
    /// </summary>
    public SequentialJobProcessor(
        IJobWorkQueue workQueue,
        IJobStateStore stateStore,
        IStageAttemptStore attemptStore,
        IJobStageExecutor stageExecutor)
    {
        this.workQueue = workQueue;
        this.stateStore = stateStore;
        this.attemptStore = attemptStore;
        this.stageExecutor = stageExecutor;
    }

    /// <summary>
    /// Захватывает и обрабатывает один Job. Если работы нет, возвращает false.
    /// </summary>
    public async Task<bool> ProcessNextAsync(
        string workerId,
        TimeSpan leaseTimeout,
        TimeSpan heartbeatInterval,
        int maxStageAttempts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("Worker ID cannot be empty.", nameof(workerId));
        }

        if (maxStageAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStageAttempts));
        }

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        JobCreationRecord? job = await workQueue.TryClaimNextAsync(
            workerId,
            leaseTimeout,
            cancellationToken);

        if (job is null)
        {
            return false;
        }

        ProductionStage stage = job.Progress.Stage;
        StageAttemptRecord? previousAttempt = await attemptStore.GetLatestAsync(
            job.JobId,
            stage,
            cancellationToken);
        int attemptNumber = (previousAttempt?.AttemptNumber ?? 0) + 1;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        await attemptStore.AppendAsync(
            new StageAttemptRecord(
                Guid.NewGuid(),
                job.JobId,
                job.VariantId,
                stage,
                attemptNumber,
                StageAttemptStatus.Running,
                null,
                null,
                startedAt,
                null),
            cancellationToken);

        StageExecutionResult result;
        using CancellationTokenSource heartbeatCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeatTask = RunHeartbeatAsync(
            job.JobId,
            workerId,
            heartbeatInterval,
            heartbeatCts.Token);

        try
        {
            result = await stageExecutor.ExecuteAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = StageExecutionResult.Cancelled(
                "HOST_STOPPING",
                "Stage execution was cancelled because the host is stopping.");
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
            {
            }
        }

        StageAttemptStatus attemptStatus = result.Kind switch
        {
            StageExecutionKind.Completed => StageAttemptStatus.Completed,
            StageExecutionKind.Retryable when attemptNumber < maxStageAttempts => StageAttemptStatus.Retryable,
            StageExecutionKind.Retryable => StageAttemptStatus.Permanent,
            StageExecutionKind.Permanent => StageAttemptStatus.Permanent,
            StageExecutionKind.Cancelled => StageAttemptStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException()
        };

        await attemptStore.AppendAsync(
            new StageAttemptRecord(
                Guid.NewGuid(),
                job.JobId,
                job.VariantId,
                stage,
                attemptNumber,
                attemptStatus,
                result.ErrorCode,
                result.ErrorMessage,
                startedAt,
                DateTimeOffset.UtcNow),
            cancellationToken);

        if (result.Kind == StageExecutionKind.Completed)
        {
            JobProgress nextProgress = JobStageTransitions.Complete(job.Progress);
            await stateStore.TryTransitionAsync(
                job.JobId,
                job.Progress.Status,
                job.Progress.Stage,
                nextProgress,
                cancellationToken);
        }
        else if (result.Kind is StageExecutionKind.Permanent
            || (result.Kind == StageExecutionKind.Retryable && attemptNumber >= maxStageAttempts))
        {
            await stateStore.TryTransitionAsync(
                job.JobId,
                job.Progress.Status,
                job.Progress.Stage,
                job.Progress.Fail(),
                cancellationToken);
        }

        return true;
    }

    private async Task RunHeartbeatAsync(
        VideoFactory.VideoProduction.Domain.Identifiers.JobId jobId,
        string workerId,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(heartbeatInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _ = await workQueue.TryHeartbeatAsync(
                jobId,
                workerId,
                cancellationToken);
        }
    }
}
