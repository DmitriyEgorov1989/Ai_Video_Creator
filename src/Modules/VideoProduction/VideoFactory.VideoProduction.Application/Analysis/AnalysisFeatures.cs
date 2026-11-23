using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Запрашивает объективные признаки одного разрешённого интервала.</summary>
/// <param name="FilePath">Путь к Master-файлу.</param>
/// <param name="Interval">Интервал на временной шкале Master в микросекундах.</param>
public sealed record FeatureExtractionRequest(string FilePath, MasterRange Interval);

/// <summary>Объективные признаки качества, нормализованные в диапазон от 0 до 1.</summary>
/// <param name="Motion">Средняя выраженность движения; значение от 0 до 1.</param>
/// <param name="Sharpness">Оценка резкости; значение от 0 до 1.</param>
/// <param name="Exposure">Оценка корректности экспозиции; значение от 0 до 1.</param>
/// <param name="AudioEnergy">Энергия аудио; значение от 0 до 1.</param>
/// <param name="TechnicalValidity">Прошёл ли интервал техническую проверку.</param>
/// <param name="TechnicalIssues">Коды обнаруженных технических проблем; может быть пустым.</param>
public sealed record SegmentQualityFeatures(
    decimal Motion,
    decimal Sharpness,
    decimal Exposure,
    decimal AudioEnergy,
    bool TechnicalValidity,
    ImmutableArray<string> TechnicalIssues)
{
    /// <summary>Безопасный fallback при недоступности feature extractor.</summary>
    public static SegmentQualityFeatures Fallback { get; } = new(
        0m,
        0.5m,
        0.5m,
        0m,
        true,
        ["features-unavailable-fallback"]);

    /// <summary>Преобразует application-результат в сохраняемую доменную модель.</summary>
    public SegmentQualitySnapshot ToSnapshot() => new(
        Motion, Sharpness, Exposure, AudioEnergy, TechnicalValidity, TechnicalIssues);
}

/// <summary>Извлекает признаки без изменения исходного видео.</summary>
public interface IAnalysisFeatureExtractor
{
    /// <summary>Извлекает признаки указанного интервала.</summary>
    Task<SegmentQualityFeatures> ExtractAsync(
        FeatureExtractionRequest request,
        CancellationToken cancellationToken);
}
