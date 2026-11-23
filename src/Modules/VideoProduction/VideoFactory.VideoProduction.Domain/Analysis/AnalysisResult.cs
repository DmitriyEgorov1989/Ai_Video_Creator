using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Analysis;

/// <summary>
/// Связывает интервал оригинального артефакта с соответствующим интервалом master-артефакта.
/// </summary>
/// <param name="OriginalAssetId">Идентификатор оригинального артефакта.</param>
/// <param name="Original">Интервал на временной шкале оригинала.</param>
/// <param name="MasterAssetId">Идентификатор нормализованного master-артефакта.</param>
/// <param name="Master">Соответствующий интервал на временной шкале master-артефакта.</param>
public sealed record SourceMapping(
    ArtifactId OriginalAssetId,
    OriginalRange Original,
    ArtifactId MasterAssetId,
    MasterRange Master);

/// <summary>
/// Представляет разрешённый для монтажа сегмент исходного материала.
/// </summary>
/// <param name="Id">Идентификатор сегмента.</param>
/// <param name="Mapping">Соответствие сегмента временным шкалам оригинала и master-артефакта; не может быть <see langword="null"/>.</param>
public sealed record AllowedSegment(SegmentId Id, SourceMapping Mapping)
{
    /// <summary>Идентификаторы связанных речевых сегментов; может быть пустым.</summary>
    public ImmutableArray<SegmentId> SpeechSegmentIds { get; init; } = [];

    /// <summary>Индексы связанных сцен; может быть пустым.</summary>
    public ImmutableArray<int> SceneIndexes { get; init; } = [];

    /// <summary>Признаки качества сегмента.</summary>
    public SegmentQualitySnapshot Features { get; init; } = SegmentQualitySnapshot.Fallback;

    /// <summary>Уверенность допуска от 0 до 1.</summary>
    public decimal Confidence { get; init; }

    /// <summary>Детерминированные причины допуска или отклонения.</summary>
    public ImmutableArray<string> Reasons { get; init; } = [];

    /// <summary>Разрешён ли сегмент для планирования.</summary>
    public bool IsApproved { get; init; }
}

/// <summary>Снимок объективных признаков, сохранённый в Analysis Result.</summary>
public sealed record SegmentQualitySnapshot(
    decimal Motion,
    decimal Sharpness,
    decimal Exposure,
    decimal AudioEnergy,
    bool TechnicalValidity,
    ImmutableArray<string> TechnicalIssues)
{
    /// <summary>Признаки при отсутствии доступного extractor.</summary>
    public static SegmentQualitySnapshot Fallback { get; } = new(
        0m, 0.5m, 0.5m, 0m, true, ["features-unavailable-fallback"]);
}

/// <summary>
/// Описывает временное положение распознанного слова в master-артефакте.
/// </summary>
/// <param name="Text">Распознанный текст слова; не может быть <see langword="null"/>.</param>
/// <param name="Interval">Интервал произнесения слова на временной шкале master-артефакта.</param>
public sealed record WordTiming(string Text, MasterRange Interval);

/// <summary>
/// Описывает распознанный речевой сегмент и временные метки составляющих его слов.
/// </summary>
/// <param name="Text">Полный распознанный текст сегмента; не может быть <see langword="null"/>.</param>
/// <param name="Interval">Интервал сегмента на временной шкале master-артефакта.</param>
/// <param name="Words">Слова сегмента с индивидуальными временными метками; элементы не могут быть <see langword="null"/>.</param>
public sealed record SpeechSegment(
    string Text,
    MasterRange Interval,
    ImmutableArray<WordTiming> Words)
{
    /// <summary>Идентификатор разрешённого сегмента; может быть <see langword="null"/>.</summary>
    public SegmentId? SegmentId { get; init; }
}

/// <summary>
/// Описывает обнаруженную сцену в master-артефакте.
/// </summary>
/// <param name="Interval">Интервал сцены на временной шкале master-артефакта.</param>
public sealed record Scene(MasterRange Interval);

/// <summary>
/// Содержит версионированный результат анализа исходного видео.
/// </summary>
/// <param name="SchemaVersion">Версия схемы результата анализа.</param>
/// <param name="RevisionId">Идентификатор ревизии анализа.</param>
/// <param name="OriginalAssetId">Идентификатор оригинального артефакта.</param>
/// <param name="MasterAssetId">Идентификатор нормализованного master-артефакта.</param>
/// <param name="Speech">Распознанные речевые сегменты; элементы не могут быть <see langword="null"/>.</param>
/// <param name="Pauses">Обнаруженные интервалы без речи.</param>
/// <param name="Scenes">Обнаруженные сцены; элементы не могут быть <see langword="null"/>.</param>
/// <param name="AllowedSegments">Сегменты, разрешённые для использования в монтажных планах; элементы не могут быть <see langword="null"/>.</param>
public sealed record AnalysisResult(
    int SchemaVersion,
    RevisionId RevisionId,
    ArtifactId OriginalAssetId,
    ArtifactId MasterAssetId,
    ImmutableArray<SpeechSegment> Speech,
    ImmutableArray<MasterRange> Pauses,
    ImmutableArray<Scene> Scenes,
    ImmutableArray<AllowedSegment> AllowedSegments);
