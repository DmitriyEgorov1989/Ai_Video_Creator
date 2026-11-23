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
public sealed record AllowedSegment(SegmentId Id, SourceMapping Mapping);

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
    ImmutableArray<WordTiming> Words);

/// <summary>
/// Описывает обнаруженную сцену в master-артефакте.
/// </summary>
/// <param name="Interval">Интервал сцены на временной шкале master-артефакта.</param>
public sealed record Scene(MasterRange Interval);

/// <summary>Объяснимые признаки качества одного разрешённого сегмента.</summary>
/// <param name="SegmentId">Идентификатор разрешённого сегмента.</param>
/// <param name="SemanticRelevance">Семантическая релевантность в диапазоне от 0 до 1.</param>
/// <param name="Sharpness">Резкость изображения в диапазоне от 0 до 1.</param>
/// <param name="Composition">Качество композиции в диапазоне от 0 до 1.</param>
/// <param name="Motion">Информативность движения в диапазоне от 0 до 1.</param>
/// <param name="AudioEnergy">Энергия звука в диапазоне от 0 до 1.</param>
/// <param name="Confidence">Уверенность вычисления признаков в диапазоне от 0 до 1.</param>
/// <param name="DuplicateGroup">Стабильный идентификатор группы дублей или <see langword="null" />.</param>
public sealed record SegmentAnalysisFeatures(
    SegmentId SegmentId,
    decimal SemanticRelevance,
    decimal Sharpness,
    decimal Composition,
    decimal Motion,
    decimal AudioEnergy,
    decimal Confidence,
    string? DuplicateGroup = null);

/// <summary>Дополнительные признаки versioned результата анализа.</summary>
/// <param name="Segments">Признаки известных сегментов.</param>
public sealed record AnalysisFeatureSet(ImmutableArray<SegmentAnalysisFeatures> Segments)
{
    /// <summary>Версия схемы дополнительных признаков.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Пустой набор признаков для совместимости с Analysis V1.</summary>
    public static AnalysisFeatureSet Empty => new([]);
}

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
    ImmutableArray<AllowedSegment> AllowedSegments)
{
    /// <summary>Дополнительные признаки Analysis V2; для V1 содержит пустой набор.</summary>
    public AnalysisFeatureSet Features { get; init; } = AnalysisFeatureSet.Empty;
}
