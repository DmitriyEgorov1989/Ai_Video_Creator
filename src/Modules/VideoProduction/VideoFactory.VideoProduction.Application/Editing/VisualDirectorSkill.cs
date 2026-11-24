using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Закрытый набор визуальных эффектов, выбираемых Visual Director.</summary>
public enum VisualEffectKind
{
    /// <summary>Плавное увеличение внутри одного shot.</summary>
    DynamicZoom,

    /// <summary>Выделение текста, уже выбранного Caption Director.</summary>
    TextHighlight,

    /// <summary>Короткий визуальный переход на границе shots.</summary>
    VisualTransition
}

/// <summary>Визуальный эффект с явным происхождением и выходным интервалом.</summary>
/// <param name="Id">Стабильный уникальный идентификатор эффекта.</param>
/// <param name="Kind">Тип разрешённого эффекта.</param>
/// <param name="Output">Интервал эффекта на выходной шкале.</param>
/// <param name="Strength">Нормализованная интенсивность от 0 до 1.</param>
/// <param name="SourceSkillId">Идентификатор skill, создавшего эффект.</param>
public sealed record VisualEffectInstruction(
    string Id,
    VisualEffectKind Kind,
    OutputRange Output,
    decimal Strength,
    string SourceSkillId);

/// <summary>Результат Visual Director.</summary>
/// <param name="SkillId">Стабильный идентификатор skill.</param>
/// <param name="SkillVersion">Версия алгоритма.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="Effects">Закрытые визуальные эффекты; может быть пустым.</param>
public sealed record VisualDirectorDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<VisualEffectInstruction> Effects)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Выбирает ограниченные эффекты только из утверждённых editing decisions.</summary>
public sealed class VisualDirectorSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "visual-director";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => ["pacing-director"];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PacingDecision? pacing = context.PreviousDecisions?.GetValueOrDefault("pacing-director") as PacingDecision;
        CaptionDirectorDecision? captions = context.PreviousDecisions?.GetValueOrDefault("caption-director") as CaptionDirectorDecision;
        if (pacing?.Status != EditingDecisionStatus.Applied)
        {
            return new VisualDirectorDecision(
                Id,
                Version,
                EditingDecisionStatus.Unavailable,
                [new("visual.directing_missing", "An applied pacing decision is required.")],
                []);
        }

        ImmutableArray<VisualEffectInstruction>.Builder effects = ImmutableArray.CreateBuilder<VisualEffectInstruction>();
        int zoomIndex = 0;
        foreach (PacingShot shot in pacing.Shots.Where(shot => shot.Accent))
        {
            effects.Add(new(
                $"{Id}-zoom-{zoomIndex++}",
                VisualEffectKind.DynamicZoom,
                shot.Output,
                context.Style?.ZoomStrength ?? 0.12m,
                Id));
        }

        int highlightIndex = 0;
        foreach (CaptionGroup group in captions?.Groups.Where(group => group.Highlight == CaptionHighlightMode.ActiveWord) ?? [])
        {
            effects.Add(new(
                $"{Id}-highlight-{highlightIndex++}",
                VisualEffectKind.TextHighlight,
                group.Output,
                1m,
                Id));
        }

        int previousTransitionEnd = -1;
        for (int index = 1; index < pacing.Shots.Length; index++)
        {
            int boundary = pacing.Shots[index].Output.StartFrame;
            int start = Math.Max(previousTransitionEnd, boundary - 1);
            int end = Math.Min(pacing.Shots[index].Output.EndFrame, boundary + 1);
            if (end <= start)
                continue;
            effects.Add(new(
                $"{Id}-transition-{index - 1}",
                VisualEffectKind.VisualTransition,
                new OutputRange(start, end),
                context.Style?.TransitionStrength ?? 0.2m,
                Id));
            previousTransitionEnd = end;
        }

        return new VisualDirectorDecision(
            Id,
            Version,
            EditingDecisionStatus.Applied,
            [new("visual.closed_effects", "Effects are derived from applied pacing and caption decisions.")],
            effects.ToImmutable());
    }
}
