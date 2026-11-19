using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.VideoProduction.Application.Jobs.Cancellation;

/// <summary>Описывает устойчивый запрос пользовательской отмены Job.</summary>
/// <param name="JobId">Идентификатор Job.</param>
/// <param name="RequestedByUserId">Telegram user ID инициатора отмены.</param>
/// <param name="Reason">Техническая причина отмены.</param>
/// <param name="RequestedAt">Время запроса отмены.</param>
public sealed record JobCancellationRequest(
    JobId JobId,
    long RequestedByUserId,
    string Reason,
    DateTimeOffset RequestedAt);

/// <summary>Результат поиска и сохранения запроса отмены.</summary>
public abstract record JobCancellationResult
{
    private JobCancellationResult() { }

    /// <summary>Отмена успешно запрошена.</summary>
    /// <param name="Job">Активное задание.</param>
    public sealed record Requested(JobCreationRecord Job) : JobCancellationResult;

    /// <summary>Запрос отмены уже был сохранён.</summary>
    /// <param name="Job">Задание, для которого ранее запрошена отмена.</param>
    public sealed record AlreadyRequested(JobCreationRecord Job) : JobCancellationResult;

    /// <summary>Активное задание не найдено.</summary>
    public sealed record NoActiveJob : JobCancellationResult;

    /// <summary>Текущий этап не поддерживает пользовательскую отмену.</summary>
    /// <param name="Stage">Текущий этап.</param>
    public sealed record UnsupportedStage(ProductionStage Stage) : JobCancellationResult;
}

/// <summary>Сохраняет запросы отмены и находит активные задания пользователя.</summary>
public interface IJobCancellationStore
{
    /// <summary>Находит последнее активное задание пользователя.</summary>
    /// <param name="telegramUserId">Telegram user ID.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задание или <see langword="null"/>.</returns>
    Task<JobCreationRecord?> FindActiveAsync(long telegramUserId, CancellationToken cancellationToken);

    /// <summary>Сохраняет запрос отмены один раз.</summary>
    /// <param name="request">Запрос отмены.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если запрос создан впервые.</returns>
    Task<bool> RequestAsync(JobCancellationRequest request, CancellationToken cancellationToken);

    /// <summary>Проверяет наличие запроса отмены.</summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если отмена была запрошена.</returns>
    Task<bool> IsRequestedAsync(JobId jobId, CancellationToken cancellationToken);
}

/// <summary>Передаёт пользовательскую отмену выполняющемуся stage.</summary>
public interface IJobCancellationCoordinator
{
    /// <summary>Регистрирует выполняющееся задание и возвращает его токен.</summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="hostToken">Токен остановки Host.</param>
    /// <returns>Токен только для отмены этого Job пользователем.</returns>
    CancellationToken Register(JobId jobId, CancellationToken hostToken);

    /// <summary>Запрашивает отмену зарегистрированного Job.</summary>
    /// <param name="jobId">Идентификатор Job.</param>
    void Cancel(JobId jobId);

    /// <summary>Удаляет регистрацию после завершения stage.</summary>
    /// <param name="jobId">Идентификатор Job.</param>
    void Unregister(JobId jobId);
}

/// <summary>Потокобезопасная координация отмены одного или нескольких Job.</summary>
public sealed class JobCancellationCoordinator : IJobCancellationCoordinator, IDisposable
{
    private readonly Lock sync = new();
    private readonly Dictionary<JobId, CancellationTokenSource> sources = [];

    /// <inheritdoc />
    public CancellationToken Register(JobId jobId, CancellationToken hostToken)
    {
        lock (sync)
        {
            if (sources.Remove(jobId, out CancellationTokenSource? previous))
            {
                previous.Dispose();
            }

            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            sources[jobId] = source;
            return source.Token;
        }
    }

    /// <inheritdoc />
    public void Cancel(JobId jobId)
    {
        lock (sync)
        {
            if (sources.TryGetValue(jobId, out CancellationTokenSource? source))
            {
                source.Cancel();
            }
        }
    }

    /// <inheritdoc />
    public void Unregister(JobId jobId)
    {
        lock (sync)
        {
            if (sources.Remove(jobId, out CancellationTokenSource? source))
            {
                source.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            foreach (CancellationTokenSource source in sources.Values)
            {
                source.Dispose();
            }

            sources.Clear();
        }
    }
}

/// <summary>Application use case отмены активного задания пользователя.</summary>
public sealed class CancelJobUseCase
{
    private readonly IJobCancellationStore store;
    private readonly IJobCancellationCoordinator coordinator;

    /// <summary>Создаёт use case отмены.</summary>
    /// <param name="store">Хранилище состояния отмены.</param>
    /// <param name="coordinator">Координатор выполняющегося процесса.</param>
    public CancelJobUseCase(IJobCancellationStore store, IJobCancellationCoordinator coordinator)
    {
        this.store = store;
        this.coordinator = coordinator;
    }

    /// <summary>Запрашивает отмену активного Analysis или Rendering.</summary>
    /// <param name="telegramUserId">Telegram user ID.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Типизированный результат отмены.</returns>
    public async Task<JobCancellationResult> ExecuteAsync(
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        JobCreationRecord? job = await store.FindActiveAsync(telegramUserId, cancellationToken);
        if (job is null)
        {
            return new JobCancellationResult.NoActiveJob();
        }

        if (job.Progress.Stage is not (ProductionStage.Analysis or ProductionStage.Rendering))
        {
            return new JobCancellationResult.UnsupportedStage(job.Progress.Stage);
        }

        JobCancellationRequest request = new(
            job.JobId,
            telegramUserId,
            "telegram.cancel-job",
            DateTimeOffset.UtcNow);
        bool created = await store.RequestAsync(request, cancellationToken);
        coordinator.Cancel(job.JobId);
        return created
            ? new JobCancellationResult.Requested(job)
            : new JobCancellationResult.AlreadyRequested(job);
    }
}
