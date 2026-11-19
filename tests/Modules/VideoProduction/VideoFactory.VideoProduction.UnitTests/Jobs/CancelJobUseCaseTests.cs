using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Jobs;

public sealed class CancelJobUseCaseTests
{
    [Fact]
    public async Task RequestsCancellationForActiveAnalysisAndSignalsCoordinator()
    {
        JobCreationRecord job = CreateJob(ProductionStage.Analysis);
        FakeStore store = new(job);
        FakeCoordinator coordinator = new();
        CancelJobUseCase useCase = new(store, coordinator);

        JobCancellationResult result = await useCase.ExecuteAsync(123, CancellationToken.None);

        Assert.IsType<JobCancellationResult.Requested>(result);
        Assert.Equal(job.JobId, coordinator.CancelledJobId);
        Assert.Equal(job.JobId, store.Request?.JobId);
    }

    [Fact]
    public async Task RepeatingCancellationIsIdempotent()
    {
        JobCreationRecord job = CreateJob(ProductionStage.Rendering);
        FakeStore store = new(job) { AlreadyRequested = true };
        FakeCoordinator coordinator = new();
        CancelJobUseCase useCase = new(store, coordinator);

        JobCancellationResult result = await useCase.ExecuteAsync(123, CancellationToken.None);

        Assert.IsType<JobCancellationResult.AlreadyRequested>(result);
        Assert.Equal(job.JobId, coordinator.CancelledJobId);
    }

    private static JobCreationRecord CreateJob(ProductionStage stage) => new(
        JobId.New(),
        VariantId.New(),
        new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
        JobProgress.Restore(JobStatus.Running, stage, ProductionMode.Fast, ProductionMode.Fast, null),
        null)
    {
        TelegramUserId = 123
    };

    private sealed class FakeStore(JobCreationRecord? job) : IJobCancellationStore
    {
        public bool AlreadyRequested { get; init; }
        public JobCancellationRequest? Request { get; private set; }

        public Task<JobCreationRecord?> FindActiveAsync(long telegramUserId, CancellationToken cancellationToken) =>
            Task.FromResult(job);

        public Task<bool> RequestAsync(JobCancellationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(!AlreadyRequested);
        }

        public Task<bool> IsRequestedAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult(AlreadyRequested);
    }

    private sealed class FakeCoordinator : IJobCancellationCoordinator
    {
        public JobId? CancelledJobId { get; private set; }

        public CancellationToken Register(JobId jobId, CancellationToken hostToken) => hostToken;

        public void Cancel(JobId jobId) => CancelledJobId = jobId;

        public void Unregister(JobId jobId) { }
    }
}
