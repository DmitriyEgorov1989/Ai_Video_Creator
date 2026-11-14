using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>
/// Описывает входные данные анализа одного исходного артефакта.
/// </summary>
/// <param name="OriginalAssetId">Идентификатор неизменённого оригинала.</param>
/// <param name="MasterAssetId">Идентификатор master, используемого для анализа.</param>
/// <param name="FilePath">Абсолютный путь к master-файлу.</param>
/// <param name="OriginalDurationMicroseconds">Длительность оригинала в микросекундах.</param>
/// <param name="MasterDurationMicroseconds">Длительность master в микросекундах.</param>
public sealed record AnalysisRequest(
    ArtifactId OriginalAssetId,
    ArtifactId MasterAssetId,
    string FilePath,
    long OriginalDurationMicroseconds,
    long MasterDurationMicroseconds)
{
    /// <summary>Проверяет обязательные значения запроса.</summary>
    public void Validate()
    {
        if (OriginalAssetId.IsEmpty) throw new ArgumentException("Original asset ID cannot be empty.", nameof(OriginalAssetId));
        if (MasterAssetId.IsEmpty) throw new ArgumentException("Master asset ID cannot be empty.", nameof(MasterAssetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(FilePath);
        if (OriginalDurationMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(OriginalDurationMicroseconds));
        if (MasterDurationMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(MasterDurationMicroseconds));
    }
}
