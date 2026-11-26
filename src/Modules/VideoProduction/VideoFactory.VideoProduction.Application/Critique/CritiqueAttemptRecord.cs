using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Представляет восстанавливаемую запись одной попытки критики готового рендера.</summary>
public sealed record CritiqueAttemptRecord
{
    /// <summary>Создаёт запись попытки критики.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="variantId">Идентификатор проверяемого варианта.</param>
    /// <param name="sourceRevisionId">Идентификатор ревизии плана, по которой создан рендер.</param>
    /// <param name="renderArtifactId">Идентификатор проверяемого артефакта рендера.</param>
    /// <param name="correctionRevisionId">Идентификатор созданной коррекции либо <see langword="null"/>, если коррекция не создана.</param>
    /// <param name="remountNumber">Номер повторного монтажа; ноль означает первую проверку.</param>
    /// <param name="estimatedCostUsd">Оценка стоимости попытки в долларах США.</param>
    /// <param name="actualCostUsd">Фактическая стоимость в долларах США либо <see langword="null"/>, пока она неизвестна.</param>
    /// <param name="status">Стабильный статус попытки; не может быть <see langword="null"/>.</param>
    /// <param name="errorCode">Машинный код ошибки либо <see langword="null"/>, если ошибки нет.</param>
    /// <param name="errorMessage">Диагностическое сообщение либо <see langword="null"/>, если ошибки нет.</param>
    /// <param name="createdAt">Момент начала попытки.</param>
    /// <param name="completedAt">Момент завершения либо <see langword="null"/>, пока попытка не завершена.</param>
    public CritiqueAttemptRecord(
        JobId jobId,
        VariantId variantId,
        RevisionId sourceRevisionId,
        ArtifactId renderArtifactId,
        RevisionId? correctionRevisionId,
        int remountNumber,
        decimal estimatedCostUsd,
        decimal? actualCostUsd,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt)
    {
        JobId = jobId;
        VariantId = variantId;
        SourceRevisionId = sourceRevisionId;
        RenderArtifactId = renderArtifactId;
        CorrectionRevisionId = correctionRevisionId;
        RemountNumber = remountNumber;
        EstimatedCostUsd = estimatedCostUsd;
        ActualCostUsd = actualCostUsd;
        Status = status;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        CompletedAt = completedAt;
    }

    /// <summary>Получает идентификатор задания.</summary>
    public JobId JobId { get; }

    /// <summary>Получает идентификатор проверяемого варианта.</summary>
    public VariantId VariantId { get; }

    /// <summary>Получает идентификатор ревизии плана, по которой создан рендер.</summary>
    public RevisionId SourceRevisionId { get; }

    /// <summary>Получает идентификатор проверяемого артефакта рендера.</summary>
    public ArtifactId RenderArtifactId { get; }

    /// <summary>Получает идентификатор созданной коррекции либо <see langword="null"/>, если коррекция не создана.</summary>
    public RevisionId? CorrectionRevisionId { get; }

    /// <summary>Получает номер повторного монтажа; ноль означает первую проверку.</summary>
    public int RemountNumber { get; }

    /// <summary>Получает оценку стоимости попытки в долларах США.</summary>
    public decimal EstimatedCostUsd { get; }

    /// <summary>Получает фактическую стоимость в долларах США либо <see langword="null"/>, пока она неизвестна.</summary>
    public decimal? ActualCostUsd { get; }

    /// <summary>Получает стабильный статус попытки.</summary>
    public string Status { get; }

    /// <summary>Получает машинный код ошибки либо <see langword="null"/>, если ошибки нет.</summary>
    public string? ErrorCode { get; }

    /// <summary>Получает диагностическое сообщение либо <see langword="null"/>, если ошибки нет.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Получает момент начала попытки.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Получает момент завершения либо <see langword="null"/>, пока попытка не завершена.</summary>
    public DateTimeOffset? CompletedAt { get; }
}

/// <summary>Определяет хранилище восстанавливаемого аудита попыток критики.</summary>
public interface ICritiqueAttemptStore
{
    /// <summary>Идемпотентно сохраняет начальное или завершённое состояние попытки.</summary>
    /// <param name="attempt">Сохраняемая запись; не может быть <see langword="null"/>.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Асинхронная операция сохранения.</returns>
    Task SaveAsync(CritiqueAttemptRecord attempt, CancellationToken cancellationToken);

    /// <summary>Находит попытку по идентичности проверяемого рендера.</summary>
    /// <param name="variantId">Идентификатор варианта.</param>
    /// <param name="sourceRevisionId">Идентификатор исходной ревизии плана.</param>
    /// <param name="renderArtifactId">Идентификатор артефакта рендера.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Найденная запись либо <see langword="null"/>, если попытка отсутствует.</returns>
    Task<CritiqueAttemptRecord?> GetAsync(
        VariantId variantId,
        RevisionId sourceRevisionId,
        ArtifactId renderArtifactId,
        CancellationToken cancellationToken);

    /// <summary>Возвращает всю историю critic/remount для варианта по номеру перемонтажа.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="variantId">Идентификатор варианта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Упорядоченные попытки; пустой список означает отсутствие критики.</returns>
    Task<IReadOnlyList<CritiqueAttemptRecord>> ListAsync(
        JobId jobId,
        VariantId variantId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CritiqueAttemptRecord>>([]);
}
