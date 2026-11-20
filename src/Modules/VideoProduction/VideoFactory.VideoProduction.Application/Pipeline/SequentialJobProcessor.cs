using System.Text.Json;
using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
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
    private readonly IJobCancellationStore? cancellationStore;
    private readonly IJobCancellationCoordinator? cancellationCoordinator;
    private readonly IJobEventStore? eventStore;
    private readonly IJobEventSignal? eventSignal;

    /// <summary>
    /// Создаёт последовательный процессор Job.
    /// </summary>
    public SequentialJobProcessor(
        IJobWorkQueue workQueue,
        IJobStateStore stateStore,
        IStageAttemptStore attemptStore,
        IJobStageExecutor stageExecutor,
        IJobCancellationStore? cancellationStore = null,
        IJobCancellationCoordinator? cancellationCoordinator = null,
        IJobEventStore? eventStore = null,
        IJobEventSignal? eventSignal = null)
    {
        this.workQueue = workQueue;
        this.stateStore = stateStore;
        this.attemptStore = attemptStore;
        this.stageExecutor = stageExecutor;
        this.cancellationStore = cancellationStore;
        this.cancellationCoordinator = cancellationCoordinator;
        this.eventStore = eventStore;
        this.eventSignal = eventSignal;
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
        if (previousAttempt?.Status == StageAttemptStatus.Running)
        {
            await attemptStore.AppendAsync(previousAttempt with
            {
                Status = StageAttemptStatus.Cancelled,
                ErrorCode = "stage.interrupted",
                ErrorMessage = "Previous worker stopped before completing this attempt.",
                CompletedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        int attemptNumber = (previousAttempt?.AttemptNumber ?? 0) + 1;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Guid attemptId = Guid.NewGuid();

        await attemptStore.AppendAsync(
            new StageAttemptRecord(
                attemptId,
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
        CancellationToken stageCancellationToken = cancellationCoordinator?.Register(job.JobId, cancellationToken)
            ?? cancellationToken;

        try
        {
            if (cancellationStore is not null
                && await cancellationStore.IsRequestedAsync(job.JobId, cancellationToken))
            {
                result = StageExecutionResult.Cancelled(
                    "USER_CANCELLED",
                    "Stage execution was cancelled by the user before it started.");
            }
            else
            {
                result = await stageExecutor.ExecuteAsync(job, stageCancellationToken);
            }
        }
        catch (OperationCanceledException) when (stageCancellationToken.IsCancellationRequested)
        {
            result = cancellationToken.IsCancellationRequested
                ? StageExecutionResult.Cancelled(
                    "HOST_STOPPING",
                    "Stage execution was cancelled because the host is stopping.")
                : StageExecutionResult.Cancelled(
                    "USER_CANCELLED",
                    "Stage execution was cancelled by the user.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TimeoutException)
        {
            // Исходная диагностика хранится в SQLite, а не выводится в обычный журнал.
            result = StageExecutionResult.Retryable(exception.GetType().Name, exception.Message);
        }
        finally
        {
            cancellationCoordinator?.Unregister(job.JobId);
            await heartbeatCts.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
            {
            }
        }

        if (result.Kind == StageExecutionKind.Cancelled
            && !cancellationToken.IsCancellationRequested
            && cancellationStore is not null
            && await cancellationStore.IsRequestedAsync(job.JobId, cancellationToken))
        {
            result = StageExecutionResult.Cancelled(
                "USER_CANCELLED",
                "Stage execution was cancelled by the user.");
        }

        StageAttemptStatus attemptStatus = result.Kind switch
        {
            StageExecutionKind.Completed => StageAttemptStatus.Completed,
            StageExecutionKind.WaitingForApproval => StageAttemptStatus.Completed,
            StageExecutionKind.Retryable when attemptNumber < maxStageAttempts => StageAttemptStatus.Retryable,
            StageExecutionKind.Retryable => StageAttemptStatus.Permanent,
            StageExecutionKind.Permanent => StageAttemptStatus.Permanent,
            StageExecutionKind.Cancelled => StageAttemptStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException()
        };

        using CancellationTokenSource persistenceCts = new(TimeSpan.FromSeconds(5));
        CancellationToken persistenceToken = cancellationToken.IsCancellationRequested ? persistenceCts.Token : cancellationToken;
        await attemptStore.AppendAsync(
            new StageAttemptRecord(
                attemptId,
                job.JobId,
                job.VariantId,
                stage,
                attemptNumber,
                attemptStatus,
                result.ErrorCode,
                result.ErrorMessage,
                startedAt,
                DateTimeOffset.UtcNow),
            persistenceToken);

        if (result.Kind == StageExecutionKind.Completed)
        {
            JobProgress nextProgress = JobStageTransitions.Complete(job.Progress);
            await PersistTransitionAsync(
                job.JobId,
                job.Progress.Status,
                job.Progress.Stage,
                nextProgress,
                result,
                persistenceToken);
        }
        else if (result.Kind == StageExecutionKind.WaitingForApproval)
        {
            if (job.Progress.Stage != ProductionStage.Planning)
            {
                await PersistTransitionAsync(
                    job.JobId,
                    job.Progress.Status,
                    job.Progress.Stage,
                    job.Progress.Fail(),
                    result,
                    persistenceToken);
            }
            else
            {
                await PersistTransitionAsync(
                    job.JobId,
                    job.Progress.Status,
                    job.Progress.Stage,
                    job.Progress.AwaitCostApproval(),
                    result,
                    persistenceToken);
            }
        }
        else if (result.Kind is StageExecutionKind.Permanent
            || (result.Kind == StageExecutionKind.Retryable && attemptNumber >= maxStageAttempts))
        {
            await PersistTransitionAsync(
                job.JobId,
                job.Progress.Status,
                job.Progress.Stage,
                job.Progress.Fail(),
                result,
                persistenceToken);
        }
        else if (result.Kind == StageExecutionKind.Cancelled
            && string.Equals(result.ErrorCode, "USER_CANCELLED", StringComparison.Ordinal))
        {
            await PersistTransitionAsync(
                job.JobId,
                job.Progress.Status,
                job.Progress.Stage,
                job.Progress.Cancel(),
                result,
                persistenceToken);
        }

        return true;
    }

    private async Task PersistTransitionAsync(
        VideoFactory.VideoProduction.Domain.Identifiers.JobId jobId,
        JobStatus expectedStatus,
        ProductionStage expectedStage,
        JobProgress nextProgress,
        StageExecutionResult result,
        CancellationToken cancellationToken)
    {
        JobStateTransitionResult transition = await stateStore.TryTransitionAsync(
            jobId,
            expectedStatus,
            expectedStage,
            nextProgress,
            cancellationToken);

        if (transition is not JobStateTransitionResult.Applied applied
            || eventStore is null)
        {
            return;
        }

        JobEventRecord eventRecord = new(
            Guid.NewGuid(),
            applied.Record.JobId,
            applied.Record.VariantId,
            GetEventType(applied.Record.Progress),
            applied.Record.Progress.Stage,
            JsonSerializer.Serialize(new { errorCode = result.ErrorCode }),
            DateTimeOffset.UtcNow);
        await eventStore.AppendAsync(eventRecord, cancellationToken);
        eventSignal?.Publish(eventRecord);
    }

    private static string GetEventType(JobProgress progress) =>
        progress.Status switch
        {
            JobStatus.Completed => "job.completed",
            JobStatus.Failed => "job.failed",
            JobStatus.Cancelled => "job.cancelled",
            JobStatus.AwaitingConfirmation => "job.awaiting_confirmation",
            _ => "job.stage_changed"
        };

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
