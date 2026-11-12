using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteStageAttemptStoreTests
{
    [Fact]
    public async Task PersistsOriginalErrorCodeMessageAndAttemptNumber()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
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
        CreateJobUseCase useCase = new(new SqliteJobCreationStore(database.ConnectionString));
        CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
            await useCase.ExecuteAsync(
                new CreateJobCommand(
                    artifactId,
                    "Сделай динамично",
                    new ProductionSettings(null, captionsEnabled: true, musicEnabled: false))));
        JobId jobId = created.JobId;
        SqliteStageAttemptStore store = new(database.ConnectionString);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        StageAttemptRecord attempt = new(
            Guid.NewGuid(),
            jobId,
            null,
            ProductionStage.Analysis,
            2,
            StageAttemptStatus.Retryable,
            "WHISPER_EXIT_1",
            "worker exited with code 1",
            startedAt,
            DateTimeOffset.UtcNow);

        await store.AppendAsync(attempt, CancellationToken.None);
        SqliteStageAttemptStore restartedStore = new(database.ConnectionString);
        StageAttemptRecord? restored = await restartedStore.GetLatestAsync(
            jobId,
            ProductionStage.Analysis,
            CancellationToken.None);

        Assert.Equal(attempt, restored);
    }
}
