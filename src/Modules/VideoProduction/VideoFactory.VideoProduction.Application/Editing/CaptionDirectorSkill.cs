using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Способ выделения слов в группе субтитров.</summary>
public enum CaptionHighlightMode
{
    /// <summary>Слова не выделяются отдельно.</summary>
    None,

    /// <summary>Выделяется слово, активное на текущем выходном кадре.</summary>
    ActiveWord
}

/// <summary>Ограниченная анимация появления группы субтитров.</summary>
public enum CaptionAnimation
{
    /// <summary>Группа появляется без анимации.</summary>
    None,

    /// <summary>Группа плавно появляется и исчезает в пределах своего интервала.</summary>
    Fade
}

/// <summary>Расположение субтитров относительно безопасной области кадра.</summary>
public enum CaptionPlacement
{
    /// <summary>Нижняя безопасная область над интерфейсом социальной сети.</summary>
    BottomSafe
}

/// <summary>Слово субтитров, привязанное к выходной временной шкале.</summary>
/// <param name="Text">Исходный Unicode-текст слова; не может быть <see langword="null"/>.</param>
/// <param name="Output">Интервал отображения слова в выходных кадрах.</param>
public sealed record CaptionWordCue(string Text, OutputRange Output);

/// <summary>Семантически завершённая группа слов для одновременного отображения.</summary>
/// <param name="Text">Текст группы без изменения исходных символов.</param>
/// <param name="Words">Слова группы с индивидуальными интервалами.</param>
/// <param name="Output">Общий интервал группы.</param>
/// <param name="MaxLines">Максимальное число отображаемых строк.</param>
/// <param name="Highlight">Способ выделения слов.</param>
/// <param name="Animation">Анимация группы.</param>
/// <param name="Placement">Положение в безопасной области.</param>
public sealed record CaptionGroup(
    string Text,
    ImmutableArray<CaptionWordCue> Words,
    OutputRange Output,
    int MaxLines,
    CaptionHighlightMode Highlight,
    CaptionAnimation Animation,
    CaptionPlacement Placement);

/// <summary>Результат Caption Director.</summary>
/// <param name="SkillId">Стабильный идентификатор skill.</param>
/// <param name="SkillVersion">Версия алгоритма.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="Groups">Группы субтитров на выходной шкале; может быть пустым.</param>
public sealed record CaptionDirectorDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<CaptionGroup> Groups)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Создаёт читаемые группы субтитров из известных word timings.</summary>
public sealed class CaptionDirectorSkill : IEditingSkill
{
    private const int MaxWordsPerGroup = 4;
    private const int MaxCharactersPerGroup = 32;

    /// <inheritdoc />
    public string Id => "caption-director";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["pacing-director"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Settings.CaptionsEnabled)
        {
            return new CaptionDirectorDecision(
                Id,
                Version,
                EditingDecisionStatus.Skipped,
                [new("captions.disabled", "Captions are disabled by production settings.")],
                []);
        }

        AnalysisResult? analysis = context.Analysis;
        PacingDecision? pacing = context.PreviousDecisions?.GetValueOrDefault("pacing-director") as PacingDecision;
        if (analysis is null || pacing?.Status != EditingDecisionStatus.Applied)
        {
            return new CaptionDirectorDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new("captions.timeline_missing", "Analysis and an applied pacing timeline are required.")],
                []);
        }

        ImmutableArray<CaptionWordCue> words = pacing.Shots
            .SelectMany(shot => MapWords(analysis, shot, context.PreviousDecisions))
            .OrderBy(word => word.Output.StartFrame)
            .ToImmutableArray();
        ImmutableArray<CaptionGroup> groups = GroupWords(words, context.Style);
        return groups.IsEmpty
            ? new CaptionDirectorDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new("captions.words_missing", "No complete words map to the selected output timeline.")],
                [])
            : new CaptionDirectorDecision(
                Id,
                Version,
                EditingDecisionStatus.Applied,
                [new("captions.word_aligned", "Caption groups preserve known words and output timing.")],
                groups);
    }

    private static IEnumerable<CaptionWordCue> MapWords(
        AnalysisResult analysis,
        PacingShot shot,
        IReadOnlyDictionary<string, EditingDecision>? decisions)
    {
        AllowedSegment? segment = analysis.AllowedSegments.FirstOrDefault(item => item.Id == shot.SegmentId);
        if (segment is null)
        {
            yield break;
        }

        SourceMapping mapping = EffectiveMapping(segment, shot.SegmentId, decisions);
        foreach (WordTiming word in analysis.Speech
                     .SelectMany(speech => speech.Words)
                     .Where(word => mapping.Master.Contains(word.Interval))
                     .OrderBy(word => word.Interval.StartMicroseconds))
        {
            int start = MapFrame(word.Interval.StartMicroseconds, mapping.Master, shot.Output);
            int end = MapFrame(word.Interval.EndMicroseconds, mapping.Master, shot.Output);
            if (end > start)
            {
                yield return new CaptionWordCue(word.Text, new OutputRange(start, end));
            }
        }
    }

    private static SourceMapping EffectiveMapping(
        AllowedSegment segment,
        VideoFactory.VideoProduction.Domain.Identifiers.SegmentId segmentId,
        IReadOnlyDictionary<string, EditingDecision>? decisions) =>
        decisions?.GetValueOrDefault("smart-cut") is SmartCutDecision cut
            ? cut.Edits.FirstOrDefault(edit => edit.SegmentId == segmentId)?.Mapping ?? segment.Mapping
            : segment.Mapping;

    private static int MapFrame(long position, MasterRange source, OutputRange output)
    {
        decimal progress = (position - source.StartMicroseconds) / (decimal)source.DurationMicroseconds;
        return output.StartFrame + (int)Math.Round(progress * output.DurationFrames, MidpointRounding.AwayFromZero);
    }

    private static ImmutableArray<CaptionGroup> GroupWords(
        ImmutableArray<CaptionWordCue> words,
        ReelsStyleConfiguration? style)
    {
        ImmutableArray<CaptionGroup>.Builder groups = ImmutableArray.CreateBuilder<CaptionGroup>();
        List<CaptionWordCue> current = [];
        foreach (CaptionWordCue word in words)
        {
            int prospectiveCharacters = current.Sum(item => item.Text.Length) + current.Count + word.Text.Length;
            if (current.Count > 0 && (current.Count == MaxWordsPerGroup || prospectiveCharacters > MaxCharactersPerGroup))
            {
                groups.Add(CreateGroup(current, style));
                current.Clear();
            }

            current.Add(word);
            if (EndsSentence(word.Text))
            {
                groups.Add(CreateGroup(current, style));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            groups.Add(CreateGroup(current, style));
        }

        return groups.ToImmutable();
    }

    private static CaptionGroup CreateGroup(List<CaptionWordCue> words, ReelsStyleConfiguration? style) => new(
        string.Join(' ', words.Select(word => word.Text)),
        words.ToImmutableArray(),
        new OutputRange(words[0].Output.StartFrame, words[^1].Output.EndFrame),
        MaxLines: 2,
        style?.CaptionHighlight ?? CaptionHighlightMode.ActiveWord,
        style?.CaptionAnimation ?? CaptionAnimation.Fade,
        CaptionPlacement.BottomSafe);

    private static bool EndsSentence(string text) =>
        text.EndsWith('.')
        || text.EndsWith('!')
        || text.EndsWith('?');
}
