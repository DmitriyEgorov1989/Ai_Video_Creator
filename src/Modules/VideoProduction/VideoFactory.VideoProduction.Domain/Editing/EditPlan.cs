using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Editing;

/// <summary>
/// Задаёт параметры выходного видеопотока и длительность монтажной шкалы.
/// </summary>
/// <param name="Width">Ширина выходного кадра в пикселях.</param>
/// <param name="Height">Высота выходного кадра в пикселях.</param>
/// <param name="FrameRate">Частота кадров выходного видео.</param>
/// <param name="DurationFrames">Полная длительность выходного видео в кадрах.</param>
public sealed record OutputProfile(
    int Width,
    int Height,
    FrameRate FrameRate,
    int DurationFrames);

/// <summary>
/// Определяет способ размещения исходного изображения в выходном кадре.
/// </summary>
public enum CropPreset
{
    /// <summary>
    /// Вписывает изображение в выходной кадр без обрезки.
    /// </summary>
    Fit,

    /// <summary>
    /// Заполняет выходной кадр с обрезкой изображения относительно центра.
    /// </summary>
    CenterCrop
}

/// <summary>
/// Описывает видеофрагмент на выходной монтажной шкале.
/// </summary>
/// <param name="SegmentId">Идентификатор разрешённого исходного сегмента.</param>
/// <param name="Mapping">Соответствие интервалов оригинала и master-артефакта; не может быть <see langword="null"/>.</param>
/// <param name="Output">Интервал размещения фрагмента на выходной шкале.</param>
/// <param name="Crop">Способ размещения изображения в выходном кадре.</param>
public sealed record VideoClip(
    SegmentId SegmentId,
    SourceMapping Mapping,
    OutputRange Output,
    CropPreset Crop);

/// <summary>
/// Содержит упорядоченные видеофрагменты монтажного плана.
/// </summary>
/// <param name="Clips">Видеофрагменты выходной шкалы; элементы не могут быть <see langword="null"/>.</param>
public sealed record VideoTrack(ImmutableArray<VideoClip> Clips);

/// <summary>
/// Описывает звуковой фрагмент, связанный с исходным видеосегментом.
/// </summary>
/// <param name="SegmentId">Идентификатор исходного сегмента.</param>
/// <param name="Output">Интервал воспроизведения на выходной шкале.</param>
/// <param name="Gain">Коэффициент усиления звука.</param>
public sealed record SourceAudioClip(
    SegmentId SegmentId,
    OutputRange Output,
    decimal Gain);

/// <summary>
/// Описывает музыкальный фрагмент на выходной монтажной шкале.
/// </summary>
/// <param name="AssetId">Идентификатор музыкального артефакта.</param>
/// <param name="Source">Используемый интервал музыкального артефакта.</param>
/// <param name="Output">Интервал воспроизведения на выходной шкале.</param>
/// <param name="Gain">Коэффициент усиления музыки.</param>
public sealed record MusicClip(
    ArtifactId AssetId,
    AssetRange Source,
    OutputRange Output,
    decimal Gain);

/// <summary>
/// Объединяет исходные звуковые и музыкальные фрагменты монтажного плана.
/// </summary>
/// <param name="SourceClips">Звуковые фрагменты исходного видео; элементы не могут быть <see langword="null"/>.</param>
/// <param name="MusicClips">Музыкальные фрагменты; элементы не могут быть <see langword="null"/>.</param>
public sealed record AudioTrack(
    ImmutableArray<SourceAudioClip> SourceClips,
    ImmutableArray<MusicClip> MusicClips);

/// <summary>
/// Описывает текст субтитра и интервал его показа.
/// </summary>
/// <param name="Text">Текст субтитра; не может быть <see langword="null"/>.</param>
/// <param name="Output">Интервал показа на выходной шкале.</param>
public sealed record Caption(string Text, OutputRange Output);

/// <summary>
/// Содержит субтитры монтажного плана.
/// </summary>
/// <param name="Items">Элементы субтитров на выходной шкале; элементы не могут быть <see langword="null"/>.</param>
public sealed record CaptionTrack(ImmutableArray<Caption> Items);

/// <summary>
/// Представляет графический элемент, наложенный на выходное видео.
/// </summary>
/// <param name="Output">Интервал показа элемента на выходной шкале.</param>
public abstract record Overlay(OutputRange Output);

/// <summary>
/// Описывает текстовый элемент, наложенный на выходное видео.
/// </summary>
/// <param name="Text">Отображаемый текст; не может быть <see langword="null"/>.</param>
/// <param name="Output">Интервал показа элемента на выходной шкале.</param>
public sealed record TextOverlay(string Text, OutputRange Output) : Overlay(Output);

/// <summary>
/// Описывает изображение, наложенное на выходное видео.
/// </summary>
/// <param name="AssetId">Идентификатор артефакта изображения.</param>
/// <param name="Output">Интервал показа элемента на выходной шкале.</param>
public sealed record ImageOverlay(ArtifactId AssetId, OutputRange Output) : Overlay(Output);

/// <summary>
/// Содержит графические элементы монтажного плана.
/// </summary>
/// <param name="Items">Элементы, наложенные на выходное видео; элементы не могут быть <see langword="null"/>.</param>
public sealed record OverlayTrack(ImmutableArray<Overlay> Items);

/// <summary>
/// Представляет версионированный монтажный план одного варианта видео.
/// </summary>
/// <param name="SchemaVersion">Версия схемы монтажного плана.</param>
/// <param name="JobId">Идентификатор производственного задания.</param>
/// <param name="VariantId">Идентификатор варианта видео.</param>
/// <param name="RevisionId">Идентификатор ревизии монтажного плана.</param>
/// <param name="AnalysisRevisionId">Идентификатор ревизии анализа, на которой основан план.</param>
/// <param name="Seed">Начальное значение для детерминированной генерации варианта.</param>
/// <param name="Output">Параметры выходного видео; не могут быть <see langword="null"/>.</param>
/// <param name="Template">Идентификатор шаблона визуализации.</param>
/// <param name="TemplateProps">Параметры выбранного шаблона; не могут быть <see langword="null"/>.</param>
/// <param name="Video">Видеодорожка монтажного плана; не может быть <see langword="null"/>.</param>
/// <param name="Audio">Аудиодорожка монтажного плана; не может быть <see langword="null"/>.</param>
/// <param name="Captions">Дорожка субтитров; не может быть <see langword="null"/>.</param>
/// <param name="Overlays">Дорожка графических элементов; не может быть <see langword="null"/>.</param>
public sealed record EditPlan(
    int SchemaVersion,
    JobId JobId,
    VariantId VariantId,
    RevisionId RevisionId,
    RevisionId AnalysisRevisionId,
    int Seed,
    OutputProfile Output,
    TemplateId Template,
    TemplateProps TemplateProps,
    VideoTrack Video,
    AudioTrack Audio,
    CaptionTrack Captions,
    OverlayTrack Overlays);
