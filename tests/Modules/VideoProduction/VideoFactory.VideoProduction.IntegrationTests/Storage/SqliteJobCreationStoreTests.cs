using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteJobCreationStoreTests
{
    [Fact]
    public async Task ArtifactRegistrationRejectsPathTraversal()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteArtifactStore store = new(database.ConnectionString);

        ArtifactRegistrationResult result = await store.RegisterAsync(
            new ArtifactRecord(
                ArtifactId.New(),
                "OriginalVideo",
                "jobs/../outside.mp4",
                null,
                null,
                null,
                null,
                null),
            CancellationToken.None);

        ArtifactRegistrationResult.Rejected rejected = Assert.IsType<ArtifactRegistrationResult.Rejected>(result);
        Assert.Contains("relative path", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentStateTransitionsAllowOnlyOneExpectedStateUpdate()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded created = await CreateJobAsync(database);
        SqliteJobStateStore store = new(database.ConnectionString);
        JobProgress next = JobProgress.Create(ProductionMode.Fast).Start();

        Task<JobStateTransitionResult> first = store.TryTransitionAsync(
            created.JobId,
            JobStatus.Queued,
            ProductionStage.Preparation,
            next,
            CancellationToken.None);
        Task<JobStateTransitionResult> second = store.TryTransitionAsync(
            created.JobId,
            JobStatus.Queued,
            ProductionStage.Preparation,
            next,
            CancellationToken.None);

        JobStateTransitionResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results.OfType<JobStateTransitionResult.Applied>());
        JobStateTransitionResult.Conflict conflict =
            Assert.Single(results.OfType<JobStateTransitionResult.Conflict>());
        Assert.Equal(JobStatus.Running, conflict.Current.Progress.Status);
        Assert.Equal(ProductionStage.Preparation, conflict.Current.Progress.Stage);
    }

    [Fact]
    public async Task StateIsRestoredByNewStoreAfterTransition()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        CreateJobResult.Succeeded created = await CreateJobAsync(database);
        SqliteJobStateStore firstStore = new(database.ConnectionString);
        JobProgress next = JobProgress.Create(ProductionMode.Fast).Start();

        JobStateTransitionResult transition = await firstStore.TryTransitionAsync(
            created.JobId,
            JobStatus.Queued,
            ProductionStage.Preparation,
            next,
            CancellationToken.None);
        Assert.IsType<JobStateTransitionResult.Applied>(transition);

        SqliteJobStateStore restartedStore = new(database.ConnectionString);
        JobCreationRecord? restored = await restartedStore.GetAsync(
            created.JobId,
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(JobStatus.Running, restored.Progress.Status);
        Assert.Equal(ProductionStage.Preparation, restored.Progress.Stage);
        Assert.Equal(ProductionMode.Fast, restored.Request.RequestedMode);
    }

    [Fact]
    public async Task RegisteredArtifactCreatesOneVariantAndRepeatedRequestReturnsIt()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        ArtifactId artifactId = ArtifactId.New();
        SqliteArtifactStore artifactStore = new(database.ConnectionString);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(
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
        Assert.IsType<ArtifactRegistrationResult.Created>(registration);

        CreateJobUseCase useCase = new(new SqliteJobCreationStore(database.ConnectionString));
        CreateJobCommand command = new(
            artifactId,
            "Сделай динамично",
            new ProductionSettings(null, captionsEnabled: true, musicEnabled: false));

        CreateJobResult first = await useCase.ExecuteAsync(command);
        CreateJobResult second = await useCase.ExecuteAsync(command);

        CreateJobResult.Succeeded firstSuccess = Assert.IsType<CreateJobResult.Succeeded>(first);
        CreateJobResult.Succeeded secondSuccess = Assert.IsType<CreateJobResult.Succeeded>(second);
        Assert.True(firstSuccess.WasCreated);
        Assert.False(secondSuccess.WasCreated);
        Assert.Equal(firstSuccess.JobId, secondSuccess.JobId);
        Assert.Equal(firstSuccess.VariantId, secondSuccess.VariantId);
    }

    [Fact]
    public async Task MissingSourceArtifactIsReturnedAsExpectedError()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteJobCreationStore store = new(database.ConnectionString);
        CreateJobUseCase useCase = new(store);

        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                ArtifactId.New(),
                "Сделай динамично",
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: false)));

        CreateJobResult.Rejected rejected = Assert.IsType<CreateJobResult.Rejected>(result);
        Assert.Equal(CreateJobErrorCode.SourceArtifactNotFound, rejected.Error.Code);
    }

    private static async Task<CreateJobResult.Succeeded> CreateJobAsync(TemporarySqliteDatabase database)
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

        CreateJobUseCase useCase = new(new SqliteJobCreationStore(database.ConnectionString));
        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                artifactId,
                "Сделай динамично",
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: false)));

        return Assert.IsType<CreateJobResult.Succeeded>(result);
    }
}

internal sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private TemporarySqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        ConnectionString = $"Data Source={databasePath};Cache=Shared;Pooling=False";
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public static async Task<TemporarySqliteDatabase> CreateAsync()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"video-factory-{Guid.NewGuid():N}.db");
        TemporarySqliteDatabase database = new(databasePath);
        SqliteMigrationRunner runner = new(database.ConnectionString);
        await runner.InitializeAsync(CancellationToken.None);
        return database;
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(DatabasePath);
        File.Delete($"{DatabasePath}-wal");
        File.Delete($"{DatabasePath}-shm");
        return ValueTask.CompletedTask;
    }
}
