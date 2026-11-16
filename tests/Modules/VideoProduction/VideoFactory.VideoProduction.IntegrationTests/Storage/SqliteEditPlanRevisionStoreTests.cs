using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteEditPlanRevisionStoreTests
{
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
