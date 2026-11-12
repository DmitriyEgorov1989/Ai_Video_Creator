using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteJobEventStoreTests
{
    [Fact]
    public async Task PersistsJobEventAndRestoresItAfterStoreRestart()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        ArtifactId artifactId = ArtifactId.New();
        await new SqliteArtifactStore(database.ConnectionString).RegisterAsync(
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
        CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
            await new CreateJobUseCase(new SqliteJobCreationStore(database.ConnectionString)).ExecuteAsync(
                new CreateJobCommand(
                    artifactId,
                    "Сделай динамично",
                    new ProductionSettings(null, captionsEnabled: true, musicEnabled: false))));
        JobEventRecord eventRecord = new(
            Guid.NewGuid(),
            created.JobId,
            created.VariantId,
            "JobCreated",
            ProductionStage.Preparation,
            "{\"source\":\"test\"}",
            DateTimeOffset.UtcNow);

        await new SqliteJobEventStore(database.ConnectionString).AppendAsync(
            eventRecord,
            CancellationToken.None);
        IReadOnlyList<JobEventRecord> restored = await new SqliteJobEventStore(database.ConnectionString).GetAsync(
            created.JobId,
            CancellationToken.None);

        Assert.Single(restored);
        Assert.Equal(eventRecord, restored[0]);
    }
}
