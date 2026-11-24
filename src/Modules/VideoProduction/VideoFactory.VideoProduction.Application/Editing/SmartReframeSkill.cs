using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Безопасная стратегия при отсутствии надёжных focal observations.</summary>
public enum ReframeFallback
{
    /// <summary>Показать источник целиком без обрезания.</summary>
    Fit,

    /// <summary>Использовать устойчивый центральный crop.</summary>
    Center
}

/// <summary>Ключевой кадр плавной траектории crop.</summary>
/// <param name="OutputFrame">Кадр выходной шкалы.</param>
/// <param name="FocalX">Горизонтальная focal coordinate от 0 до 1.</param>
/// <param name="FocalY">Вертикальная focal coordinate от 0 до 1.</param>
/// <param name="Zoom">Масштаб не меньше 1.</param>
public sealed record ReframeKeyframe(int OutputFrame, decimal FocalX, decimal FocalY, decimal Zoom);

/// <summary>Траектория crop одного выбранного сегмента.</summary>
/// <param name="SegmentId">Идентификатор сегмента.</param>
/// <param name="Fallback">Стратегия при отсутствии keyframes.</param>
/// <param name="Keyframes">Сглаженные ключевые кадры; может быть пустым.</param>
public sealed record ReframeTrack(
    SegmentId SegmentId,
    ReframeFallback Fallback,
    ImmutableArray<ReframeKeyframe> Keyframes);

/// <summary>Результат Smart Reframe.</summary>
/// <param name="SkillId">Стабильный идентификатор skill.</param>
/// <param name="SkillVersion">Версия алгоритма.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="Tracks">Траектории известных segments.</param>
public sealed record SmartReframeDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<ReframeTrack> Tracks)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Строит ограниченную crop-траекторию или выбирает явный safe fallback.</summary>
public sealed class SmartReframeSkill : IEditingSkill
{
    private const decimal MinimumConfidence = 0.5m;
    private const decimal MaximumKeyframeMovement = 0.15m;

    /// <inheritdoc />
    public string Id => "smart-reframe";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["pacing-director"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AnalysisResult? analysis = context.Analysis;
        PacingDecision? pacing = context.PreviousDecisions?.GetValueOrDefault("pacing-director") as PacingDecision;
        if (analysis is null || pacing?.Status != EditingDecisionStatus.Applied)
        {
            return new SmartReframeDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new("reframe.timeline_missing", "Analysis and an applied pacing timeline are required.")],
                []);
        }

        ImmutableArray<ReframeTrack> tracks = pacing.Shots
            .Select(shot => CreateTrack(
                analysis,
                shot,
                context.PreviousDecisions,
                context.Style?.MaximumCropMovement ?? MaximumKeyframeMovement))
            .ToImmutableArray();
        bool hasTracking = tracks.Any(track => !track.Keyframes.IsEmpty);
        return new SmartReframeDecision(
            Id,
            Version,
            EditingDecisionStatus.Applied,
            [hasTracking
                ? new("reframe.smoothed", "Focal observations are weighted and movement is bounded.")
                : new("reframe.tracking_unavailable", "No reliable focal observations exist; fit fallback is required.")],
            tracks);
    }

    private static ReframeTrack CreateTrack(
        AnalysisResult analysis,
        PacingShot shot,
        IReadOnlyDictionary<string, EditingDecision>? decisions,
        decimal maximumMovement)
    {
        AllowedSegment? segment = analysis.AllowedSegments.FirstOrDefault(item => item.Id == shot.SegmentId);
        if (segment is null)
            return new ReframeTrack(shot.SegmentId, ReframeFallback.Fit, []);
        SourceMapping mapping = decisions?.GetValueOrDefault("smart-cut") is SmartCutDecision cut
            ? cut.Edits.FirstOrDefault(edit => edit.SegmentId == shot.SegmentId)?.Mapping ?? segment.Mapping
            : segment.Mapping;

        IGrouping<long, FocalObservation>[] observations = analysis.Features.FocalObservations
            .Where(item => item.SegmentId == shot.SegmentId
                && item.Confidence >= MinimumConfidence
                && IsNormalized(item)
                && item.MasterTimeMicroseconds >= mapping.Master.StartMicroseconds
                && item.MasterTimeMicroseconds < mapping.Master.EndMicroseconds)
            .GroupBy(item => item.MasterTimeMicroseconds)
            .OrderBy(group => group.Key)
            .ToArray();
        if (observations.Length == 0)
            return new ReframeTrack(shot.SegmentId, ReframeFallback.Fit, []);

        ImmutableArray<ReframeKeyframe>.Builder keyframes = ImmutableArray.CreateBuilder<ReframeKeyframe>();
        decimal? previousX = null;
        decimal? previousY = null;
        foreach (IGrouping<long, FocalObservation> group in observations)
        {
            decimal totalWeight = group.Sum(item => item.Confidence);
            decimal x = group.Sum(item => (item.X + item.Width / 2m) * item.Confidence) / totalWeight;
            decimal y = group.Sum(item => (item.Y + item.Height / 2m) * item.Confidence) / totalWeight;
            if (previousX.HasValue)
            {
                x = ClampMovement(previousX.Value, x, maximumMovement);
                y = ClampMovement(previousY!.Value, y, maximumMovement);
            }

            long offset = group.Key - mapping.Master.StartMicroseconds;
            int frame = shot.Output.StartFrame + (int)Math.Round(
                offset / (decimal)mapping.Master.DurationMicroseconds * shot.Output.DurationFrames,
                MidpointRounding.AwayFromZero);
            keyframes.Add(new ReframeKeyframe(frame, x, y, group.Count() > 1 ? 1m : 1.1m));
            previousX = x;
            previousY = y;
        }

        return new ReframeTrack(shot.SegmentId, ReframeFallback.Center, keyframes.ToImmutable());
    }

    private static decimal ClampMovement(decimal previous, decimal current, decimal maximumMovement) =>
        Math.Clamp(current, previous - maximumMovement, previous + maximumMovement);

    private static bool IsNormalized(FocalObservation observation) =>
        observation.X is >= 0 and <= 1
        && observation.Y is >= 0 and <= 1
        && observation.Width is > 0 and <= 1
        && observation.Height is > 0 and <= 1
        && observation.X + observation.Width <= 1
        && observation.Y + observation.Height <= 1;
}
