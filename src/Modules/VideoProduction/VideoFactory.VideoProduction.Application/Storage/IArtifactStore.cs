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
