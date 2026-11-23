using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Контекст, доступный одному editing skill.</summary>
/// <param name="Analysis">Результат анализа или <see langword="null" />, если анализ ещё недоступен.</param>
/// <param name="Settings">Настройки производства; не могут быть <see langword="null" />.</param>
public sealed record EditingContext(
    AnalysisResult? Analysis,
    ProductionSettings Settings,
    string? Comment = null,
    IReadOnlyDictionary<string, EditingDecision>? PreviousDecisions = null);

/// <summary>Статус детерминированного решения editing skill.</summary>
public enum EditingDecisionStatus
{
    /// <summary>Skill применил решение.</summary>
    Applied,

    /// <summary>Skill отключён конфигурацией.</summary>
    Skipped,

    /// <summary>Skill не может примениться из-за отсутствующих входных данных.</summary>
    Unavailable
}

/// <summary>Причина, сохранённая вместе с решением skill.</summary>
/// <param name="Code">Стабильный машинный код причины.</param>
/// <param name="Message">Читаемое описание причины; не может быть <see langword="null" />.</param>
public sealed record EditingDecisionReason(string Code, string Message);

/// <summary>Типизированный результат работы одного editing skill.</summary>
/// <param name="SkillId">Идентификатор skill.</param>
/// <param name="SkillVersion">Версия skill.</param>
/// <param name="Status">Статус результата.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
public abstract record EditingDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons);

/// <summary>Настройка одного зарегистрированного skill.</summary>
/// <param name="SkillId">Идентификатор skill.</param>
/// <param name="Enabled">Признак включения skill.</param>
public sealed record EditingSkillConfigurationEntry(string SkillId, bool Enabled);

/// <summary>Конфигурация последовательного запуска editing skills.</summary>
/// <param name="Skills">Настройки skills в порядке выполнения.</param>
public sealed record EditingSkillConfiguration(
    ImmutableArray<EditingSkillConfigurationEntry> Skills);

/// <summary>Результат запуска всех настроенных skills.</summary>
/// <param name="Decisions">Типизированные решения в порядке выполнения.</param>
public sealed record EditingEvaluation(
    ImmutableArray<EditingDecision> Decisions);

/// <summary>Публичный контракт одного детерминированного editing skill.</summary>
public interface IEditingSkill
{
    /// <summary>Стабильный идентификатор skill.</summary>
    string Id { get; }

    /// <summary>Версия алгоритма skill.</summary>
    string Version { get; }

    /// <summary>Идентификаторы skills, результаты которых требуются до запуска.</summary>
    ImmutableArray<string> Prerequisites { get; }

    /// <summary>Вычисляет typed decision без изменения состояния задания.</summary>
    EditingDecision Evaluate(EditingContext context);
}

/// <summary>Детерминированный engine последовательного запуска editing skills.</summary>
public sealed class EditingSkillEngine
{
    private readonly IReadOnlyDictionary<string, IEditingSkill> skills;

    /// <summary>Создаёт engine из зарегистрированных skills.</summary>
    /// <param name="skills">Зарегистрированные skills; идентификаторы должны быть уникальны.</param>
    public EditingSkillEngine(IEnumerable<IEditingSkill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        this.skills = skills.ToDictionary(skill => skill.Id, StringComparer.Ordinal);
    }

    /// <summary>Выполняет skills в порядке конфигурации.</summary>
    /// <param name="context">Входной контекст.</param>
    /// <param name="configuration">Конфигурация запуска.</param>
    /// <returns>Typed decisions без исключения для отсутствующего анализа.</returns>
    public EditingEvaluation Evaluate(
        EditingContext context,
        EditingSkillConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        Dictionary<string, EditingDecision> decisions = new(StringComparer.Ordinal);
        ImmutableArray<EditingDecision>.Builder result = ImmutableArray.CreateBuilder<EditingDecision>();
        foreach (EditingSkillConfigurationEntry entry in configuration.Skills)
        {
            if (!skills.TryGetValue(entry.SkillId, out IEditingSkill? skill))
            {
                result.Add(new MissingSkillDecision(
                    entry.SkillId,
                    "unknown",
                    EditingDecisionStatus.Unavailable,
                    [new EditingDecisionReason("skill.unknown", "The configured skill is not registered.")]));
                continue;
            }

            if (!entry.Enabled)
            {
                EditingDecision skipped = new MissingInputDecision(
                    skill.Id,
                    skill.Version,
                    EditingDecisionStatus.Skipped,
                    [new EditingDecisionReason("skill.disabled", "The skill is disabled by configuration.")]);
                result.Add(skipped);
                decisions[skill.Id] = skipped;
                continue;
            }

            string? missingPrerequisite = skill.Prerequisites.FirstOrDefault(
                prerequisite => !decisions.TryGetValue(prerequisite, out EditingDecision? decision)
                    || decision.Status != EditingDecisionStatus.Applied);
            if (missingPrerequisite is not null)
            {
                EditingDecision unavailable = new MissingInputDecision(
                    skill.Id,
                    skill.Version,
                    EditingDecisionStatus.Unavailable,
                    [new EditingDecisionReason("skill.prerequisite_missing", missingPrerequisite)]);
                result.Add(unavailable);
                decisions[skill.Id] = unavailable;
                continue;
            }

            EditingDecision decisionResult = context.Analysis is null
                ? new MissingInputDecision(
                    skill.Id,
                    skill.Version,
                    EditingDecisionStatus.Unavailable,
                    [new EditingDecisionReason("skill.analysis_missing", "Analysis is required by the editing skill.")])
                : skill.Evaluate(context with { PreviousDecisions = decisions });
            result.Add(decisionResult);
            decisions[skill.Id] = decisionResult;
        }

        return new EditingEvaluation(result.ToImmutable());
    }

    private sealed record MissingSkillDecision(
        string SkillId,
        string SkillVersion,
        EditingDecisionStatus Status,
        ImmutableArray<EditingDecisionReason> Reasons)
        : EditingDecision(SkillId, SkillVersion, Status, Reasons);

    private sealed record MissingInputDecision(
        string SkillId,
        string SkillVersion,
        EditingDecisionStatus Status,
        ImmutableArray<EditingDecisionReason> Reasons)
        : EditingDecision(SkillId, SkillVersion, Status, Reasons);
}
