using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Определяет persistence seam для регистрации неизменяемых Artifact.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Находит описание Artifact по идентификатору.</summary>
    /// <param name="artifactId">Идентификатор Artifact.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Описание Artifact или <see langword="null"/>.</returns>
    Task<ArtifactRecord?> GetAsync(
        ArtifactId artifactId,
        CancellationToken cancellationToken);

    /// <summary>Находит последний артефакт заданного типа для Job.</summary>
    Task<ArtifactRecord?> FindLatestAsync(
        JobId jobId,
        string artifactType,
        CancellationToken cancellationToken);

    /// <summary>Находит последний артефакт заданного типа для Variant.</summary>
    async Task<ArtifactRecord?> FindLatestAsync(
        JobId jobId,
        VariantId variantId,
        string artifactType,
        CancellationToken cancellationToken) =>
        (await ListAsync(jobId, artifactType, cancellationToken))
            .LastOrDefault(record => record.VariantId == variantId);

    /// <summary>Возвращает все артефакты заданного типа для Job в порядке регистрации.</summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="artifactType">Непустой тип артефакта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Снимок зарегистрированных артефактов; никогда не <see langword="null"/>.</returns>
    Task<IReadOnlyList<ArtifactRecord>> ListAsync(
        JobId jobId,
        string artifactType,
        CancellationToken cancellationToken);

    /// <summary>Возвращает артефакты заданного типа для Variant в порядке регистрации.</summary>
    async Task<IReadOnlyList<ArtifactRecord>> ListAsync(
        JobId jobId,
        VariantId variantId,
        string artifactType,
        CancellationToken cancellationToken) =>
        (await ListAsync(jobId, artifactType, cancellationToken))
            .Where(record => record.VariantId == variantId)
            .ToArray();

    /// <summary>
    /// Регистрирует описание Artifact или возвращает уже существующее описание.
    /// </summary>
    /// <param name="record">Описание артефакта.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат регистрации.</returns>
    Task<ArtifactRegistrationResult> RegisterAsync(
        ArtifactRecord record,
        CancellationToken cancellationToken);
}
