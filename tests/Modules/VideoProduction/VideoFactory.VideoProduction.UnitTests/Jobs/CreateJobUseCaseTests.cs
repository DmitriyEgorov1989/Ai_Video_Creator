using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Jobs;

public sealed class CreateJobUseCaseTests
{
    [Fact]
    public async Task CreatesJobWithOrderedSources()
    {
        ArtifactId first = ArtifactId.New();
        ArtifactId second = ArtifactId.New();
        FakeJobCreationStore store = new();
        CreateJobUseCase useCase = new(store);

        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                [first, second],
                "Собери единый обзор",
                new ProductionSettings(null, true, false)));

        Assert.IsType<CreateJobResult.Succeeded>(result);
        JobCreationRecord created = Assert.Single(store.Records);
        Assert.Equal([first, second], created.Request.SourceAssetIds.ToArray());
    }

    [Fact]
    public async Task CreatesFastJobWithOneVariant()
    {
        ArtifactId artifactId = ArtifactId.New();
        FakeJobCreationStore store = new();
        CreateJobUseCase useCase = new(store);

        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                artifactId,
                "Убери паузы и добавь субтитры",
                new ProductionSettings(30, captionsEnabled: true, musicEnabled: false)));

        CreateJobResult.Succeeded success = Assert.IsType<CreateJobResult.Succeeded>(result);
        JobCreationRecord created = Assert.Single(store.Records);

        Assert.True(success.WasCreated);
        Assert.Equal(created.JobId, success.JobId);
        Assert.Equal(created.VariantId, success.VariantId);
        Assert.Equal(artifactId, created.Request.SourceAssetId);
        Assert.Equal(ProductionMode.Fast, created.Request.RequestedMode);
        Assert.Equal(JobStatus.Queued, created.Progress.Status);
        Assert.Equal(ProductionStage.Preparation, created.Progress.Stage);
        Assert.Null(created.CurrentApprovedEditPlanRevisionId);
    }

    [Fact]
    public async Task RepeatedArtifactReturnsExistingJobAndDoesNotCreateSecondVariant()
    {
        ArtifactId artifactId = ArtifactId.New();
        FakeJobCreationStore store = new();
        CreateJobUseCase useCase = new(store);
        CreateJobCommand command = new(
            artifactId,
            "Сделай динамично",
            new ProductionSettings(null, captionsEnabled: false, musicEnabled: false));

        CreateJobResult first = await useCase.ExecuteAsync(command);
        CreateJobResult second = await useCase.ExecuteAsync(command);

        CreateJobResult.Succeeded firstSuccess = Assert.IsType<CreateJobResult.Succeeded>(first);
        CreateJobResult.Succeeded secondSuccess = Assert.IsType<CreateJobResult.Succeeded>(second);

        Assert.True(firstSuccess.WasCreated);
        Assert.False(secondSuccess.WasCreated);
        Assert.Equal(firstSuccess.JobId, secondSuccess.JobId);
        Assert.Equal(firstSuccess.VariantId, secondSuccess.VariantId);
        Assert.Single(store.Records);
        Assert.Equal(2, store.AttemptCount);
    }

    [Fact]
    public async Task MissingSourceArtifactIsReturnedAsExpectedError()
    {
        ArtifactId artifactId = ArtifactId.New();
        FakeJobCreationStore store = new()
        {
            Rejection = new CreateJobError(CreateJobErrorCode.SourceArtifactNotFound)
        };
        CreateJobUseCase useCase = new(store);

        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(
                artifactId,
                "Сделай коротко",
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: false)));

        CreateJobResult.Rejected rejected = Assert.IsType<CreateJobResult.Rejected>(result);

        Assert.Equal(CreateJobErrorCode.SourceArtifactNotFound, rejected.Error.Code);
        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData(CreateJobErrorCode.InvalidSourceArtifactId)]
    [InlineData(CreateJobErrorCode.EmptyComment)]
    public async Task InvalidInputIsReturnedWithoutCallingStore(CreateJobErrorCode expectedCode)
    {
        FakeJobCreationStore store = new();
        CreateJobUseCase useCase = new(store);
        CreateJobCommand command = expectedCode switch
        {
            CreateJobErrorCode.InvalidSourceArtifactId => new(
                default(ArtifactId),
                "Комментарий",
                new ProductionSettings(null, captionsEnabled: false, musicEnabled: false)),
            CreateJobErrorCode.EmptyComment => new(
                ArtifactId.New(),
                "  ",
                new ProductionSettings(null, captionsEnabled: false, musicEnabled: false)),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedCode))
        };

        CreateJobResult result = await useCase.ExecuteAsync(command);

        CreateJobResult.Rejected rejected = Assert.IsType<CreateJobResult.Rejected>(result);

        Assert.Equal(expectedCode, rejected.Error.Code);
        Assert.Equal(0, store.AttemptCount);
    }

    [Fact]
    public async Task MissingSettingsIsReturnedWithoutCallingStore()
    {
        FakeJobCreationStore store = new();
        CreateJobUseCase useCase = new(store);

        CreateJobResult result = await useCase.ExecuteAsync(
            new CreateJobCommand(ArtifactId.New(), "Сделай динамично", null));

        CreateJobResult.Rejected rejected = Assert.IsType<CreateJobResult.Rejected>(result);

        Assert.Equal(CreateJobErrorCode.MissingSettings, rejected.Error.Code);
        Assert.Equal(0, store.AttemptCount);
    }

    private sealed class FakeJobCreationStore : IJobCreationStore
    {
        private readonly Dictionary<ArtifactId, JobCreationRecord> records = [];

        public CreateJobError? Rejection { get; init; }

        public int AttemptCount { get; private set; }

        public IReadOnlyCollection<JobCreationRecord> Records => records.Values;

        public Task<JobCreationStoreResult> CreateAsync(
            JobCreationRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;

            if (Rejection is not null)
            {
                return Task.FromResult<JobCreationStoreResult>(
                    new JobCreationStoreResult.Rejected(Rejection));
            }

            if (records.TryGetValue(record.Request.SourceAssetId, out JobCreationRecord? existing))
            {
                return Task.FromResult<JobCreationStoreResult>(
                    new JobCreationStoreResult.Existing(existing));
            }

            records.Add(record.Request.SourceAssetId, record);
            return Task.FromResult<JobCreationStoreResult>(
                new JobCreationStoreResult.Created(record));
        }
    }
}
