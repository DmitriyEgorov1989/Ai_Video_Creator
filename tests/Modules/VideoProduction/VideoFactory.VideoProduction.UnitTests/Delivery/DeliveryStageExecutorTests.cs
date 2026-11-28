using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Delivery;
using System.Security.Cryptography;
using System.Text.Json;

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

    [Fact]
    public async Task SuccessfulSendPersistsReceiptWhenCallerIsCancelledAfterSend()
    {
        string root = Path.Combine(Path.GetTempPath(), $"delivery-cancelled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "final"));
        try
        {
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            byte[] finalBytes = [1, 2, 3];
            string finalHash = Convert.ToHexStringLower(SHA256.HashData(finalBytes));
            await File.WriteAllBytesAsync(Path.Combine(root, "final", "video.mp4"), finalBytes);
            ArtifactRecord render = Artifact("render-output", "render.mp4", "render-hash", 4);
            ArtifactRecord qa = Artifact("qa-report", "qa.json", "qa-hash", 5, render.ArtifactId);
            ArtifactRecord final = Artifact("final-mp4", "final/video.mp4", finalHash, finalBytes.Length, render.ArtifactId);
            string manifestJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId,
                variantId,
                render = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes },
                qa = new { artifactId = qa.ArtifactId, sha256 = qa.Sha256, sizeBytes = qa.SizeBytes },
                final = new { artifactId = final.ArtifactId, sha256 = final.Sha256, sizeBytes = final.SizeBytes },
                finalArtifactId = final.ArtifactId,
                qaArtifactId = qa.ArtifactId
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
            await File.WriteAllBytesAsync(Path.Combine(root, "final", "manifest.json"), manifestBytes);
            ArtifactRecord manifest = Artifact(
                "manifest", "final/manifest.json", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                manifestBytes.Length, final.ArtifactId);
            using CancellationTokenSource cancellation = new();
            Receipts receipts = new();
            DeliveryStageExecutor executor = new(
                new ArtifactSet([render, qa, final, manifest]),
                new CancellingDelivery(cancellation, finalHash, finalBytes.Length, () =>
                {
                    Assert.Equal("sending", Assert.Single(receipts.Items).Status);
                }), receipts, root, 123);
            JobCreationRecord job = new(
                jobId, variantId,
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null);

            StageExecutionResult result = await executor.ExecuteAsync(job, cancellation.Token);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal("succeeded", Assert.Single(receipts.Items).Status);

            ArtifactRecord Artifact(string type, string path, string hash, long size, ArtifactId? parent = null) =>
                new(ArtifactId.New(), type, path, hash, size, null, parent, "test", jobId, variantId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IndeterminateRecoveryDoesNotSendAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), $"delivery-indeterminate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "final"));
        try
        {
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            byte[] finalBytes = [1, 2, 3];
            string finalHash = Convert.ToHexStringLower(SHA256.HashData(finalBytes));
            await File.WriteAllBytesAsync(Path.Combine(root, "final", "video.mp4"), finalBytes);
            ArtifactRecord render = Artifact("render-output", "render.mp4", "render-hash", 4);
            ArtifactRecord qa = Artifact("qa-report", "qa.json", "qa-hash", 5, render.ArtifactId);
            ArtifactRecord final = Artifact("final-mp4", "final/video.mp4", finalHash, finalBytes.Length, render.ArtifactId);
            string manifestJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId,
                variantId,
                render = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes },
                qa = new { artifactId = qa.ArtifactId, sha256 = qa.Sha256, sizeBytes = qa.SizeBytes },
                final = new { artifactId = final.ArtifactId, sha256 = final.Sha256, sizeBytes = final.SizeBytes },
                finalArtifactId = final.ArtifactId,
                qaArtifactId = qa.ArtifactId
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
            await File.WriteAllBytesAsync(Path.Combine(root, "final", "manifest.json"), manifestBytes);
            ArtifactRecord manifest = Artifact(
                "manifest", "final/manifest.json", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                manifestBytes.Length, final.ArtifactId);
            Delivery delivery = new();
            DeliveryStageExecutor executor = new(
                new ArtifactSet([render, qa, final, manifest]), delivery,
                new Receipts(new DeliveryIntentStart.Indeterminate()), root, 123);
            JobCreationRecord job = new(
                jobId, variantId,
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal("delivery.unknown_result", result.ErrorCode);
            Assert.Equal(0, delivery.Calls);

            ArtifactRecord Artifact(string type, string path, string hash, long size, ArtifactId? parent = null) =>
                new(ArtifactId.New(), type, path, hash, size, null, parent, "test", jobId, variantId);
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
            return Task.FromResult(VideoDeliveryResult.Succeeded("message", new string('a', 64), 1));
        }
    }

    private sealed class Receipts(
        DeliveryIntentStart? start = null) : IDeliveryReceiptStore
    {
        public List<DeliveryReceipt> Items { get; } = [];

        public Task<DeliveryIntentStart> TryStartAsync(DeliveryIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (start is not null) return Task.FromResult(start);
            Items.Add(new DeliveryReceipt(
                intent.ReceiptId, intent.JobId, intent.VariantId, intent.ArtifactId, intent.RecipientId,
                null, "sending", null, null));
            return Task.FromResult<DeliveryIntentStart>(new DeliveryIntentStart.Started());
        }

        public Task CompleteAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Items.FindIndex(item => item.ReceiptId == receipt.ReceiptId);
            if (index >= 0) Items[index] = receipt;
            return Task.CompletedTask;
        }

        public Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class CancellingDelivery(
        CancellationTokenSource cancellation,
        string hash,
        long size,
        Action beforeReturn) : IVideoDelivery
    {
        public Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken)
        {
            beforeReturn();
            cancellation.Cancel();
            return Task.FromResult(VideoDeliveryResult.Succeeded("message", hash, size));
        }
    }

    private sealed class ArtifactSet(IReadOnlyList<ArtifactRecord> records) : IArtifactStore
    {
        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(records.SingleOrDefault(record => record.ArtifactId == artifactId));

        public Task<ArtifactRecord?> FindLatestAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult(records.LastOrDefault(record => record.JobId == jobId && record.ArtifactType == artifactType));

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>(
                records.Where(record => record.JobId == jobId && record.ArtifactType == artifactType).ToArray());

        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
