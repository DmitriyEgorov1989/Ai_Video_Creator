using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Определяет seam локального хранения неизменяемых файлов Artifact.
/// </summary>
public interface ILocalArtifactStorage
{
    /// <summary>
    /// Публикует содержимое Artifact в каталоге Job.
    /// </summary>
    /// <param name="jobId">Идентификатор Job.</param>
    /// <param name="artifactId">Идентификатор Artifact.</param>
    /// <param name="content">Поток содержимого, который не закрывается storage service.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Описание опубликованного файла или ожидаемый отказ.</returns>
    Task<ArtifactPublishResult> PublishAsync(
        JobId jobId,
        ArtifactId artifactId,
        Stream content,
        CancellationToken cancellationToken);
}
