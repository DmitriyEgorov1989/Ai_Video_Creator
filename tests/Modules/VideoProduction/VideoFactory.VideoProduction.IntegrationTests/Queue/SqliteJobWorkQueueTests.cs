using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.IntegrationTests.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Queue;

public sealed class SqliteJobWorkQueueTests
{
    [Fact]
    public async Task QueuedJobIsClaimedAndMarkedRunning()
    {
        await using TemporarySqliteDatabase database =
            await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded created = await CreateJobAsync(database);
        SqliteJobWorkQueue queue = new(database.ConnectionString);

        JobCreationRecord? claimed = await queue.TryClaimNextAsync(
            "worker-1",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(created.JobId, claimed.JobId);
        Assert.Equal(JobStatus.Running, claimed.Progress.Status);
        Assert.Equal(ProductionStage.Preparation, claimed.Progress.Stage);
        Assert.True(await queue.TryHeartbeatAsync(
            created.JobId,
            "worker-1",
            CancellationToken.None));
    }

    [Fact]
    public async Task SameWorkerCanClaimItsRunningJobForTheNextStage()
    {
        await using TemporarySqliteDatabase database =
            await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded created = await CreateJobAsync(database);
        SqliteJobWorkQueue queue = new(database.ConnectionString);

        Assert.NotNull(await queue.TryClaimNextAsync(
            "worker-1",
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        JobCreationRecord? continued = await queue.TryClaimNextAsync(
            "worker-1",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(continued);
        Assert.Equal(created.JobId, continued.JobId);
    }

    [Fact]
    public async Task StaleRunningJobIsRecoveredWithSameVariantAndStage()
    {
        await using TemporarySqliteDatabase database =
            await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded created = await CreateJobAsync(database);

        SqliteJobStateStore stateStore = new(database.ConnectionString);
        JobProgress running = JobProgress.Create(ProductionMode.Fast).Start();
        Assert.IsType<VideoFactory.VideoProduction.Application.Jobs.State.JobStateTransitionResult.Applied>(
            await stateStore.TryTransitionAsync(
                created.JobId,
                JobStatus.Queued,
                ProductionStage.Preparation,
                running,
                CancellationToken.None));

        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE jobs SET heartbeat_at = $heartbeatAt, lease_owner = $owner WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$heartbeatAt", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        command.Parameters.AddWithValue("$owner", "dead-worker");
        command.Parameters.AddWithValue("$jobId", created.JobId.ToString());
        await command.ExecuteNonQueryAsync();

        SqliteJobWorkQueue queue = new(database.ConnectionString);
        JobCreationRecord? recovered = await queue.TryClaimNextAsync(
            "new-worker",
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(created.JobId, recovered.JobId);
        Assert.Equal(created.VariantId, recovered.VariantId);
        Assert.Equal(ProductionStage.Preparation, recovered.Progress.Stage);
    }

    private static async Task<CreateJobResult.Succeeded> CreateJobAsync(
        TemporarySqliteDatabase database)
    {
        ArtifactId artifactId = ArtifactId.New();
        SqliteArtifactStore artifactStore = new(database.ConnectionString);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(
            new ArtifactRecord(
                artifactId,
                "OriginalVideo",
                "jobs/source/original.mp4",
                "hash",
                10,
                "{}",
                null,
                "test"),
            CancellationToken.None);
        Assert.IsType<ArtifactRegistrationResult.Created>(registration);

        CreateJobUseCase useCase = new(new SqliteJobCreationStore(database.ConnectionString));
        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                artifactId,
                "Сделай динамично",
                new ProductionSettings(null, true, false)));
        return Assert.IsType<CreateJobResult.Succeeded>(result);
    }
}
