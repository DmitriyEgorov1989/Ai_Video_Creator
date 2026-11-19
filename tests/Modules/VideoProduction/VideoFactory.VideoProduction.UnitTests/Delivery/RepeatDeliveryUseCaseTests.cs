using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Delivery;

public sealed class RepeatDeliveryUseCaseTests
{
    [Fact]
    public async Task SendsVerifiedFinalMp4WithoutRenderingAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            JobCreationRecord job = new(
                jobId,
                variantId,
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null)
            {
                TelegramUserId = 123456
            };
            Delivery delivery = new();
            RepeatDeliveryUseCase useCase = new(
                new StateStore(job),
                new ArtifactSource(path),
                delivery,
                new RequestStore(),
                new ReceiptStore());

            RepeatDeliveryResult result = await useCase.ExecuteAsync(
                new RepeatDeliveryCommand(jobId, 123456, 9001),
                CancellationToken.None);

            Assert.IsType<RepeatDeliveryResult.Succeeded>(result);
            Assert.Equal([1, 2, 3], delivery.Bytes);
            Assert.Equal(1, delivery.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StateStore(JobCreationRecord job) : IJobStateStore
    {
        public Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(job.JobId == jobId ? job : null);

        public Task<JobStateTransitionResult> TryTransitionAsync(JobId jobId, JobStatus expectedStatus, ProductionStage expectedStage, JobProgress nextProgress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ArtifactSource(string path) : IRepeatDeliveryArtifactSource
    {
        public Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult<RepeatDeliveryArtifactResult>(new RepeatDeliveryArtifactResult.Succeeded(
                new RepeatDeliveryArtifact(ArtifactId.New(), path)));
    }

    private sealed class Delivery : IVideoDelivery
    {
        public int Calls { get; private set; }
        public byte[] Bytes { get; private set; } = [];

        public Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Bytes = File.ReadAllBytes(request.FilePath);
            return Task.FromResult(VideoDeliveryResult.Succeeded("message-1"));
        }
    }

    private sealed class RequestStore : IRepeatDeliveryRequestStore
    {
        public Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken) =>
            Task.FromResult<RepeatDeliveryRequestStart>(new RepeatDeliveryRequestStart.Started());

        public Task CompleteAsync(long requestId, DeliveryReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReceiptStore : IDeliveryReceiptStore
    {
        public Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
