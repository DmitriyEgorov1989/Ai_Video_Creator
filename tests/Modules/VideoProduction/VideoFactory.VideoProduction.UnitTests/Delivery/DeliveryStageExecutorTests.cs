using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Delivery;

namespace VideoFactory.VideoProduction.UnitTests.Delivery;

public sealed class DeliveryStageExecutorTests
{
    [Theory]
    [InlineData(false, "delivery.final_missing", StageExecutionKind.Retryable)]
    [InlineData(true, "delivery.file_missing", StageExecutionKind.Permanent)]
    public async Task MissingFinalNeverSendsOrWritesReceipt(
        bool registerMissingFile,
        string expectedCode,
        StageExecutionKind expectedKind)
    {
        string root = Path.Combine(Path.GetTempPath(), $"delivery-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            ArtifactRecord? final = registerMissingFile
                ? new ArtifactRecord(
                    ArtifactId.New(),
                    "final-mp4",
                    "final/missing.mp4",
                    "hash",
                    3,
                    null,
                    null,
                    "test",
                    jobId,
                    variantId)
                : null;
            Delivery delivery = new();
            Receipts receipts = new();
            DeliveryStageExecutor executor = new(
                new Artifacts(final), delivery, receipts, root, 123);
            JobCreationRecord job = new(
                jobId,
                variantId,
                new JobRequest(
                    ArtifactId.New(),
                    "comment",
                    ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(
                    JobStatus.Running,
                    ProductionStage.Delivery,
                    ProductionMode.Fast,
                    ProductionMode.Fast,
                    null),
                null);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(expectedKind, result.Kind);
            Assert.Equal(expectedCode, result.ErrorCode);
            Assert.Equal(0, delivery.Calls);
            Assert.Empty(receipts.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Delivery : IVideoDelivery
    {
        public int Calls { get; private set; }

        public Task<VideoDeliveryResult> SendAsync(
            VideoDeliveryRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(VideoDeliveryResult.Succeeded("message"));
        }
    }

    private sealed class Receipts : IDeliveryReceiptStore
    {
        public List<DeliveryReceipt> Items { get; } = [];

        public Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            Items.Add(receipt);
            return Task.CompletedTask;
        }

        public Task<bool> HasSucceededAsync(
            JobId jobId,
            VariantId variantId,
            ArtifactId artifactId,
            long recipientId,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class Artifacts(ArtifactRecord? final) : IArtifactStore
    {
        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(final?.ArtifactId == artifactId ? final : null);

        public Task<ArtifactRecord?> FindLatestAsync(
            JobId jobId,
            string artifactType,
            CancellationToken cancellationToken) =>
            Task.FromResult(final is { ArtifactType: "final-mp4" } && artifactType == "final-mp4" ? final : null);

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(
            JobId jobId,
            string artifactType,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>(
                final is { ArtifactType: "final-mp4" } && artifactType == "final-mp4" ? [final] : []);

        public Task<ArtifactRegistrationResult> RegisterAsync(
            ArtifactRecord record,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
