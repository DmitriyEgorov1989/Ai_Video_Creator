using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Domain.Jobs;

/// <summary>
/// Представляет проверенный запрос на создание вариантов видео из исходного артефакта.
/// </summary>
public sealed record JobRequest
{
    /// <summary>
    /// Получает идентификатор исходного видеоартефакта.
    /// </summary>
    /// <value>Непустой идентификатор артефакта.</value>
    public ArtifactId SourceAssetId { get; }

    /// <summary>
    /// Получает пользовательский комментарий с требованиями к результату.
    /// </summary>
    /// <value>Непустой текст пользовательского комментария.</value>
    public string Comment { get; }

    /// <summary>
    /// Получает запрошенный режим производства.
    /// </summary>
    /// <value>Режим, выбранный пользователем или заданный для автоматического уточнения.</value>
    public ProductionMode RequestedMode { get; }

    /// <summary>
    /// Получает настройки создаваемых вариантов.
    /// </summary>
    /// <value>Ненулевой набор настроек производства.</value>
    public ProductionSettings Settings { get; }

    /// <summary>
    /// Создаёт проверенный запрос на производство видео.
    /// </summary>
    /// <param name="sourceAssetId">Непустой идентификатор исходного видеоартефакта.</param>
    /// <param name="comment">Непустой пользовательский комментарий с требованиями.</param>
    /// <param name="requestedMode">Запрошенный режим производства.</param>
    /// <param name="settings">Ненулевые настройки создаваемых вариантов.</param>
    public JobRequest(
        ArtifactId sourceAssetId,
        string comment,
        ProductionMode requestedMode,
        ProductionSettings settings)
    {
        if (sourceAssetId.IsEmpty)
        {
            throw new ArgumentException("Source asset ID cannot be empty.", nameof(sourceAssetId));
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Comment cannot be empty.", nameof(comment));
        }

        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        ArgumentNullException.ThrowIfNull(settings);

        SourceAssetId = sourceAssetId;
        Comment = comment;
        RequestedMode = requestedMode;
        Settings = settings;
    }
}
