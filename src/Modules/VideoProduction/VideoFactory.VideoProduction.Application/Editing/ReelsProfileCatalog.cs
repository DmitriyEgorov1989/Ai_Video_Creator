using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Поддерживаемый стиль единого Reels pipeline.</summary>
public enum ReelsStyle
{
    /// <summary>Детерминированный выбор конкретного стиля по analysis features.</summary>
    Auto,

    /// <summary>Сдержанное профессиональное оформление.</summary>
    ExpertClean,

    /// <summary>Активное оформление с более заметными акцентами.</summary>
    DynamicViral,

    /// <summary>Плавное оформление с длинными переходами.</summary>
    Cinematic,

    /// <summary>Строгое рекламное оформление с приоритетом безопасного crop.</summary>
    PremiumAds
}

/// <summary>Технический профиль Instagram Reels.</summary>
/// <param name="Id">Стабильный идентификатор профиля.</param>
/// <param name="Width">Ширина результата в пикселях.</param>
/// <param name="Height">Высота результата в пикселях.</param>
/// <param name="FramesPerSecond">Частота кадров.</param>
/// <param name="VideoCodec">Требуемый video codec.</param>
/// <param name="AudioCodec">Требуемый audio codec.</param>
/// <param name="TargetDurationSeconds">Целевая длительность в секундах.</param>
/// <param name="MinimumSemanticDurationSeconds">Нижняя граница предпочтительного semantic window в секундах.</param>
/// <param name="MaximumSemanticDurationSeconds">Верхняя граница предпочтительного semantic window в секундах.</param>
public sealed record ReelsProfile(
    string Id,
    int Width,
    int Height,
    int FramesPerSecond,
    string VideoCodec,
    string AudioCodec,
    int TargetDurationSeconds,
    int MinimumSemanticDurationSeconds,
    int MaximumSemanticDurationSeconds);

/// <summary>Параметры одних и тех же directors для выбранного стиля.</summary>
/// <param name="RequestedStyle">Запрошенный стиль, включая <see cref="ReelsStyle.Auto"/>.</param>
/// <param name="ResolvedStyle">Фактический конкретный стиль.</param>
/// <param name="CaptionHighlight">Способ выделения captions.</param>
/// <param name="CaptionAnimation">Анимация captions.</param>
/// <param name="ZoomStrength">Интенсивность dynamic zoom от 0 до 1.</param>
/// <param name="TransitionStrength">Интенсивность transition от 0 до 1.</param>
/// <param name="MaximumCropMovement">Максимальное перемещение focal point между keyframes.</param>
/// <param name="FadeFrames">Длительность audio fade в кадрах.</param>
/// <param name="Denoise">Нужно ли применять optional denoise.</param>
public sealed record ReelsStyleConfiguration(
    ReelsStyle RequestedStyle,
    ReelsStyle ResolvedStyle,
    CaptionHighlightMode CaptionHighlight,
    CaptionAnimation CaptionAnimation,
    decimal ZoomStrength,
    decimal TransitionStrength,
    decimal MaximumCropMovement,
    int FadeFrames,
    bool Denoise)
{
    /// <summary>Стабильное представление параметров для проверки различий и воспроизводимости.</summary>
    public string ParameterFingerprint => FormattableString.Invariant(
        $"{ResolvedStyle}|{CaptionHighlight}|{CaptionAnimation}|{ZoomStrength:0.###}|{TransitionStrength:0.###}|{MaximumCropMovement:0.###}|{FadeFrames}|{Denoise}");
}

/// <summary>Предоставляет единый технический профиль и неизменяемые style parameters.</summary>
public sealed class ReelsProfileCatalog
{
    /// <summary>Получает технический профиль Instagram Reels.</summary>
    public ReelsProfile InstagramReels { get; } = new(
        "instagram-reels", 1080, 1920, 30, "h264", "aac", 30, 27, 33);

    /// <summary>Разрешает запрошенный стиль в конкретную детерминированную конфигурацию.</summary>
    /// <param name="requested">Запрошенный стиль.</param>
    /// <param name="analysis">Результат анализа; не может быть <see langword="null"/>.</param>
    /// <returns>Параметры directors с сохранёнными requested и resolved styles.</returns>
    public ReelsStyleConfiguration Resolve(ReelsStyle requested, AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        ReelsStyle resolved = requested == ReelsStyle.Auto ? ResolveAuto(analysis) : requested;
        return resolved switch
        {
            ReelsStyle.ExpertClean => new(requested, resolved, CaptionHighlightMode.None, CaptionAnimation.Fade, 0.04m, 0.05m, 0.08m, 9, false),
            ReelsStyle.DynamicViral => new(requested, resolved, CaptionHighlightMode.ActiveWord, CaptionAnimation.Fade, 0.12m, 0.2m, 0.15m, 4, false),
            ReelsStyle.Cinematic => new(requested, resolved, CaptionHighlightMode.None, CaptionAnimation.Fade, 0.08m, 0.3m, 0.06m, 12, true),
            ReelsStyle.PremiumAds => new(requested, resolved, CaptionHighlightMode.ActiveWord, CaptionAnimation.None, 0.06m, 0.1m, 0.05m, 6, true),
            _ => throw new InvalidOperationException("Auto style must resolve to a concrete style."),
        };
    }

    /// <summary>Выбирает ближайшую к 30 секундам завершённую смысловую границу.</summary>
    /// <param name="analysis">Результат анализа с speech или allowed segment boundaries.</param>
    /// <returns>Позиция завершённой границы на master timeline в микросекундах.</returns>
    public long SelectSemanticDurationMicroseconds(
        AnalysisResult analysis,
        IReadOnlyDictionary<SegmentId, SourceMapping>? effectiveMappings = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        const long target = 30_000_000;
        long minimum = InstagramReels.MinimumSemanticDurationSeconds * 1_000_000L;
        long maximum = InstagramReels.MaximumSemanticDurationSeconds * 1_000_000L;
        long cumulative = 0;
        long[] boundaries = analysis.AllowedSegments
            .Select(segment =>
            {
                cumulative += effectiveMappings?.GetValueOrDefault(segment.Id)?.Master.DurationMicroseconds
                    ?? segment.Mapping.Master.DurationMicroseconds;
                return cumulative;
            })
            .ToArray();
        if (boundaries.Length == 0)
        {
            long start = analysis.Speech.IsEmpty ? 0 : analysis.Speech.Min(segment => segment.Interval.StartMicroseconds);
            boundaries = analysis.Speech
                .Select(segment => segment.Interval.EndMicroseconds - start)
                .Distinct()
                .ToArray();
        }
        if (boundaries.Length == 0) return target;
        long[] preferred = boundaries.Where(value => value >= minimum && value <= maximum).ToArray();
        return (preferred.Length == 0 ? boundaries : preferred)
            .OrderBy(value => Math.Abs(value - target))
            .ThenBy(value => value)
            .First();
    }

    private static ReelsStyle ResolveAuto(AnalysisResult analysis)
    {
        if (analysis.Features.Segments.IsEmpty) return ReelsStyle.ExpertClean;
        decimal motion = analysis.Features.Segments.Average(item => item.Motion);
        if (motion >= 0.65m) return ReelsStyle.DynamicViral;
        decimal composition = analysis.Features.Segments.Average(item => item.Composition);
        return composition >= 0.8m ? ReelsStyle.PremiumAds : ReelsStyle.ExpertClean;
    }
}
