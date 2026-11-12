using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Pipeline;

public sealed class SequentialJobProcessorTests
{
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

    private sealed class FakeAttemptStore : IStageAttemptStore
    {
        public StageAttemptRecord? Last { get; private set; }

        public Task AppendAsync(
            StageAttemptRecord attempt,
            CancellationToken cancellationToken)
        {
            Last = attempt;
            return Task.CompletedTask;
        }

        public Task<StageAttemptRecord?> GetLatestAsync(
            JobId jobId,
            ProductionStage stage,
            CancellationToken cancellationToken) =>
            Task.FromResult<StageAttemptRecord?>(null);
    }
}
