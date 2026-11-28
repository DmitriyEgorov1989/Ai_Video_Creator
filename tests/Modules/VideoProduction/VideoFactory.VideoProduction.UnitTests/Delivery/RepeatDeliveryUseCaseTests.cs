using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using System.Security.Cryptography;

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

    [Fact]
    public async Task RejectsSuccessfulTransportWhenUploadedBytesDoNotMatch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = new(
                jobId,
                VariantId.New(),
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null)
            {
                TelegramUserId = 123456
            };
            RequestStore requests = new();
            RepeatDeliveryResult result = await new RepeatDeliveryUseCase(
                new StateStore(job), new ArtifactSource(path), new Delivery(mismatch: true),
                requests, new ReceiptStore())
                .ExecuteAsync(new RepeatDeliveryCommand(jobId, 123456, 9001), CancellationToken.None);

            RepeatDeliveryResult.Rejected rejected = Assert.IsType<RepeatDeliveryResult.Rejected>(result);
            Assert.Equal("delivery.evidence_mismatch", rejected.Code);
            Assert.Equal("failed", requests.Receipt?.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnavailableArtifactIsRejectedWithoutReservingRequest()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4");
        JobId jobId = JobId.New();
        JobCreationRecord job = new(
            jobId, VariantId.New(),
            new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
            JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
            null)
        {
            TelegramUserId = 123456
        };
        ReceiptStore receipts = new();
        RequestStore requests = new();

        RepeatDeliveryResult result = await new RepeatDeliveryUseCase(
            new StateStore(job), new ArtifactSource(missingPath), new Delivery(), requests, receipts)
            .ExecuteAsync(new RepeatDeliveryCommand(jobId, 123456, 9001), CancellationToken.None);

        Assert.Equal("delivery.artifact_unavailable", Assert.IsType<RepeatDeliveryResult.Rejected>(result).Code);
        Assert.Null(receipts.Receipt);
        Assert.Null(requests.Receipt);
        Assert.Equal(0, requests.StartCalls);
    }

    [Fact]
    public async Task CancellationBeforeReservationDoesNotStartRequest()
    {
        string path = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = new(
                jobId, VariantId.New(),
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null)
            {
                TelegramUserId = 123456
            };
            ReceiptStore receipts = new();
            RequestStore requests = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RepeatDeliveryUseCase(
                new StateStore(job), new ArtifactSource(path), new Delivery(), requests, receipts)
                .ExecuteAsync(new RepeatDeliveryCommand(jobId, 123456, 9001), cancellation.Token));

            Assert.Null(receipts.Receipt);
            Assert.Null(requests.Receipt);
            Assert.Equal(0, requests.StartCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SuccessfulSendPersistsReceiptWhenCallerIsCancelledAfterSend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = CompletedJob(jobId);
            using CancellationTokenSource cancellation = new();
            ReceiptStore receipts = new();
            RequestStore requests = new();
            Delivery delivery = new(onSend: cancellation.Cancel);

            RepeatDeliveryResult result = await new RepeatDeliveryUseCase(
                new StateStore(job), new ArtifactSource(path), delivery, requests, receipts)
                .ExecuteAsync(new RepeatDeliveryCommand(jobId, 123456, 9001), cancellation.Token);

            Assert.IsType<RepeatDeliveryResult.Succeeded>(result);
            Assert.Equal("succeeded", requests.Receipt?.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectedArtifactDoesNotReserveUpdateAndSameUpdateCanBeRetried()
    {
        string path = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            JobId jobId = JobId.New();
            RequestStore requests = new();
            SequenceArtifactSource artifacts = new(path);
            Delivery delivery = new();
            RepeatDeliveryUseCase useCase = new(
                new StateStore(CompletedJob(jobId)), artifacts, delivery, requests, new ReceiptStore());
            RepeatDeliveryCommand command = new(jobId, 123456, 9001);

            RepeatDeliveryResult first = await useCase.ExecuteAsync(command, CancellationToken.None);
            RepeatDeliveryResult second = await useCase.ExecuteAsync(command, CancellationToken.None);

            Assert.Equal("delivery.file_changed", Assert.IsType<RepeatDeliveryResult.Rejected>(first).Code);
            Assert.IsType<RepeatDeliveryResult.Succeeded>(second);
            Assert.Equal(1, requests.StartCalls);
            Assert.Equal(1, delivery.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AlreadyProcessedUpdateDoesNotCheckArtifactAgain()
    {
        JobId jobId = JobId.New();
        DeliveryReceipt previous = new(
            Guid.NewGuid(), jobId, VariantId.New(), ArtifactId.New(), 123456,
            "message-1", "succeeded", null, null);
        RejectingArtifactSource artifacts = new();
        RequestStore requests = new(new RepeatDeliveryRequestStart.AlreadyProcessed(previous));

        RepeatDeliveryResult result = await new RepeatDeliveryUseCase(
            new StateStore(CompletedJob(jobId)), artifacts, new Delivery(), requests, new ReceiptStore())
            .ExecuteAsync(new RepeatDeliveryCommand(jobId, 123456, 9001), CancellationToken.None);

        RepeatDeliveryResult.AlreadyProcessed processed = Assert.IsType<RepeatDeliveryResult.AlreadyProcessed>(result);
        Assert.Equal("message-1", processed.RemoteMessageId);
        Assert.Equal(0, artifacts.Calls);
    }

    private static JobCreationRecord CompletedJob(JobId jobId) => new(
        jobId, VariantId.New(),
        new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
        JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
        null)
    {
        TelegramUserId = 123456
    };

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

    private sealed class SequenceArtifactSource(string path) : IRepeatDeliveryArtifactSource
    {
        private int calls;

        public Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult<RepeatDeliveryArtifactResult>(calls++ == 0
                ? new RepeatDeliveryArtifactResult.Rejected("delivery.file_changed", "The packaged MP4 changed after verification.")
                : new RepeatDeliveryArtifactResult.Succeeded(new RepeatDeliveryArtifact(ArtifactId.New(), path)));
    }

    private sealed class RejectingArtifactSource : IRepeatDeliveryArtifactSource
    {
        public int Calls { get; private set; }

        public Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(JobCreationRecord job, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<RepeatDeliveryArtifactResult>(
                new RepeatDeliveryArtifactResult.Rejected("delivery.file_changed", "File changed."));
        }
    }

    private sealed class Delivery(bool mismatch = false, Action? onSend = null) : IVideoDelivery
    {
        public int Calls { get; private set; }
        public byte[] Bytes { get; private set; } = [];

        public Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Bytes = File.ReadAllBytes(request.FilePath);
            onSend?.Invoke();
            return Task.FromResult(VideoDeliveryResult.Succeeded(
                "message-1", Convert.ToHexStringLower(SHA256.HashData(Bytes)).ToUpperInvariant(), mismatch ? Bytes.LongLength + 1 : Bytes.LongLength));
        }
    }

    private sealed class RequestStore(
        RepeatDeliveryRequestStart? start = null) : IRepeatDeliveryRequestStore
    {
        public DeliveryReceipt? Receipt { get; private set; }
        public int StartCalls { get; private set; }
        public Task<RepeatDeliveryRequestStart?> FindAsync(long telegramUpdateId, CancellationToken cancellationToken) =>
            Task.FromResult(start);

        public Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken) =>
            Start(cancellationToken);

        private Task<RepeatDeliveryRequestStart> Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            return Task.FromResult(start ?? new RepeatDeliveryRequestStart.Started());
        }

        public Task CompleteAsync(long requestId, DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Receipt = receipt;
            return Task.CompletedTask;
        }
    }

    private sealed class ReceiptStore : IDeliveryReceiptStore
    {
        public DeliveryReceipt? Receipt { get; private set; }
        public Task<DeliveryIntentStart> TryStartAsync(DeliveryIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult<DeliveryIntentStart>(new DeliveryIntentStart.Started());

        public Task CompleteAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            Receipt = receipt;
            return Task.CompletedTask;
        }

        public Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Receipt = receipt;
            return Task.CompletedTask;
        }

        public Task<bool> HasSucceededAsync(JobId jobId, VariantId variantId, ArtifactId artifactId, long recipientId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
