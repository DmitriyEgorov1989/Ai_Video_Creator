using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Изменение одного разрешённого сегмента после удаления безопасных пауз.</summary>
/// <param name="SegmentId">Идентификатор исходного сегмента.</param>
/// <param name="Mapping">Обрезанное соответствие original/master; не может быть <see langword="null" />.</param>
public sealed record SpeechTighteningEdit(
    SegmentId SegmentId,
    SourceMapping Mapping);

/// <summary>Typed результат Speech Tightening.</summary>
/// <param name="SkillId">Идентификатор skill.</param>
/// <param name="SkillVersion">Версия skill.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="Edits">Изменения разрешённых сегментов.</param>
public sealed record SpeechTighteningDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<SpeechTighteningEdit> Edits)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Удаляет только безопасные leading/trailing pauses и сохраняет целые слова.</summary>
public sealed class SpeechTighteningSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "speech-tightening";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => [];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Analysis is null)
        {
            return new SpeechTighteningDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new EditingDecisionReason("skill.analysis_missing", "Analysis is required by Speech Tightening.")],
                []);
        }

        AnalysisResult analysis = context.Analysis;
        ImmutableArray<SpeechTighteningEdit>.Builder edits = ImmutableArray.CreateBuilder<SpeechTighteningEdit>();
        foreach (AllowedSegment segment in analysis.AllowedSegments)
        {
            SpeechTighteningEdit? edit = CreateEdit(segment, analysis);
            if (edit is not null)
            {
                edits.Add(edit);
            }
        }

        ImmutableArray<SpeechTighteningEdit> result = edits.ToImmutable();
        return new SpeechTighteningDecision(
            Id,
            Version,
            EditingDecisionStatus.Applied,
            result.IsEmpty
                ? [new EditingDecisionReason("speech_tightening.no_change", "No fully pause-covered boundary was found.")]
                : [new EditingDecisionReason("speech_tightening.applied", "Removed only pause-covered boundaries around whole words.")],
            result);
    }

    private static SpeechTighteningEdit? CreateEdit(
        AllowedSegment segment,
        AnalysisResult analysis)
    {
        SourceMapping mapping = segment.Mapping;
        WordTiming[] words = analysis.Speech
            .SelectMany(speech => speech.Words)
            .Where(word => mapping.Master.Contains(word.Interval))
            .OrderBy(word => word.Interval.StartMicroseconds)
            .ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        long start = mapping.Master.StartMicroseconds;
        long end = mapping.Master.EndMicroseconds;
        if (IsPauseCovered(start, words[0].Interval.StartMicroseconds, analysis.Pauses))
        {
            start = words[0].Interval.StartMicroseconds;
        }

        if (IsPauseCovered(words[^1].Interval.EndMicroseconds, end, analysis.Pauses))
        {
            end = words[^1].Interval.EndMicroseconds;
        }

        if (start == mapping.Master.StartMicroseconds && end == mapping.Master.EndMicroseconds)
        {
            return null;
        }

        long originalStart = checked(
            mapping.Original.StartMicroseconds + start - mapping.Master.StartMicroseconds);
        long originalEnd = checked(
            mapping.Original.EndMicroseconds - mapping.Master.EndMicroseconds + end);
        SourceMapping trimmed = mapping with
        {
            Original = new OriginalRange(originalStart, originalEnd),
            Master = new MasterRange(start, end)
        };
        return new SpeechTighteningEdit(segment.Id, trimmed);
    }

    private static bool IsPauseCovered(
        long start,
        long end,
        ImmutableArray<MasterRange> pauses)
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
