using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Контекст, доступный одному editing skill.</summary>
/// <param name="Analysis">Результат анализа или <see langword="null" />, если анализ ещё недоступен.</param>
/// <param name="Settings">Настройки производства; не могут быть <see langword="null" />.</param>
/// <param name="Comment">Пользовательская инструкция или <see langword="null" />, если она отсутствует.</param>
/// <param name="PreviousDecisions">Предыдущие решения по идентификаторам skills или <see langword="null" />.</param>
/// <param name="MusicAssetId">Выбранный идентификатор music asset или <see langword="null" />.</param>
/// <param name="KnownAssetIds">Известные идентификаторы assets или <see langword="null" />, если список недоступен.</param>
/// <param name="Style">Разрешённые параметры Reels style или <see langword="null" /> для defaults.</param>
/// <param name="MultiSourceAnalysis">Analysis V3 или <see langword="null"/> для legacy V1/V2.</param>
public sealed record EditingContext(
    AnalysisResult? Analysis,
    ProductionSettings Settings,
    string? Comment = null,
    IReadOnlyDictionary<string, EditingDecision>? PreviousDecisions = null,
    string? MusicAssetId = null,
    IReadOnlySet<string>? KnownAssetIds = null,
    ReelsStyleConfiguration? Style = null,
    MultiSourceAnalysisResult? MultiSourceAnalysis = null)
{
    /// <summary>Получает разрешённые сегменты текущего Analysis-контракта.</summary>
    public ImmutableArray<AllowedSegment> AllowedSegments =>
        MultiSourceAnalysis?.AllowedSegments ?? Analysis?.AllowedSegments ?? [];

    /// <summary>Возвращает слова только из source, которому принадлежит mapping.</summary>
    public ImmutableArray<WordTiming> WordsFor(SourceMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        IEnumerable<SpeechSegment> speech = MultiSourceAnalysis is null
            ? Analysis?.Speech ?? []
            : MultiSourceAnalysis.Sources
                .Where(source => source.OriginalAssetId == mapping.OriginalAssetId
                    && source.MasterAssetId == mapping.MasterAssetId)
                .SelectMany(source => source.Speech);
        return [.. speech.SelectMany(item => item.Words)];
    }

    /// <summary>Возвращает паузы только указанного Master.</summary>
    public ImmutableArray<MasterRange> PausesFor(ArtifactId masterAssetId) =>
        MultiSourceAnalysis is null
            ? Analysis?.MasterAssetId == masterAssetId ? Analysis.Pauses : []
            : MultiSourceAnalysis.Sources.FirstOrDefault(source => source.MasterAssetId == masterAssetId)?.Pauses ?? [];

    /// <summary>Возвращает сцены только указанного Master.</summary>
    public ImmutableArray<Scene> ScenesFor(ArtifactId masterAssetId) =>
        MultiSourceAnalysis is null
            ? Analysis?.MasterAssetId == masterAssetId ? Analysis.Scenes : []
            : MultiSourceAnalysis.Sources.FirstOrDefault(source => source.MasterAssetId == masterAssetId)?.Scenes ?? [];

    /// <summary>Возвращает объединённые признаки с глобально уникальными segment IDs.</summary>
    public ImmutableArray<SegmentAnalysisFeatures> SegmentFeatures =>
        MultiSourceAnalysis is null
            ? Analysis?.Features.Segments ?? []
            : [.. MultiSourceAnalysis.Sources.SelectMany(source => source.Features.Segments)];

    /// <summary>Возвращает focal observations только source указанного mapping.</summary>
    public ImmutableArray<FocalObservation> FocalObservationsFor(SourceMapping mapping) =>
        MultiSourceAnalysis is null
            ? Analysis?.MasterAssetId == mapping.MasterAssetId ? Analysis.Features.FocalObservations : []
            : MultiSourceAnalysis.Sources.FirstOrDefault(source =>
                source.OriginalAssetId == mapping.OriginalAssetId
                && source.MasterAssetId == mapping.MasterAssetId)?.Features.FocalObservations ?? [];
}

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

            EditingDecision decisionResult = context.Analysis is null && context.MultiSourceAnalysis is null
                ? new MissingInputDecision(
                    skill.Id,
                    skill.Version,
                    EditingDecisionStatus.Unavailable,
                    [new EditingDecisionReason("skill.analysis_missing", "Analysis is required by the editing skill.")])
                : skill.Evaluate(context with { PreviousDecisions = decisions });
            result.Add(decisionResult);
            decisions[skill.Id] = decisionResult;
            if (decisionResult is MultiClipDecision { Status: EditingDecisionStatus.Applied } multiClip)
            {
                HashSet<SegmentId> selected = multiClip.Selections
                    .Select(selection => selection.SegmentId)
                    .ToHashSet();
                context = context with
                {
                    Analysis = context.Analysis is null
                        ? null
                        : context.Analysis with
                        {
                            AllowedSegments = [.. context.Analysis.AllowedSegments.Where(segment => selected.Contains(segment.Id))]
                        },
                    MultiSourceAnalysis = context.MultiSourceAnalysis is null
                        ? null
                        : context.MultiSourceAnalysis with
                        {
                            AllowedSegments = [.. context.MultiSourceAnalysis.AllowedSegments.Where(segment => selected.Contains(segment.Id))]
                        }
                };
            }
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
