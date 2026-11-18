using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Pipeline;
using VideoFactory.VideoProduction.Infrastructure.Planning;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Planning;

public sealed class PlanningStageExecutorTests
{
    [Fact]
    public async Task SequentialProcessorCreatesApprovedPlanAndRepeatDoesNotCallProvider()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Assert.True(await fixture.ProcessAsync());

        JobCreationRecord firstState = (await fixture.State.GetAsync(fixture.Job.JobId, CancellationToken.None))!;
        StageAttemptRecord planningAttempt = (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Planning, CancellationToken.None))!;
        Assert.True(firstState.Progress.Stage == ProductionStage.Rendering, planningAttempt.ErrorMessage);
        Assert.Equal("planning.created", planningAttempt.ErrorCode);
        EditPlanRevision? revision = await fixture.Revisions.FindApprovedAsync(
            fixture.Job.JobId,
            fixture.Job.VariantId,
            CancellationToken.None);
        Assert.NotNull(revision);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);

        JobCreationRecord planningState = firstState with
        {
            Progress = JobProgress.Restore(
                JobStatus.Running,
                ProductionStage.Planning,
                ProductionMode.Fast,
                ProductionMode.Fast,
                null)
        };
        StageExecutionResult repeat = await fixture.Executor.ExecuteAsync(planningState, CancellationToken.None);

        Assert.Equal(StageExecutionKind.Completed, repeat.Kind);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);
        EditPlanRevision repeatedRevision = (await fixture.Revisions.FindApprovedAsync(
            fixture.Job.JobId,
            fixture.Job.VariantId,
            CancellationToken.None))!;
        Assert.Equal(revision.RevisionId, repeatedRevision.RevisionId);
        Assert.Equal(revision.PlanHash, repeatedRevision.PlanHash);
    }

    [Fact]
    public async Task CostLimitLeavesJobWaitingWithoutCallingProvider()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Provider.EstimatedCostUsd = 0.08m;

        Assert.True(await fixture.ProcessAsync());

        JobCreationRecord state = (await fixture.State.GetAsync(fixture.Job.JobId, CancellationToken.None))!;
        Assert.Equal(JobStatus.AwaitingConfirmation, state.Progress.Status);
        Assert.Equal(ProductionStage.Planning, state.Progress.Stage);
        Assert.Equal(ConfirmationKind.CostApproval, state.Progress.PendingConfirmation);
        Assert.Equal(0, fixture.Provider.CreatePlanCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"planning-stage-{Guid.NewGuid():N}");
        public string ConnectionString => $"Data Source={Path.Combine(Root, "jobs.db")};Pooling=False";
        public SqliteArtifactStore Artifacts { get; private set; } = null!;
        public SqliteJobStateStore State { get; private set; } = null!;
        public SqliteEditPlanRevisionStore Revisions { get; private set; } = null!;
        public CreateJobResult.Succeeded Job { get; private set; } = null!;
        public TestProvider Provider { get; private set; } = null!;
        public PlanningStageExecutor Executor { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.Root);
            await new SqliteMigrationRunner(fixture.ConnectionString).InitializeAsync(CancellationToken.None);
            fixture.Artifacts = new SqliteArtifactStore(fixture.ConnectionString);
            fixture.State = new SqliteJobStateStore(fixture.ConnectionString);
            fixture.Revisions = new SqliteEditPlanRevisionStore(fixture.ConnectionString);

            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            string sourcePath = Path.Combine(fixture.Root, "source.mp4");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            MediaMetadata metadata = new(
                "mp4",
                6_000_000,
                [new VideoStreamMetadata(0, "h264", 640, 360, new FrameRate(30, 1), false, 0)],
                [new AudioStreamMetadata(1, "aac", 48_000, 2)]);
            fixture.Artifacts.RegisterAsync(new ArtifactRecord(
                originalId,
                "OriginalVideo",
                "source.mp4",
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))),
                3,
                JsonSerializer.Serialize(new { mediaMetadata = metadata, originalMetadata = metadata }, JsonOptions),
                null,
                "test"), CancellationToken.None).GetAwaiter().GetResult();

            fixture.Job = Assert.IsType<CreateJobResult.Succeeded>(await new CreateJobUseCase(
                new SqliteJobCreationStore(fixture.ConnectionString)).ExecuteAsync(
                    new CreateJobCommand(originalId, "Сделай коротко", new ProductionSettings(null, false, false))));

            ArtifactRecord master = new(
                masterId,
                "master-video",
                $"data/jobs/{fixture.Job.JobId}/normalized/master.mp4",
                "master-hash",
                3,
                JsonSerializer.Serialize(new { mediaMetadata = metadata, originalMetadata = metadata }, JsonOptions),
                originalId,
                "test",
                fixture.Job.JobId);
            await fixture.Artifacts.RegisterAsync(master, CancellationToken.None);

            SegmentId segmentId = SegmentId.New();
            AnalysisResult analysis = new(
                1,
                RevisionId.New(),
                originalId,
                masterId,
                [],
                [],
                [new Scene(new MasterRange(0, 3_000_000))],
                [new AllowedSegment(segmentId, new SourceMapping(
                    originalId,
                    new OriginalRange(0, 3_000_000),
                    masterId,
                    new MasterRange(0, 3_000_000)))]);
            string analysisPath = Path.Combine(fixture.Root, $"data/jobs/{fixture.Job.JobId}/analysis/result.json");
            Directory.CreateDirectory(Path.GetDirectoryName(analysisPath)!);
            await File.WriteAllTextAsync(analysisPath, JsonSerializer.Serialize(analysis, JsonOptions));
            byte[] analysisBytes = await File.ReadAllBytesAsync(analysisPath);
            await fixture.Artifacts.RegisterAsync(new ArtifactRecord(
                ArtifactId.New(),
                "analysis-result",
                Path.GetRelativePath(fixture.Root, analysisPath).Replace(Path.DirectorySeparatorChar, '/'),
                Convert.ToHexStringLower(SHA256.HashData(analysisBytes)),
                analysisBytes.Length,
                JsonSerializer.Serialize(new { schemaVersion = 1, analysis.RevisionId }, JsonOptions),
                masterId,
                "analysis-v1",
                fixture.Job.JobId), CancellationToken.None);

            JobCreationRecord current = (await fixture.State.GetAsync(fixture.Job.JobId, CancellationToken.None))!;
            await fixture.State.TryTransitionAsync(current.JobId, current.Progress.Status, current.Progress.Stage,
                current.Progress.Start(), CancellationToken.None);
            current = (await fixture.State.GetAsync(fixture.Job.JobId, CancellationToken.None))!;
            await fixture.State.TryTransitionAsync(current.JobId, current.Progress.Status, current.Progress.Stage,
                current.Progress.CompletePreparation(), CancellationToken.None);
            current = (await fixture.State.GetAsync(fixture.Job.JobId, CancellationToken.None))!;
            await fixture.State.TryTransitionAsync(current.JobId, current.Progress.Status, current.Progress.Stage,
                current.Progress.CompleteAnalysis(), CancellationToken.None);

            fixture.Provider = new TestProvider(segmentId);
            EditPlanRevisionStore revisionStore = new(fixture.Revisions);
            fixture.Executor = new PlanningStageExecutor(
                new EditPlanPlanner(
                    fixture.Provider,
                    new PlanningCostPolicy(new PlanningCostPolicyOptions(0.05m)),
                    revisionStore,
                    new SqlitePlanningAttemptStore(fixture.ConnectionString)),
                fixture.Artifacts,
                fixture.Root,
                0.05m);
            return fixture;
        }

        public async Task<bool> ProcessAsync()
        {
            ProductionStageExecutor router = new(
                new UnexpectedStageExecutor(),
                new UnexpectedStageExecutor(),
                Executor,
                new UnexpectedStageExecutor(),
                new UnexpectedStageExecutor(),
                new UnexpectedStageExecutor(),
                new UnexpectedStageExecutor());
            SequentialJobProcessor processor = new(
                new SqliteJobWorkQueue(ConnectionString),
                State,
                new SqliteStageAttemptStore(ConnectionString),
                router);
            return await processor.ProcessNextAsync("test-worker", TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), 3, CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProvider(SegmentId segmentId) : IEditPlanProvider
    {
        public int CreatePlanCount { get; private set; }
        public decimal EstimatedCostUsd { get; set; } = 0.01m;

        public Task<PlanningCostEstimate> EstimateCostAsync(ProviderPlanningRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PlanningCostEstimate(EstimatedCostUsd, "test", "test"));

        public Task<ProviderPlanResponse> CreatePlanAsync(ProviderPlanningRequest request, CancellationToken cancellationToken)
        {
            CreatePlanCount++;
            return Task.FromResult(new ProviderPlanResponse(
                1,
                7,
                [new ProviderVideoClip(segmentId, new OutputRange(0, 90), CropPreset.CenterCrop)],
                ImmutableArray<ProviderCaption>.Empty,
                0.01m));
        }
    }

    private sealed class EditPlanRevisionStore(IEditPlanRevisionStore inner) : IEditPlanRevisionStore
    {
        public Task<EditPlanRevision?> FindApprovedAsync(JobId jobId, VariantId variantId, CancellationToken cancellationToken) => inner.FindApprovedAsync(jobId, variantId, cancellationToken);
        public Task<EditPlanRevision?> GetAsync(RevisionId revisionId, CancellationToken cancellationToken) => inner.GetAsync(revisionId, cancellationToken);
        public Task<EditPlanRevision?> FindAsync(JobId jobId, VariantId variantId, string planHash, CancellationToken cancellationToken) => inner.FindAsync(jobId, variantId, planHash, cancellationToken);
        public Task<EditPlanRevision> SaveAsync(EditPlan plan, string planHash, decimal actualCostUsd, CancellationToken cancellationToken) => inner.SaveAsync(plan, planHash, actualCostUsd, cancellationToken);
    }

    private sealed class UnexpectedStageExecutor : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected stage: {job.Progress.Stage}");
    }
}
