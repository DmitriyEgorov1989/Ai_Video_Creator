using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Application.Jobs.Queue;

namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Создаёт один Job с одним Variant в режиме Fast.
/// </summary>
public sealed class CreateJobUseCase
{
    private readonly IJobCreationStore store;
    private readonly IJobSignal? signal;

    /// <summary>
    /// Создаёт сценарий создания задания.
    /// </summary>
    /// <param name="store">Persistence seam атомарного создания задания.</param>
    public CreateJobUseCase(IJobCreationStore store, IJobSignal? signal = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.signal = signal;
    }

    /// <summary>
    /// Создаёт или идемпотентно находит Job по исходному ArtifactId.
    /// </summary>
    /// <param name="command">Входные данные сценария.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Успешный результат с Job и Variant либо ожидаемый типизированный отказ.</returns>
    public async Task<CreateJobResult> ExecuteAsync(
        CreateJobCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.SourceArtifactId.IsEmpty)
        {
            return new CreateJobResult.Rejected(
                new CreateJobError(CreateJobErrorCode.InvalidSourceArtifactId));
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return new CreateJobResult.Rejected(
                new CreateJobError(CreateJobErrorCode.EmptyComment));
        }

        if (command.Settings is null)
        {
            return new CreateJobResult.Rejected(
                new CreateJobError(CreateJobErrorCode.MissingSettings));
        }

        JobRequest request = new(
            command.SourceArtifactId,
            command.Comment,
            ProductionMode.Fast,
            command.Settings);

        JobCreationRecord record = new(
            JobId.New(),
            VariantId.New(),
            request,
            JobProgress.Create(ProductionMode.Fast),
            null)
        {
            TelegramUserId = command.TelegramUserId
        };

        JobCreationStoreResult stored = await store.CreateAsync(record, cancellationToken);

        if (stored is JobCreationStoreResult.Created newlyCreated && signal is not null)
        {
            await signal.PublishAsync(newlyCreated.Record.JobId, cancellationToken);
        }

        return stored switch
        {
            JobCreationStoreResult.Created created =>
                ToSuccess(created.Record, wasCreated: true),
            JobCreationStoreResult.Existing existing =>
                ToSuccess(existing.Record, wasCreated: false),
            JobCreationStoreResult.Rejected rejected =>
                new CreateJobResult.Rejected(rejected.Error),
            _ => throw new InvalidOperationException("Unknown job creation store result.")
        };
    }

    private static CreateJobResult.Succeeded ToSuccess(
        JobCreationRecord record,
        bool wasCreated) =>
        new(record.JobId, record.VariantId, wasCreated);
}
