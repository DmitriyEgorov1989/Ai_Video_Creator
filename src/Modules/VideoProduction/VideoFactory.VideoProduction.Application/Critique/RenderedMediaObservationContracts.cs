using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Описывает запрос объективных измерений готового рендера.</summary>
/// <param name="FilePath">Абсолютный путь к MP4; не может быть <see langword="null"/>.</param>
/// <param name="FrameRate">Частота кадров утверждённого плана.</param>
/// <param name="DurationFrames">Ожидаемая длительность рендера в кадрах.</param>
public sealed record RenderedMediaObservationRequest(
    string FilePath,
    FrameRate FrameRate,
    int DurationFrames);

/// <summary>Содержит объективные интервалы, измеренные в готовом рендере.</summary>
/// <param name="BlackIntervals">Чёрные интервалы на выходной шкале; никогда не содержит <see langword="null"/>.</param>
public sealed record RenderedMediaObservation(
    ImmutableArray<OutputRange> BlackIntervals)
{
    /// <summary>Незапланированные неподвижные интервалы на выходной шкале.</summary>
    public ImmutableArray<OutputRange> FrozenIntervals { get; init; } = [];

    /// <summary>Измеренный true peak в dBTP или <see langword="null"/>, если audio stream не измерялся.</summary>
    public decimal? TruePeakDbtp { get; init; }
}

/// <summary>Представляет ожидаемый результат измерения готового рендера.</summary>
public abstract record RenderedMediaObservationResult
{
    private RenderedMediaObservationResult() { }

    /// <summary>Измерение успешно завершено.</summary>
    /// <param name="Observation">Полученные наблюдения; не могут быть <see langword="null"/>.</param>
    public sealed record Succeeded(RenderedMediaObservation Observation) : RenderedMediaObservationResult;

    /// <summary>Измерение завершилось ожидаемой интеграционной ошибкой.</summary>
    /// <param name="ErrorCode">Исходный код ошибки; не может быть <see langword="null"/>.</param>
    /// <param name="ErrorMessage">Исходное сообщение ошибки; не может быть <see langword="null"/>.</param>
    public sealed record Failed(string ErrorCode, string ErrorMessage) : RenderedMediaObservationResult;
}

/// <summary>Измеряет объективные признаки уже созданного MP4.</summary>
public interface IRenderedMediaObserver
{
    /// <summary>Возвращает наблюдения либо ожидаемую ошибку внешнего процесса.</summary>
    Task<RenderedMediaObservationResult> ObserveAsync(
        RenderedMediaObservationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Загружает утверждённый Analysis для объективной коррекции плана.</summary>
public interface ICritiqueAnalysisSource
{
    /// <summary>Возвращает Analysis V3 задания или <see langword="null"/>, если артефакт недоступен или невалиден.</summary>
    Task<MultiSourceAnalysisResult?> ReadAsync(
        JobId jobId,
        CancellationToken cancellationToken);
}
