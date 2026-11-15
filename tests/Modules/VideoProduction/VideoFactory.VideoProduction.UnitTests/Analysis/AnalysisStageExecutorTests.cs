using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Pipeline;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class AnalysisStageExecutorTests
{
    [Fact]
    public async Task ExecutesAnalysisAndPublishesResult()
    {
        string root = Path.Combine(Path.GetTempPath(), "analysis-stage-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            string masterPath = Path.Combine(root, "master.mp4");
            await File.WriteAllBytesAsync(masterPath, [1, 2, 3]);
            FakeArtifactStore artifacts = new(
                new ArtifactRecord(originalId, "original-video", "original.mp4", "original", 3,
                    Metadata(25_000_000), null, "test", jobId),
                new ArtifactRecord(masterId, "master-video", "master.mp4", "master", 3,
                    MasterMetadata(originalId, masterId), originalId, "test", jobId));
            AnalysisResult analysis = new(
                1,
                RevisionId.New(),
                originalId,
                masterId,
                [],
                [],
                [new Scene(new MasterRange(0, 25_000_000))],
                []);
            FakeAnalysisService service = new(analysis);
            FakePublisher publisher = new();
            AnalysisStageExecutor executor = new(service, publisher, artifacts, root);

            StageExecutionResult result = await executor.ExecuteAsync(CreateJob(jobId, originalId), CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.NotNull(service.Request);
            Assert.Equal(originalId, service.Request.OriginalAssetId);
            Assert.Equal(masterId, service.Request.MasterAssetId);
            Assert.Equal(25_000_000, service.Request.MasterDurationMicroseconds);
            Assert.Same(analysis, publisher.Analysis);
            Assert.Equal(jobId, publisher.JobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingMasterReturnsPermanentFailureWithoutPublishing()
    {
        string root = Path.Combine(Path.GetTempPath(), "analysis-stage-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            ArtifactId originalId = ArtifactId.New();
            FakeArtifactStore artifacts = new(new ArtifactRecord(
                originalId, "original-video", "original.mp4", "original", 3,
                Metadata(25_000_000), null, "test", jobId));
            FakePublisher publisher = new();
            AnalysisStageExecutor executor = new(
                new FakeAnalysisService(null), publisher, artifacts, root);

            StageExecutionResult result = await executor.ExecuteAsync(CreateJob(jobId, originalId), CancellationToken.None);

            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal("analysis.master_missing", result.ErrorCode);
            Assert.Null(publisher.Analysis);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingIntactAnalysisArtifactIsReused()
    {
        string root = Path.Combine(Path.GetTempPath(), "analysis-stage-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            string masterPath = Path.Combine(root, "master.mp4");
            await File.WriteAllBytesAsync(masterPath, [1, 2, 3]);
            string analysisPath = Path.Combine(root, "analysis.json");
            byte[] analysisBytes = [4, 5, 6];
            await File.WriteAllBytesAsync(analysisPath, analysisBytes);
            FakeArtifactStore artifacts = new(
                new ArtifactRecord(originalId, "original-video", "original.mp4", "original", 3,
                    Metadata(25_000_000), null, "test", jobId),
                new ArtifactRecord(masterId, "master-video", "master.mp4", "master", 3,
                    MasterMetadata(originalId, masterId), originalId, "test", jobId),
                new ArtifactRecord(ArtifactId.New(), "analysis-result", "analysis.json",
                    Convert.ToHexStringLower(SHA256.HashData(analysisBytes)), analysisBytes.Length,
                    null, masterId, "analysis-v1", jobId));
            FakePublisher publisher = new();
            AnalysisStageExecutor executor = new(
                new FakeAnalysisService(null), publisher, artifacts, root);

            StageExecutionResult result = await executor.ExecuteAsync(CreateJob(jobId, originalId), CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal(0, publisher.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionStageExecutorRoutesAnalysisToAnalysisExecutor()
    {
        FakeStageExecutor analysis = new();
        ProductionStageExecutor router = new(
            new FakeStageExecutor(), analysis, new FakeStageExecutor(),
            new FakeStageExecutor(), new FakeStageExecutor(), new FakeStageExecutor());

        StageExecutionResult result = await router.ExecuteAsync(
            CreateJob(JobId.New(), ArtifactId.New()), CancellationToken.None);

        Assert.Equal(StageExecutionKind.Completed, result.Kind);
        Assert.Equal(1, analysis.Calls);
    }

    private static JobCreationRecord CreateJob(JobId jobId, ArtifactId originalId) => new(
        jobId,
        VariantId.New(),
        new JobRequest(originalId, "Сделай динамично", ProductionMode.Fast,
            new ProductionSettings(null, true, false)),
        JobProgress.Create(ProductionMode.Fast).Start().CompletePreparation(),
        null);

    private static string Metadata(long duration) => JsonSerializer.Serialize(new
    {
        mediaMetadata = new { durationMicroseconds = duration }
    });

    private static string MasterMetadata(ArtifactId originalId, ArtifactId masterId) => JsonSerializer.Serialize(new
    {
        originalMetadata = new { durationMicroseconds = 25_000_000L },
        mediaMetadata = new { durationMicroseconds = 25_000_000L },
        mapping = new
        {
            originalArtifactId = originalId.ToString(),
            masterArtifactId = masterId.ToString(),
            originalStartMicroseconds = 0L,
            originalDurationMicroseconds = 25_000_000L,
            masterStartMicroseconds = 0L,
            masterDurationMicroseconds = 25_000_000L
        }
    });

    private sealed class FakeAnalysisService(AnalysisResult? analysis) : IAnalysisService
    {
        public AnalysisRequest? Request { get; private set; }

        public Task<AnalysisExecutionResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<AnalysisExecutionResult>(analysis is null
                ? new AnalysisExecutionResult.Failed("analysis.failed", "failed")
                : new AnalysisExecutionResult.Succeeded(analysis));
        }
    }

    private sealed class FakePublisher : IAnalysisArtifactPublisher
    {
        public JobId? JobId { get; private set; }
        public AnalysisResult? Analysis { get; private set; }
        public int Calls { get; private set; }

        public Task<AnalysisArtifactPublishResult> PublishAsync(
            JobId jobId,
            AnalysisResult analysis,
            CancellationToken cancellationToken)
        {
            Calls++;
            JobId = jobId;
            Analysis = analysis;
            return Task.FromResult(new AnalysisArtifactPublishResult(true, ArtifactId.New(), null, null));
        }
    }

    private sealed class FakeStageExecutor : IJobStageExecutor
    {
        public int Calls { get; private set; }

        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(StageExecutionResult.Success());
        }
    }

    private sealed class FakeArtifactStore(params ArtifactRecord[] records) : IArtifactStore
    {
        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(records.SingleOrDefault(record => record.ArtifactId == artifactId));

        public Task<ArtifactRecord?> FindLatestAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult(records.LastOrDefault(record => record.JobId == jobId && record.ArtifactType == artifactType));

        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
    }
}
