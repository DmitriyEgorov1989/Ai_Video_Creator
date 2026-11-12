namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Представляет результат атомарной публикации файла Artifact.
/// </summary>
public abstract record ArtifactPublishResult
{
    /// <summary>
    /// Файл опубликован впервые или уже существует с тем же содержимым.
    /// </summary>
    /// <param name="File">Описание опубликованного файла.</param>
    /// <param name="WasPublished">Признак публикации файла в текущем вызове.</param>
    public sealed record Succeeded(StoredArtifactFile File, bool WasPublished) : ArtifactPublishResult;

    /// <summary>
    /// Публикация отклонена из-за ожидаемой ошибки.
    /// </summary>
    /// <param name="Message">Понятное описание причины отказа.</param>
    public sealed record Rejected(string Message) : ArtifactPublishResult;
}
