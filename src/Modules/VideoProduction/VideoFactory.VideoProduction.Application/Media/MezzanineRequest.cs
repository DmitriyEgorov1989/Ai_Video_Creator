using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает подготовку Master из Original.</summary>
public sealed record MezzanineRequest(
    string OriginalPath,
    string MasterPath,
    string OriginalArtifactId,
    string MasterArtifactId,
    MediaMetadata OriginalMetadata,
    TimeSpan NormalizeTimeout,
    TimeSpan ProbeTimeout,
    TimeSpan DecodeTimeout,
    int MaxOutputBytes);

/// <summary>Результат подготовки Master.</summary>
public sealed record MezzanineResult(
    bool IsSuccess,
    string PublishedPath,
    bool WasNormalized,
    TimelineMapping? Mapping,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static MezzanineResult Succeeded(
        string path,
        bool normalized,
        TimelineMapping mapping) =>
        new(true, path, normalized, mapping, null, null);

    /// <summary>Создаёт ошибочный результат без опубликованного Master.</summary>
    public static MezzanineResult Failed(string code, string message) =>
        new(false, string.Empty, false, null, code, message);
}

/// <summary>Создаёт проверенный Master из Original при необходимости.</summary>
public interface IMezzanineBuilder
{
    /// <summary>Создаёт или переиспользует Master.</summary>
    Task<MezzanineResult> BuildAsync(
        MezzanineRequest request,
        CancellationToken cancellationToken);
}
