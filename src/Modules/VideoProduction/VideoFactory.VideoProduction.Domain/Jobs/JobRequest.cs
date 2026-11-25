using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Domain.Jobs;

/// <summary>
/// Представляет проверенный запрос на создание одного видео из исходного артефакта.
/// </summary>
public sealed record JobRequest
{
    /// <summary>
    /// Получает упорядоченные идентификаторы исходных видеоартефактов.
    /// </summary>
    /// <value>От одного до пяти уникальных непустых идентификаторов в порядке загрузки.</value>
    public ImmutableArray<ArtifactId> SourceAssetIds { get; }

    /// <summary>
    /// Получает основной исходный видеоартефакт для совместимого чтения single-source кода.
    /// </summary>
    /// <value>Первый элемент <see cref="SourceAssetIds"/>.</value>
    public ArtifactId PrimarySourceAssetId => SourceAssetIds[0];

    /// <summary>
    /// Получает основной исходный видеоартефакт для совместимости существующих потребителей.
    /// </summary>
    /// <value>Первый элемент <see cref="SourceAssetIds"/>.</value>
    public ArtifactId SourceAssetId => PrimarySourceAssetId;

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
    /// Получает настройки создаваемого видео.
    /// </summary>
    /// <value>Ненулевой набор настроек производства.</value>
    public ProductionSettings Settings { get; }

    /// <summary>
    /// Создаёт проверенный запрос на производство видео.
    /// </summary>
    /// <param name="sourceAssetId">Непустой идентификатор исходного видеоартефакта.</param>
    /// <param name="comment">Непустой пользовательский комментарий с требованиями.</param>
    /// <param name="requestedMode">Запрошенный режим производства.</param>
    /// <param name="settings">Ненулевые настройки создаваемого видео.</param>
    public JobRequest(
        ArtifactId sourceAssetId,
        string comment,
        ProductionMode requestedMode,
        ProductionSettings settings)
        : this([sourceAssetId], comment, requestedMode, settings)
    {
    }

    /// <summary>
    /// Создаёт проверенный запрос на производство видео из упорядоченного набора источников.
    /// </summary>
    /// <param name="sourceAssetIds">От одного до пяти уникальных непустых идентификаторов в порядке загрузки.</param>
    /// <param name="comment">Непустой пользовательский комментарий с требованиями.</param>
    /// <param name="requestedMode">Запрошенный режим производства.</param>
    /// <param name="settings">Ненулевые настройки создаваемого видео.</param>
    public JobRequest(
        IReadOnlyList<ArtifactId> sourceAssetIds,
        string comment,
        ProductionMode requestedMode,
        ProductionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetIds);
        if (sourceAssetIds.Count is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceAssetIds), "A Job requires from one to five source assets.");
        }

        if (sourceAssetIds.Any(sourceAssetId => sourceAssetId.IsEmpty))
        {
            throw new ArgumentException("Source asset IDs cannot be empty.", nameof(sourceAssetIds));
        }

        if (sourceAssetIds.Distinct().Count() != sourceAssetIds.Count)
        {
            throw new ArgumentException("Source asset IDs must be unique.", nameof(sourceAssetIds));
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

        SourceAssetIds = [.. sourceAssetIds];
        Comment = comment;
        RequestedMode = requestedMode;
        Settings = settings;
    }
}
