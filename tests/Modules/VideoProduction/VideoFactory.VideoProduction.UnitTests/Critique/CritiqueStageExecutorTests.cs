using System.Text.Json;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class CritiqueStageExecutorTests
{
    [Fact]
    public async Task RenderWithoutObjectiveDefectsAdvancesToVerification()
    {
        string root = Path.Combine(Path.GetTempPath(), $"critic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            EditPlanAudit audit = CreateAudit();
            EditPlanRevision revision = new(
                fixture.Plan.RevisionId,
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                "plan-hash",
                fixture.Plan,
                0m,
                null,
                audit);
            string relativePath = "data/render.mp4";
            string outputPath = Path.Combine(root, "data", "render.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, [1, 2, 3]);
            ArtifactRecord render = new(
                ArtifactId.New(),
                "render-output",
                relativePath,
                "hash",
                3,
                JsonSerializer.Serialize(new
                {
                    variantId = fixture.Plan.VariantId,
                    planRevisionId = fixture.Plan.RevisionId.ToString(),
                    planHash = revision.PlanHash
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                null,
                "test",
                fixture.Plan.JobId,
                fixture.Plan.VariantId);
            CritiqueStageExecutor executor = new(
                new FakePlanStore(revision),
                new FakeArtifactStore(render),
                new FakeObserver(),
                root,
                maxRemounts: 2);
            JobCreationRecord job = new(
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                new JobRequest(
                    fixture.Analysis.OriginalAssetId,
                    "comment",
                    ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(
                    JobStatus.Running,
                    ProductionStage.Critique,
                    ProductionMode.Fast,
                    ProductionMode.Fast,
                    null),
                fixture.Plan.RevisionId);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal(StageCompletionAction.Advance, result.CompletionAction);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepairableBlackIntervalCreatesRevisionAndRequestsRemount()
    {
        string root = Path.Combine(Path.GetTempPath(), $"critic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            EditPlanAudit audit = CreateAudit();
            EditPlanRevision revision = new(
                fixture.Plan.RevisionId,
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                "plan-hash",
                fixture.Plan,
                0m,
                null,
                audit);
            string outputPath = Path.Combine(root, "data", "render.mp4");
            await File.WriteAllBytesAsync(outputPath, [1, 2, 3]);
            ArtifactRecord render = CreateRender(revision, fixture.Plan.JobId, "data/render.mp4");
            FakePlanStore plans = new(revision);
            FakeCritiqueAttemptStore attempts = new();
            FakeArtifactStore artifacts = new(render);
            MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(
                fixture.Analysis with
                {
                    AllowedSegments =
                    [
                        .. fixture.Analysis.AllowedSegments.Select(
                            segment => segment with { IsApproved = true })
                    ]
                });
            CritiqueStageExecutor executor = new(
                plans,
                artifacts,
                new BlackIntervalObserver(),
                root,
                maxRemounts: 2,
                new FakeAnalysisSource(analysis),
                attempts);

            StageExecutionResult result = await executor.ExecuteAsync(
                CreateJob(fixture, revision.RevisionId),
                CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal(StageCompletionAction.Remount, result.CompletionAction);
            Assert.NotNull(plans.Saved);
            Assert.NotEqual(revision.RevisionId, plans.Saved!.RevisionId);
            Assert.Equal(analysis.AllowedSegments[1].Id, plans.Saved.Video.Clips[0].SegmentId);
            Assert.Same(audit, plans.SavedAudit);
            Assert.Equal(
                EditPlanRevisionIdentity.CalculateHash(plans.Saved, revision.Presentation, audit),
                plans.SavedRevision?.PlanHash);
            ArtifactRecord report = Assert.Single(artifacts.Registered, artifact =>
                artifact.ArtifactType == "objective-critique-report"
                && artifact.ParentArtifactId == render.ArtifactId);
            using (JsonDocument reportDocument = JsonDocument.Parse(report.MetadataJson!))
            {
                Assert.Equal(2, reportDocument.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(revision.PlanHash, reportDocument.RootElement.GetProperty("sourcePlanHash").GetString());
                Assert.Equal(0, reportDocument.RootElement.GetProperty("remountNumber").GetInt32());
                Assert.Equal(plans.Saved.RevisionId.ToString(), reportDocument.RootElement.GetProperty("correctionRevisionId").GetString());
            }

            attempts.SimulateInterruptionBeforeAttemptCompletion();

            StageExecutionResult recovered = await executor.ExecuteAsync(
                CreateJob(fixture, plans.Saved.RevisionId),
                CancellationToken.None);

            Assert.Equal(StageCompletionAction.Remount, recovered.CompletionAction);
            Assert.Equal(1, plans.SaveCalls);
            Assert.Equal("remount", attempts.Attempt?.Status);
            Assert.NotNull(attempts.Attempt?.CompletedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JobCreationRecord CreateJob(ValidPlanFixture fixture, RevisionId revisionId) => new(
        fixture.Plan.JobId,
        fixture.Plan.VariantId,
        new JobRequest(
            fixture.Analysis.OriginalAssetId,
            "comment",
            ProductionMode.Fast,
            new ProductionSettings(null, true, false)),
        JobProgress.Restore(
            JobStatus.Running,
            ProductionStage.Critique,
            ProductionMode.Fast,
            ProductionMode.Fast,
            null),
        revisionId);

    private static EditPlanAudit CreateAudit() => new(
        1,
        [
            .. new[]
            {
                "multi-clip-director", "semantic-story", "hook-builder", "smart-cut",
                "best-shot", "pacing-director", "speech-tightening", "caption-director",
                "visual-director", "smart-reframe", "audio-director"
            }.Select(id => new EditingSkillAuditEntry(
                id,
                "1.0.0",
                EditingDecisionStatus.Applied,
                [new EditingDecisionReason("decision.recorded", "Decision recorded.")]))
        ]);

    private static ArtifactRecord CreateRender(EditPlanRevision revision, JobId jobId, string relativePath) => new(
        ArtifactId.New(),
        "render-output",
        relativePath,
        "hash",
        3,
        JsonSerializer.Serialize(new
        {
            variantId = revision.VariantId,
            planRevisionId = revision.RevisionId.ToString(),
            planHash = revision.PlanHash
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        null,
        "test",
        jobId,
        revision.VariantId);

    private sealed class FakeObserver : IRenderedMediaObserver
    {
        public Task<RenderedMediaObservationResult> ObserveAsync(
            RenderedMediaObservationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<RenderedMediaObservationResult>(
                new RenderedMediaObservationResult.Succeeded(new RenderedMediaObservation([])));
    }

    private sealed class BlackIntervalObserver : IRenderedMediaObserver
    {
        public Task<RenderedMediaObservationResult> ObserveAsync(
            RenderedMediaObservationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<RenderedMediaObservationResult>(
                new RenderedMediaObservationResult.Succeeded(
                    new RenderedMediaObservation([new OutputRange(30, 60)])));
    }

    private sealed class FakeAnalysisSource(MultiSourceAnalysisResult analysis) : ICritiqueAnalysisSource
    {
        public Task<MultiSourceAnalysisResult?> ReadAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<MultiSourceAnalysisResult?>(analysis);
    }

    private sealed class FakePlanStore(EditPlanRevision revision) : IEditPlanRevisionStore
    {
        public EditPlan? Saved { get; private set; }
        public EditPlanAudit? SavedAudit { get; private set; }
        public EditPlanRevision? SavedRevision => savedRevision;
        public int SaveCalls { get; private set; }
        private EditPlanRevision? savedRevision;

        public Task<EditPlanRevision?> FindApprovedAsync(JobId jobId, VariantId variantId, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(revision);

        public Task<EditPlanRevision?> GetAsync(RevisionId revisionId, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(savedRevision?.RevisionId == revisionId ? savedRevision : revision);

        public Task<EditPlanRevision?> FindAsync(JobId jobId, VariantId variantId, string planHash, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(null);

        public Task<EditPlanRevision> SaveAsync(EditPlan plan, string planHash, decimal actualCostUsd, CancellationToken cancellationToken) =>
            SaveAsync(plan, planHash, actualCostUsd, null, null, cancellationToken);

        public Task<EditPlanRevision> SaveAsync(EditPlan plan, string planHash, decimal actualCostUsd, ReelsPresentationPlan? presentation, CancellationToken cancellationToken)
            => SaveAsync(plan, planHash, actualCostUsd, presentation, null, cancellationToken);

        public Task<EditPlanRevision> SaveAsync(EditPlan plan, string planHash, decimal actualCostUsd, ReelsPresentationPlan? presentation, EditPlanAudit? audit, CancellationToken cancellationToken)
        {
            Saved = plan;
            SavedAudit = audit;
            SaveCalls++;
            savedRevision = new EditPlanRevision(
                plan.RevisionId,
                plan.JobId,
                plan.VariantId,
                planHash,
                plan,
                actualCostUsd,
                presentation,
                audit);
            return Task.FromResult(savedRevision);
        }
    }

    private sealed class FakeCritiqueAttemptStore : ICritiqueAttemptStore
    {
        private CritiqueAttemptRecord? attempt;
        public CritiqueAttemptRecord? Attempt => attempt;

        public void SimulateInterruptionBeforeAttemptCompletion()
        {
            Assert.NotNull(attempt);
            attempt = new CritiqueAttemptRecord(
                attempt.JobId,
                attempt.VariantId,
                attempt.SourceRevisionId,
                attempt.RenderArtifactId,
                null,
                attempt.RemountNumber,
                attempt.EstimatedCostUsd,
                null,
                "running",
                null,
                null,
                attempt.CreatedAt,
                null);
        }

        public Task SaveAsync(CritiqueAttemptRecord value, CancellationToken cancellationToken)
        {
            attempt = value;
            return Task.CompletedTask;
        }

        public Task<CritiqueAttemptRecord?> GetAsync(
            VariantId variantId,
            RevisionId sourceRevisionId,
            ArtifactId renderArtifactId,
            CancellationToken cancellationToken) =>
            Task.FromResult(attempt is not null
                && attempt.VariantId == variantId
                && attempt.SourceRevisionId == sourceRevisionId
                && attempt.RenderArtifactId == renderArtifactId
                    ? attempt
                    : null);
    }

    private sealed class FakeArtifactStore(ArtifactRecord render) : IArtifactStore
    {
        public List<ArtifactRecord> Registered { get; } = [];

        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRecord?>(null);

        public Task<ArtifactRecord?> FindLatestAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRecord?>(artifactType == "render-output"
                ? render
                : Registered.LastOrDefault(artifact => artifact.ArtifactType == artifactType));

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>(artifactType == "render-output"
                ? [render]
                : Registered.Where(artifact => artifact.ArtifactType == artifactType).ToArray());

        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken cancellationToken)
        {
            Registered.Add(record);
            return Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
        }
    }
}
