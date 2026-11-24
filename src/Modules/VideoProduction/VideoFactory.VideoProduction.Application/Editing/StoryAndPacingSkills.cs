using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Роль выбранного сегмента в объяснимой истории.</summary>
public enum StoryRole
{
    Hook,
    Context,
    Value,
    Conclusion
}

/// <summary>Сегмент, выбранный для структуры истории.</summary>
public sealed record StorySegment(SegmentId SegmentId, StoryRole Role, string Text);

/// <summary>Результат Semantic Story Director.</summary>
public sealed record SemanticStoryDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<StorySegment> Segments)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Кандидат на первый фрагмент без выдуманного текста.</summary>
public sealed record HookCandidate(SegmentId SegmentId, string Text, decimal Score);

/// <summary>Результат Hook Builder.</summary>
public sealed record HookDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<HookCandidate> Candidates)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Безопасное изменение границ разрешённого сегмента.</summary>
public sealed record SmartCutEdit(SegmentId SegmentId, SourceMapping Mapping, string Reason);

/// <summary>Результат Smart Cut.</summary>
public sealed record SmartCutDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<SmartCutEdit> Edits)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Оценка выбранного кадра или сегмента.</summary>
public sealed record BestShotSelection(
    SegmentId SegmentId,
    decimal Score,
    decimal SemanticRelevance,
    decimal Sharpness,
    decimal Composition,
    decimal Motion);

/// <summary>Результат Best Shot Selector.</summary>
public sealed record BestShotDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<BestShotSelection> Selections)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Ритмический интервал сегмента на выходной шкале.</summary>
public sealed record PacingShot(SegmentId SegmentId, OutputRange Output, bool Accent, bool RestPoint);

/// <summary>Результат Pacing Director.</summary>
public sealed record PacingDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<PacingShot> Shots,
    decimal CutDensity)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Выбирает структуру истории из существующих речевых сегментов.</summary>
public sealed class SemanticStorySkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "semantic-story";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => [];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AnalysisResult? analysis = context.Analysis;
        if (analysis is null)
        {
            return new SemanticStoryDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("story.analysis_missing", "Analysis is required to build a story.")], []);
        }

        List<StorySegment> segments = analysis.AllowedSegments
            .Select((segment, index) =>
            {
                string text = string.Join(' ', analysis.Speech
                    .Where(speech => speech.Interval.Intersects(segment.Mapping.Master))
                    .Select(speech => speech.Text));
                StoryRole role = index == 0
                    ? StoryRole.Hook
                    : index == analysis.AllowedSegments.Length - 1
                        ? StoryRole.Conclusion
                        : index % 2 == 0 ? StoryRole.Value : StoryRole.Context;
                return new StorySegment(segment.Id, role, text);
            })
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .ToList();

        return segments.Count == 0
            ? new SemanticStoryDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("story.speech_missing", "No allowed segment has linked speech.")], [])
            : new SemanticStoryDecision(Id, Version, EditingDecisionStatus.Applied,
                [new("story.known_segments_only", "Story structure uses only known segments and transcript text.")],
                segments.ToImmutableArray());
    }
}

/// <summary>Ранжирует реальные речевые hook-кандидаты.</summary>
public sealed class HookBuilderSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "hook-builder";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["semantic-story"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Analysis is null)
        {
            return new HookDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("hook.analysis_missing", "Analysis is required to build hooks.")], []);
        }

        SemanticStoryDecision? story = context.PreviousDecisions?.GetValueOrDefault("semantic-story") as SemanticStoryDecision;
        if (story is null || story.Status != EditingDecisionStatus.Applied)
        {
            return new HookDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("hook.story_missing", "Semantic Story must be applied before Hook Builder.")], []);
        }

        Dictionary<SegmentId, SegmentAnalysisFeatures> features = context.Analysis.Features.Segments
            .ToDictionary(item => item.SegmentId);
        ImmutableArray<HookCandidate> candidates = story.Segments
            .Where(segment => segment.Role == StoryRole.Hook || segment.Role == StoryRole.Context)
            .Select(segment => new HookCandidate(
                segment.SegmentId,
                segment.Text,
                features.TryGetValue(segment.SegmentId, out SegmentAnalysisFeatures? value)
                    ? value.SemanticRelevance
                    : 0.5m))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.SegmentId.Value)
            .ToImmutableArray();

        return candidates.IsEmpty
            ? new HookDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("hook.candidates_missing", "No real speech hook candidate is available.")], [])
            : new HookDecision(Id, Version, EditingDecisionStatus.Applied,
                [new("hook.ranked_known_candidates", "Hooks are ranked from known transcript segments.")], candidates);
    }
}

/// <summary>Удаляет только безопасные повторяющиеся и pause-covered границы.</summary>
public sealed class SmartCutSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "smart-cut";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["semantic-story"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AnalysisResult? analysis = context.Analysis;
        if (analysis is null)
        {
            return new SmartCutDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("cut.analysis_missing", "Analysis is required for Smart Cut.")], []);
        }

        ImmutableHashSet<string> duplicateGroups = analysis.Features.Segments
            .Where(feature => feature.DuplicateGroup is not null)
            .GroupBy(feature => feature.DuplicateGroup!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToImmutableHashSet(StringComparer.Ordinal);
        ImmutableArray<SmartCutEdit> edits = analysis.AllowedSegments
            .Select(segment => CreateEdit(segment, analysis, duplicateGroups))
            .Where(edit => edit is not null)
            .Select(edit => edit!)
            .ToImmutableArray();

        return new SmartCutDecision(Id, Version, EditingDecisionStatus.Applied,
            [new("cut.whole_words_only", "Smart Cut changes boundaries only at complete word intervals.")], edits);
    }

    private static SmartCutEdit? CreateEdit(
        AllowedSegment segment,
        AnalysisResult analysis,
        ImmutableHashSet<string> duplicateGroups)
    {
        WordTiming[] words = analysis.Speech
            .SelectMany(speech => speech.Words)
            .Where(word => segment.Mapping.Master.Contains(word.Interval))
            .OrderBy(word => word.Interval.StartMicroseconds)
            .ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        long start = segment.Mapping.Master.StartMicroseconds;
        long end = segment.Mapping.Master.EndMicroseconds;
        if (IsPauseCovered(start, words[0].Interval.StartMicroseconds, analysis.Pauses))
        {
            start = words[0].Interval.StartMicroseconds;
        }

        if (IsPauseCovered(words[^1].Interval.EndMicroseconds, end, analysis.Pauses))
        {
            end = words[^1].Interval.EndMicroseconds;
        }

        if (start == segment.Mapping.Master.StartMicroseconds && end == segment.Mapping.Master.EndMicroseconds)
        {
            return null;
        }

        SourceMapping mapping = segment.Mapping with
        {
            Master = new MasterRange(start, end),
            Original = new OriginalRange(
                segment.Mapping.Original.StartMicroseconds + start - segment.Mapping.Master.StartMicroseconds,
                segment.Mapping.Original.EndMicroseconds - segment.Mapping.Master.EndMicroseconds + end)
        };
        string reason = analysis.Features.Segments.Any(feature =>
            feature.SegmentId == segment.Id && feature.DuplicateGroup is not null
            && duplicateGroups.Contains(feature.DuplicateGroup))
            ? "duplicate-boundary"
            : "pause-boundary";
        return new(segment.Id, mapping, reason);
    }

    private static bool IsPauseCovered(long start, long end, ImmutableArray<MasterRange> pauses)
    {
        if (start >= end)
        {
            return true;
        }

        long cursor = start;
        foreach (MasterRange pause in pauses.OrderBy(pause => pause.StartMicroseconds))
        {
            if (pause.EndMicroseconds <= cursor)
            {
                continue;
            }

            if (pause.StartMicroseconds > cursor)
            {
                return false;
            }

            cursor = Math.Max(cursor, pause.EndMicroseconds);
            if (cursor >= end)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Выбирает сегменты по объяснимой комбинации качества.</summary>
public sealed class BestShotSelectorSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "best-shot";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["semantic-story"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Analysis is null || context.Analysis.Features.Segments.IsEmpty)
        {
            return new BestShotDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("shot.features_missing", "Visual quality features are required for Best Shot.")], []);
        }

        ImmutableArray<BestShotSelection> selections = context.Analysis.Features.Segments
            .Select(feature => new BestShotSelection(
                feature.SegmentId,
                feature.SemanticRelevance * 0.4m
                    + feature.Sharpness * 0.25m
                    + feature.Composition * 0.2m
                    + feature.Motion * 0.15m,
                feature.SemanticRelevance,
                feature.Sharpness,
                feature.Composition,
                feature.Motion))
            .OrderByDescending(selection => selection.Score)
            .ThenBy(selection => selection.SegmentId.Value)
            .ToImmutableArray();
        return new BestShotDecision(Id, Version, EditingDecisionStatus.Applied,
            [new("shot.explainable_score", "Score weights semantic relevance, sharpness, composition and motion.")], selections);
    }
}

/// <summary>Распределяет выходную длительность по содержательным сегментам.</summary>
public sealed class PacingDirectorSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "pacing-director";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["semantic-story"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AnalysisResult? analysis = context.Analysis;
        if (analysis is null || analysis.AllowedSegments.IsEmpty)
        {
            return new PacingDecision(Id, Version, EditingDecisionStatus.Unavailable,
                [new("pacing.segments_missing", "Allowed segments are required for pacing.")], [], 0m);
        }

        SmartCutDecision? cut = context.PreviousDecisions?.GetValueOrDefault("smart-cut") as SmartCutDecision;
        IReadOnlyDictionary<SegmentId, SourceMapping>? effectiveMappings = cut?.Edits
            .ToDictionary(edit => edit.SegmentId, edit => edit.Mapping);
        int cursor = 0;
        long selectedDuration = context.Settings.TargetDurationSeconds is int targetSeconds
            ? targetSeconds * 1_000_000L
            : new ReelsProfileCatalog().SelectSemanticDurationMicroseconds(analysis, effectiveMappings);
        long cumulativeDuration = 0;
        ImmutableArray<PacingShot>.Builder shots = ImmutableArray.CreateBuilder<PacingShot>();
        for (int index = 0; index < analysis.AllowedSegments.Length; index++)
        {
            AllowedSegment segment = analysis.AllowedSegments[index];
            SourceMapping mapping = effectiveMappings?.GetValueOrDefault(segment.Id) ?? segment.Mapping;
            if (index > 0 && cumulativeDuration >= selectedDuration)
                break;
            int duration = TimelineMath.ToNearestFrameCount(
                mapping.Master.DurationMicroseconds,
                new FrameRate(30, 1));
            OutputRange output = new(cursor, cursor + duration);
            shots.Add(new(segment.Id, output,
                index == 0 || context.Settings.Tempo == EditTempo.Dynamic && index % 2 == 1,
                context.Settings.Tempo == EditTempo.Calm && index % 2 == 0));
            cursor += duration;
            cumulativeDuration += mapping.Master.DurationMicroseconds;
        }

        decimal density = cursor == 0 ? 0m : (analysis.AllowedSegments.Length - 1) / (decimal)cursor;
        return new PacingDecision(Id, Version, EditingDecisionStatus.Applied,
            [new("pacing.contiguous_timeline", "Pacing creates a contiguous output timeline in frames.")], shots.ToImmutable(), density);
    }
}

file static class MasterRangeExtensions
{
    public static bool Intersects(this MasterRange left, MasterRange right) =>
        left.StartMicroseconds < right.EndMicroseconds && right.StartMicroseconds < left.EndMicroseconds;
}
