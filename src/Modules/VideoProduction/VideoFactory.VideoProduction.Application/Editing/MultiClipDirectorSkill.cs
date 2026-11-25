using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Один выбранный source segment и его детерминированный порядок в монтаже.</summary>
/// <param name="SegmentId">Идентификатор известного разрешённого сегмента.</param>
/// <param name="SourceOrder">Нулевой ordinal источника в порядке загрузки.</param>
/// <param name="Mapping">Проверенная связь Original/Master интервалов.</param>
/// <param name="OutputOrder">Нулевой ordinal сегмента в выходной последовательности.</param>
/// <param name="Score">Объяснимая оценка качества от 0 до 1.</param>
public sealed record MultiClipSelection(
    SegmentId SegmentId,
    int SourceOrder,
    SourceMapping Mapping,
    int OutputOrder,
    decimal Score);

/// <summary>Закрытое решение выбора и порядка фрагментов нескольких источников.</summary>
/// <param name="SkillId">Стабильный идентификатор skill.</param>
/// <param name="SkillVersion">Версия алгоритма.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="Selections">Выбранные известные сегменты в output order.</param>
public sealed record MultiClipDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<MultiClipSelection> Selections)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Детерминированно выбирает и упорядочивает известные source segments.</summary>
public sealed class MultiClipDirectorSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "multi-clip-director";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => [];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MultiSourceAnalysisResult? analysis = context.MultiSourceAnalysis
            ?? (context.Analysis is null ? null : AnalysisContractAdapter.ToMultiSource(context.Analysis));
        if (analysis is null)
        {
            return new MultiClipDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new("multi_clip.analysis_v3_missing", "Analysis V3 is required for multi-source selection.")],
                []);
        }

        Dictionary<SegmentId, SegmentAnalysisFeatures> features = context.SegmentFeatures
            .ToDictionary(feature => feature.SegmentId);
        Dictionary<(ArtifactId Original, ArtifactId Master), int> sourceOrders = analysis.Sources
            .Select((source, index) => (source, index))
            .ToDictionary(
                item => (item.source.OriginalAssetId, item.source.MasterAssetId),
                item => item.index);
        var candidates = analysis.AllowedSegments
            .Where(segment => segment.IsApproved)
            .Select(segment => new
            {
                Segment = segment,
                SourceOrder = sourceOrders.GetValueOrDefault(
                    (segment.Mapping.OriginalAssetId, segment.Mapping.MasterAssetId), -1),
                Feature = features.GetValueOrDefault(segment.Id)
            })
            .Where(candidate => candidate.SourceOrder >= 0)
            .Select(candidate => new
            {
                candidate.Segment,
                candidate.SourceOrder,
                DuplicateGroup = candidate.Feature?.DuplicateGroup,
                Score = candidate.Feature is null
                    ? candidate.Segment.Confidence
                    : candidate.Feature.SemanticRelevance * 0.4m
                        + candidate.Feature.Sharpness * 0.25m
                        + candidate.Feature.Composition * 0.2m
                        + candidate.Feature.Motion * 0.15m
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.SourceOrder)
            .ThenBy(candidate => candidate.Segment.Id.Value)
            .GroupBy(candidate => candidate.DuplicateGroup is null
                ? $"segment:{candidate.Segment.Id}"
                : $"duplicate:{candidate.DuplicateGroup}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        ImmutableArray<MultiClipSelection> selections = [.. candidates.Select((candidate, outputOrder) =>
            new MultiClipSelection(
                candidate.Segment.Id,
                candidate.SourceOrder,
                candidate.Segment.Mapping,
                outputOrder,
                candidate.Score))];
        return selections.IsEmpty
            ? new MultiClipDecision(
                Id, Version, EditingDecisionStatus.Unavailable,
                [new("multi_clip.segments_missing", "No approved known segments are available.")], [])
            : new MultiClipDecision(
                Id, Version, EditingDecisionStatus.Applied,
                [new("multi_clip.known_segments_ranked", "Known segments are ranked with deterministic source-order tie-breaking.")],
                selections);
    }
}
