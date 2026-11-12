namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Определяет persistence seam для регистрации неизменяемых Artifact.
/// </summary>
public interface IArtifactStore
{
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
