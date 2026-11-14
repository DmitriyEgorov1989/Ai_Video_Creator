using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Публикует неизменяемую ревизию результата анализа.</summary>
public interface IAnalysisArtifactPublisher
{
    /// <summary>Атомарно публикует JSON и регистрирует его как Artifact.</summary>
    Task<AnalysisArtifactPublishResult> PublishAsync(
        JobId jobId,
        AnalysisResult analysis,
        CancellationToken cancellationToken);
}

/// <summary>Результат публикации Analysis Artifact.</summary>
public sealed record AnalysisArtifactPublishResult(
    bool IsSuccess,
    ArtifactId? ArtifactId,
    string? ErrorCode,
    string? ErrorMessage);
