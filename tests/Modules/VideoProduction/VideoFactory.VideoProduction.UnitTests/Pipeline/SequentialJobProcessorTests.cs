using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Pipeline;

public sealed class SequentialJobProcessorTests
{
    [Theory]
    [InlineData("network")]
    [InlineData("disk")]
    public async Task ExpectedIntegrationFailurePreservesStageAndAttempt(string failure)
    {
        FakeStateStore state = new();
        FakeAttemptStore attempts = new();
        SequentialJobProcessor processor = new(new FakeQueue(CreateJob()), state, attempts, new ThrowingExecutor(failure));
        Assert.True(await processor.ProcessNextAsync("worker", TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), 3, CancellationToken.None));
        Assert.Null(state.NextProgress);
        Assert.Equal(StageAttemptStatus.Retryable, attempts.Last?.Status);
        Assert.Equal("private provider message", attempts.Last?.ErrorMessage);
    }

    private sealed class ThrowingExecutor(string failure) : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken token) =>
            throw (failure == "network" ? new HttpRequestException("private provider message") : new IOException("private provider message"));
    }

    [Fact]
    public async Task ProcessesOneClaimedJobAndPersistsNextStage()
    {
        JobCreationRecord job = CreateJob();
        FakeQueue queue = new(job);
        FakeStateStore stateStore = new();
        FakeAttemptStore attempts = new();
        SequentialJobProcessor processor = new(
            queue,
            stateStore,
            attempts,
            new FakeExecutor(StageExecutionResult.Success()));

        bool processed = await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(ProductionStage.Analysis, stateStore.NextProgress?.Stage);
        Assert.Equal(JobStatus.Running, stateStore.NextProgress?.Status);
        Assert.Equal(StageAttemptStatus.Completed, attempts.Last?.Status);
    }

    [Fact]
    public async Task AppliedTransitionIsRecordedAsNotificationEvent()
    {
        JobCreationRecord job = CreateJob();
        FakeEventStore events = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            new AppliedStateStore(job),
            new FakeAttemptStore(),
            new FakeExecutor(StageExecutionResult.Success()),
            eventStore: events);

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Equal("job.stage_changed", events.Last?.EventType);
        Assert.Equal(ProductionStage.Analysis, events.Last?.Stage);
    }

    [Fact]
    public async Task PlanningApprovalWaitIsPersistedWithoutFailingTheJob()
    {
        JobCreationRecord job = CreateJob() with
        {
            Progress = JobProgress.Create(ProductionMode.Fast).Start()
                .CompletePreparation()
                .CompleteAnalysis()
        };
        FakeStateStore stateStore = new();
        FakeAttemptStore attempts = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            stateStore,
            attempts,
            new FakeExecutor(StageExecutionResult.WaitingForApproval(
                "planning.cost_approval_required",
                "Approval is required.",
                0.08m,
                1)));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Equal(JobStatus.AwaitingConfirmation, stateStore.NextProgress?.Status);
        Assert.Equal(ProductionStage.Planning, stateStore.NextProgress?.Stage);
        Assert.Equal(ConfirmationKind.CostApproval, stateStore.NextProgress?.PendingConfirmation);
        Assert.Equal(StageAttemptStatus.Completed, attempts.Last?.Status);
    }

    [Theory]
    [InlineData("Analysis")]
    [InlineData("Rendering")]
    public async Task UserCancellationMovesJobToCancelledWithoutAdvancingStage(string stageName)
    {
        ProductionStage stage = Enum.Parse<ProductionStage>(stageName);
        JobCreationRecord job = CreateJob() with
        {
            Progress = JobProgress.Restore(
                JobStatus.Running,
                stage,
                ProductionMode.Fast,
                ProductionMode.Fast,
                null)
        };
        FakeStateStore stateStore = new();
        FakeAttemptStore attempts = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            stateStore,
            attempts,
            new FakeExecutor(StageExecutionResult.Cancelled("USER_CANCELLED", "Cancelled by user.")));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Equal(JobStatus.Cancelled, stateStore.NextProgress?.Status);
        Assert.Equal(stage, stateStore.NextProgress?.Stage);
        Assert.Equal(StageAttemptStatus.Cancelled, attempts.Last?.Status);
    }

    [Fact]
    public async Task HostShutdownCancellationLeavesJobRecoverable()
    {
        JobCreationRecord job = CreateJob();
        FakeStateStore stateStore = new();
        FakeAttemptStore attempts = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            stateStore,
            attempts,
            new FakeExecutor(StageExecutionResult.Cancelled("HOST_STOPPING", "Host is stopping.")));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Null(stateStore.NextProgress);
        Assert.Equal(StageAttemptStatus.Cancelled, attempts.Last?.Status);
    }

    [Fact]
    public async Task HostShutdownPreservesProcessCancellationDiagnostic()
    {
        JobCreationRecord job = CreateJob();
        FakeAttemptStore attempts = new();
        using CancellationTokenSource stopping = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            new FakeStateStore(),
            attempts,
            new CancellingExecutor(stopping));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            stopping.Token));

        Assert.Equal(StageAttemptStatus.Cancelled, attempts.Last?.Status);
        Assert.Equal("HOST_STOPPING", attempts.Last?.ErrorCode);
        Assert.Contains("process.cancelled", attempts.Last?.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("external process", attempts.Last?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProductionStage.Preparation, ProductionStage.Analysis, JobStatus.Running)]
    [InlineData(ProductionStage.Analysis, ProductionStage.Planning, JobStatus.Running)]
    [InlineData(ProductionStage.Planning, ProductionStage.Rendering, JobStatus.Running)]
    [InlineData(ProductionStage.Rendering, ProductionStage.Critique, JobStatus.Running)]
    [InlineData(ProductionStage.Critique, ProductionStage.Verification, JobStatus.Running)]
    [InlineData(ProductionStage.Verification, ProductionStage.Packaging, JobStatus.Running)]
    [InlineData(ProductionStage.Packaging, ProductionStage.Delivery, JobStatus.Running)]
    [InlineData(ProductionStage.Delivery, ProductionStage.Delivery, JobStatus.Completed)]
    public async Task RestartRecoversInterruptedAttemptAtEveryStage(
        ProductionStage interruptedStage,
        ProductionStage expectedStage,
        JobStatus expectedStatus)
    {
        JobCreationRecord job = CreateJob() with
        {
            Progress = JobProgress.Restore(
                JobStatus.Running,
                interruptedStage,
                ProductionMode.Fast,
                ProductionMode.Fast,
                null)
        };
        StageAttemptRecord interrupted = new(
            Guid.NewGuid(),
            job.JobId,
            job.VariantId,
            interruptedStage,
            1,
            StageAttemptStatus.Running,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null);
        FakeAttemptStore attempts = new() { Previous = interrupted };
        FakeStateStore stateStore = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            stateStore,
            attempts,
            new FakeExecutor(StageExecutionResult.Success()));

        Assert.True(await processor.ProcessNextAsync(
            "recovery-worker",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        StageAttemptRecord recovered = Assert.Single(
            attempts.Records,
            attempt => attempt.AttemptId == interrupted.AttemptId
                && attempt.Status == StageAttemptStatus.Cancelled);
        Assert.Equal("stage.interrupted", recovered.ErrorCode);
        StageAttemptRecord completed = Assert.Single(
            attempts.Records,
            attempt => attempt.AttemptId != interrupted.AttemptId
                && attempt.Status == StageAttemptStatus.Completed);
        Assert.Equal(job.JobId, completed.JobId);
        Assert.Equal(job.VariantId, completed.VariantId);
        Assert.Equal(expectedStage, stateStore.NextProgress?.Stage);
        Assert.Equal(expectedStatus, stateStore.NextProgress?.Status);
    }

    [Fact]
    public async Task CritiqueRemountActionReturnsJobToRendering()
    {
        JobCreationRecord job = CreateJob() with
        {
            Progress = JobProgress.Restore(
                JobStatus.Running,
                ProductionStage.Critique,
                ProductionMode.Fast,
                ProductionMode.Fast,
                null)
        };
        FakeStateStore stateStore = new();
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            stateStore,
            new FakeAttemptStore(),
            new FakeExecutor(StageExecutionResult.Success(
                completionAction: StageCompletionAction.Remount)));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Equal(JobStatus.Running, stateStore.NextProgress?.Status);
        Assert.Equal(ProductionStage.Rendering, stateStore.NextProgress?.Stage);
    }

    [Fact]
    public async Task CompletedPreviousVisitDoesNotConsumeNewRetryBudget()
    {
        JobCreationRecord job = CreateJob() with
        {
            Progress = JobProgress.Restore(
                JobStatus.Running,
                ProductionStage.Rendering,
                ProductionMode.Fast,
                ProductionMode.Fast,
                null)
        };
        FakeAttemptStore attempts = new()
        {
            Previous = new StageAttemptRecord(
                Guid.NewGuid(),
                job.JobId,
                job.VariantId,
                ProductionStage.Rendering,
                3,
                StageAttemptStatus.Completed,
                null,
                null,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(-1))
        };
        SequentialJobProcessor processor = new(
            new FakeQueue(job),
            new FakeStateStore(),
            attempts,
            new FakeExecutor(StageExecutionResult.Retryable("render.transient", "Retry.")));

        Assert.True(await processor.ProcessNextAsync(
            "test-worker",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            3,
            CancellationToken.None));

        Assert.Equal(StageAttemptStatus.Retryable, attempts.Last?.Status);
    }

    private static JobCreationRecord CreateJob() => new(
        JobId.New(),
        VariantId.New(),
        new JobRequest(
            ArtifactId.New(),
            "Сделай динамично",
            ProductionMode.Fast,
            new ProductionSettings(null, true, false)),
        JobProgress.Create(ProductionMode.Fast).Start(),
        null);

    private sealed class FakeQueue(JobCreationRecord job) : IJobWorkQueue
    {
        private bool claimed;

        public Task<JobCreationRecord?> TryClaimNextAsync(
            string workerId,
            TimeSpan leaseTimeout,
            CancellationToken cancellationToken)
        {
            if (claimed)
            {
                return Task.FromResult<JobCreationRecord?>(null);
            }

            claimed = true;
            return Task.FromResult<JobCreationRecord?>(job);
        }

        public Task<bool> TryHeartbeatAsync(
            JobId jobId,
            string workerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeExecutor(StageExecutionResult result) : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(
            JobCreationRecord job,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CancellingExecutor(CancellationTokenSource stopping) : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(
            JobCreationRecord job,
            CancellationToken cancellationToken)
        {
            stopping.Cancel();
            return Task.FromResult(StageExecutionResult.Cancelled(
                "process.cancelled",
                "The external process was cancelled."));
        }
    }

    private sealed class FakeStateStore : IJobStateStore
    {
        public JobProgress? NextProgress { get; private set; }

        public Task<JobCreationRecord?> GetAsync(
            JobId jobId,
            CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(null);

        public Task<JobStateTransitionResult> TryTransitionAsync(
            JobId jobId,
            JobStatus expectedStatus,
            ProductionStage expectedStage,
            JobProgress nextProgress,
            CancellationToken cancellationToken)
        {
            NextProgress = nextProgress;
            return Task.FromResult<JobStateTransitionResult>(
                new JobStateTransitionResult.NotFound());
        }
    }

    private sealed class AppliedStateStore(JobCreationRecord job) : IJobStateStore
    {
        public Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(job);

        public Task<JobStateTransitionResult> TryTransitionAsync(
            JobId jobId,
            JobStatus expectedStatus,
            ProductionStage expectedStage,
            JobProgress nextProgress,
            CancellationToken cancellationToken) =>
            Task.FromResult<JobStateTransitionResult>(new JobStateTransitionResult.Applied(job with { Progress = nextProgress }));
    }

    private sealed class FakeEventStore : IJobEventStore
    {
        public JobEventRecord? Last { get; private set; }

        public Task AppendAsync(JobEventRecord eventRecord, CancellationToken cancellationToken)
        {
            Last = eventRecord;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JobEventRecord>> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JobEventRecord>>([]);
    }

    private sealed class FakeAttemptStore : IStageAttemptStore
    {
        public StageAttemptRecord? Last { get; private set; }
        public StageAttemptRecord? Previous { get; init; }
        public List<StageAttemptRecord> Records { get; } = [];

        public Task AppendAsync(
            StageAttemptRecord attempt,
            CancellationToken cancellationToken)
        {
            Last = attempt;
            Records.Add(attempt);
            return Task.CompletedTask;
        }

        public Task<StageAttemptRecord?> GetLatestAsync(
            JobId jobId,
            ProductionStage stage,
            CancellationToken cancellationToken) =>
            Task.FromResult(Previous);
    }
}
