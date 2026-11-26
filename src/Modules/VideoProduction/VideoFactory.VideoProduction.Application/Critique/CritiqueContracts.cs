using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Определяет вид подтверждённого объективного дефекта монтажа.</summary>
public enum CriticFindingKind
{
    /// <summary>Готовый рендер содержит чёрные кадры.</summary>
    BlackFrames,

    /// <summary>Готовый рендер содержит незапланированный неподвижный интервал.</summary>
    FrozenFrames,

    /// <summary>Готовый рендер превышает разрешённый true peak.</summary>
    AudioPeak
}

/// <summary>Определяет решение объективного критика по готовому рендеру.</summary>
public enum CriticDecision
{
    /// <summary>Подтверждённых объективных дефектов нет.</summary>
    Accepted,

    /// <summary>Найдена поддерживаемая коррекция, требующая нового рендера.</summary>
    RemountRequired,

    /// <summary>Найден объективный дефект, для которого нет безопасной коррекции.</summary>
    Rejected
}

/// <summary>Описывает подтверждённый объективный дефект на выходной шкале.</summary>
/// <param name="Code">Стабильный машинный код дефекта; не может быть <see langword="null"/>.</param>
/// <param name="Kind">Вид дефекта.</param>
/// <param name="Output">Полуоткрытый интервал дефекта в выходных кадрах.</param>
/// <param name="IsRepairable">Признак наличия поддерживаемой коррекции.</param>
public sealed record CriticFinding(
    string Code,
    CriticFindingKind Kind,
    OutputRange Output,
    bool IsRepairable);

/// <summary>Представляет закрытый набор поддерживаемых операций исправления плана.</summary>
public abstract record CorrectionProposal;

/// <summary>Предлагает заменить целый проблемный видеоклип известным одобренным сегментом.</summary>
/// <param name="DefectOutput">Интервал подтверждённого дефекта в выходных кадрах.</param>
/// <param name="ClipOutput">Полный выходной интервал заменяемого клипа.</param>
/// <param name="ExpectedSegmentId">Сегмент исходной ревизии, служащий precondition.</param>
/// <param name="ReplacementSegmentId">Идентификатор одобренного сегмента замены.</param>
/// <param name="ReplacementMapping">Проверенное соответствие шкал сегмента замены; не может быть <see langword="null"/>.</param>
public sealed record ReplaceVideoClipCorrection(
    OutputRange DefectOutput,
    OutputRange ClipOutput,
    SegmentId ExpectedSegmentId,
    SegmentId ReplacementSegmentId,
    SourceMapping ReplacementMapping) : CorrectionProposal;

/// <summary>Предлагает пропорционально уменьшить gain всех известных audio clips.</summary>
/// <param name="Factor">Линейный коэффициент больше нуля и не больше единицы.</param>
public sealed record ScaleAudioGainCorrection(decimal Factor) : CorrectionProposal;

/// <summary>Входные данные детерминированной объективной критики готового рендера.</summary>
/// <param name="Plan">Утверждённый монтажный план; не может быть <see langword="null"/>.</param>
/// <param name="Analysis">Утверждённый анализ с разрешёнными сегментами; не может быть <see langword="null"/>.</param>
/// <param name="BlackIntervals">Подтверждённые чёрные интервалы в выходных кадрах.</param>
public sealed record ObjectiveCritiqueRequest(
    EditPlan Plan,
    MultiSourceAnalysisResult Analysis,
    ImmutableArray<OutputRange> BlackIntervals)
{
    /// <summary>Подтверждённые незапланированные неподвижные интервалы в выходных кадрах.</summary>
    public ImmutableArray<OutputRange> FrozenIntervals { get; init; } = [];

    /// <summary>Измеренный true peak в dBTP или <see langword="null"/>, если звук не измерялся.</summary>
    public decimal? MeasuredTruePeakDbtp { get; init; }

    /// <summary>Максимально допустимый true peak в dBTP или <see langword="null"/>, если ограничение отсутствует.</summary>
    public decimal? MaximumTruePeakDbtp { get; init; }
}

/// <summary>Содержит объективные дефекты, поддерживаемые коррекции и итоговое решение.</summary>
/// <param name="Findings">Подтверждённые дефекты; никогда не содержит <see langword="null"/>.</param>
/// <param name="Corrections">Безопасные типизированные коррекции; никогда не содержит <see langword="null"/>.</param>
/// <param name="Decision">Решение о дальнейшем прохождении конвейера.</param>
public sealed record ObjectiveCritiqueResult(
    ImmutableArray<CriticFinding> Findings,
    ImmutableArray<CorrectionProposal> Corrections,
    CriticDecision Decision);
