using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Application.Variants;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteEditPlanRevisionStoreTests
{
    [Fact]
    public async Task ReelsPresentationIsRestoredWithApprovedPlanWithoutMigration()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded job = await CreateJobAsync(database);
        EditPlan plan = CreatePlan(job);
        ReelsStyleConfiguration style = new ReelsProfileCatalog().Resolve(
            ReelsStyle.ExpertClean,
            new AnalysisResult(
                1,
                plan.AnalysisRevisionId,
                plan.Video.Clips[0].Mapping.OriginalAssetId,
                plan.Video.Clips[0].Mapping.MasterAssetId,
                [], [], [], []));
        ReelsPresentationPlan presentation = new(style, null, null, null, null);
        SqliteEditPlanRevisionStore store = new(database.ConnectionString);

        await store.SaveAsync(plan, "presentation-hash", 0m, presentation, CancellationToken.None);
        EditPlanRevision? restored = await store.GetAsync(plan.RevisionId, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(ReelsStyle.ExpertClean, restored.Presentation?.Style.ResolvedStyle);
    }

    [Fact]
    public async Task SamePlanHashIsRestoredWithoutCreatingSecondRevision()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded job = await CreateJobAsync(database);
        EditPlan plan = CreatePlan(job);
        SqliteEditPlanRevisionStore store = new(database.ConnectionString);

        EditPlanRevision first = await store.SaveAsync(
            plan,
            "plan-hash",
            0.03m,
            CancellationToken.None);
        EditPlanRevision second = await store.SaveAsync(
            plan,
            "plan-hash",
            0.03m,
            CancellationToken.None);

        Assert.Equal(first.RevisionId, second.RevisionId);
        Assert.Equal(first.Plan.JobId, second.Plan.JobId);
        Assert.Equal(first.Plan.VariantId, second.Plan.VariantId);
        Assert.Equal(first.Plan.RevisionId, second.Plan.RevisionId);
        Assert.Equal(first.Plan.Output, second.Plan.Output);
        Assert.Equal(first.Plan.Video.Clips.Length, second.Plan.Video.Clips.Length);
        Assert.Equal(first.Plan.Video.Clips[0].Mapping, second.Plan.Video.Clips[0].Mapping);
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(0.03m, second.ActualCostUsd);

        EditPlanRevision? approved = await store.FindApprovedAsync(
            job.JobId,
            job.VariantId,
            CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(first.RevisionId, approved.RevisionId);

        await new SqlitePlanningAttemptStore(database.ConnectionString).SaveAsync(
            new PlanningAttemptRecord(
                Guid.NewGuid(),
                job.JobId,
                job.VariantId,
                1,
                0,
                0.04m,
                0.03m,
                "invalid",
                "planning.plan_validation_failed",
                "Invalid plan.",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand attemptCommand = connection.CreateCommand();
        attemptCommand.CommandText = "SELECT status, actual_cost_usd FROM planning_attempts WHERE job_id = $jobId;";
        attemptCommand.Parameters.AddWithValue("$jobId", job.JobId.ToString());
        await using SqliteDataReader attemptReader = await attemptCommand.ExecuteReaderAsync();
        Assert.True(await attemptReader.ReadAsync());
        Assert.Equal("invalid", attemptReader.GetString(0));
        Assert.Equal("0.03", attemptReader.GetString(1));
    }

    [Fact]
    public async Task ChildVariantFindsParentsApprovedRevision()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded job = await CreateJobAsync(database);
        EditPlan plan = CreatePlan(job);
        SqliteEditPlanRevisionStore store = new(database.ConnectionString);
        EditPlanRevision parent = await store.SaveAsync(plan, "parent-hash", 0m, CancellationToken.None);
        await using (SqliteConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand complete = connection.CreateCommand();
            complete.CommandText = "UPDATE jobs SET status = $status, stage = $stage, telegram_user_id = 1 WHERE job_id = $jobId;";
            complete.Parameters.AddWithValue("$status", (int)JobStatus.Completed);
            complete.Parameters.AddWithValue("$stage", (int)ProductionStage.Delivery);
            complete.Parameters.AddWithValue("$jobId", job.JobId.ToString());
            await complete.ExecuteNonQueryAsync();
        }
        RequestVariantChangeResult.Succeeded child = Assert.IsType<RequestVariantChangeResult.Succeeded>(
            await new RequestVariantChangeUseCase(new SqliteVariantChangeStore(database.ConnectionString))
                .ExecuteAsync(
                    new RequestVariantChangeCommand(job.JobId, job.VariantId, 1, "Сделай иначе", "parent-read"),
                    CancellationToken.None));

        EditPlanRevision? restored = await store.FindParentApprovedAsync(
            job.JobId,
            child.VariantId,
            CancellationToken.None);

        Assert.Equal(parent.RevisionId, restored?.RevisionId);
    }

    [Fact]
    public async Task ArtifactLookupIsIsolatedByVariant()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded job = await CreateJobAsync(database);
        VariantId otherVariant = VariantId.New();
        ArtifactRecord first = new(
            ArtifactId.New(), "render-output", "jobs/first.mp4", "first", 1, null, null, "test",
            job.JobId, job.VariantId);
        ArtifactRecord second = new(
            ArtifactId.New(), "render-output", "jobs/second.mp4", "second", 2, null, null, "test",
            job.JobId, otherVariant);
        SqliteArtifactStore store = new(database.ConnectionString);
        await store.RegisterAsync(first, CancellationToken.None);
        await store.RegisterAsync(second, CancellationToken.None);

        ArtifactRecord? restored = await store.FindLatestAsync(
            job.JobId,
            job.VariantId,
            "render-output",
            CancellationToken.None);

        Assert.Equal(first.ArtifactId, restored?.ArtifactId);
        Assert.Equal(job.VariantId, restored?.VariantId);
    }

    private static async Task<CreateJobResult.Succeeded> CreateJobAsync(
        TemporarySqliteDatabase database)
    {
        ArtifactId artifactId = ArtifactId.New();
        SqliteArtifactStore artifactStore = new(database.ConnectionString);
        await artifactStore.RegisterAsync(
            new ArtifactRecord(
                artifactId,
                "OriginalVideo",
                "jobs/source/original.mp4",
                "abc123",
                12_345,
                "{}",
                null,
                "test"),
            CancellationToken.None);

        CreateJobResult result = await new CreateJobUseCase(
            new SqliteJobCreationStore(database.ConnectionString)).ExecuteAsync(
                new CreateJobCommand(
                    artifactId,
                    "Сделай динамично",
                    new ProductionSettings(null, true, false)));
        return Assert.IsType<CreateJobResult.Succeeded>(result);
    }

    private static EditPlan CreatePlan(CreateJobResult.Succeeded job)
    {
        ArtifactId original = ArtifactId.New();
        ArtifactId master = ArtifactId.New();
        SegmentId segment = SegmentId.New();
        RevisionId analysisRevision = RevisionId.New();
        SourceMapping mapping = new(
            original,
            new OriginalRange(0, 3_000_000),
            master,
            new MasterRange(0, 3_000_000));
        OutputRange output = new(0, 90);

        return new EditPlan(
            1,
            job.JobId,
            job.VariantId,
            RevisionId.New(),
            analysisRevision,
            42,
            new OutputProfile(1080, 1920, new FrameRate(30, 1), 90),
            TemplateId.Dynamic,
            new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null),
            new VideoTrack([new VideoClip(segment, mapping, output, CropPreset.CenterCrop)]),
            new AudioTrack(
                [new SourceAudioClip(segment, output, 1m)],
                ImmutableArray<MusicClip>.Empty),
            new CaptionTrack(ImmutableArray<Caption>.Empty),
            new OverlayTrack(ImmutableArray<Overlay>.Empty));
    }
}
